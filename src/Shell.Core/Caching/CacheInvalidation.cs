using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Shell.Core.Caching;

/// <summary>Shared cache key formatting so local evictions and <c>LISTEN/NOTIFY</c> handlers target identical keys.</summary>
public static class CacheInvalidationKeys
{
    public static string TenantDomain(string domain) => $"tenant:{domain.Trim().ToLowerInvariant()}";

    public static string TenantId(Guid id) => $"tenant-id:{id}";

    public static string Membership(Guid tenantId, Guid userId) => $"membership:{tenantId}:{userId}";
}

/// <summary>Publishes cross-instance cache invalidation events for tenant metadata and member access.</summary>
public interface ICacheInvalidationBus
{
    Task PublishTenantAsync(string domain, Guid? tenantId = null, CancellationToken ct = default);

    Task PublishMembershipAsync(Guid tenantId, Guid userId, CancellationToken ct = default);
}

internal sealed class PostgresCacheInvalidationBus(
    IOptions<ConnectionStringsOptions> connectionStrings,
    ILogger<PostgresCacheInvalidationBus> logger) : ICacheInvalidationBus
{
    public const string ChannelName = "shell_cache_invalidation";

    public Task PublishTenantAsync(string domain, Guid? tenantId = null, CancellationToken ct = default) =>
        NotifyAsync($"t|{(tenantId?.ToString("N") ?? "")}|{domain.Trim().ToLowerInvariant()}", ct);

    public Task PublishMembershipAsync(Guid tenantId, Guid userId, CancellationToken ct = default) =>
        NotifyAsync($"m|{tenantId:N}|{userId:N}", ct);

    private async Task NotifyAsync(string payload, CancellationToken ct)
    {
        var catalogCs = connectionStrings.Value.Catalog;
        if (string.IsNullOrWhiteSpace(catalogCs))
        {
            return;
        }

        try
        {
            await using var conn = new NpgsqlConnection(catalogCs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand($"SELECT pg_notify('{ChannelName}', @payload);", conn);
            cmd.Parameters.AddWithValue("payload", payload);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish cache invalidation notification ({Payload})", payload);
        }
    }
}

internal sealed class PostgresCacheInvalidationListener(
    IOptions<ConnectionStringsOptions> connectionStrings,
    IMemoryCache cache,
    ILogger<PostgresCacheInvalidationListener> logger) : BackgroundService
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        await Task.WhenAny(_ready.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var catalogCs = connectionStrings.Value.Catalog;
        if (string.IsNullOrWhiteSpace(catalogCs))
        {
            _ready.TrySetResult();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(catalogCs);
                conn.Notification += (_, e) => EvictLocal(e.Payload);
                await conn.OpenAsync(stoppingToken);

                await using (var cmd = new NpgsqlCommand($"LISTEN {PostgresCacheInvalidationBus.ChannelName};", conn))
                {
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                }

                _ready.TrySetResult();
                while (!stoppingToken.IsCancellationRequested)
                {
                    await conn.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _ready.TrySetResult();
                break;
            }
            catch (Exception ex)
            {
                _ready.TrySetResult();
                logger.LogWarning(ex, "Postgres cache invalidation listener disconnected; reconnecting in 2s.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private void EvictLocal(string payload)
    {
        var parts = payload.Split('|');
        switch (parts)
        {
            case ["t", var rawTid, var domain]:
                if (!string.IsNullOrWhiteSpace(domain))
                {
                    cache.Remove(CacheInvalidationKeys.TenantDomain(domain));
                }

                if (Guid.TryParse(rawTid, out var tenantId))
                {
                    cache.Remove(CacheInvalidationKeys.TenantId(tenantId));
                }

                break;

            case ["m", var rawTid, var rawUid] when Guid.TryParse(rawTid, out var tid) && Guid.TryParse(rawUid, out var uid):
                cache.Remove(CacheInvalidationKeys.Membership(tid, uid));
                break;
        }
    }
}
