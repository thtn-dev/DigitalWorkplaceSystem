using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    AuditLog audit,
    TimeProvider time,
    IOptions<TenancyOptions> tenancyOptions)
{
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

        if (isSilo)
        {
            await migrations.MigrateTenantAsync(new TenantInfo(tenant.Id, domain, true, siloConnectionString, true), ct);
        }

        tenants.Invalidate(domain, tenant.Id);
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
            access.Invalidate(tenant.Id, existingOwner.Id);
        }
        else
        {
            audit.Add(
                AuditEvents.TenantCreated,
                tenantId: tenant.Id,
                actorId: actorId,
                data: new { name, domain, isolated = isSilo, invitedOwner = users.NormalizeEmail(ownerEmail) });
            await authDb.SaveChangesAsync(ct);

            var (inviteResult, _) = await memberships.InviteAsync(
                tenant.Id,
                ownerEmail,
                TenantRoles.Owner,
                new IamActor(actorId ?? Guid.Empty, TenantRoles.Owner),
                ct);
            if (inviteResult.Outcome != IamOutcome.Ok)
            {
                return new(TenantProvisioningOutcome.Invalid, inviteResult.Detail);
            }
        }

        return new(TenantProvisioningOutcome.Ok, TenantId: tenant.Id, Domain: domain);
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex DomainPattern();
}
