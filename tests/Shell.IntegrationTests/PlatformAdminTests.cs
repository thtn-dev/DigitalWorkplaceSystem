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
}
