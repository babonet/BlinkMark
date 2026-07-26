using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;

namespace BlinkMark.TestHost.Fakes;

/// <summary>
/// In-memory blob storage that models expiry the way the platform does.
/// </summary>
/// <remarks>
/// <see cref="ExistsAsync"/> and <see cref="ReadAsync"/> honour the scheduled deletion time
/// rather than only the presence of bytes. That is what lets a retention test assert physical
/// deletion instead of merely API-level absence — the distinction SC-006 turns on, and the one a
/// naive fake would quietly erase.
/// </remarks>
public sealed class InMemoryBlobFileStore(IClock clock) : IBlobFileStore
{
    private readonly ConcurrentDictionary<string, StoredBlob> _blobs = new(StringComparer.Ordinal);
    private readonly IClock _clock = clock;

    /// <summary>Every stored key, including any whose expiry has passed but which still exists.</summary>
    public IReadOnlyCollection<string> Keys => _blobs.Keys.ToList();

    /// <summary>
    /// True when bytes are physically present, whatever their expiry says.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="ExistsAsync"/>. A test that wants to prove content
    /// was actually removed must be able to look past the "reads as gone" behaviour.
    /// </remarks>
    public bool PhysicallyExists(string fileId, BlobArtifact artifact, string? renderVersion = null) =>
        _blobs.ContainsKey(Key(fileId, artifact, renderVersion));

    /// <summary>
    /// Replaces a file's text projection, simulating the document being edited underneath its
    /// comments.
    /// </summary>
    /// <remarks>
    /// Matches on file id alone rather than on render version, because a test that wants to
    /// orphan an anchor should not also have to know how the render version was composed.
    /// </remarks>
    public void OverwriteProjection(string fileId, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var prefix = $"projections/{fileId}/";

        foreach (var key in _blobs.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _blobs[key] = _blobs[key] with { Content = bytes };
        }
    }

    public Task WriteAsync(
        string fileId,
        BlobArtifact artifact,
        Stream content,
        string contentType,
        DateTimeOffset expiresAt,
        string? renderVersion = null,
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        _blobs[Key(fileId, artifact, renderVersion)] = new StoredBlob
        {
            Content = buffer.ToArray(),
            ContentType = contentType,
            ExpiresAt = expiresAt,
        };

        return Task.CompletedTask;
    }

    public Task<Stream?> ReadAsync(
        string fileId,
        BlobArtifact artifact,
        string? renderVersion = null,
        CancellationToken cancellationToken = default)
    {
        var blob = Resolve(fileId, artifact, renderVersion);
        return Task.FromResult<Stream?>(blob is null ? null : new MemoryStream(blob.Content, writable: false));
    }

    public Task<string?> ReadTextAsync(
        string fileId,
        BlobArtifact artifact,
        string? renderVersion = null,
        CancellationToken cancellationToken = default)
    {
        var blob = Resolve(fileId, artifact, renderVersion);
        return Task.FromResult(blob is null ? null : Encoding.UTF8.GetString(blob.Content));
    }

    public Task UpdateExpiryAsync(
        string fileId,
        string renderVersion,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        foreach (var key in EnumerateKeys(fileId, renderVersion))
        {
            if (_blobs.TryGetValue(key, out var blob))
            {
                _blobs[key] = blob with { ExpiresAt = expiresAt };
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAllAsync(
        string fileId,
        string renderVersion,
        CancellationToken cancellationToken = default)
    {
        foreach (var key in EnumerateKeys(fileId, renderVersion))
        {
            _blobs.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(
        string fileId,
        BlobArtifact artifact,
        string? renderVersion = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(fileId, artifact, renderVersion) is not null);

    /// <summary>Simulates the platform sweeping everything whose expiry has passed.</summary>
    public void RunPlatformExpirySweep()
    {
        var now = _clock.UtcNow;
        foreach (var (key, blob) in _blobs)
        {
            if (blob.ExpiresAt <= now)
            {
                _blobs.TryRemove(key, out _);
            }
        }
    }

    private StoredBlob? Resolve(string fileId, BlobArtifact artifact, string? renderVersion)
    {
        if (!_blobs.TryGetValue(Key(fileId, artifact, renderVersion), out var blob))
        {
            return null;
        }

        // Expired content reads as absent even before the sweep removes it (FR-032).
        return blob.ExpiresAt <= _clock.UtcNow ? null : blob;
    }

    private static IEnumerable<string> EnumerateKeys(string fileId, string renderVersion)
    {
        yield return Key(fileId, BlobArtifact.Original, null);
        yield return Key(fileId, BlobArtifact.Render, renderVersion);
        yield return Key(fileId, BlobArtifact.Projection, renderVersion);
    }

    private static string Key(string fileId, BlobArtifact artifact, string? renderVersion) =>
        artifact switch
        {
            BlobArtifact.Original => $"originals/{fileId}",
            BlobArtifact.Render => $"renders/{fileId}/{renderVersion}.html",
            BlobArtifact.Projection => $"projections/{fileId}/{renderVersion}.txt",
            _ => throw new ArgumentOutOfRangeException(nameof(artifact)),
        };

    private sealed record StoredBlob
    {
        public required byte[] Content { get; init; }

        public required string ContentType { get; init; }

        public required DateTimeOffset ExpiresAt { get; init; }
    }
}

/// <summary>
/// In-memory audit trail.
/// </summary>
/// <remarks>
/// Like the real one, it can only append. A fake that offered a way to remove an entry would let
/// a test pass while the property it is meant to prove — that the trail cannot be rewritten —
/// quietly did not hold.
/// </remarks>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly ConcurrentBag<AuditEntry> _entries = [];

    public IReadOnlyCollection<AuditEntry> Entries => _entries.ToList();

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public IEnumerable<AuditEntry> For(AuditAction action) => Entries.Where(entry => entry.Action == action);

    public IEnumerable<AuditEntry> ForTarget(string targetId) =>
        Entries.Where(entry => entry.TargetId == targetId);
}

/// <summary>In-memory rate-limit and cache store.</summary>
public sealed class InMemoryRateLimitStore(IClock clock) : IRateLimitStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IClock _clock = clock;

    /// <summary>Set to false to simulate a Redis outage and prove the product degrades quietly.</summary>
    public bool IsAvailable { get; set; } = true;

    public Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return Task.FromResult(0L);
        }

        var entry = _entries.AddOrUpdate(
            key,
            _ => new Entry { Count = 1, ExpiresAt = _clock.UtcNow + window },
            (_, existing) => existing.ExpiresAt <= _clock.UtcNow
                ? new Entry { Count = 1, ExpiresAt = _clock.UtcNow + window }
                : existing with { Count = existing.Count + 1 });

        return Task.FromResult(entry.Count);
    }

    public Task<long> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || !_entries.TryGetValue(key, out var entry) || entry.ExpiresAt <= _clock.UtcNow)
        {
            return Task.FromResult(0L);
        }

        return Task.FromResult(entry.Count);
    }

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (IsAvailable)
        {
            _entries[key] = new Entry { Count = 0, Value = value, ExpiresAt = _clock.UtcNow + ttl };
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || !_entries.TryGetValue(key, out var entry) || entry.ExpiresAt <= _clock.UtcNow)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(entry.Value);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private sealed record Entry
    {
        public long Count { get; init; }

        public string? Value { get; init; }

        public required DateTimeOffset ExpiresAt { get; init; }
    }
}

/// <summary>In-memory notification queue.</summary>
public sealed class InMemoryNotificationQueue : INotificationQueue
{
    private readonly ConcurrentQueue<QueuedNotification> _messages = new();

    /// <summary>
    /// Set to true to simulate the notification channel being unavailable.
    /// </summary>
    /// <remarks>
    /// FR-037 says a notification failure must never affect the action that triggered it, and the
    /// only way to prove that is to break the queue and confirm the comment still succeeds.
    /// </remarks>
    public bool ThrowOnEnqueue { get; set; }

    public IReadOnlyCollection<NotificationMessage> Enqueued =>
        _messages.Select(message => message.Message).ToList();

    public Task EnqueueAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        if (ThrowOnEnqueue)
        {
            throw new InvalidOperationException("Simulated notification channel failure.");
        }

        _messages.Enqueue(new QueuedNotification
        {
            Message = message,
            MessageId = Identifiers.New(),
            PopReceipt = "test",
            DequeueCount = 1,
        });

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueuedNotification>> ReceiveAsync(
        int maxMessages,
        CancellationToken cancellationToken = default)
    {
        var received = new List<QueuedNotification>();
        while (received.Count < maxMessages && _messages.TryDequeue(out var message))
        {
            received.Add(message);
        }

        return Task.FromResult<IReadOnlyList<QueuedNotification>>(received);
    }

    public Task CompleteAsync(QueuedNotification message, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>In-memory presence store.</summary>
public sealed class InMemoryPresenceStore(IClock clock) : IPresenceStore
{
    private readonly ConcurrentDictionary<string, (PresenceEntry Entry, DateTimeOffset ExpiresAt)> _viewers =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, List<Func<string, CancellationToken, Task>>> _subscribers =
        new(StringComparer.Ordinal);

    private readonly IClock _clock = clock;

    /// <summary>Set to false to prove preview and commenting are unaffected (FR-069).</summary>
    public bool IsAvailable { get; set; } = true;

    public Task HeartbeatAsync(
        string fileId,
        PresenceEntry entry,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        if (IsAvailable)
        {
            _viewers[$"{fileId}:{entry.UserId}"] = (entry, _clock.UtcNow + ttl);
        }

        return Task.CompletedTask;
    }

    public Task LeaveAsync(string fileId, string userId, CancellationToken cancellationToken = default)
    {
        _viewers.TryRemove($"{fileId}:{userId}", out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PresenceEntry>> ListAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return Task.FromResult<IReadOnlyList<PresenceEntry>>([]);
        }

        var now = _clock.UtcNow;
        var prefix = $"{fileId}:";

        IReadOnlyList<PresenceEntry> results = _viewers
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Value.ExpiresAt > now)
            .Select(pair => pair.Value.Entry)
            .OrderBy(entry => entry.JoinedAt)
            .ToList();

        return Task.FromResult(results);
    }

    public async Task PublishAsync(
        string fileId,
        string eventJson,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || !_subscribers.TryGetValue(fileId, out var handlers))
        {
            return;
        }

        foreach (var handler in handlers.ToList())
        {
            await handler(eventJson, cancellationToken);
        }
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        string fileId,
        Func<string, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        var handlers = _subscribers.GetOrAdd(fileId, _ => []);
        lock (handlers)
        {
            handlers.Add(onEvent);
        }

        return Task.FromResult<IAsyncDisposable>(new Subscription(handlers, onEvent));
    }

    private sealed class Subscription(
        List<Func<string, CancellationToken, Task>> handlers,
        Func<string, CancellationToken, Task> handler) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (handlers)
            {
                handlers.Remove(handler);
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A test signer.
/// </summary>
/// <remarks>
/// HMAC over an ephemeral key rather than ECDSA in Key Vault. The token <em>shape</em> is
/// identical, which is what the preview-origin contract tests exercise; where the signature comes
/// from is not something those tests are trying to prove.
/// </remarks>
public sealed class TestTokenSigner : ITokenSigner
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public string Algorithm => "ES256";

    public Task<string> GetKeyIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("test-key");

    public Task<byte[]> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        Task.FromResult(HMACSHA256.HashData(_key, payload.Span));

    public Task<bool> VerifyAsync(
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte> signature,
        CancellationToken cancellationToken = default)
    {
        var expected = HMACSHA256.HashData(_key, payload.Span);
        return Task.FromResult(CryptographicOperations.FixedTimeEquals(expected, signature.Span));
    }
}
