using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Shell.Abstractions.Security;
using Shell.Abstractions.Tenancy;
using Shell.Core;
using Shell.Core.Data;
using Shell.Core.Tenancy;
using Shell.Identity.Access;
using Shell.Identity.Audit;
using Shell.Identity.Clients;
using Shell.Identity.Data;

namespace Shell.Identity.Tenants;

public enum TenantProvisioningOutcome
{
    Ok,
    Invalid,
    Forbidden,
    Conflict,
}

public sealed record TenantProvisioningResult(
    TenantProvisioningOutcome Outcome,
    string? Detail = null,
    Guid? TenantId = null,
    string? Domain = null);

/// <summary>
/// Provisions Pool and Silo tenants: validates domain &amp; owner, creates catalog records, migrates Silo DBs,
/// registers the OpenIddict client, assigns or invites the initial <c>owner</c>, and writes an audit event.
/// </summary>
public sealed partial class TenantProvisioningService(
    CatalogDbContext catalog,
    AuthDbContext authDb,
    UserManager<User> users,
    MembershipService memberships,
    TenantAccessService access,
    TenantStore tenants,
    MigrationRunner migrations,
    TenantClientSynchronizer clients,
    IOpenIddictApplicationManager applications,
    AuditLog audit,
    TimeProvider time,
    IOptions<TenancyOptions> tenancyOptions,
    IOptions<ShellIdentityOptions> identityOptions)
{
    private static readonly HashSet<string> ReservedSubdomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth", "admin", "api", "www", "mail", "status", "health", "system", "platform", "billing", "support",
    };

    public async Task<TenantProvisioningResult> ProvisionAsync(
        string name,
        string domain,
        string ownerEmail,
        string? siloConnectionString,
        Guid? actorId,
        CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 200)
        {
            return new(TenantProvisioningOutcome.Invalid, "Tenant name must be between 1 and 200 characters.");
        }

        domain = domain.Trim().ToLowerInvariant();
        if (domain.Length is < 3 or > 253 || !DomainPattern().IsMatch(domain))
        {
            return new(TenantProvisioningOutcome.Invalid, "A valid domain name is required (e.g. acme.localhost).");
        }

        if (tenancyOptions.Value.PlatformHosts.Any(h => string.Equals(h, domain, StringComparison.OrdinalIgnoreCase)))
        {
            return new(TenantProvisioningOutcome.Conflict, "This domain is reserved for the platform.");
        }

        ownerEmail = ownerEmail.Trim();
        if (ownerEmail.Length is < 3 or > 256 || !ownerEmail.Contains('@', StringComparison.Ordinal))
        {
            return new(TenantProvisioningOutcome.Invalid, "A valid owner email address is required.");
        }

        var isSilo = !string.IsNullOrWhiteSpace(siloConnectionString);
        if (isSilo && !ConnectionStringsOptions.IsValid(siloConnectionString!))
        {
            return new(TenantProvisioningOutcome.Invalid, "The Silo database connection string is invalid.");
        }

        var existingOwner = await users.FindByEmailAsync(ownerEmail);
        if (existingOwner is { IsDisabled: true })
        {
            return new(TenantProvisioningOutcome.Forbidden, "The owner account is disabled.");
        }

        if (existingOwner is { ManagedByTenantId: not null })
        {
            return new(TenantProvisioningOutcome.Forbidden, "An account managed by an organization cannot own a new tenant.");
        }

        if (await catalog.TenantDomains.AnyAsync(d => d.Domain == domain, ct))
        {
            return new(TenantProvisioningOutcome.Conflict, $"Domain '{domain}' is already in use.");
        }

        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            IsActive = true,
            IsIsolatedDb = isSilo,
            ConnectionString = isSilo ? siloConnectionString : null,
            Domains = [new TenantDomain { Id = Guid.CreateVersion7(), Domain = domain }],
        };
        catalog.Tenants.Add(tenant);
        await catalog.SaveChangesAsync(ct);

        try
        {
            if (isSilo)
            {
                await migrations.MigrateTenantAsync(new TenantInfo(tenant.Id, domain, true, siloConnectionString, true), ct);
            }

            await tenants.InvalidateAsync(domain, tenant.Id, ct);
            await clients.SynchronizeTenantAsync(tenant.Id, ct);

            var now = time.GetUtcNow();
            if (existingOwner is not null)
            {
                authDb.Memberships.Add(new TenantMembership
                {
                    TenantId = tenant.Id,
                    UserId = existingOwner.Id,
                    Role = TenantRoles.Owner,
                    Status = MembershipStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                audit.Add(
                    AuditEvents.TenantCreated,
                    userId: existingOwner.Id,
                    tenantId: tenant.Id,
                    actorId: actorId ?? existingOwner.Id,
                    data: new { name, domain, isolated = isSilo });
                await authDb.SaveChangesAsync(ct);
                await access.InvalidateAsync(tenant.Id, existingOwner.Id, ct);
            }
            else
            {
                var (inviteResult, _) = await memberships.InviteAsync(
                    tenant.Id,
                    ownerEmail,
                    TenantRoles.Owner,
                    new IamActor(actorId ?? Guid.Empty, TenantRoles.Owner),
                    ct);
                if (inviteResult.Outcome != IamOutcome.Ok)
                {
                    await CompensateAsync(tenant.Id, domain);
                    return new(TenantProvisioningOutcome.Invalid, inviteResult.Detail);
                }

                await audit.RecordAsync(
                    AuditEvents.TenantCreated,
                    tenantId: tenant.Id,
                    actorId: actorId,
                    data: new { name, domain, isolated = isSilo, invitedOwner = users.NormalizeEmail(ownerEmail) });
            }
        }
        catch
        {
            await CompensateAsync(tenant.Id, domain);
            throw;
        }

        return new(TenantProvisioningOutcome.Ok, TenantId: tenant.Id, Domain: domain);
    }

    /// <summary>Self-service creation of a trial Pool tenant under <c>&lt;slug&gt;.&lt;SelfSignUpBaseDomain&gt;</c> by a confirmed personal account.</summary>
    public async Task<TenantProvisioningResult> CreateTrialAsync(User owner, string name, string subdomainSlug, CancellationToken ct)
    {
        if (owner.IsDisabled)
        {
            return new(TenantProvisioningOutcome.Forbidden, "This account is disabled.");
        }

        if (owner.ManagedByTenantId is not null)
        {
            return new(TenantProvisioningOutcome.Forbidden, "An account managed by an organization cannot create a workspace.");
        }

        if (!owner.EmailConfirmed || string.IsNullOrWhiteSpace(owner.Email))
        {
            return new(TenantProvisioningOutcome.Forbidden, "Please confirm your email address before creating a workspace.");
        }

        subdomainSlug = subdomainSlug.Trim().ToLowerInvariant();
        if (subdomainSlug.Length is < 2 or > 63 || !SlugPattern().IsMatch(subdomainSlug))
        {
            return new(TenantProvisioningOutcome.Invalid, "Subdomain must be 2–63 lowercase letters, digits, or hyphens, and cannot start or end with a hyphen.");
        }

        if (ReservedSubdomains.Contains(subdomainSlug))
        {
            return new(TenantProvisioningOutcome.Conflict, $"Subdomain '{subdomainSlug}' is reserved.");
        }

        await using var tx = await authDb.Database.BeginTransactionAsync(ct);
        var lockKey = $"trial:{owner.Id:D}";
        await authDb.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct);

        var maxTenants = identityOptions.Value.MaxTenantsPerUser;
        var ownedCount = await authDb.Memberships.CountAsync(
            m => m.UserId == owner.Id && m.Role == TenantRoles.Owner && m.Status == MembershipStatus.Active,
            ct);
        if (ownedCount >= maxTenants)
        {
            return new(TenantProvisioningOutcome.Forbidden, $"You can create at most {maxTenants} workspace(s).");
        }

        var baseDomain = identityOptions.Value.SelfSignUpBaseDomain.Trim().TrimStart('.').ToLowerInvariant();
        var domain = $"{subdomainSlug}.{baseDomain}";
        var result = await ProvisionAsync(name, domain, owner.Email, siloConnectionString: null, actorId: owner.Id, ct);
        if (result.Outcome == TenantProvisioningOutcome.Ok)
        {
            await tx.CommitAsync(ct);
        }

        return result;
    }

    private async Task CompensateAsync(Guid tenantId, string domain)
    {
        var app = await applications.FindByClientIdAsync(TenantClients.ClientIdFor(tenantId), CancellationToken.None);
        if (app is not null)
        {
            await applications.DeleteAsync(app, CancellationToken.None);
        }

        catalog.ChangeTracker.Clear();
        await catalog.TenantDomains.Where(d => d.TenantId == tenantId).ExecuteDeleteAsync(CancellationToken.None);
        await catalog.Tenants.Where(t => t.Id == tenantId).ExecuteDeleteAsync(CancellationToken.None);
        await tenants.InvalidateAsync(domain, tenantId, CancellationToken.None);
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    internal static partial Regex DomainPattern();

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex SlugPattern();
}
