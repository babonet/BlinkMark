using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlinkMark.Jobs.Reconciliation;

/// <summary>
/// The retention backstop (T044).
/// </summary>
/// <remarks>
/// Blob expiry and Cosmos TTL do the real deleting, and they do it without this job running. So
/// what is left for it?
/// <para>
/// Three things the platform does not cascade. Comments belong to a different Cosmos container
/// than the file they hang off, and Cosmos has no cascading delete. Blob artifacts and the file
/// document expire on separate clocks and can drift apart. And there is a window between
/// <c>expiresAt</c> passing — at which point the file already reads as gone (FR-032) — and the
/// platform physically removing it.
/// <para>
/// Closing that window is what makes SC-006 defensible. "Deleted" has to mean the bytes are
/// gone, not that the API stopped admitting they exist; a file hidden from the API but still
/// sitting in blob storage fails the entire compliance premise.
/// </para>
/// </para>
/// </remarks>
public sealed class RetentionReconciler(
    IFileRepository files,
    ICommentRepository comments,
    IBlobFileStore blobs,
    IAuditStore audit,
    IClock clock,
    CommentTtlSync commentTtlSync,
    IHostApplicationLifetime lifetime,
    ILogger<RetentionReconciler> logger) : BackgroundService
{
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Retention reconciliation starting.");

        try
        {
            var expired = await files.ListExpiredAsync(BatchSize, stoppingToken).ConfigureAwait(false);
            logger.LogInformation("Found {Count} expired file(s) to purge.", expired.Count);

            foreach (var file in expired)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                await PurgeAsync(file, stoppingToken).ConfigureAwait(false);
            }

            // Extended files last. Purging is the time-critical half of this job — content that
            // should be gone staying reachable is a Principle II failure — whereas a stale comment
            // TTL only risks a comment expiring early. If the run is cut short, it is cut short
            // here rather than before the purge.
            var synced = await commentTtlSync.SyncExtendedAsync(BatchSize, stoppingToken).ConfigureAwait(false);
            logger.LogInformation("Synchronized TTL on {Count} comment(s) of extended files.", synced);

            logger.LogInformation("Retention reconciliation finished.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Retention reconciliation failed. The next scheduled run will retry.");
        }
        finally
        {
            // A cron job that never exits is a cron job that runs forever and bills forever.
            lifetime.StopApplication();
        }
    }

    private async Task PurgeAsync(FileRecord file, CancellationToken cancellationToken)
    {
        try
        {
            // Comments first. If the run dies midway, an orphaned comment set is harmless — the
            // read path validates the parent file, so it can never surface. An orphaned *blob*
            // after the document is gone is not harmless: nothing would ever look for it again.
            var deletedComments = await comments.DeleteByFileAsync(file.Id, cancellationToken)
                .ConfigureAwait(false);

            await blobs.DeleteAllAsync(file.Id, file.RenderVersion, cancellationToken).ConfigureAwait(false);
            await files.DeleteAsync(file.Id, cancellationToken).ConfigureAwait(false);

            // The audit entry is the one thing that survives. Deleting a file removes its blobs,
            // its document, and all its comments — and never its audit trail (FR-031, FR-043).
            await audit.AppendAsync(
                AuditEntry.Create(
                    AuditAction.FileExpire,
                    AuditTargetType.File,
                    file.Id,
                    file.OwnerId,
                    file.OwnerDisplayName,
                    correlationId: $"reconcile-{Identifiers.New()}",
                    clock.UtcNow),
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Purged file {FileId} and {CommentCount} comment(s) after expiry at {ExpiresAt}.",
                file.Id,
                deletedComments,
                file.ExpiresAt);
        }
        catch (Exception exception)
        {
            // One bad file must not stop the sweep. The next run picks it up again, because the
            // file is still expired and still listed.
            logger.LogError(exception, "Could not purge expired file {FileId}.", file.Id);
        }
    }
}
