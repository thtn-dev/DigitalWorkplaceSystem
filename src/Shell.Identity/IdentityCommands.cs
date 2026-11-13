using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shell.Identity.Access;
using Shell.Identity.Tenants;

namespace Shell.Identity;

/// <summary>
/// Platform CLI commands of the host: <c>disable-user</c>, <c>enable-user</c>, and <c>create-tenant</c>. Exit code 0 on success.
/// </summary>
public static class IdentityCommands
{
    public static Task<int> RunAsync(IServiceProvider services, string command, string email, CancellationToken ct) =>
        RunAsync(services, [command, email], ct);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IdentityCommands));

        if (args is [var cmd and ("disable-user" or "enable-user"), var email, ..])
        {
            var accounts = scope.ServiceProvider.GetRequiredService<UserAccountService>();
            var disable = cmd == "disable-user";
            if (!await accounts.SetDisabledAsync(email, disable, ct))
            {
                logger.LogError("No account with email {Email}", email);
                return 1;
            }

            logger.LogInformation("Account {Email} {State}", email, disable ? "disabled, all grants revoked" : "enabled");
            return 0;
        }

        if (args is ["create-tenant", var name, var domain, var ownerEmail, .. var rest])
        {
            string? siloConnectionString = null;
            if (rest is ["--silo", var cs])
            {
                siloConnectionString = cs;
            }
            else if (rest.Length > 0)
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
