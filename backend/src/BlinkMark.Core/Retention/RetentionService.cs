using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;

namespace BlinkMark.Core.Retention;

/// <summary>
/// Moves a file's expiry across every store that holds a copy of it (T071).
/// </summary>
/// <remarks>
/// Retention is recorded in three places and they must not disagree: the file document's
/// <c>expiresAt</c>, the Cosmos per-item TTL, and the absolute expiry set on each blob. The blob
/// expiry is the one that actually deletes the content — the platform enforces it and no job has
/// to run for it to happen (Principle II) — so it is the one that matters most.
/// <para>
/// The ordering below is deliberate and is the whole reason this is a service rather than three
/// lines in an endpoint. <strong>Blob expiry is extended before the document is updated.</strong>
/// If the process dies between the two steps, the blobs live slightly longer than the document
/// claims, and the document is what every read path consults — so the file still reads as expired
/// on time, and the reconciliation sweep collects the stragglers. Doing it the other way round
/// produces the failure that Principle II forbids: a document promising the file is alive while
/// the platform has already deleted the bytes.
/// </para>
/// <para>
/// Comment TTLs are handled separately, by the reconciliation job, because a file may have many
/// comments and an extension request should not block on rewriting all of them (research.md R7).
/// Comments expiring slightly before their file is the safe direction of drift: the content
/// outlives its annotations rather than annotations outliving the thing they annotate.
/// </para>
/// </remarks>
public sealed class RetentionService(
    IFileRepository files,
    IBlobFileStore blobs,
    IClock clock)
{
    private readonly IFileRepository _files = files;
    private readonly IBlobFileStore _blobs = blobs;
    private readonly IClock _clock = clock;

    /// <summary>
    /// Moves <paramref name="fileId"/>'s expiry to <paramref name="proposedExpiresAt"/>.
    /// </summary>
    /// <remarks>
    /// Validation happens here <em>and</em> again in the repository. That is not redundant: this
    /// call produces the message the user sees, and the repository check is what makes the
    /// ceiling unbypassable by a future code path that forgets to come through here (FR-028).
    /// </remarks>
    public async Task<RetentionChangeResult> ExtendAsync(
        string fileId,
        DateTimeOffset proposedExpiresAt,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var file = await _files.GetAsync(fileId, cancellationToken).ConfigureAwait(false);

        // An expired file is gone, and a gone file cannot be brought back by extending it
        // (FR-030). Treating this as "not found" keeps that promise honest.
        if (file is null || file.IsExpired(now))
        {
            return RetentionChangeResult.NotFound();
        }

        var validation = RetentionPolicy.Validate(proposedExpiresAt, file.MaxExpiresAt, now);
        if (!validation.IsValid)
        {
            return RetentionChangeResult.Rejected(file, validation);
        }

        var previousExpiresAt = file.ExpiresAt;

        await _blobs.UpdateExpiryAsync(fileId, file.RenderVersion, proposedExpiresAt, cancellationToken)
            .ConfigureAwait(false);

        var updated = await _files.UpdateExpiryAsync(fileId, proposedExpiresAt, cancellationToken)
            .ConfigureAwait(false);

        return RetentionChangeResult.Changed(updated, previousExpiresAt);
    }

    /// <summary>
    /// The Cosmos TTL a file's comments should carry, given the file's current expiry.
    /// </summary>
    /// <remarks>
    /// Exposed so the reconciliation job derives it the same way the write path does, rather than
    /// computing its own and drifting.
    /// </remarks>
    public int CommentTtlSecondsFor(FileRecord file) =>
        RetentionPolicy.TtlSeconds(file.ExpiresAt, _clock.UtcNow);
}

/// <summary>Why a retention change did not happen, or that it did.</summary>
public enum RetentionChangeStatus
{
    Changed,
    Rejected,
    NotFound,
}

/// <summary>The outcome of a retention change.</summary>
public sealed record RetentionChangeResult
{
    public required RetentionChangeStatus Status { get; init; }

    public FileRecord? File { get; init; }

    public DateTimeOffset? PreviousExpiresAt { get; init; }

    public RetentionViolation Violation { get; init; }

    public string? Message { get; init; }

    public static RetentionChangeResult Changed(FileRecord file, DateTimeOffset previousExpiresAt) => new()
    {
        Status = RetentionChangeStatus.Changed,
        File = file,
        PreviousExpiresAt = previousExpiresAt,
        Violation = RetentionViolation.None,
    };

    public static RetentionChangeResult Rejected(FileRecord file, RetentionValidationResult validation) => new()
    {
        Status = RetentionChangeStatus.Rejected,
        File = file,
        Violation = validation.Violation,
        Message = validation.Message,
    };

    public static RetentionChangeResult NotFound() => new()
    {
        Status = RetentionChangeStatus.NotFound,
        Violation = RetentionViolation.None,
    };
}
