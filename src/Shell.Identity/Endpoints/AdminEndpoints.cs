using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shell.Core;
using Shell.Core.Tenancy;
using Shell.Identity.Access;
using Shell.Identity.Audit;
using Shell.Identity.Clients;
using Shell.Identity.Data;
using Shell.Identity.Tenants;

namespace Shell.Identity.Endpoints;

public sealed record AdminTenantDto(Guid Id, string Name, bool IsActive, bool IsIsolatedDb, IReadOnlyList<string> Domains);

public sealed record AddDomainRequest(string Domain);

public sealed record AdminUserDto(
    Guid Id,
    string? Email,
    bool EmailConfirmed,
    bool IsDisabled,
    bool IsPlatformAdmin,
    Guid? ManagedByTenantId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Platform administration endpoints on the authorization server host (<c>/admin/*</c>).
/// Requires an authenticated platform admin user (<see cref="PlatformPolicies.Admin"/>).
/// Mutating endpoints also require a valid antiforgery header (<see cref="IAntiforgery"/>).
/// </summary>
internal static class AdminEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints, string authHost)
    {
        var admin = endpoints.MapGroup("/admin")
            .RequireHost(authHost)
            .RequireAuthorization(PlatformPolicies.Admin)
            .AddEndpointFilter(ValidateAntiforgeryForMutationsAsync)
            .WithTags("Admin");

        admin.MapGet("/antiforgery", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            return Results.Ok(new { headerName = tokens.HeaderName ?? "RequestVerificationToken", token = tokens.RequestToken });
        });

        admin.MapGet("/tenants", ListTenantsAsync);
        admin.MapPost("/tenants/{id:guid}/lock", (Guid id, HttpContext http, CatalogDbContext catalog, TenantStore tenants, AuditLog audit, CancellationToken ct) =>
            SetTenantActiveAsync(id, active: false, http, catalog, tenants, audit, ct));
        admin.MapPost("/tenants/{id:guid}/unlock", (Guid id, HttpContext http, CatalogDbContext catalog, TenantStore tenants, AuditLog audit, CancellationToken ct) =>
            SetTenantActiveAsync(id, active: true, http, catalog, tenants, audit, ct));
        admin.MapPost("/tenants/{id:guid}/domains", AddDomainAsync);
        admin.MapDelete("/tenants/{id:guid}/domains/{domain}", RemoveDomainAsync);

        admin.MapGet("/users", ListUsersAsync);
        admin.MapPost("/users/{id:guid}/disable", (Guid id, HttpContext http, UserAccountService accounts, CancellationToken ct) =>
            SetUserDisabledAsync(id, disabled: true, http, accounts, ct));
        admin.MapPost("/users/{id:guid}/enable", (Guid id, HttpContext http, UserAccountService accounts, CancellationToken ct) =>
            SetUserDisabledAsync(id, disabled: false, http, accounts, ct));
    }

    private static async ValueTask<object?> ValidateAntiforgeryForMutationsAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
        {
            var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
            try
            {
                await antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(detail: "Invalid or missing antiforgery token.", statusCode: StatusCodes.Status400BadRequest);
            }
        }

        return await next(context);
    }

    private static async Task<IResult> ListTenantsAsync(
        string? search, int? limit, int? offset, CatalogDbContext catalog, CancellationToken ct)
    {
        var query = catalog.Tenants.AsNoTracking().Include(t => t.Domains).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(t => EF.Functions.ILike(t.Name, $"%{term}%")
                || t.Domains.Any(d => EF.Functions.ILike(d.Domain, $"%{term}%")));
        }

        var tenants = await query
            .OrderBy(t => t.Name)
            .ThenBy(t => t.Id)
            .Skip(Math.Max(offset ?? 0, 0))
            .Take(Math.Clamp(limit ?? 100, 1, 500))
            .ToListAsync(ct);

        return Results.Ok(tenants.Select(t => new AdminTenantDto(
            t.Id,
            t.Name,
            t.IsActive,
            t.IsIsolatedDb,
            t.Domains.OrderBy(d => d.Domain).Select(d => d.Domain).ToList())));
    }

    private static async Task<IResult> SetTenantActiveAsync(
        Guid id,
        bool active,
        HttpContext http,
        CatalogDbContext catalog,
        TenantStore tenants,
        AuditLog audit,
        CancellationToken ct)
    {
        var tenant = await catalog.Tenants.Include(t => t.Domains).SingleOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null)
        {
            return Results.Problem(detail: "Tenant not found.", statusCode: StatusCodes.Status404NotFound);
        }

        tenant.IsActive = active;
        await catalog.SaveChangesAsync(ct);
        await audit.RecordAsync(active ? AuditEvents.TenantUnlocked : AuditEvents.TenantLocked, tenantId: tenant.Id, actorId: ActorId(http));

        foreach (var domain in tenant.Domains)
        {
            await tenants.InvalidateAsync(domain.Domain, tenant.Id, ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> AddDomainAsync(
        Guid id,
        AddDomainRequest body,
        HttpContext http,
        CatalogDbContext catalog,
        TenantStore tenants,
        TenantClientSynchronizer clients,
        IOptions<TenancyOptions> tenancyOptions,
        AuditLog audit,
        CancellationToken ct)
    {
        var domain = (body.Domain ?? "").Trim().ToLowerInvariant();
        if (domain.Length is < 3 or > 253 || !TenantProvisioningService.DomainPattern().IsMatch(domain))
        {
            return Results.Problem(detail: "Domain must be a valid host name.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (tenancyOptions.Value.PlatformHosts.Any(h => string.Equals(h, domain, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Problem(detail: "This domain is reserved for the platform.", statusCode: StatusCodes.Status409Conflict);
        }

        if (!await catalog.Tenants.AnyAsync(t => t.Id == id, ct))
        {
            return Results.Problem(detail: "Tenant not found.", statusCode: StatusCodes.Status404NotFound);
        }

        if (await catalog.TenantDomains.AnyAsync(d => d.Domain == domain, ct))
        {
            return Results.Problem(detail: $"Domain '{domain}' is already assigned.", statusCode: StatusCodes.Status409Conflict);
        }

        catalog.TenantDomains.Add(new TenantDomain { Id = Guid.CreateVersion7(), TenantId = id, Domain = domain });
        try
        {
            await catalog.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Problem(detail: $"Domain '{domain}' is already assigned.", statusCode: StatusCodes.Status409Conflict);
        }

        await audit.RecordAsync(AuditEvents.TenantDomainAdded, tenantId: id, actorId: ActorId(http), data: new { domain });
        await tenants.InvalidateAsync(domain, id, ct);
        await clients.SynchronizeTenantAsync(id, ct);

        return Results.Created($"/admin/tenants/{id}/domains/{domain}", new { tenantId = id, domain });
    }

    private static async Task<IResult> RemoveDomainAsync(
        Guid id,
        string domain,
        HttpContext http,
        CatalogDbContext catalog,
        TenantStore tenants,
        TenantClientSynchronizer clients,
        AuditLog audit,
        CancellationToken ct)
    {
        domain = domain.Trim().ToLowerInvariant();
        await using var tx = await catalog.Database.BeginTransactionAsync(ct);
        var lockKey = $"tenant-domains:{id:D}";
        await catalog.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct);

        var domains = await catalog.TenantDomains.Where(d => d.TenantId == id).ToListAsync(ct);
        var target = domains.SingleOrDefault(d => d.Domain == domain);
        if (target is null)
        {
            return Results.Problem(detail: "Domain not found for this tenant.", statusCode: StatusCodes.Status404NotFound);
        }

        if (domains.Count <= 1)
        {
            return Results.Problem(detail: "A tenant must keep at least one domain.", statusCode: StatusCodes.Status409Conflict);
        }

        catalog.TenantDomains.Remove(target);
        await catalog.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await audit.RecordAsync(AuditEvents.TenantDomainRemoved, tenantId: id, actorId: ActorId(http), data: new { domain });
        await tenants.InvalidateAsync(domain, id, ct);
        await clients.SynchronizeTenantAsync(id, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ListUsersAsync(
        string? search, int? limit, int? offset, AuthDbContext db, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(u => u.Email != null && EF.Functions.ILike(u.Email, $"%{term}%"));
        }

        var users = await query
            .OrderBy(u => u.Email)
            .ThenBy(u => u.Id)
            .Skip(Math.Max(offset ?? 0, 0))
            .Take(Math.Clamp(limit ?? 100, 1, 500))
            .Select(u => new AdminUserDto(u.Id, u.Email, u.EmailConfirmed, u.IsDisabled, u.IsPlatformAdmin, u.ManagedByTenantId, u.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(users);
    }

    private static async Task<IResult> SetUserDisabledAsync(
        Guid id, bool disabled, HttpContext http, UserAccountService accounts, CancellationToken ct)
    {
        var actorId = ActorId(http);
        if (disabled && actorId == id)
        {
            return Results.Problem(detail: "An administrator cannot disable their own account.", statusCode: StatusCodes.Status409Conflict);
        }

        return await accounts.SetDisabledByIdAsync(id, disabled, actorId, ct)
            ? Results.NoContent()
            : Results.Problem(detail: "User not found.", statusCode: StatusCodes.Status404NotFound);
    }

    private static Guid? ActorId(HttpContext http)
    {
        var raw = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
