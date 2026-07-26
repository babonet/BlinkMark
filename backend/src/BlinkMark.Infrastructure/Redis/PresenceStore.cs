using System.Text.Json;
using BlinkMark.Core.Abstractions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BlinkMark.Infrastructure.Redis;

/// <summary>
/// Presence state in Redis, with pub/sub fan-out across API replicas (T100).
/// </summary>
/// <remarks>
/// The whole design rests on one idea: <strong>departure is the absence of heartbeats</strong>.
/// Each viewer is a key with a 60-second TTL that their browser refreshes. A clean close, a
/// dropped VPN, a closed laptop, and a force-quit browser are therefore handled identically,
/// because none of them requires anything to work correctly at the moment of failure. That is
/// what makes SC-018 achievable rather than aspirational — there is no disconnect detection to
/// get wrong, no tombstone to write, and no cleanup job to run.
/// <para>
/// Deduplication per person is structural rather than computed: the key is
/// <c>presence:{fileId}:{userId}</c>, so three tabs are one key and "3 viewers" always means
/// three people (FR-063).
/// </para>
/// <para>
/// Nothing here is ever persisted (FR-067), and none of it is the record of who accessed a file
/// (FR-068) — that is the audit trail's job.
/// </para>
/// </remarks>
public sealed class PresenceStore(RedisConnectionProvider connections, ILogger<PresenceStore> logger)
    : IPresenceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly RedisConnectionProvider _connections = connections;
    private readonly ILogger<PresenceStore> _logger = logger;

    public bool IsAvailable => _connections.IsAvailable;

    public async Task HeartbeatAsync(
        string fileId,
        PresenceEntry entry,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var database = await _connections.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (database is null)
        {
            return;
        }

        try
        {
            var key = ViewerKey(fileId, entry.UserId);
            var payload = JsonSerializer.Serialize(entry, SerializerOptions);

            // A plain SET with an expiry. Refreshing the value as well as the TTL keeps a
            // display name change from needing a separate path.
            await database.StringSetAsync(key, payload, ttl).ConfigureAwait(false);

            // The set index makes listing viewers one round trip instead of a KEYS scan, which
            // is O(n) across the whole cache and is never acceptable on a request path.
            await database.SetAddAsync(IndexKey(fileId), entry.UserId).ConfigureAwait(false);
            await database.KeyExpireAsync(IndexKey(fileId), ttl + TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            _logger.LogDebug(exception, "Presence heartbeat for file {FileId} could not be recorded.", fileId);
        }
    }

    public async Task LeaveAsync(string fileId, string userId, CancellationToken cancellationToken = default)
    {
        var database = await _connections.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (database is null)
        {
            return;
        }

        try
        {
            await database.KeyDeleteAsync(ViewerKey(fileId, userId)).ConfigureAwait(false);
            await database.SetRemoveAsync(IndexKey(fileId), userId).ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            _logger.LogDebug(exception, "Presence departure for file {FileId} could not be recorded.", fileId);
        }
    }

    public async Task<IReadOnlyList<PresenceEntry>> ListAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        var database = await _connections.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (database is null)
        {
            // Empty, not an exception. An unavailable presence store means "nobody is shown",
            // which is exactly the graceful degradation FR-069 asks for.
            return [];
        }

        try
        {
            var userIds = await database.SetMembersAsync(IndexKey(fileId)).ConfigureAwait(false);
            if (userIds.Length == 0)
            {
                return [];
            }

            var keys = userIds.Select(id => (RedisKey)ViewerKey(fileId, id!)).ToArray();
            var values = await database.StringGetAsync(keys).ConfigureAwait(false);

            var entries = new List<PresenceEntry>(values.Length);
            var stale = new List<RedisValue>();

            for (var index = 0; index < values.Length; index++)
            {
                if (!values[index].HasValue)
                {
                    // The viewer key expired but the index still names them. Tidy up, because an
                    // index that outlives its entries would slowly inflate the viewer count.
                    stale.Add(userIds[index]);
                    continue;
                }

                var entry = JsonSerializer.Deserialize<PresenceEntry>(values[index].ToString(), SerializerOptions);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            if (stale.Count > 0)
            {
                await database.SetRemoveAsync(IndexKey(fileId), [.. stale]).ConfigureAwait(false);
            }

            return entries.OrderBy(entry => entry.JoinedAt).ToList();
        }
        catch (Exception exception) when (exception is RedisException or JsonException)
        {
            _logger.LogDebug(exception, "Presence for file {FileId} could not be listed.", fileId);
            return [];
        }
    }

    public async Task PublishAsync(string fileId, string eventJson, CancellationToken cancellationToken = default)
    {
        var subscriber = await _connections.GetSubscriberAsync(cancellationToken).ConfigureAwait(false);
        if (subscriber is null)
        {
            return;
        }

        try
        {
            await subscriber.PublishAsync(RedisChannel.Literal(ChannelKey(fileId)), eventJson).ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            _logger.LogDebug(exception, "Presence event for file {FileId} could not be published.", fileId);
        }
    }

    public async Task<IAsyncDisposable> SubscribeAsync(
        string fileId,
        Func<string, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        var subscriber = await _connections.GetSubscriberAsync(cancellationToken).ConfigureAwait(false);
        if (subscriber is null)
        {
            return NullSubscription.Instance;
        }

        var channel = RedisChannel.Literal(ChannelKey(fileId));
        var queue = await subscriber.SubscribeAsync(channel).ConfigureAwait(false);

        queue.OnMessage(async message =>
        {
            try
            {
                await onEvent(message.Message.ToString(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A failing subscriber must not take the connection down with it.
                _logger.LogDebug(exception, "A presence subscriber for file {FileId} threw.", fileId);
            }
        });

        return new RedisSubscription(subscriber, channel);
    }

    private static string ViewerKey(string fileId, string userId) => $"presence:{fileId}:{userId}";

    private static string IndexKey(string fileId) => $"presence:{fileId}:viewers";

    private static string ChannelKey(string fileId) => $"presence:{fileId}";

    private sealed class RedisSubscription(ISubscriber subscriber, RedisChannel channel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await subscriber.UnsubscribeAsync(channel).ConfigureAwait(false);
            }
            catch (RedisException)
            {
                // The connection has already gone. Nothing left to unsubscribe from.
            }
        }
    }

    private sealed class NullSubscription : IAsyncDisposable
    {
        public static readonly NullSubscription Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
