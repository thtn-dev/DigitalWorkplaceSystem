using System.Net;
using System.Net.Http.Json;
using Shell.Core.Tenancy;
using Shell.Identity;
using Shell.Identity.Data;

namespace Shell.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PlatformAdminTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Fact]
    public async Task Admin_endpoints_require_auth_host_and_platform_admin_and_revoke_immediately()
    {
        await using var host = db.CreateHost();

        // 1. Unauthenticated caller gets 401 (not 302 redirect).
        var unauthenticated = new OidcTestClient(host);
        Assert.Equal(HttpStatusCode.Unauthorized, (await unauthenticated.GetAsync("/admin/tenants")).StatusCode);

        // 2. Authenticated non-admin gets 403.
        var browser = new OidcTestClient(host);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SubmitLoginAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/admin/tenants")).StatusCode);

        // 3. Tenant host returns 404 for /admin/*.
        using var tenantClient = host.CreateClient();
        using var tenantRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/tenants");
        tenantRequest.Headers.Add(TenantResolutionMiddleware.TenantHostHeader, DemoTenantSeeder.PoolTenantDomain);
        Assert.Equal(HttpStatusCode.NotFound, (await tenantClient.SendAsync(tenantRequest, Ct)).StatusCode);

        // 4. Granting admin allows access; revoking revokes the active session immediately.
        Assert.Equal(0, await IdentityCommands.RunAsync(host.Services, ["grant-admin", DemoUserSeeder.Alice], Ct));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin/tenants")).StatusCode);

        Assert.Equal(0, await IdentityCommands.RunAsync(host.Services, ["revoke-admin", DemoUserSeeder.Alice], Ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/admin/tenants")).StatusCode);
    }

    [Fact]
    public async Task Admin_can_list_lock_unlock_tenants_and_manage_domains()
    {
        await using var host = db.CreateHost();
        Assert.Equal(0, await IdentityCommands.RunAsync(host.Services, ["grant-admin", DemoUserSeeder.Alice], Ct));
        var browser = new OidcTestClient(host);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SubmitLoginAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password)).StatusCode);

        var listResponse = await browser.GetAsync("/admin/tenants");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var tenants = (await listResponse.Content.ReadFromJsonAsync<List<TenantItem>>(Ct))!;
        Assert.Contains(tenants, t => t.Domains.Contains(DemoTenantSeeder.PoolTenantDomain));
        Assert.Contains(tenants, t => t.Domains.Contains(DemoTenantSeeder.SiloTenantDomain));

        var tenantAId = await db.TenantIdAsync(DemoTenantSeeder.PoolTenantDomain);
        var token = (await browser.GetTokensAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password, tenantAId, DemoTenantSeeder.PoolTenantDomain)).AccessToken!;
        using var apiClient = host.CreateClient();

        // Mutating admin endpoint without antiforgery token is rejected with 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendJsonAsync(HttpMethod.Post, $"/admin/tenants/{tenantAId}/lock")).StatusCode);

        // Lock tenant -> immediate 403 on tenant host.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAdminAsync(HttpMethod.Post, $"/admin/tenants/{tenantAId}/lock")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetProductsAsync(apiClient, DemoTenantSeeder.PoolTenantDomain, token)).StatusCode);
        Assert.True(await db.CountAsync(db.CatalogConnectionString,
            $"SELECT count(*) FROM identity.audit_events WHERE tenant_id = '{tenantAId}' AND type = 'tenant.locked'") >= 1);

        // Unlock tenant -> 200 restored.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAdminAsync(HttpMethod.Post, $"/admin/tenants/{tenantAId}/unlock")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetProductsAsync(apiClient, DemoTenantSeeder.PoolTenantDomain, token)).StatusCode);
        Assert.True(await db.CountAsync(db.CatalogConnectionString,
            $"SELECT count(*) FROM identity.audit_events WHERE tenant_id = '{tenantAId}' AND type = 'tenant.unlocked'") >= 1);

        // Domain validation (trailing dot, port), PlatformHosts conflict, duplicate conflict, add/remove + OAuth client sync, and last-domain guard.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAdminAsync(HttpMethod.Post, $"/admin/tenants/{tenantAId}/domains", new { Domain = "bad:5080" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAdminAsync(HttpMethod.Post, $"/admin/tenants/{tenantAId}/domains", new { Domain = "trailing.localhost." })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await browser.SendAdminAsync(HttpMethod.Post, $"/admin/tenants/{tenantAId}/domains", new { Domain = ShellHostFactory.AuthHost })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await browser.SendAdminAsync(HttpMethod.Post, $"/admin/tenants/{tenantAId}/domains", new { Domain = DemoTenantSeeder.PoolTenantDomain })).StatusCode);

        var customDomain = $"custom-{Guid.NewGuid():N}.localhost";
        var addResponse = await browser.SendAdminAsync(HttpMethod.Post, $"/admin/tenants/{tenantAId}/domains", new { Domain = $"  {customDomain.ToUpperInvariant()}  " });
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetProductsAsync(apiClient, customDomain, token)).StatusCode);
        var addedAuth = await browser.AuthorizeAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password, tenantAId, customDomain);
        Assert.NotNull(addedAuth.Code);
        Assert.True(await db.CountAsync(db.CatalogConnectionString,
            $"SELECT count(*) FROM identity.audit_events WHERE tenant_id = '{tenantAId}' AND type = 'tenant.domain_added'") >= 1);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAdminAsync(HttpMethod.Delete, $"/admin/tenants/{tenantAId}/domains/{customDomain}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetProductsAsync(apiClient, customDomain, token)).StatusCode);
        var removedAuth = await browser.AuthorizeAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password, tenantAId, customDomain);
        Assert.Null(removedAuth.Code);
        Assert.True(await db.CountAsync(db.CatalogConnectionString,
            $"SELECT count(*) FROM identity.audit_events WHERE tenant_id = '{tenantAId}' AND type = 'tenant.domain_removed'") >= 1);

        // Cannot delete last remaining domain.
        Assert.Equal(HttpStatusCode.Conflict, (await browser.SendAdminAsync(HttpMethod.Delete, $"/admin/tenants/{tenantAId}/domains/{DemoTenantSeeder.PoolTenantDomain}")).StatusCode);

        // Restore Alice admin flag to false cleanly.
        Assert.Equal(0, await IdentityCommands.RunAsync(host.Services, ["revoke-admin", DemoUserSeeder.Alice], Ct));
    }

    [Fact]
    public async Task Admin_can_list_disable_and_enable_users_but_cannot_disable_self()
    {
        await using var host = db.CreateHost();
        Assert.Equal(0, await IdentityCommands.RunAsync(host.Services, ["grant-admin", DemoUserSeeder.Alice], Ct));
        var browser = new OidcTestClient(host);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SubmitLoginAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password)).StatusCode);

        var usersResponse = await browser.GetAsync("/admin/users");
        Assert.Equal(HttpStatusCode.OK, usersResponse.StatusCode);
        var users = (await usersResponse.Content.ReadFromJsonAsync<List<UserItem>>(Ct))!;
        var alice = Assert.Single(users, u => u.Email == DemoUserSeeder.Alice);
        var bob = Assert.Single(users, u => u.Email == DemoUserSeeder.Bob);
        Assert.True(alice.IsPlatformAdmin);

        // Self-disable is rejected.
        Assert.Equal(HttpStatusCode.Conflict, (await browser.SendAdminAsync(HttpMethod.Post, $"/admin/users/{alice.Id}/disable")).StatusCode);

        // Disable then enable Bob.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAdminAsync(HttpMethod.Post, $"/admin/users/{bob.Id}/disable")).StatusCode);
        Assert.Equal("true", await db.ScalarAsync($"SELECT is_disabled::text FROM identity.users WHERE id = '{bob.Id}'"));

        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAdminAsync(HttpMethod.Post, $"/admin/users/{bob.Id}/enable")).StatusCode);
        Assert.Equal("false", await db.ScalarAsync($"SELECT is_disabled::text FROM identity.users WHERE id = '{bob.Id}'"));

        Assert.Equal(0, await IdentityCommands.RunAsync(host.Services, ["revoke-admin", DemoUserSeeder.Alice], Ct));
    }

    [Fact]
    public async Task Concurrent_domain_add_and_delete_preserve_uniqueness_and_last_domain_invariant()
    {
        await using var host = db.CreateHost();
        var domain1 = $"c1-{Guid.NewGuid():N}.localhost";
        var domain2 = $"c2-{Guid.NewGuid():N}.localhost";
        Assert.Equal(0, await IdentityCommands.RunAsync(host.Services, ["create-tenant", "Race Domain Co", domain1, DemoUserSeeder.Alice], Ct));
        var tenantId = await db.TenantIdAsync(domain1);

        Assert.Equal(0, await IdentityCommands.RunAsync(host.Services, ["grant-admin", DemoUserSeeder.Alice], Ct));
        try
        {
            var browser1 = new OidcTestClient(host);
            var browser2 = new OidcTestClient(host);
            Assert.Equal(HttpStatusCode.Redirect, (await browser1.SubmitLoginAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password)).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await browser2.SubmitLoginAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password)).StatusCode);

            // Concurrent adds of the same domain: one 201, one 409 (never 500).
            var addResults = await Task.WhenAll(
                browser1.SendAdminAsync(HttpMethod.Post, $"/admin/tenants/{tenantId}/domains", new { Domain = domain2 }),
                browser2.SendAdminAsync(HttpMethod.Post, $"/admin/tenants/{tenantId}/domains", new { Domain = domain2 }));
            Assert.Equal(1, addResults.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(1, addResults.Count(r => r.StatusCode == HttpStatusCode.Conflict));

            // Concurrent deletes of the two remaining domains: one 204, one 409 (at least 1 domain remains).
            var delResults = await Task.WhenAll(
                browser1.SendAdminAsync(HttpMethod.Delete, $"/admin/tenants/{tenantId}/domains/{domain1}"),
                browser2.SendAdminAsync(HttpMethod.Delete, $"/admin/tenants/{tenantId}/domains/{domain2}"));
            Assert.Equal(1, delResults.Count(r => r.StatusCode == HttpStatusCode.NoContent));
            Assert.Equal(1, delResults.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            Assert.Equal(1, await db.CountAsync(db.CatalogConnectionString,
                $"SELECT count(*) FROM catalog.tenant_domains WHERE tenant_id = '{tenantId}'"));
        }
        finally
        {
            Assert.Equal(0, await IdentityCommands.RunAsync(host.Services, ["revoke-admin", DemoUserSeeder.Alice], Ct));
        }
    }

    private static async Task<HttpResponseMessage> GetProductsAsync(HttpClient client, string domain, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.Add(TenantResolutionMiddleware.TenantHostHeader, domain);
        request.Headers.Authorization = new("Bearer", accessToken);
        return await client.SendAsync(request, Ct);
    }

    private sealed record TenantItem(Guid Id, string Name, bool IsActive, bool IsIsolatedDb, List<string> Domains);

    private sealed record UserItem(Guid Id, string? Email, bool EmailConfirmed, bool IsDisabled, bool IsPlatformAdmin, Guid? ManagedByTenantId, DateTimeOffset CreatedAt);
}
