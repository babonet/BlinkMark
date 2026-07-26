using BlinkMark.Core.Abstractions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BlinkMark.Infrastructure.Redis;

/// <summary>
/// Fixed-window counters and short-lived cache entries in Redis (T019).
/// </summary>
/// <remarks>
/// Rate limits (FR-054, FR-085) and quota counters (FR-083) need state shared across API
/// replicas. Holding either in process would be wrong under scale-out — five replicas would
/// each grant the full limit — and is separately forbidden by the constitution's stateless
/// requirement.
/// <para>
/// Every method degrades rather than throws. A Redis outage must not stop people uploading;
/// it means the limiter is temporarily unenforced, and the quota is confirmed against Cosmos
/// before an upload commits anyway, which is why Cosmos is the authority and this is the cache.
/// </para>
/// </remarks>
public sealed class RedisStore(RedisConnectionProvider connections, ILogger<RedisStore> logger) : IRateLimitStore
{
    private readonly RedisConnectionProvider _connections = connections;
    private readonly ILogger<RedisStore> _logger = logger;

    public bool IsAvailable => _connections.IsAvailable;

    public async Task<long> IncrementAsync(
        string key,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var database = await _connections.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (database is null)
        {
            // Zero reads as "no requests counted", which fails open. That is the correct trade
            // here: a brief window of unenforced rate limiting is a smaller harm than refusing
            // every upload in the tenant because a cache restarted.
            _logger.LogDebug("Rate limit counter '{Key}' skipped: Redis unavailable.", key);
            return 0;
        }

        try
        {
            var value = await database.StringIncrementAsync(key).ConfigureAwait(false);

            // Only the first increment in a window sets the expiry, so the window is fixed from
            // its first request rather than sliding forward with every one.
            if (value == 1)
            {
                await database.KeyExpireAsync(key, window).ConfigureAwait(false);
            }

            return value;
        }
        catch (RedisException exception)
        {
            _logger.LogWarning(exception, "Rate limit counter '{Key}' could not be incremented.", key);
            return 0;
        }
    }

    public async Task<long> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var database = await _connections.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (database is null)
        {
            return 0;
        }

        try
        {
            var value = await database.StringGetAsync(key).ConfigureAwait(false);
            return value.TryParse(out long parsed) ? parsed : 0;
        }
        catch (RedisException exception)
        {
            _logger.LogWarning(exception, "Rate limit counter '{Key}' could not be read.", key);
            return 0;
        }
    }

    public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var database = await _connections.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (database is null)
        {
            return;
        }

        try
        {
            await database.StringSetAsync(key, value, ttl).ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            _logger.LogWarning(exception, "Cache entry '{Key}' could not be written.", key);
        }
    }

    public async Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        var database = await _connections.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (database is null)
        {
            return null;
        }

        try
        {
            var value = await database.StringGetAsync(key).ConfigureAwait(false);
            return value.HasValue ? value.ToString() : null;
        }
        catch (RedisException exception)
        {
            _logger.LogWarning(exception, "Cache entry '{Key}' could not be read.", key);
            return null;
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var database = await _connections.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (database is null)
        {
            return;
        }

        try
        {
            await database.KeyDeleteAsync(key).ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            _logger.LogWarning(exception, "Cache entry '{Key}' could not be removed.", key);
        }
    }
}
