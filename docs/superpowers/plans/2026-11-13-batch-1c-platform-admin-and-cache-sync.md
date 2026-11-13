# Batch 1C: Platform Admin & Instant Cache Synchronization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Platform Admin role (`grant-admin`/`revoke-admin` CLI + `/admin/*` Minimal API on the auth host) and instant cross-instance cache invalidation for tenant and membership caches via PostgreSQL `LISTEN/NOTIFY`.

**Architecture:** Platform admin status is stored on `identity.users` (`IsPlatformAdmin`), exposed as a `"platform_admin"` claim on sign-in, and enforced on `/admin/*` via a DB-backed `IAuthorizationHandler` on the auth host only. Tenant (`TenantStore`) and membership (`TenantAccessService`) caches share a `Shell.Core` `ICacheInvalidationBus` that publishes `pg_notify('shell_cache_invalidation', ...)` on mutations and a `BackgroundService` listener that evicts local `IMemoryCache` keys across all running host instances.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core + Npgsql (`LISTEN/NOTIFY`), ASP.NET Core Identity, `IMemoryCache`, xUnit + Testcontainers PostgreSQL.

**Spec:** [docs/core-gap-analysis.md](../../core-gap-analysis.md) (§2 `T2`, `T4`; §9 Đợt 1C; §10 Quyết định đã chốt)

## Global Constraints

- Target framework: `net10.0`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `.editorconfig` enforced (`dotnet format DigitalWorkspace.slnx --verify-no-changes --no-restore`).
- `/admin/*` is an auth-host-only Minimal API JSON surface (consistent with `/iam/*`), returning `404` on tenant hosts via `AuthHostGuardMiddleware`.
- Database schema changes require an EF Core migration on `AuthDbContext` (`identity` schema); `CatalogDbContext` requires no schema changes.
- All state-changing admin operations must write an `AuditEvent` to `identity.audit_events` and invalidate relevant caches.
- Never use raw string interpolation in SQL (`SELECT pg_notify(...)` must use parameterized `@payload`).

## Review Focus

1. Unauthenticated requests to `/admin/*` on the auth host return `401 Unauthorized` (not `302` redirect to `/account/login`), authenticated non-admins return `403 Forbidden`, and requests to `/admin/*` on tenant hosts return `404 Not Found`.
2. Revoking admin (`revoke-admin`) or disabling a user (`disable-user`) revokes `/admin/*` access immediately even with an active cookie session; an admin cannot disable themselves via `POST /admin/users/{id}/disable` (`409 Conflict`).
3. `POST /admin/tenants/{id}/domains` rejects invalid hostnames (ports, slashes, spaces) with `400`, duplicate domains (case-insensitive) with `409`, and `DELETE /admin/tenants/{id}/domains/{domain}` refuses removing a tenant's last domain with `409`.
4. `PostgresCacheInvalidationListener` evicts `IMemoryCache` locally without re-publishing `NOTIFY` (no echo loop) and reconnects with backoff if the Postgres connection drops.
5. Locking a tenant, revoking a membership, or disabling a user on Host A evicts Host B's cached `TenantStore` / `TenantAccessService` entries via `LISTEN/NOTIFY`, so Host B immediately returns `403` (locked tenant) or `401` (revoked access).

---

### Task 1: Platform Admin Role, DB-Backed Policy & CLI (`grant-admin` / `revoke-admin`)

**Files:**
- Modify: `src/Shell.Abstractions/Security/ICurrentUser.cs` (or add `src/Shell.Identity/Access/PlatformAdminAuthorization.cs`)
- Modify: `src/Shell.Identity/Data/AuthDbContext.cs:97-106`
- Create: `src/Shell.Identity/Data/Migrations/<timestamp>_AddPlatformAdmin.cs` (via `dotnet ef migrations add`)
- Modify: `src/Shell.Identity/Audit/AuditLog.cs:8-31`
- Modify: `src/Shell.Identity/Access/UserAccountService.cs:20-43`
- Modify: `src/Shell.Identity/IdentityCommands.cs:16-62`
- Modify: `src/Host/Program.cs:126-132`
- Modify: `src/Shell.Identity/ShellIdentityExtensions.cs:105-140,210-231`
- Test: `tests/Shell.IntegrationTests/PlatformAdminTests.cs`

**Interfaces:**
- Produces:
  - `User.IsPlatformAdmin` (`bool`)
  - `PlatformPolicies.Admin` (`const string = "PlatformAdmin"`)
  - `UserAccountService.SetPlatformAdminAsync(string email, bool isPlatformAdmin, Guid? actorId, CancellationToken ct) -> Task<bool>`
  - `UserAccountService.SetDisabledByIdAsync(Guid userId, bool disabled, Guid? actorId, CancellationToken ct) -> Task<bool>`
  - `AuditEvents.PlatformAdminGranted`, `AuditEvents.PlatformAdminRevoked`, `AuditEvents.TenantLocked`, `AuditEvents.TenantUnlocked`, `AuditEvents.TenantDomainAdded`, `AuditEvents.TenantDomainRemoved`

- [ ] **Step 1: Write the failing tests for CLI `grant-admin`/`revoke-admin` and `PlatformPolicies.Admin`**

Create `tests/Shell.IntegrationTests/PlatformAdminTests.cs` with `PostgresCollection` fixture:
```csharp
[Fact]
public async Task Grant_and_revoke_admin_cli_updates_user_flag_writes_audit_and_rejects_unknown_email()
{
    await using var host = db.CreateHost();
    Assert.Equal(1, await IdentityCommands.RunAsync(host.Services, ["grant-admin", "missing@test.local"], Ct));

    Assert.Equal(0, await IdentityCommands.RunAsync(host.Services, ["grant-admin", DemoUserSeeder.Bob], Ct));
    Assert.Equal("true", await db.ScalarAsync($"SELECT is_platform_admin::text FROM identity.users WHERE email = '{DemoUserSeeder.Bob}'"));

    var bobId = await db.ScalarAsync($"SELECT id::text FROM identity.users WHERE email = '{DemoUserSeeder.Bob}'");
    Assert.Equal(1, await db.CountAsync(db.CatalogConnectionString,
        $"SELECT count(*) FROM identity.audit_events WHERE user_id = '{bobId}' AND type = 'platform_admin.granted'"));

    Assert.Equal(0, await IdentityCommands.RunAsync(host.Services, ["revoke-admin", DemoUserSeeder.Bob], Ct));
    Assert.Equal("false", await db.ScalarAsync($"SELECT is_platform_admin::text FROM identity.users WHERE email = '{DemoUserSeeder.Bob}'"));
    Assert.Equal(1, await db.CountAsync(db.CatalogConnectionString,
        $"SELECT count(*) FROM identity.audit_events WHERE user_id = '{bobId}' AND type = 'platform_admin.revoked'"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --solution DigitalWorkspace.slnx -c Release --filter "FullyQualifiedName~PlatformAdminTests"`
Expected: FAIL (compile error: `is_platform_admin` / `PlatformAdminTests` / `grant-admin` not implemented).

- [ ] **Step 3: Add `IsPlatformAdmin` to `User` and generate EF Core migration**

In `src/Shell.Identity/Data/AuthDbContext.cs`, add `public bool IsPlatformAdmin { get; set; }` to `User`.
Generate migration on `AuthDbContext`:
`dotnet ef migrations add AddPlatformAdmin --project src/Shell.Identity --startup-project src/Host --context AuthDbContext --output-dir Data/Migrations`

- [ ] **Step 4: Add `AuditEvents` constants and `UserAccountService` methods**

In `src/Shell.Identity/Audit/AuditLog.cs`, add:
- `PlatformAdminGranted = "platform_admin.granted"`
- `PlatformAdminRevoked = "platform_admin.revoked"`
- `TenantLocked = "tenant.locked"`
- `TenantUnlocked = "tenant.unlocked"`
- `TenantDomainAdded = "tenant.domain_added"`
- `TenantDomainRemoved = "tenant.domain_removed"`

In `src/Shell.Identity/Access/UserAccountService.cs`:
- Add `SetPlatformAdminAsync(string email, bool isPlatformAdmin, Guid? actorId, CancellationToken ct)` returning `Task<bool>`: finds user by email, sets `user.IsPlatformAdmin = isPlatformAdmin`, calls `audit.Add(...)`, calls `users.UpdateSecurityStampAsync(user)`.
- Add `SetDisabledByIdAsync(Guid userId, bool disabled, Guid? actorId, CancellationToken ct)` returning `Task<bool>` (reusing a shared private helper `SetDisabledAsync(User user, bool disabled, Guid? actorId, CancellationToken ct)` so `SetDisabledAsync(string email, ...)` and `SetDisabledByIdAsync(Guid userId, ...)` share one code path).

- [ ] **Step 5: Wire `grant-admin` and `revoke-admin` in `IdentityCommands` and `Program.ParseCliArgs`**

In `src/Host/Program.cs` `ParseCliArgs`: match `"disable-user" or "enable-user" or "grant-admin" or "revoke-admin"`.
In `src/Shell.Identity/IdentityCommands.cs`: handle `"grant-admin" or "revoke-admin"`, calling `UserAccountService.SetPlatformAdminAsync(email, cmd == "grant-admin", actorId: null, ct)`.

- [ ] **Step 6: Add `PlatformPolicies.Admin`, `PlatformAdminClaimsPrincipalFactory`, `PlatformAdminHandler`, and `/admin` cookie 401/403 events**

Create `src/Shell.Identity/Access/PlatformAdminAuthorization.cs`:
- `public static class PlatformPolicies { public const string Admin = "PlatformAdmin"; }`
- `PlatformAdminClaimsPrincipalFactory(UserManager<User> userManager, IOptions<IdentityOptions> optionsAccessor) : UserClaimsPrincipalFactory<User>(userManager, optionsAccessor)` adding `new Claim("platform_admin", "true")` when `user.IsPlatformAdmin` is true.
- `PlatformAdminRequirement : IAuthorizationRequirement` and `PlatformAdminHandler(AuthDbContext db) : AuthorizationHandler<PlatformAdminRequirement>`: parses `ClaimTypes.NameIdentifier` (or `"sub"`) as `Guid userId`, checks `await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsPlatformAdmin && !u.IsDisabled)` so revoked/disabled admins lose access immediately.

In `src/Shell.Identity/ShellIdentityExtensions.cs`:
- Register `.AddClaimsPrincipalFactory<PlatformAdminClaimsPrincipalFactory>()` on `AddIdentityCore<User>(...)`.
- Register `services.AddScoped<IAuthorizationHandler, PlatformAdminHandler>()` and `services.AddAuthorizationBuilder().AddPolicy(PlatformPolicies.Admin, policy => policy.AddRequirements(new PlatformAdminRequirement()))`.
- In `CookieAuthenticationOptions` for `IdentityConstants.ApplicationScheme`, set `cookie.Events.OnRedirectToLogin` and `cookie.Events.OnRedirectToAccessDenied` so requests starting with `"/admin"` return `401` / `403` instead of `302` redirect.

- [ ] **Step 7: Run tests to verify Task 1 passes**

Run: `dotnet test --solution DigitalWorkspace.slnx -c Release --filter "FullyQualifiedName~PlatformAdminTests"`
Expected: PASS.

- [ ] **Step 8: Commit Task 1**

```bash
git add src/Host/Program.cs src/Shell.Identity/ tests/Shell.IntegrationTests/PlatformAdminTests.cs
git commit -m "feat(identity): add platform admin role, policy, and grant/revoke CLI"
```

---

### Task 2: Platform Admin Minimal API (`/admin/*` on Auth Host)

**Files:**
- Create: `src/Shell.Identity/Endpoints/AdminEndpoints.cs`
- Modify: `src/Shell.Identity/Access/TenantAccessMiddleware.cs:59-74`
- Modify: `src/Shell.Identity/ShellIdentityExtensions.cs:251-258`
- Modify: `tests/Shell.Testing/OidcTestClient.cs:140-170`
- Modify: `tests/Shell.IntegrationTests/PlatformAdminTests.cs`

**Interfaces:**
- Consumes: `PlatformPolicies.Admin`, `UserAccountService.SetDisabledByIdAsync`, `TenantStore.Invalidate`, `AuditEvents.*`
- Produces:
  - Auth-host-only endpoints under `/admin`:
    - `GET /admin/tenants?search=&limit=&offset=`
    - `POST /admin/tenants/{id:guid}/lock`
    - `POST /admin/tenants/{id:guid}/unlock`
    - `POST /admin/tenants/{id:guid}/domains` (`AddDomainRequest(string Domain)`)
    - `DELETE /admin/tenants/{id:guid}/domains/{domain}`
    - `GET /admin/users?search=&limit=&offset=`
    - `POST /admin/users/{id:guid}/disable`
    - `POST /admin/users/{id:guid}/enable`
  - `OidcTestClient.SendJsonAsync(HttpMethod method, string url, object? body = null) -> Task<HttpResponseMessage>`

- [ ] **Step 1: Add `OidcTestClient.SendJsonAsync` and write failing `/admin/*` tests**

In `tests/Shell.Testing/OidcTestClient.cs`, add `SendJsonAsync(HttpMethod method, string url, object? body = null)` using `_browser`.

In `tests/Shell.IntegrationTests/PlatformAdminTests.cs`, add tests covering:
1. `Admin_endpoints_require_auth_host_and_platform_admin_and_revoke_immediately`:
   - Unauthenticated `GET /admin/tenants` on auth host -> `401`.
   - Authenticated non-admin (Alice) `GET /admin/tenants` -> `403`.
   - Request to `/admin/tenants` on `TenantA` host -> `404`.
   - After `grant-admin` for Alice and sign-in, `GET /admin/tenants` -> `200`.
   - After `revoke-admin` for Alice (without signing out), `GET /admin/tenants` -> `403` immediately.
2. `Admin_can_list_lock_unlock_tenants_and_manage_domains`:
   - `GET /admin/tenants` returns seeded tenants.
   - `POST /admin/tenants/{id}/lock` -> `204`, `TenantA` API returns `403`, audit event `tenant.locked` recorded.
   - `POST /admin/tenants/{id}/unlock` -> `204`, `TenantA` API returns `200`, audit event `tenant.unlocked` recorded.
   - `POST /admin/tenants/{id}/domains` with `"bad:5080"` -> `400`; with existing `"tenant-a.localhost"` -> `409`; with `"custom-a.localhost"` -> `201`.
   - `DELETE /admin/tenants/{id}/domains/custom-a.localhost` -> `204`; deleting the last remaining domain `"tenant-a.localhost"` -> `409`.
3. `Admin_can_list_disable_and_enable_users_but_cannot_disable_self`:
   - `GET /admin/users` returns users.
   - `POST /admin/users/{adminId}/disable` (self) -> `409`.
   - `POST /admin/users/{bobId}/disable` -> `204`, `is_disabled = true`.
   - `POST /admin/users/{bobId}/enable` -> `204`, `is_disabled = false`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --solution DigitalWorkspace.slnx -c Release --filter "FullyQualifiedName~PlatformAdminTests"`
Expected: FAIL (`404` on `/admin/*`).

- [ ] **Step 3: Implement `AdminEndpoints.cs` and wire into `ShellIdentityExtensions` and `AuthHostGuardMiddleware`**

In `src/Shell.Identity/Access/TenantAccessMiddleware.cs`, add `"/admin"` to `AuthHostGuardMiddleware.AuthPaths`.
In `src/Shell.Identity/ShellIdentityExtensions.cs` `MapShellIdentity`, call `AdminEndpoints.Map(endpoints, authHost);`.
Create `src/Shell.Identity/Endpoints/AdminEndpoints.cs`:
- Group: `endpoints.MapGroup("/admin").RequireHost(authHost).RequireAuthorization(PlatformPolicies.Admin).WithTags("Admin")`
- DTOs: `AdminTenantDto(Guid Id, string Name, bool IsActive, bool IsIsolatedDb, IReadOnlyList<string> Domains)`, `AddDomainRequest(string Domain)`, `AdminUserDto(Guid Id, string? Email, bool EmailConfirmed, bool IsDisabled, bool IsPlatformAdmin, Guid? ManagedByTenantId, DateTimeOffset CreatedAt)`.
- `SetTenantActiveAsync(Guid id, bool active, ...)` shared helper for `/lock` and `/unlock` (updates `tenant.IsActive`, saves `CatalogDbContext`, records `AuditEvents.TenantLocked` / `TenantUnlocked`, calls `tenants.Invalidate(domain, id)` for each domain of the tenant).
- `AddDomainAsync`: trims & lowercases `body.Domain`, validates `Uri.CheckHostName(domain) == UriHostNameType.Dns`, checks tenant exists (`404`), checks domain uniqueness across `catalog.TenantDomains` (`409`), inserts `TenantDomain`, records `AuditEvents.TenantDomainAdded`, invalidates cache.
- `RemoveDomainAsync`: normalizes `domain`, loads tenant's domains (`404` if tenant or domain not found), refuses if `domains.Count <= 1` (`409`), removes domain, records `AuditEvents.TenantDomainRemoved`, invalidates cache.
- `DisableUserAsync` / `EnableUserAsync`: extracts actor `Guid` from `HttpContext.User`; if disabling self -> `409 Conflict`; calls `UserAccountService.SetDisabledByIdAsync(id, disabled, actorId, ct)` (`404` if not found, `204` on success).

- [ ] **Step 4: Run tests to verify Task 2 passes**

Run: `dotnet test --solution DigitalWorkspace.slnx -c Release --filter "FullyQualifiedName~PlatformAdminTests"`
Expected: PASS.

- [ ] **Step 5: Commit Task 2**

```bash
git add src/Shell.Identity/ tests/Shell.Testing/OidcTestClient.cs tests/Shell.IntegrationTests/PlatformAdminTests.cs
git commit -m "feat(identity): add /admin Minimal API endpoints on auth host"
```

---

### Task 3: Instant Cache Invalidation via PostgreSQL `LISTEN/NOTIFY` (`T4`)

**Files:**
- Create: `src/Shell.Core/Caching/CacheInvalidation.cs`
- Modify: `src/Shell.Core/ShellServiceCollectionExtensions.cs:50-75`
- Modify: `src/Shell.Core/Tenancy/TenantStore.cs:12-65`
- Modify: `src/Shell.Identity/Access/TenantAccessService.cs:15-48`
- Modify: `src/Shell.Identity/Access/MembershipService.cs` (calls to `access.Invalidate(...)`)
- Modify: `src/Shell.Identity/Access/UserAccountService.cs` (call to `access.Invalidate(...)`)
- Modify: `src/Shell.Identity/Tenants/TenantProvisioningService.cs` (calls to `tenants.Invalidate(...)` / `access.Invalidate(...)`)
- Modify: `src/Shell.Identity/Endpoints/AdminEndpoints.cs` (calls to `tenants.Invalidate(...)`)
- Create: `tests/Shell.IntegrationTests/CacheInvalidationTests.cs`

**Interfaces:**
- Produces:
  - `CacheInvalidationKeys.TenantDomain(string domain)`, `CacheInvalidationKeys.TenantId(Guid tenantId)`, `CacheInvalidationKeys.Membership(Guid tenantId, Guid userId)`
  - `ICacheInvalidationBus` (`PublishTenantAsync(string domain, Guid? tenantId, CancellationToken ct)`, `PublishMembershipAsync(Guid tenantId, Guid userId, CancellationToken ct)`)
  - `TenantStore.InvalidateAsync(string domain, Guid? tenantId = null, CancellationToken ct = default)`
  - `TenantAccessService.InvalidateAsync(Guid tenantId, Guid userId, CancellationToken ct = default)`

- [ ] **Step 1: Write the failing multi-instance `LISTEN/NOTIFY` integration tests**

Create `tests/Shell.IntegrationTests/CacheInvalidationTests.cs`:
1. `Locking_tenant_on_one_instance_immediately_invalidates_tenant_cache_on_another_instance`:
   - Start two `ShellHostFactory` instances `hostA` and `hostB` on the same `PostgresFixture` database.
   - Warm `hostB`'s `TenantStore` cache by calling `GET /api/products` on `TenantA` with Alice's token -> `200`.
   - Grant admin to Bob on `hostA`, sign in as Bob on `hostA`, and `POST /admin/tenants/{tenantAId}/lock`.
   - Poll `hostB` (`GET /api/products` on `TenantA` with Alice's token) with short delay up to 3s -> `hostB` returns `403` (well before 10-minute `CacheMinutes`).
2. `Revoking_membership_on_one_instance_immediately_invalidates_access_cache_on_another_instance`:
   - Start `hostA` and `hostB`.
   - Warm `hostB`'s `TenantAccessService` cache (`GET /api/products` on `TenantA` with Bob's token -> `200`).
   - Revoke Bob's membership on `hostA` (`POST /iam/members/{bobId}/revoke` with Alice's token -> `204`).
   - Poll `hostB` (`GET /api/products` on `TenantA` with Bob's token) up to 3s -> `hostB` returns `401` (well before 60s `MembershipCacheSeconds`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --solution DigitalWorkspace.slnx -c Release --filter "FullyQualifiedName~CacheInvalidationTests"`
Expected: FAIL (`hostB` keeps returning cached `200`).

- [ ] **Step 3: Implement `CacheInvalidationKeys`, `PostgresCacheInvalidationBus`, and `PostgresCacheInvalidationListener` in `Shell.Core`**

In `src/Shell.Core/Caching/CacheInvalidation.cs`:
- Channel constant: `public const string ChannelName = "shell_cache_invalidation";`
- Payload format:
  - Tenant: `$"t|{(tenantId?.ToString("N") ?? "")}|{domain.Trim().ToLowerInvariant()}"`
  - Membership: `$"m|{tenantId:N}|{userId:N}"`
- `PostgresCacheInvalidationBus(IOptions<ConnectionStringsOptions> connectionStrings) : ICacheInvalidationBus`:
  - Opens an `NpgsqlConnection(connectionStrings.Value.Catalog)`, runs `SELECT pg_notify('shell_cache_invalidation', @payload)` with `NpgsqlParameter("payload", payload)`.
- `PostgresCacheInvalidationListener(IOptions<ConnectionStringsOptions> connectionStrings, IMemoryCache cache, ILogger<PostgresCacheInvalidationListener> logger) : BackgroundService`:
  - Connects to `Catalog`, subscribes `conn.Notification += (_, e) => EvictLocal(e.Payload)`, executes `LISTEN shell_cache_invalidation;`, loops `await conn.WaitAsync(stoppingToken);`.
  - Wraps connection loop in `while (!stoppingToken.IsCancellationRequested)` with `catch (Exception ex) when (ex is not OperationCanceledException)` logging warning and delaying `TimeSpan.FromSeconds(2)` before reconnecting.
  - `EvictLocal(string payload)` removes `IMemoryCache` keys directly using `CacheInvalidationKeys` (never calls `ICacheInvalidationBus`, preventing echo loops).

Register in `src/Shell.Core/ShellServiceCollectionExtensions.cs`:
- `services.TryAddSingleton<ICacheInvalidationBus, PostgresCacheInvalidationBus>();`
- `services.AddHostedService<PostgresCacheInvalidationListener>();`

- [ ] **Step 4: Wire `TenantStore.InvalidateAsync`, `TenantAccessService.InvalidateAsync`, and async callers**

- In `src/Shell.Core/Tenancy/TenantStore.cs`: inject `ICacheInvalidationBus bus`, use `CacheInvalidationKeys.TenantDomain` / `TenantId`, keep sync `Invalidate(string domain, Guid? tenantId = null)` for local-only eviction, add `InvalidateAsync(string domain, Guid? tenantId = null, CancellationToken ct = default)` which calls `Invalidate(domain, tenantId)` and `await bus.PublishTenantAsync(domain, tenantId, ct)`.
- In `src/Shell.Identity/Access/TenantAccessService.cs`: inject `ICacheInvalidationBus bus`, use `CacheInvalidationKeys.Membership`, keep sync `Invalidate(Guid tenantId, Guid userId)`, add `InvalidateAsync(Guid tenantId, Guid userId, CancellationToken ct = default)` which calls `Invalidate(tenantId, userId)` and `await bus.PublishMembershipAsync(tenantId, userId, ct)`.
- Update async callers (`AdminEndpoints`, `TenantProvisioningService`, `MembershipService`, `UserAccountService`) to `await ...InvalidateAsync(...)`.

- [ ] **Step 5: Run full verification suite**

1. `dotnet test --solution DigitalWorkspace.slnx -c Release --filter "FullyQualifiedName~CacheInvalidationTests"` -> PASS.
2. `dotnet format DigitalWorkspace.slnx --verify-no-changes --no-restore` -> Exit `0`.
3. `dotnet build DigitalWorkspace.slnx -c Release` -> `0 Warning(s)`, `0 Error(s)`.
4. `dotnet test --solution DigitalWorkspace.slnx -c Release --no-build` -> all tests PASS.

- [ ] **Step 6: Commit Task 3**

```bash
git add src/ tests/
git commit -m "feat(core,identity): invalidate tenant and membership caches via Postgres LISTEN/NOTIFY"
```
