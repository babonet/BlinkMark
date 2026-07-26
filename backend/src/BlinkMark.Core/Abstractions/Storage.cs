using BlinkMark.Core.Models;

namespace BlinkMark.Core.Abstractions;

/// <summary>Which stored artifact a blob operation refers to.</summary>
public enum BlobArtifact
{
    /// <summary>The bytes exactly as uploaded. Never served to a browser.</summary>
    Original,

    /// <summary>The sanitized HTML render. The only thing the preview origin serves.</summary>
    Render,

    /// <summary>
    /// The normalized text projection.
    /// </summary>
    /// <remarks>
    /// Anchors resolve against this and agents read it. If the two diverged, an agent could
    /// comment on text no human ever saw.
    /// </remarks>
    Projection,
}

/// <summary>Stores file content with platform-enforced expiry.</summary>
public interface IBlobFileStore
{
    /// <summary>
    /// Writes an artifact and sets its absolute expiry.
    /// </summary>
    /// <remarks>
    /// Expiry is set on the blob itself, in absolute mode, so the platform performs the deletion.
    /// A cleanup job that has to run for content to disappear is a cleanup job that can fail
    /// silently, which is what Principle II exists to avoid.
    /// </remarks>
    Task WriteAsync(
        string fileId,
        BlobArtifact artifact,
        Stream content,
        string contentType,
        DateTimeOffset expiresAt,
        string? renderVersion = null,
        CancellationToken cancellationToken = default);

    Task<Stream?> ReadAsync(
        string fileId,
        BlobArtifact artifact,
        string? renderVersion = null,
        CancellationToken cancellationToken = default);

    Task<string?> ReadTextAsync(
        string fileId,
        BlobArtifact artifact,
        string? renderVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>Moves the expiry of every artifact belonging to a file.</summary>
    Task UpdateExpiryAsync(
        string fileId,
        string renderVersion,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes every artifact belonging to a file, physically.</summary>
    Task DeleteAllAsync(string fileId, string renderVersion, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string fileId,
        BlobArtifact artifact,
        string? renderVersion = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Appends to the audit trail.
/// </summary>
/// <remarks>
/// This interface has exactly one method, and that is the design (FR-044). There is deliberately
/// no update, no delete, and no upsert — not because nobody would call them, but so that nobody
/// can. A test asserts that no such member is ever added.
/// </remarks>
public interface IAuditStore
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>Enqueues notification work.</summary>
/// <remarks>
/// Enqueue-and-forget is the only shape that satisfies FR-037: a comment must succeed whether or
/// not anyone can be told about it.
/// </remarks>
public interface INotificationQueue
{
    Task EnqueueAsync(NotificationMessage message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QueuedNotification>> ReceiveAsync(int maxMessages, CancellationToken cancellationToken = default);

    Task CompleteAsync(QueuedNotification message, CancellationToken cancellationToken = default);
}

/// <summary>The payload placed on the notification queue.</summary>
public sealed record NotificationMessage
{
    public required string FileId { get; init; }

    public required string CommentId { get; init; }

    public required string ThreadId { get; init; }

    public required string ActorId { get; init; }

    public required string ActorDisplayName { get; init; }

    public string? ActingAgentId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string CorrelationId { get; init; }
}

/// <summary>A received queue message together with what the queue needs to delete it.</summary>
public sealed record QueuedNotification
{
    public required NotificationMessage Message { get; init; }

    public required string MessageId { get; init; }

    public required string PopReceipt { get; init; }

    public required long DequeueCount { get; init; }
}

/// <summary>Signs data with a key that never leaves its custody.</summary>
/// <remarks>
/// The distinction between "sign with this key" and "give me this key" is the whole of
/// Principle VII on this path. The application can produce signatures and cannot produce the
/// key, so a memory dump of the application tier yields nothing worth having.
/// </remarks>
public interface ITokenSigner
{
    /// <summary>The JOSE algorithm identifier the signatures use, for example <c>ES256</c>.</summary>
    string Algorithm { get; }

    /// <summary>Key identifier to publish in the token header.</summary>
    Task<string> GetKeyIdAsync(CancellationToken cancellationToken = default);

    Task<byte[]> SignAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(ReadOnlyMemory<byte> payload, ReadOnlyMemory<byte> signature, CancellationToken cancellationToken = default);
}
