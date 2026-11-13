using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Products.Contracts;
using Shell.Core.Tenancy;
using Shell.Identity;
using Shell.Identity.Data;
using Shell.Identity.Tenants;

namespace Shell.IntegrationTests;

/// <summary>
/// End to end: migrate command, tenant resolution, Pool vs Silo storage. Calls use alice's tokens (member of A and B).
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenancyTests(PostgresFixture db)
{
    private const string TenantA = DemoTenantSeeder.PoolTenantDomain;
    private const string TenantB = DemoTenantSeeder.SiloTenantDomain;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrate_creates_catalog_shared_and_silo_databases_and_seeds_demo_tenants()
    {
        // Running again is a no-op (and does not seed twice).
        await using var host = db.CreateHost();
        await PostgresFixture.MigrateAsync(host);

        const string demoTenants = $"""
            SELECT count(*) FROM catalog.tenants t JOIN catalog.tenant_domains d ON d.tenant_id = t.id
            WHERE d.domain IN ('{TenantA}', '{TenantB}')
            """;
        Assert.Equal(2, await db.CountAsync(db.CatalogConnectionString, demoTenants));
        Assert.Equal(1, await db.CountAsync(db.CatalogConnectionString, demoTenants + " AND t.is_isolated_db"));
        Assert.Equal(2, await db.CountAsync(db.CatalogConnectionString, $"SELECT count(*) FROM identity.users WHERE email IN ('{DemoUserSeeder.Alice}', '{DemoUserSeeder.Bob}')"));

        const string productsTable =
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'products' AND table_name IN ('products', '__ef_migrations_history')";
        Assert.Equal(2, await db.CountAsync(db.SharedConnectionString, productsTable));
        Assert.Equal(2, await db.CountAsync(db.TenantBConnectionString, productsTable));
    }

    [Fact]
    public async Task Each_tenant_only_sees_its_own_products_and_silo_data_stays_in_its_own_database()
    {
        await using var host = db.CreateHost();
        var client = host.CreateClient();
        var alice = new OidcTestClient(host);
        var tokenA = await AliceTokenAsync(alice, TenantA);
        var tokenB = await AliceTokenAsync(alice, TenantB);
        var nameA = $"A-{Guid.NewGuid()}";
        var nameB = $"B-{Guid.NewGuid()}";

        var createdA = await CreateProductAsync(client, TenantA, nameA, tokenA);
        await CreateProductAsync(client, TenantB, nameB, tokenB);

        var seenByA = await ListProductsAsync(client, TenantA, tokenA);
        var seenByB = await ListProductsAsync(client, TenantB, tokenB);
        Assert.Contains(nameA, seenByA);
        Assert.DoesNotContain(nameB, seenByA);
        Assert.Contains(nameB, seenByB);
        Assert.DoesNotContain(nameA, seenByB);

        // A's product id is invisible to B.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, HttpMethod.Get, $"/api/products/{createdA.Id}", TenantB, tokenB)).StatusCode);

        // Pool tenant A → shared DB; Silo tenant B → its own DB only.
        Assert.Equal(1, await db.CountAsync(db.SharedConnectionString, $"SELECT count(*) FROM products.products WHERE name = '{nameA}'"));
        Assert.Equal(0, await db.CountAsync(db.SharedConnectionString, $"SELECT count(*) FROM products.products WHERE name = '{nameB}'"));
        Assert.Equal(1, await db.CountAsync(db.TenantBConnectionString, $"SELECT count(*) FROM products.products WHERE name = '{nameB}'"));
    }

    [Fact]
    public async Task Tenant_is_resolved_from_the_request_host()
    {
        await using var host = db.CreateHost();
        var client = host.CreateClient(new() { BaseAddress = new Uri($"http://{TenantB}") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", await AliceTokenAsync(new OidcTestClient(host), TenantB));
        var name = $"by-host-{Guid.NewGuid()}";

        var response = await client.PostAsJsonAsync("/api/products", new CreateProductRequest(name, 1m), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await db.CountAsync(db.TenantBConnectionString, $"SELECT count(*) FROM products.products WHERE name = '{name}'"));
    }

    [Fact]
    public async Task Unknown_host_gets_404_and_locked_tenant_gets_403()
    {
        var lockedDomain = $"locked-{Guid.NewGuid():N}.localhost";
        await db.CountAsync(db.CatalogConnectionString, $"""
            WITH t AS (
                INSERT INTO catalog.tenants (id, name, is_active, is_isolated_db) VALUES (gen_random_uuid(), 'Locked', false, false) RETURNING id)
            INSERT INTO catalog.tenant_domains (id, tenant_id, domain) SELECT gen_random_uuid(), id, '{lockedDomain}' FROM t
            RETURNING 1
            """);

        await using var host = db.CreateHost();
        var client = host.CreateClient();

        var unknown = await SendAsync(client, HttpMethod.Get, "/api/products", "unknown.localhost");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, "/api/products", lockedDomain)).StatusCode);
    }

    [Fact]
    public async Task System_routes_need_no_tenant()
    {
        await using var host = db.CreateHost();
        var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/products", Ct)).StatusCode); // host "localhost" is no tenant
    }

    [Fact]
    public async Task Temporary_tenant_helper_provisions_oauth_client_and_membership_for_module_tests()
    {
        await using var host = db.CreateHost();
        var (tenantId, domain) = await db.CreateTenantAsync(host);
        await db.AddMemberAsync(tenantId, DemoUserSeeder.Bob, role: "owner");

        var tokens = await new OidcTestClient(host).GetTokensAsync(DemoUserSeeder.Bob, DemoUserSeeder.Password, tenantId, domain);
        var response = await OidcTestClient.CallApiAsync(host, domain, HttpMethod.Get, "/api/products", tokens.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AddMemberAsync_throws_when_user_email_does_not_exist()
    {
        await using var host = db.CreateHost();
        var (tenantId, _) = await db.CreateTenantAsync(host);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.AddMemberAsync(tenantId, $"missing-{Guid.NewGuid():N}@test.local"));
        Assert.Contains("not found", ex.Message);
    }

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

    [Fact]
    public async Task Create_tenant_cli_preserves_trailing_silo_flag_for_error_reporting()
    {
        await using var host = db.CreateHost();
        var (commandArgs, hostArgs) = Program.ParseCliArgs(["create-tenant", "Acme", "acme.localhost", DemoUserSeeder.Bob, "--silo"]);

        Assert.NotNull(commandArgs);
        Assert.Empty(hostArgs);
        Assert.Contains("--silo", commandArgs);
        Assert.Equal(1, await IdentityCommands.RunAsync(host.Services, commandArgs, Ct));
        Assert.Equal(0, await db.CountAsync(db.CatalogConnectionString, "SELECT count(*) FROM catalog.tenant_domains WHERE domain = 'acme.localhost'"));
    }

    [Fact]
    public async Task ProvisionAsync_rolls_back_catalog_records_when_silo_migration_fails()
    {
        await using var host = db.CreateHost();
        var domain = $"rollback-{Guid.NewGuid():N}.localhost";
        var brokenCs = "Host=127.0.0.1;Port=1;Database=broken_silo;Username=postgres;Password=postgres;Timeout=1";

        await using var scope = host.Services.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            provisioning.ProvisionAsync("Broken Silo", domain, DemoUserSeeder.Bob, brokenCs, actorId: null, Ct));

        Assert.Equal(0, await db.CountAsync(db.CatalogConnectionString, $"SELECT count(*) FROM catalog.tenant_domains WHERE domain = '{domain}'"));
        Assert.Equal(0, await db.CountAsync(db.CatalogConnectionString, "SELECT count(*) FROM catalog.tenants WHERE name = 'Broken Silo'"));

        // Retrying the same domain with a working DB succeeds without conflict.
        var validCs = db.ForDatabase($"shell_recovered_{Guid.NewGuid():N}");
        var retry = await provisioning.ProvisionAsync("Recovered Silo", domain, DemoUserSeeder.Bob, validCs, actorId: null, Ct);
        Assert.Equal(TenantProvisioningOutcome.Ok, retry.Outcome);
    }

    [Fact]
    public async Task Concurrent_trial_signup_requests_respect_MaxTenantsPerUser_quota()
    {
        await using var host = db.CreateHost(new Dictionary<string, string?>
        {
            ["Identity:SelfSignUpBaseDomain"] = "localhost",
            ["Identity:MaxTenantsPerUser"] = "1",
        });

        var email = $"race-{Guid.NewGuid():N}@test.local";
        var browser = new OidcTestClient(host);
        await browser.RegisterAsync(email, DemoUserSeeder.Password);
        await browser.GetPageAsync(db.Emails.LastLinkTo(email));

        await using var lookupScope = host.Services.CreateAsyncScope();
        var owner = await lookupScope.ServiceProvider.GetRequiredService<AuthDbContext>().Users
            .AsNoTracking()
            .SingleAsync(u => u.Email == email, Ct);

        var slug1 = $"r1-{Guid.NewGuid():N}"[..20];
        var slug2 = $"r2-{Guid.NewGuid():N}"[..20];

        async Task<TenantProvisioningResult> AttemptAsync(string slug)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
            return await service.CreateTrialAsync(owner, $"Race {slug}", slug, Ct);
        }

        var results = await Task.WhenAll(AttemptAsync(slug1), AttemptAsync(slug2));
        Assert.Equal(1, results.Count(r => r.Outcome == TenantProvisioningOutcome.Ok));
        Assert.Equal(1, results.Count(r => r.Outcome == TenantProvisioningOutcome.Forbidden));
        Assert.Equal(1, await db.CountAsync(db.CatalogConnectionString,
            $"SELECT count(*) FROM identity.tenant_memberships WHERE user_id = '{owner.Id}'"));
    }

    private async Task<string> AliceTokenAsync(OidcTestClient browser, string domain) =>
        (await browser.GetTokensAsync(DemoUserSeeder.Alice, DemoUserSeeder.Password, await db.TenantIdAsync(domain), domain)).AccessToken!;

    private static async Task<ProductDto> CreateProductAsync(HttpClient client, string tenant, string name, string? accessToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/products")
        {
            Content = JsonContent.Create(new CreateProductRequest(name, 9.99m)),
        };
        request.Headers.Add(TenantResolutionMiddleware.TenantHostHeader, tenant);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new("Bearer", accessToken);
        }

        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductDto>(Ct))!;
    }

    private static async Task<List<string>> ListProductsAsync(HttpClient client, string tenant, string? accessToken = null)
    {
        var response = await SendAsync(client, HttpMethod.Get, "/api/products", tenant, accessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ProductDto>>(Ct))!.Select(p => p.Name).ToList();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string tenant, string? accessToken = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TenantResolutionMiddleware.TenantHostHeader, tenant);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new("Bearer", accessToken);
        }

        return await client.SendAsync(request, Ct);
    }
}
