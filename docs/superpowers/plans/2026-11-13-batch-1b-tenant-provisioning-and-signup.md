# Batch 1B (Tenant Provisioning & Public Self Sign-up — `T1`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver `T1` (Batch 1B): single-tenant Silo database migration (`MigrationRunner.MigrateTenantAsync`), single-tenant OpenIddict client synchronization (`TenantClientSynchronizer.SynchronizeTenantAsync`), `TenantStore` cache invalidation, `TenantProvisioningService` + CLI `create-tenant` command (supporting Pool and Silo tenants), user email confirmation (`/account/confirm-email`), and public trial Pool tenant self sign-up (`/tenants/new`).

**Architecture:**
- **Incremental single-tenant primitives (`T1` Core):** `MigrationRunner` exposes `MigrateTenantAsync(TenantInfo, CancellationToken)` so provisioning a Silo tenant migrates only that tenant's isolated DB without scanning all tenants. `TenantClientSynchronizer` exposes `SynchronizeTenantAsync(Guid, CancellationToken)` so OAuth clients (`tenant-{id}`) are registered on the fly. `TenantStore` exposes `Invalidate(string domain, Guid? tenantId = null)` so newly provisioned domains immediately evict negative-cache entries.
- **Unified Provisioning Service & CLI (`T1` Provisioning):** `TenantProvisioningService` in `Shell.Identity` validates tenant names, domains/subdomains, Silo connection strings, and owner accounts; creates `Tenant` + `TenantDomain` in `CatalogDbContext`; runs `MigrateTenantAsync` for Silo tenants; synchronizes the OpenIddict client; grants `owner` membership if the owner user already exists or sends an `owner` invitation email via `MembershipService.InviteAsync` if not; and records `tenant.created` in `identity.audit_events`. `Program.cs` and `IdentityCommands` expose `create-tenant <name> <domain> <owner-email> [--silo <connection-string>]`.
- **Email Confirmation & Public Self Sign-up (`T1` Self-Service):** Registration (`/account/register`) sends a confirmation link to `/account/confirm-email?userId=...&token=...` via `IAccountEmailSender.SendEmailConfirmationAsync`. Confirmed personal users can visit `/tenants/new` on the auth host (guarded by `AuthHostGuardMiddleware`) to provision a trial Pool tenant under `<slug>.<SelfSignUpBaseDomain>` (enforcing `EmailConfirmed`, `MaxTenantsPerUser`, slug format, and a reserved-subdomain blocklist).

**Tech Stack:** .NET 10 (`net10.0`), ASP.NET Core Minimal APIs & Razor Pages, ASP.NET Core Identity, OpenIddict, EF Core 10 + Npgsql, xUnit v3, Testcontainers for PostgreSQL.

---

## File Structure

### Created Files
- `src/Shell.Identity/Tenants/TenantProvisioningService.cs` — Core tenant provisioning service (`ProvisionAsync` for CLI/Admin Pool & Silo creation, `CreateTrialAsync` for self-service Pool trial creation with slug validation, reserved subdomain blocklist, and per-user quota).
- `src/Shell.Identity/Pages/Account/ConfirmEmail.cshtml` — Razor view for `/account/confirm-email`.
- `src/Shell.Identity/Pages/Account/ConfirmEmail.cshtml.cs` — PageModel verifying email confirmation tokens and recording `AuditEvents.EmailConfirmed`.
- `src/Shell.Identity/Pages/Tenants/New.cshtml` — Razor view for `/tenants/new` (trial Pool workspace creation on the auth host).
- `src/Shell.Identity/Pages/Tenants/New.cshtml.cs` — PageModel for `/tenants/new` requiring an authenticated user with confirmed email.

### Modified Files
- `src/Shell.Core/ShellOptions.cs` — Change `ConnectionStringsOptions.IsValid` visibility from `internal` to `public` so `TenantProvisioningService` can validate Silo connection strings.
- `src/Shell.Core/Data/MigrationRunner.cs` — Add `MigrateTenantAsync(TenantInfo tenant, CancellationToken cancellationToken)`.
- `src/Shell.Core/Tenancy/TenantStore.cs` — Add `Invalidate(string domain, Guid? tenantId = null)`.
- `src/Shell.Identity/Clients/TenantClients.cs` — Add `SynchronizeTenantAsync(Guid tenantId, CancellationToken cancellationToken)` and share `UpsertClientAsync` with `SynchronizeAsync`.
- `src/Shell.Identity/Audit/AuditLog.cs` — Add `EmailConfirmed = "user.email_confirmed"` and `TenantCreated = "tenant.created"`.
- `src/Shell.Identity/Email/AccountEmails.cs` — Add `SendEmailConfirmationAsync(string email, string link)` to `IAccountEmailSender`, `LoggingAccountEmailSender`, and `SmtpAccountEmailSender`; add `ConfirmEmail(Guid userId, string token)` to `AccountLinks`.
- `src/Shell.Identity/ShellIdentityOptions.cs` — Add `SelfSignUpBaseDomain` (default `"localhost"`) and `MaxTenantsPerUser` (default `3`).
- `src/Shell.Identity/ShellIdentityExtensions.cs` — Validate `SelfSignUpBaseDomain` at startup and register `TenantProvisioningService`.
- `src/Shell.Identity/Access/TenantAccessMiddleware.cs` — Add `"/tenants"` to `AuthHostGuardMiddleware.AuthPaths`.
- `src/Shell.Identity/IdentityCommands.cs` — Add `create-tenant <name> <domain> <owner-email> [--silo <connection-string>]` command handling.
- `src/Host/Program.cs` — Route `create-tenant` CLI arguments to `IdentityCommands.RunAsync`.
- `src/Shell.Identity/Pages/Account/Register.cshtml.cs` — Generate and send email confirmation link on registration.
- `src/Shell.Identity/Pages/Index.cshtml` & `src/Shell.Identity/Pages/Index.cshtml.cs` — Display email confirmation status, resend confirmation button, and link to `/tenants/new`.
- `tests/Shell.Testing/PostgresFixture.cs` — Update `CreateTenantAsync` to use `SynchronizeTenantAsync` and `MigrateTenantAsync` (when `isIsolatedDb` is true).
- `tests/Shell.Testing/TestDoubles.cs` — Implement `SendEmailConfirmationAsync` on `TestEmailSender`.
- `tests/Shell.IntegrationTests/TenancyTests.cs` — Add integration tests for single-tenant Silo migration/sync, CLI `create-tenant`, and `/tenants/new` self sign-up.
- `tests/Shell.IntegrationTests/AccountSecurityTests.cs` — Add integration tests for registration email confirmation, token verification, and resend confirmation.
- `docs/README.md`, `docs/extension-points.md`, `docs/core-gap-analysis.md` — Update documentation for `create-tenant`, email confirmation, `/tenants/new`, and Batch 1B completion status.

---

## Task 1: Single-Tenant Silo Migration, Single-Tenant OAuth Client Sync & `TenantStore` Cache Eviction (`T1` Core)

**Files:**
- Modify: `src/Shell.Core/Data/MigrationRunner.cs`
- Modify: `src/Shell.Core/Tenancy/TenantStore.cs`
- Modify: `src/Shell.Identity/Clients/TenantClients.cs`
- Modify: `tests/Shell.Testing/PostgresFixture.cs`
- Modify: `tests/Shell.IntegrationTests/TenancyTests.cs`

- [ ] **Step 1: Write the failing test in `tests/Shell.IntegrationTests/TenancyTests.cs`**

Add the following test to `tests/Shell.IntegrationTests/TenancyTests.cs` right after `AddMemberAsync_throws_when_user_email_does_not_exist`:

```csharp
    [Fact]
    public async Task Single_tenant_provisioning_helpers_evict_negative_cache_and_migrate_silo_database_on_the_fly()
    {
        await using var host = db.CreateHost();
        var client = host.CreateClient();
        var domain = $"silo-{Guid.NewGuid():N}.localhost";
        var siloCs = db.ForDatabase($"shell_silo_{Guid.NewGuid():N}");

        // Prime negative cache in TenantStore with a 404 before the tenant exists.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, HttpMethod.Get, "/api/products", domain)).StatusCode);

        var (tenantId, _) = await db.CreateTenantAsync(host, domain: domain, isIsolatedDb: true, connectionString: siloCs);
        await db.AddMemberAsync(tenantId, DemoUserSeeder.Alice, role: "owner");

        var tokens = await new OidcTestClient(host).GetTokensAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password, tenantId, domain);
        var productName = $"silo-fly-{Guid.NewGuid()}";
        await CreateProductAsync(client, domain, productName, tokens.AccessToken);

        Assert.Equal(1, await db.CountAsync(siloCs, $"SELECT count(*) FROM products.products WHERE name = '{productName}'"));
        Assert.Equal(0, await db.CountAsync(db.SharedConnectionString, $"SELECT count(*) FROM products.products WHERE name = '{productName}'"));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```powershell
dotnet test --solution DigitalWorkspace.slnx -c Release --filter "FullyQualifiedName~Single_tenant_provisioning_helpers_evict_negative_cache"
```
Expected: FAIL (the negative cache still returns `404 NotFound` for `domain`, and the Silo DB `siloCs` has not been created or migrated by `CreateTenantAsync`).

- [ ] **Step 3: Add `MigrateTenantAsync` to `src/Shell.Core/Data/MigrationRunner.cs`, `Invalidate` to `src/Shell.Core/Tenancy/TenantStore.cs`, and `SynchronizeTenantAsync` to `src/Shell.Identity/Clients/TenantClients.cs`**

In `src/Shell.Core/Data/MigrationRunner.cs`, add `MigrateTenantAsync` right after `RunAsync`:

```csharp
    /// <summary>Creates and migrates the isolated database of a single Silo tenant (no-op for Pool tenants).</summary>
    public async Task MigrateTenantAsync(TenantInfo tenant, CancellationToken cancellationToken)
    {
        if (!tenant.IsIsolatedDb || string.IsNullOrWhiteSpace(tenant.ConnectionString))
        {
            return;
        }

        await EnsureDatabaseExistsAsync(tenant.ConnectionString, cancellationToken);
        await MigrateModulesAsync(tenant, $"silo database of tenant {tenant.Identifier} ({tenant.Id})", cancellationToken);
    }
```

In `src/Shell.Core/Tenancy/TenantStore.cs`, add `Invalidate` right after `FindByIdAsync`:

```csharp
    /// <summary>Evicts cached lookup entries for a domain and (optionally) tenant id after provisioning or updates.</summary>
    public void Invalidate(string domain, Guid? tenantId = null)
    {
        cache.Remove($"tenant:{domain}");
        if (tenantId is { } id)
        {
            cache.Remove($"tenant-id:{id}");
        }
    }
```

Update `src/Shell.Identity/Clients/TenantClients.cs` so `TenantClientSynchronizer` supports both `SynchronizeAsync` (all tenants) and `SynchronizeTenantAsync` (a single tenant):

```csharp
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Shell.Core.Tenancy;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Shell.Identity.Clients;

/// <summary>Naming convention linking a tenant to its OpenIddict application.</summary>
public static class TenantClients
{
    public const string Prefix = "tenant-";
    public const string ApiScope = "api";
    public const string ApiAudience = "shell-api";

    public static string ClientId(Guid tenantId) => $"{Prefix}{tenantId:D}";

    public static Guid? TenantIdOf(string? clientId) =>
        clientId is not null && clientId.StartsWith(Prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(clientId.AsSpan(Prefix.Length), "D", out var id)
            ? id
            : null;
}

/// <summary>
/// Registers the <c>api</c> scope and one public PKCE client per tenant (<c>tenant-&lt;id&gt;</c>), with redirect URIs built from the
/// tenant's domains in the catalog. Idempotent: safe to run on every <c>migrate</c> or when provisioning a single tenant.
/// </summary>
public sealed class TenantClientSynchronizer(
    CatalogDbContext catalog,
    IOpenIddictApplicationManager applications,
    IOpenIddictScopeManager scopes,
    IOptions<ShellIdentityOptions> options,
    ILogger<TenantClientSynchronizer> logger)
{
    public async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        await EnsureApiScopeAsync(cancellationToken);

        var tenants = await catalog.Tenants
            .AsNoTracking()
            .Include(t => t.Domains)
            .ToListAsync(cancellationToken);

        foreach (var tenant in tenants)
        {
            await UpsertClientAsync(tenant, cancellationToken);
        }
    }

    /// <summary>Registers or updates the OpenIddict client of a single tenant.</summary>
    public async Task SynchronizeTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await catalog.Tenants
            .AsNoTracking()
            .Include(t => t.Domains)
            .SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' was not found in catalog.");

        await EnsureApiScopeAsync(cancellationToken);
        await UpsertClientAsync(tenant, cancellationToken);
    }

    private async Task EnsureApiScopeAsync(CancellationToken cancellationToken)
    {
        if (await scopes.FindByNameAsync(TenantClients.ApiScope, cancellationToken) is null)
        {
            await scopes.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = TenantClients.ApiScope,
                DisplayName = "Workspace API",
                Resources = { TenantClients.ApiAudience },
            }, cancellationToken);
        }
    }

    private async Task UpsertClientAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        var descriptor = Descriptor(tenant.Id, tenant.Name, tenant.Domains.Select(d => d.Domain));
        var existing = await applications.FindByClientIdAsync(descriptor.ClientId!, cancellationToken);
        if (existing is null)
        {
            await applications.CreateAsync(descriptor, cancellationToken);
            logger.LogInformation("Registered OAuth client {ClientId} for tenant {TenantName}", descriptor.ClientId, tenant.Name);
        }
        else
        {
            await applications.UpdateAsync(existing, descriptor, cancellationToken);
        }
    }

    private OpenIddictApplicationDescriptor Descriptor(Guid tenantId, string name, IEnumerable<string> domains)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = TenantClients.ClientId(tenantId),
            DisplayName = name,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.Profile,
                Permissions.Prefixes.Scope + Scopes.Email,
                Permissions.Prefixes.Scope + TenantClients.ApiScope,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        foreach (var domain in domains)
        {
            foreach (var template in options.Value.Clients.RedirectUris)
            {
                descriptor.RedirectUris.Add(Expand(template, domain));
            }

            foreach (var template in options.Value.Clients.PostLogoutRedirectUris)
            {
                descriptor.PostLogoutRedirectUris.Add(Expand(template, domain));
            }
        }

        return descriptor;
    }

    private static Uri Expand(string template, string domain) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{template.Replace("{domain}", domain, StringComparison.Ordinal)}"));
}
```

In `tests/Shell.Testing/PostgresFixture.cs`, add `using Shell.Abstractions.Tenancy;` and `using Shell.Core.Tenancy;`, and update `CreateTenantAsync` (lines 84-87) to:

```csharp
        await using var scope = host.Services.CreateAsyncScope();
        if (isIsolatedDb && !string.IsNullOrWhiteSpace(connectionString))
        {
            await scope.ServiceProvider.GetRequiredService<MigrationRunner>()
                .MigrateTenantAsync(new TenantInfo(tenantId, domain, true, connectionString, isActive), CancellationToken.None);
        }

        scope.ServiceProvider.GetRequiredService<TenantStore>().Invalidate(domain, tenantId);
        await scope.ServiceProvider.GetRequiredService<TenantClientSynchronizer>()
            .SynchronizeTenantAsync(tenantId, CancellationToken.None);
        return (tenantId, domain);
```

- [ ] **Step 4: Run test suite and verify formatting**

Run:
```powershell
dotnet format DigitalWorkspace.slnx --verify-no-changes --no-restore
dotnet build DigitalWorkspace.slnx -c Release
dotnet test --solution DigitalWorkspace.slnx -c Release --no-build
```
Expected: `0 Warning(s)`, `0 Error(s)`, all 55 tests pass.

- [ ] **Step 5: Commit Task 1**

Run:
```powershell
git add src/Shell.Core/Data/MigrationRunner.cs src/Shell.Core/Tenancy/TenantStore.cs src/Shell.Identity/Clients/TenantClients.cs tests/Shell.Testing/PostgresFixture.cs tests/Shell.IntegrationTests/TenancyTests.cs
git commit -m "feat(tenancy): add single-tenant Silo migration, OAuth sync, and cache eviction"
```

---

## Task 2: `TenantProvisioningService` & CLI `create-tenant` Command (`T1` Provisioning & Silo Support)

**Files:**
- Modify: `src/Shell.Core/ShellOptions.cs`
- Modify: `src/Shell.Identity/Audit/AuditLog.cs`
- Create: `src/Shell.Identity/Tenants/TenantProvisioningService.cs`
- Modify: `src/Shell.Identity/ShellIdentityExtensions.cs`
- Modify: `src/Shell.Identity/IdentityCommands.cs`
- Modify: `src/Host/Program.cs`
- Modify: `tests/Shell.IntegrationTests/TenancyTests.cs`

- [ ] **Step 1: Write the failing CLI provisioning tests in `tests/Shell.IntegrationTests/TenancyTests.cs`**

Add `using Shell.Identity;` to the top of `tests/Shell.IntegrationTests/TenancyTests.cs` and add the following tests:

```csharp
    [Fact]
    public async Task Create_tenant_command_provisions_pool_tenant_for_existing_owner_and_silo_tenant_for_invited_owner()
    {
        await using var host = db.CreateHost();
        var client = host.CreateClient();

        // 1. Pool tenant with existing user (Bob) -> Bob immediately gets active Owner membership.
        var poolDomain = $"cli-pool-{Guid.NewGuid():N}.localhost";
        var exitPool = await IdentityCommands.RunAsync(
            host.Services,
            ["create-tenant", "CLI Pool Co", poolDomain, DemoUserSeeder.Bob],
            Ct);
        Assert.Equal(0, exitPool);

        var poolTenantId = await db.TenantIdAsync(poolDomain);
        var bobTokens = await new OidcTestClient(host).GetTokensAsync(DemoUserSeeder.Bob, DemoUserSeeder.Password, poolTenantId, poolDomain);
        var createdPoolProduct = await CreateProductAsync(client, poolDomain, $"pool-{Guid.NewGuid()}", bobTokens.AccessToken);
        Assert.NotEqual(Guid.Empty, createdPoolProduct.Id);
        Assert.Equal(1, await db.CountAsync(db.CatalogConnectionString,
            $"SELECT count(*) FROM identity.audit_events WHERE tenant_id = '{poolTenantId}' AND type = 'tenant.created'"));

        // 2. Silo tenant with a brand-new owner email -> migrates Silo DB and sends owner invitation link.
        var siloDomain = $"cli-silo-{Guid.NewGuid():N}.localhost";
        var siloCs = db.ForDatabase($"shell_cli_silo_{Guid.NewGuid():N}");
        var newOwnerEmail = $"founder-{Guid.NewGuid():N}@test.local";
        var exitSilo = await IdentityCommands.RunAsync(
            host.Services,
            ["create-tenant", "CLI Silo Inc", siloDomain, newOwnerEmail, "--silo", siloCs],
            Ct);
        Assert.Equal(0, exitSilo);

        var inviteLink = db.Emails.LastLinkTo(newOwnerEmail);
        var founder = new OidcTestClient(host);
        await founder.RegisterAsync(newOwnerEmail, DemoUserSeeder.Password);
        var acceptHtml = await (await founder.PostFormAsync(inviteLink, new(), tokenPage: "/account/login")).Content.ReadAsStringAsync(Ct);
        Assert.Contains("id=\"accepted\"", acceptHtml);

        var siloTenantId = await db.TenantIdAsync(siloDomain);
        var founderTokens = await founder.GetTokensAsync(newOwnerEmail, DemoUserSeeder.Password, siloTenantId, siloDomain);
        var siloProductName = $"silo-cli-{Guid.NewGuid()}";
        await CreateProductAsync(client, siloDomain, siloProductName, founderTokens.AccessToken);
        Assert.Equal(1, await db.CountAsync(siloCs, $"SELECT count(*) FROM products.products WHERE name = '{siloProductName}'"));

        // 3. Duplicate domain or platform host is rejected with exit code 1.
        Assert.Equal(1, await IdentityCommands.RunAsync(host.Services, ["create-tenant", "Dup", poolDomain, DemoUserSeeder.Bob], Ct));
        Assert.Equal(1, await IdentityCommands.RunAsync(host.Services, ["create-tenant", "AuthHost", ShellHostFactory.AuthHost, DemoUserSeeder.Bob], Ct));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```powershell
dotnet test --solution DigitalWorkspace.slnx -c Release --filter "FullyQualifiedName~Create_tenant_command_provisions"
```
Expected: FAIL (compile error: `IdentityCommands.RunAsync` does not accept `IReadOnlyList<string>` / `string[]` yet).

- [ ] **Step 3: Implement `TenantProvisioningService` and wire `create-tenant` in `IdentityCommands` and `Program.cs`**

In `src/Shell.Core/ShellOptions.cs` (line 35), make `ConnectionStringsOptions.IsValid` public:

```csharp
    public static bool IsValid(string value)
```

In `src/Shell.Identity/Audit/AuditLog.cs`, add `EmailConfirmed` and `TenantCreated` constants to `AuditEvents`:

```csharp
    public const string EmailConfirmed = "user.email_confirmed";
    public const string TenantCreated = "tenant.created";
```

Create `src/Shell.Identity/Tenants/TenantProvisioningService.cs`:

```csharp
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
```

In `src/Shell.Identity/ShellIdentityExtensions.cs`, add `using Shell.Identity.Tenants;` and register `TenantProvisioningService` right after `services.AddScoped<UserAccountService>();`:

```csharp
        services.AddScoped<TenantProvisioningService>();
```

Update `src/Shell.Identity/IdentityCommands.cs` to support both `disable-user`/`enable-user` and `create-tenant`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shell.Identity.Access;
using Shell.Identity.Tenants;

namespace Shell.Identity;

/// <summary>CLI commands for account and tenant administration (<c>disable-user</c>, <c>enable-user</c>, <c>create-tenant</c>).</summary>
public static class IdentityCommands
{
    public static Task<int> RunAsync(IServiceProvider services, string command, string email, CancellationToken ct) =>
        RunAsync(services, [command, email], ct);

    public static async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> args, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("IdentityCommands");

        if (args is [var cmd and ("disable-user" or "enable-user"), var email, ..])
        {
            var accounts = scope.ServiceProvider.GetRequiredService<UserAccountService>();
            var disable = cmd == "disable-user";
            if (!await accounts.SetDisabledAsync(email, disable, ct))
            {
                logger.LogError("Account {Email} not found", email);
                return 1;
            }

            logger.LogInformation("Account {Email} is now {State}", email, disable ? "disabled" : "enabled");
            return 0;
        }

        if (args is ["create-tenant", var name, var domain, var ownerEmail, .. var rest])
        {
            string? siloConnectionString = null;
            if (rest is ["--silo", var cs])
            {
                siloConnectionString = cs;
            }
            else if (rest.Count > 0)
            {
                logger.LogError("Usage: create-tenant <name> <domain> <owner-email> [--silo <connection-string>]");
                return 1;
            }

            var provisioning = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
            var result = await provisioning.ProvisionAsync(name, domain, ownerEmail, siloConnectionString, actorId: null, ct);
            if (result.Outcome != TenantProvisioningOutcome.Ok)
            {
                logger.LogError("Cannot create tenant {Domain}: {Detail}", domain, result.Detail);
                return 1;
            }

            logger.LogInformation("Created tenant {TenantId} ({Domain}) for owner {OwnerEmail}", result.TenantId, result.Domain, ownerEmail);
            return 0;
        }

        logger.LogError("Unknown or incomplete command.");
        return 1;
    }
}
```

In `src/Host/Program.cs`, update the CLI argument handling block (lines 62-70) to:

```csharp
if (args is ["migrate", ..])
{
    await app.Services.GetRequiredService<MigrationRunner>().RunAsync(CancellationToken.None);
    return 0;
}

if (args is [("disable-user" or "enable-user" or "create-tenant"), ..])
{
    return await IdentityCommands.RunAsync(app.Services, args, CancellationToken.None);
}
```

- [ ] **Step 4: Run test suite and verify formatting**

Run:
```powershell
dotnet format DigitalWorkspace.slnx --verify-no-changes --no-restore
dotnet build DigitalWorkspace.slnx -c Release
dotnet test --solution DigitalWorkspace.slnx -c Release --no-build
```
Expected: `0 Warning(s)`, `0 Error(s)`, all 56 tests pass.

- [ ] **Step 5: Commit Task 2**

Run:
```powershell
git add src/Shell.Core/ShellOptions.cs src/Shell.Identity/Audit/AuditLog.cs src/Shell.Identity/Tenants/TenantProvisioningService.cs src/Shell.Identity/ShellIdentityExtensions.cs src/Shell.Identity/IdentityCommands.cs src/Host/Program.cs tests/Shell.IntegrationTests/TenancyTests.cs
git commit -m "feat(tenancy): add TenantProvisioningService and create-tenant CLI command"
```

---

## Task 3: Account Email Confirmation Flow (`T1` Anti-spam Prerequisite)

**Files:**
- Modify: `src/Shell.Identity/Email/AccountEmails.cs`
- Modify: `src/Shell.Identity/Pages/Account/Register.cshtml.cs`
- Create: `src/Shell.Identity/Pages/Account/ConfirmEmail.cshtml`
- Create: `src/Shell.Identity/Pages/Account/ConfirmEmail.cshtml.cs`
- Modify: `src/Shell.Identity/Pages/Index.cshtml`
- Modify: `src/Shell.Identity/Pages/Index.cshtml.cs`
- Modify: `tests/Shell.Testing/TestDoubles.cs`
- Modify: `tests/Shell.IntegrationTests/AccountSecurityTests.cs`

- [ ] **Step 1: Write the failing email confirmation tests in `tests/Shell.IntegrationTests/AccountSecurityTests.cs`**

Add the following test to `tests/Shell.IntegrationTests/AccountSecurityTests.cs`:

```csharp
    [Fact]
    public async Task Registration_sends_confirmation_link_that_verifies_email_and_rejects_forged_token()
    {
        await using var host = db.CreateHost();
        var email = IamTests.NewEmail("confirm");
        var browser = new OidcTestClient(host);
        await browser.RegisterAsync(email, Password);

        Assert.Equal("false", await db.ScalarAsync($"SELECT email_confirmed::text FROM identity.users WHERE email = '{email}'"));
        var link = db.Emails.LastLinkTo(email);
        Assert.StartsWith("/account/confirm-email?", link, StringComparison.Ordinal);

        // Forged token is rejected.
        var forged = Regex.Replace(link, "token=[^&]+", "token=forged");
        var forgedHtml = await browser.GetPageAsync(forged);
        Assert.Contains("invalid or expired", forgedHtml);
        Assert.Equal("false", await db.ScalarAsync($"SELECT email_confirmed::text FROM identity.users WHERE email = '{email}'"));

        // Resend confirmation link from home page works and issues a valid link.
        await browser.PostFormAsync("/?handler=ResendConfirmation", new(), tokenPage: "/");
        link = db.Emails.LastLinkTo(email);

        var confirmedHtml = await browser.GetPageAsync(link);
        Assert.Contains("id=\"email-confirmed\"", confirmedHtml);
        Assert.Equal("true", await db.ScalarAsync($"SELECT email_confirmed::text FROM identity.users WHERE email = '{email}'"));

        var userId = await db.ScalarAsync($"SELECT id::text FROM identity.users WHERE email = '{email}'");
        Assert.Equal(1, await db.CountAsync(db.CatalogConnectionString,
            $"SELECT count(*) FROM identity.audit_events WHERE user_id = '{userId}' AND type = 'user.email_confirmed'"));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```powershell
dotnet test --solution DigitalWorkspace.slnx -c Release --filter "FullyQualifiedName~Registration_sends_confirmation_link"
```
Expected: FAIL (`No email sent to confirm-...` because `RegisterModel` does not send a confirmation email yet).

- [ ] **Step 3: Add `SendEmailConfirmationAsync`, `ConfirmEmail` page, registration email trigger, and resend handler**

Update `src/Shell.Identity/Email/AccountEmails.cs`:
1. Add `Task SendEmailConfirmationAsync(string email, string link);` to `IAccountEmailSender`.
2. Add `SendEmailConfirmationAsync` to `LoggingAccountEmailSender`:
```csharp
    public Task SendEmailConfirmationAsync(string email, string link)
    {
        logger.LogInformation("Email confirmation for {Email}: {Link}", email, link);
        return Task.CompletedTask;
    }
```
3. Add `SendEmailConfirmationAsync` to `SmtpAccountEmailSender`:
```csharp
    public Task SendEmailConfirmationAsync(string email, string link)
    {
        var encodedLink = HtmlEncoder.Default.Encode(link);
        return SendAsync(
            email,
            subject: "Confirm your email address",
            plainTextBody: $"Confirm your email address by opening this link:\n{link}",
            htmlBody: $"<p>Confirm your email address by opening <a href=\"{encodedLink}\">this link</a>.</p>");
    }
```
4. Add `ConfirmEmail` to `AccountLinks`:
```csharp
    public string ConfirmEmail(Guid userId, string token) =>
        Build($"account/confirm-email?userId={userId}&token={Uri.EscapeDataString(token)}");
```

In `tests/Shell.Testing/TestDoubles.cs`, add `SendEmailConfirmationAsync` to `TestEmailSender`:

```csharp
    public Task SendEmailConfirmationAsync(string email, string link) => Keep(email, link);
```

Update `src/Shell.Identity/Pages/Account/Register.cshtml.cs` to inject `IAccountEmailSender emails, AccountLinks links` and send the confirmation email right after `users.CreateAsync`:

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shell.Identity.Audit;
using Shell.Identity.Data;
using Shell.Identity.Email;

namespace Shell.Identity.Pages.Account;

/// <summary>Self-registration of a personal account. Grants no tenant membership: the user creates a tenant or accepts an invitation.</summary>
public sealed class RegisterModel(
    UserManager<User> users,
    SignInManager<User> signIn,
    IAccountEmailSender emails,
    AccountLinks links,
    AuditLog audit,
    TimeProvider time) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public async Task<IActionResult> OnPostAsync()
    {
        var returnUrl = Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
        var email = Email.Trim();
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            CreatedAt = time.GetUtcNow(),
        };

        var result = await users.CreateAsync(user, Password);
        if (!result.Succeeded)
        {
            Errors = [.. result.Errors.Select(e => e.Description)];
            return Page();
        }

        await audit.RecordAsync(AuditEvents.UserRegistered, user.Id, data: new { method = "pwd" });
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        await emails.SendEmailConfirmationAsync(email, links.ConfirmEmail(user.Id, token));

        await signIn.SignInAsync(user, isPersistent: false);
        return LocalRedirect(returnUrl);
    }
}
```

Create `src/Shell.Identity/Pages/Account/ConfirmEmail.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shell.Identity.Audit;
using Shell.Identity.Data;

namespace Shell.Identity.Pages.Account;

public sealed class ConfirmEmailModel(UserManager<User> users, AuditLog audit) : PageModel
{
    public bool Confirmed { get; private set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid userId, string? token)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(token))
        {
            Error = "This confirmation link is invalid or expired.";
            return Page();
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            Error = "This confirmation link is invalid or expired.";
            return Page();
        }

        var result = await users.ConfirmEmailAsync(user, token);
        if (!result.Succeeded)
        {
            Error = "This confirmation link is invalid or expired.";
            return Page();
        }

        await audit.RecordAsync(AuditEvents.EmailConfirmed, user.Id);
        Confirmed = true;
        return Page();
    }
}
```

Create `src/Shell.Identity/Pages/Account/ConfirmEmail.cshtml`:

```cshtml
@page "/account/confirm-email"
@model Shell.Identity.Pages.Account.ConfirmEmailModel
@{
    ViewData["Title"] = "Confirm email";
}
<h1>Confirm email</h1>

@if (Model.Error is not null)
{
    <div class="error" role="alert">@Model.Error</div>
    <p class="links"><a href="/">Back to account</a></p>
}
else if (Model.Confirmed)
{
    <div id="email-confirmed" class="ok">Your email address has been confirmed.</div>
    <p class="links">
        <a href="/tenants/new">Create a workspace</a> ·
        <a href="/">Account</a>
    </p>
}
```

Update `src/Shell.Identity/Pages/Index.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shell.Identity.Data;
using Shell.Identity.Email;

namespace Shell.Identity.Pages;

public sealed class IndexModel(UserManager<User> users, IAccountEmailSender emails, AccountLinks links) : PageModel
{
    public string? Email { get; private set; }

    public bool EmailConfirmed { get; private set; }

    public bool TwoFactorEnabled { get; private set; }

    public string? StatusMessage { get; private set; }

    public async Task OnGetAsync(string? status = null)
    {
        StatusMessage = status;
        if (User.Identity?.IsAuthenticated == true && await users.GetUserAsync(User) is { } account)
        {
            Email = account.Email;
            EmailConfirmed = account.EmailConfirmed;
            TwoFactorEnabled = account.TwoFactorEnabled;
        }
    }

    public async Task<IActionResult> OnPostResendConfirmationAsync()
    {
        if (User.Identity?.IsAuthenticated != true || await users.GetUserAsync(User) is not { } account)
        {
            return RedirectToPage("/Account/Login");
        }

        if (!account.EmailConfirmed && !string.IsNullOrWhiteSpace(account.Email))
        {
            var token = await users.GenerateEmailConfirmationTokenAsync(account);
            await emails.SendEmailConfirmationAsync(account.Email, links.ConfirmEmail(account.Id, token));
        }

        return RedirectToPage(new { status = "Confirmation email sent." });
    }
}
```

Update `src/Shell.Identity/Pages/Index.cshtml`:

```cshtml
@page "/"
@model Shell.Identity.Pages.IndexModel
@{
    ViewData["Title"] = "Account";
}
<h1>Account</h1>
@if (Model.StatusMessage is not null)
{
    <div class="ok">@Model.StatusMessage</div>
}
@if (Model.Email is not null)
{
    <p class="muted">Signed in as <strong>@Model.Email</strong></p>
    @if (!Model.EmailConfirmed)
    {
        <p class="muted">Email not confirmed yet.</p>
        <form method="post" asp-page-handler="ResendConfirmation">
            <button type="submit" class="secondary">Resend confirmation email</button>
        </form>
    }
    <p class="links">
        <a href="/tenants/new">Create a workspace</a> ·
        <a href="/account/mfa">Two-factor authentication @(Model.TwoFactorEnabled ? "(on)" : "(off)")</a> ·
        <a href="/account/logins">Sign-in methods</a>
    </p>
    <form method="post" action="/account/logout">
        @Html.AntiForgeryToken()
        <button type="submit" class="secondary">Sign out</button>
    </form>
}
else
{
    <p class="links"><a href="/account/login">Sign in</a> · <a href="/account/register">Create account</a></p>
}
```

- [ ] **Step 4: Run test suite and verify formatting**

Run:
```powershell
dotnet format DigitalWorkspace.slnx --verify-no-changes --no-restore
dotnet build DigitalWorkspace.slnx -c Release
dotnet test --solution DigitalWorkspace.slnx -c Release --no-build
```
Expected: `0 Warning(s)`, `0 Error(s)`, all 57 tests pass.

- [ ] **Step 5: Commit Task 3**

Run:
```powershell
git add src/Shell.Identity/Email/AccountEmails.cs src/Shell.Identity/Pages/Account/Register.cshtml.cs src/Shell.Identity/Pages/Account/ConfirmEmail.cshtml src/Shell.Identity/Pages/Account/ConfirmEmail.cshtml.cs src/Shell.Identity/Pages/Index.cshtml src/Shell.Identity/Pages/Index.cshtml.cs tests/Shell.Testing/TestDoubles.cs tests/Shell.IntegrationTests/AccountSecurityTests.cs
git commit -m "feat(identity): add account email confirmation flow and resend handler"
```

---

## Task 4: Public Trial Pool Tenant Self Sign-up at `/tenants/new` (`T1` Self Sign-up & Docs)

**Files:**
- Modify: `src/Shell.Identity/ShellIdentityOptions.cs`
- Modify: `src/Shell.Identity/ShellIdentityExtensions.cs`
- Modify: `src/Shell.Identity/Access/TenantAccessMiddleware.cs`
- Modify: `src/Shell.Identity/Tenants/TenantProvisioningService.cs`
- Create: `src/Shell.Identity/Pages/Tenants/New.cshtml`
- Create: `src/Shell.Identity/Pages/Tenants/New.cshtml.cs`
- Modify: `tests/Shell.IntegrationTests/TenancyTests.cs`
- Modify: `docs/README.md`
- Modify: `docs/extension-points.md`
- Modify: `docs/core-gap-analysis.md`

- [ ] **Step 1: Write the failing self sign-up integration tests in `tests/Shell.IntegrationTests/TenancyTests.cs`**

Add the following test to `tests/Shell.IntegrationTests/TenancyTests.cs`:

```csharp
    [Fact]
    public async Task Public_self_signup_requires_confirmed_email_enforces_reserved_slugs_and_quota_and_provisions_trial_pool_tenant()
    {
        await using var host = db.CreateHost(new Dictionary<string, string?>
        {
            ["Identity:SelfSignUpBaseDomain"] = "localhost",
            ["Identity:MaxTenantsPerUser"] = "1",
        });
        var client = host.CreateClient();
        var email = $"signup-{Guid.NewGuid():N}@test.local";
        var browser = new OidcTestClient(host);
        await browser.RegisterAsync(email, DemoUserSeeder.Password);
        var confirmLink = db.Emails.LastLinkTo(email);

        // 1. /tenants/new is an auth-host-only route: tenant hosts get 404.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, HttpMethod.Get, "/tenants/new", TenantA)).StatusCode);

        // 2. Unconfirmed user cannot provision a workspace.
        var slug = $"trial-{Guid.NewGuid():N}"[..20];
        var unconfirmedPost = await browser.PostFormAsync(
            "/tenants/new",
            new() { ["Name"] = "Unconfirmed Co", ["Subdomain"] = slug },
            tokenPage: "/tenants/new");
        Assert.Contains("confirm your email", await unconfirmedPost.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);

        // 3. Confirm email, then verify reserved slugs ("auth", "admin") are rejected.
        Assert.Contains("id=\"email-confirmed\"", await browser.GetPageAsync(confirmLink));
        var reservedPost = await browser.PostFormAsync(
            "/tenants/new",
            new() { ["Name"] = "Reserved Co", ["Subdomain"] = "admin" },
            tokenPage: "/tenants/new");
        Assert.Contains("reserved", await reservedPost.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);

        // 4. Valid slug provisions Pool tenant "<slug>.localhost", registers OAuth client, and grants owner membership.
        var createdPost = await browser.PostFormAsync(
            "/tenants/new",
            new() { ["Name"] = "Trial Workspace", ["Subdomain"] = slug },
            tokenPage: "/tenants/new");
        var createdHtml = await createdPost.Content.ReadAsStringAsync(Ct);
        var expectedDomain = $"{slug}.localhost";
        Assert.Contains("id=\"tenant-created\"", createdHtml);
        Assert.Contains(expectedDomain, createdHtml);

        var tenantId = await db.TenantIdAsync(expectedDomain);
        var tokens = await browser.GetTokensAsync(email, DemoUserSeeder.Password, tenantId, expectedDomain);
        var product = await CreateProductAsync(client, expectedDomain, $"trial-prod-{Guid.NewGuid()}", tokens.AccessToken);
        Assert.NotEqual(Guid.Empty, product.Id);

        // 5. Quota (MaxTenantsPerUser = 1) blocks creating a second workspace.
        var secondSlug = $"second-{Guid.NewGuid():N}"[..20];
        var quotaPost = await browser.PostFormAsync(
            "/tenants/new",
            new() { ["Name"] = "Second Workspace", ["Subdomain"] = secondSlug },
            tokenPage: "/tenants/new");
        Assert.Contains("at most 1", await quotaPost.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```powershell
dotnet test --solution DigitalWorkspace.slnx -c Release --filter "FullyQualifiedName~Public_self_signup_requires_confirmed_email"
```
Expected: FAIL (`/tenants/new` does not exist yet).

- [ ] **Step 3: Add `SelfSignUpBaseDomain` & `MaxTenantsPerUser`, guard `/tenants` on tenant hosts, implement `CreateTrialAsync`, and create `/tenants/new`**

In `src/Shell.Identity/ShellIdentityOptions.cs`, add the following properties right after `RateLimitPerMinute`:

```csharp
    /// <summary>Base domain appended to customer-chosen subdomain slugs during self sign-up (e.g. <c>localhost</c> or <c>app.com</c>).</summary>
    [Required]
    public string SelfSignUpBaseDomain { get; set; } = "localhost";

    /// <summary>Maximum number of workspaces a single user may own via self sign-up.</summary>
    [Range(1, 100)]
    public int MaxTenantsPerUser { get; set; } = 3;
```

In `src/Shell.Identity/ShellIdentityExtensions.cs`, add validation for `SelfSignUpBaseDomain` in `AddShellIdentity` right after the `Identity:Email` `.Validate(...)` call:

```csharp
            .Validate(o => !string.IsNullOrWhiteSpace(o.SelfSignUpBaseDomain) && !o.SelfSignUpBaseDomain.Contains('/', StringComparison.Ordinal),
                "Identity:SelfSignUpBaseDomain must be a valid host suffix.")
```

In `src/Shell.Identity/Access/TenantAccessMiddleware.cs`, add `"/tenants"` to `AuthHostGuardMiddleware.AuthPaths`:

```csharp
    private static readonly PathString[] AuthPaths = ["/connect", "/account", "/invitations", "/tenants", "/.well-known", "/signin-google", "/signin-microsoft"];
```

In `src/Shell.Identity/Tenants/TenantProvisioningService.cs`, inject `IOptions<ShellIdentityOptions> identityOptions` in the primary constructor and add `ReservedSubdomains`, `CreateTrialAsync`, and `SlugPattern()`:

```csharp
    private static readonly HashSet<string> ReservedSubdomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth", "admin", "api", "www", "mail", "status", "health", "system", "platform", "billing", "support",
    };
```

Add `CreateTrialAsync` to `TenantProvisioningService`:

```csharp
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
        return await ProvisionAsync(name, domain, owner.Email, siloConnectionString: null, actorId: owner.Id, ct);
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex SlugPattern();
```

Create `src/Shell.Identity/Pages/Tenants/New.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Shell.Identity.Data;
using Shell.Identity.Tenants;

namespace Shell.Identity.Pages.Tenants;

[Authorize]
public sealed class NewTenantModel(
    UserManager<User> users,
    TenantProvisioningService provisioning,
    IOptions<ShellIdentityOptions> options) : PageModel
{
    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string Subdomain { get; set; } = "";

    public string BaseDomain => options.Value.SelfSignUpBaseDomain;

    public bool EmailConfirmed { get; private set; }

    public string? Error { get; private set; }

    public string? CreatedDomain { get; private set; }

    public Guid? CreatedTenantId { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await users.GetUserAsync(User) is not { } user)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = "/tenants/new" });
        }

        EmailConfirmed = user.EmailConfirmed;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await users.GetUserAsync(User) is not { } user)
        {
            return RedirectToPage("/Account/Login", new { returnUrl = "/tenants/new" });
        }

        EmailConfirmed = user.EmailConfirmed;
        var result = await provisioning.CreateTrialAsync(user, Name, Subdomain, HttpContext.RequestAborted);
        if (result.Outcome != TenantProvisioningOutcome.Ok)
        {
            Error = result.Detail;
            return Page();
        }

        CreatedDomain = result.Domain;
        CreatedTenantId = result.TenantId;
        return Page();
    }
}
```

Create `src/Shell.Identity/Pages/Tenants/New.cshtml`:

```cshtml
@page "/tenants/new"
@model Shell.Identity.Pages.Tenants.NewTenantModel
@{
    ViewData["Title"] = "Create a workspace";
}
<h1>Create a workspace</h1>

@if (Model.Error is not null)
{
    <div class="error" role="alert">@Model.Error</div>
}

@if (Model.CreatedDomain is not null)
{
    <div id="tenant-created" class="ok">
        Workspace <strong>@Model.Name</strong> is ready at <code>@Model.CreatedDomain</code> (ID: <code>@Model.CreatedTenantId</code>).
    </div>
    <p class="links"><a href="/">Back to account</a></p>
}
else
{
    @if (!Model.EmailConfirmed)
    {
        <div id="email-unconfirmed" class="error" role="alert">
            Please confirm your email address before creating a workspace.
        </div>
    }
    <form method="post">
        <label for="name">Workspace name</label>
        <input id="name" name="Name" type="text" value="@Model.Name" required maxlength="200" />

        <label for="subdomain">Subdomain (<code>&lt;subdomain&gt;.@Model.BaseDomain</code>)</label>
        <input id="subdomain" name="Subdomain" type="text" value="@Model.Subdomain" required maxlength="63" pattern="[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?" />

        <button type="submit">Create workspace</button>
    </form>
    <p class="links"><a href="/">Back to account</a></p>
}
```

Update `docs/README.md`, `docs/extension-points.md`, and `docs/core-gap-analysis.md`:
- In `docs/README.md`, document `create-tenant <name> <domain> <owner-email> [--silo <connection-string>]` alongside `migrate`, `disable-user`, and `enable-user`, and note `/account/confirm-email` + `/tenants/new` on the authorization server.
- In `docs/extension-points.md` (§5 Onboarding a new tenant), replace the manual SQL snippet with `dotnet run --project src/Host -- create-tenant "<Name>" <domain> <owner-email> [--silo "<connection-string>"]` and self-service `/tenants/new`.
- In `docs/core-gap-analysis.md`, mark `T1` (Đợt 1B) as completed (`✅ Đã hoàn thành ở Đợt 1B`).

- [ ] **Step 4: Run full verification suite**

Run:
```powershell
dotnet format DigitalWorkspace.slnx --verify-no-changes --no-restore
dotnet build DigitalWorkspace.slnx -c Release
dotnet test --solution DigitalWorkspace.slnx -c Release --no-build
```
Expected: `0 Warning(s)`, `0 Error(s)`, all 58 tests pass (`15` in `Shell.UnitTests`, `43` in `Shell.IntegrationTests`).

- [ ] **Step 5: Commit Task 4**

Run:
```powershell
git add src/Shell.Identity/ShellIdentityOptions.cs src/Shell.Identity/ShellIdentityExtensions.cs src/Shell.Identity/Access/TenantAccessMiddleware.cs src/Shell.Identity/Tenants/TenantProvisioningService.cs src/Shell.Identity/Pages/Tenants/New.cshtml src/Shell.Identity/Pages/Tenants/New.cshtml.cs tests/Shell.IntegrationTests/TenancyTests.cs docs/README.md docs/extension-points.md docs/core-gap-analysis.md
git commit -m "feat(tenancy): add public trial workspace self sign-up at /tenants/new"
```
