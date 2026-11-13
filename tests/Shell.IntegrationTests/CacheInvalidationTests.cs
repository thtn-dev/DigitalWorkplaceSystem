using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shell.Abstractions.Security;
using Shell.Core.Tenancy;
using Shell.Identity;
using Shell.Identity.Data;

namespace Shell.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class CacheInvalidationTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Locking_tenant_on_one_instance_immediately_invalidates_tenant_cache_on_another_instance()
    {
        await using var hostA = db.CreateHost();
        await using var hostB = db.CreateHost();
        using var clientB = hostB.CreateClient();

        var tenantAId = await db.TenantIdAsync(DemoTenantSeeder.PoolTenantDomain);
        var browserA = new OidcTestClient(hostA);
        var token = (await browserA.GetTokensAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password, tenantAId, DemoTenantSeeder.PoolTenantDomain)).AccessToken!;

        // Warm hostB's TenantStore cache.
        Assert.Equal(HttpStatusCode.OK, (await GetProductsAsync(clientB, DemoTenantSeeder.PoolTenantDomain, token)).StatusCode);

        // Lock tenant via hostA /admin API.
        Assert.Equal(0, await IdentityCommands.RunAsync(hostA.Services, ["grant-admin", DemoUserSeeder.Alice], Ct));
        Assert.Equal(HttpStatusCode.Redirect, (await browserA.SubmitLoginAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browserA.SendJsonAsync(HttpMethod.Post, $"/admin/tenants/{tenantAId}/lock")).StatusCode);

        try
        {
            // hostB should observe 403 immediately via LISTEN/NOTIFY (well within 3s vs 10m cache TTL).
            var statusAfterLock = await PollStatusAsync(clientB, DemoTenantSeeder.PoolTenantDomain, token, HttpStatusCode.Forbidden);
            Assert.Equal(HttpStatusCode.Forbidden, statusAfterLock);
        }
        finally
        {
            await browserA.SendJsonAsync(HttpMethod.Post, $"/admin/tenants/{tenantAId}/unlock");
            await IdentityCommands.RunAsync(hostA.Services, ["revoke-admin", DemoUserSeeder.Alice], Ct);
        }

        var statusAfterUnlock = await PollStatusAsync(clientB, DemoTenantSeeder.PoolTenantDomain, token, HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, statusAfterUnlock);
    }

    [Fact]
    public async Task Revoking_membership_on_one_instance_immediately_invalidates_access_cache_on_another_instance()
    {
        await using var hostA = db.CreateHost();
        await using var hostB = db.CreateHost();
        using var clientA = hostA.CreateClient();
        using var clientB = hostB.CreateClient();

        var domain = $"notify-{Guid.NewGuid():N}.localhost";
        Assert.Equal(0, await IdentityCommands.RunAsync(hostA.Services, ["create-tenant", "Notify Co", domain, DemoUserSeeder.Alice], Ct));
        var tenantId = await db.TenantIdAsync(domain);

        Guid bobId;
        await using (var scope = hostA.Services.CreateAsyncScope())
        {
            var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var bob = authDb.Users.Single(u => u.Email == DemoUserSeeder.Bob);
            bobId = bob.Id;
            var now = DateTimeOffset.UtcNow;
            authDb.Memberships.Add(new TenantMembership
            {
                TenantId = tenantId,
                UserId = bobId,
                Role = TenantRoles.Member,
                Status = MembershipStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await authDb.SaveChangesAsync(Ct);
        }

        var aliceBrowser = new OidcTestClient(hostA);
        var aliceToken = (await aliceBrowser.GetTokensAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password, tenantId, domain)).AccessToken!;
        var bobBrowser = new OidcTestClient(hostA);
        var bobToken = (await bobBrowser.GetTokensAsync(DemoUserSeeder.Bob, DemoUserSeeder.Password, tenantId, domain)).AccessToken!;

        // Warm hostB's TenantAccessService cache for Bob.
        Assert.Equal(HttpStatusCode.OK, (await GetProductsAsync(clientB, domain, bobToken)).StatusCode);

        // Alice revokes Bob's membership on hostA.
        using var revokeRequest = new HttpRequestMessage(HttpMethod.Post, $"/iam/members/{bobId}/revoke");
        revokeRequest.Headers.Add(TenantResolutionMiddleware.TenantHostHeader, domain);
        revokeRequest.Headers.Authorization = new("Bearer", aliceToken);
        var revokeResponse = await clientA.SendAsync(revokeRequest, Ct);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        // hostB should reject Bob with 401 immediately via LISTEN/NOTIFY (well within 3s vs 60s membership cache TTL).
        var statusAfterRevoke = await PollStatusAsync(clientB, domain, bobToken, HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.Unauthorized, statusAfterRevoke);
    }

    private static async Task<HttpStatusCode> PollStatusAsync(
        HttpClient client,
        string domain,
        string accessToken,
        HttpStatusCode expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        var lastStatus = HttpStatusCode.InternalServerError;
        while (DateTimeOffset.UtcNow < deadline)
        {
            lastStatus = (await GetProductsAsync(client, domain, accessToken)).StatusCode;
            if (lastStatus == expected)
            {
                return lastStatus;
            }

            await Task.Delay(50, Ct);
        }

        return lastStatus;
    }

    private static async Task<HttpResponseMessage> GetProductsAsync(HttpClient client, string domain, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.Add(TenantResolutionMiddleware.TenantHostHeader, domain);
        request.Headers.Authorization = new("Bearer", accessToken);
        return await client.SendAsync(request, Ct);
    }
}
