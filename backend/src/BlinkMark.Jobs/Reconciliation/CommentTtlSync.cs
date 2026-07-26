using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Retention;
using Microsoft.Extensions.Logging;

namespace BlinkMark.Jobs.Reconciliation;

/// <summary>
/// Re-derives comment TTLs after a retention extension (T072, research.md R7).
/// </summary>
/// <remarks>
/// A comment's Cosmos TTL is computed from its file's expiry at the moment the comment is
/// written. Extending the file's retention therefore leaves every existing comment on a TTL that
/// is now too short — the file would outlive its own review thread.
/// <para>
/// This runs out of band rather than inside the extension request on purpose. A file can carry
/// hundreds of comments, and rewriting all of them synchronously would make the extension
/// endpoint's latency a function of how much discussion a document attracted. The drift it leaves
/// in the meantime is in the safe direction: comments expiring slightly early is a smaller
/// failure than annotations surviving the content they annotate, and the window is minutes.
/// </para>
/// <para>
/// Deliberately idempotent. It computes the correct TTL from current state rather than applying a
/// delta, so running it twice, or running it on a file that was never extended, changes nothing.
/// That is what lets it be scheduled blindly instead of triggered from the write path, which
/// would make correctness depend on a message being delivered.
/// </para>
/// </remarks>
public sealed class CommentTtlSync(
    IFileRepository files,
    ICommentRepository comments,
    RetentionService retention,
    ILogger<CommentTtlSync> logger)
{
    private readonly IFileRepository _files = files;
    private readonly ICommentRepository _comments = comments;
    private readonly RetentionService _retention = retention;
    private readonly ILogger<CommentTtlSync> _logger = logger;

    /// <summary>Synchronizes comment TTLs for one file.</summary>
    public async Task<int> SyncAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var file = await _files.GetAsync(fileId, cancellationToken).ConfigureAwait(false);

        // The file is gone. Its comments carry TTLs of their own and will expire on their own;
        // the reconciliation sweep removes any that outlast it.
        if (file is null)
        {
            return 0;
        }

        var ttlSeconds = _retention.CommentTtlSecondsFor(file);
        var updated = await _comments.SyncTtlAsync(file.Id, ttlSeconds, cancellationToken)
            .ConfigureAwait(false);

        if (updated > 0)
        {
            // File id only. Comment bodies never reach a log (FR-045).
            _logger.LogInformation(
                "Synchronized TTL for {CommentCount} comments on file {FileId} to {TtlSeconds}s.",
                updated,
                file.Id,
                ttlSeconds);
        }

        return updated;
    }

    /// <summary>Synchronizes comment TTLs for every file whose expiry has moved.</summary>
    public async Task<int> SyncExtendedAsync(int maxFiles, CancellationToken cancellationToken = default)
    {
        var total = 0;

        foreach (var file in await _files.ListExtendedAsync(maxFiles, cancellationToken).ConfigureAwait(false))
        {
            total += await SyncAsync(file.Id, cancellationToken).ConfigureAwait(false);
        }

        return total;
    }
}
