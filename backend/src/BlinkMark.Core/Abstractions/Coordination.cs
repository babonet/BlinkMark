namespace BlinkMark.Core.Abstractions;

/// <summary>One person currently viewing a file.</summary>
public sealed record PresenceEntry
{
    public required string UserId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Set when the viewer is an agent working for this user.</summary>
    public string? ActingAgentId { get; init; }

    public required DateTimeOffset JoinedAt { get; init; }
}

/// <summary>
/// Transient presence state, shared across API replicas.
/// </summary>
/// <remarks>
/// Backed by Redis with a per-viewer TTL. The TTL <em>is</em> the departure mechanism: a clean
/// close, a dropped VPN, and a force-quit browser are all handled identically because none of
/// them has to do anything correctly at the moment of failure (FR-062, SC-018).
/// <para>
/// Nothing here is ever persisted (FR-067), and presence is never the record of who accessed a
/// file — that is what the audit trail is for (FR-068).
/// </para>
/// </remarks>
public interface IPresenceStore
{
    /// <summary>Records or refreshes a viewer. Called on join and on each heartbeat.</summary>
    Task HeartbeatAsync(string fileId, PresenceEntry entry, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Removes a viewer on a clean departure. Expiry covers every other case.</summary>
    Task LeaveAsync(string fileId, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PresenceEntry>> ListAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>Publishes a change to every replica.</summary>
    Task PublishAsync(string fileId, string eventJson, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to changes for one file.</summary>
    Task<IAsyncDisposable> SubscribeAsync(
        string fileId,
        Func<string, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the backing store is reachable.
    /// </summary>
    /// <remarks>
    /// Callers use this to degrade quietly rather than to fail. FR-069 requires preview and
    /// commenting to be unaffected when presence is unavailable, which means presence must be
    /// able to say "not right now" without anything upstream treating that as an error.
    /// </remarks>
    bool IsAvailable { get; }
}

/// <summary>Fixed-window counters shared across replicas.</summary>
/// <remarks>
/// A rate limiter in replica memory is both wrong under scale-out and prohibited by the
/// constitution's stateless requirement — five replicas would grant five times the limit.
/// </remarks>
public interface IRateLimitStore
{
    /// <summary>Increments a counter and returns its new value, setting the window on first use.</summary>
    Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default);

    Task<long> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);

    Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    bool IsAvailable { get; }
}
