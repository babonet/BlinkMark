using System.Net;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;
using Microsoft.Azure.Cosmos;

namespace BlinkMark.Infrastructure.Cosmos;

/// <summary>
/// Comment documents in Cosmos (T016, T055).
/// </summary>
/// <remarks>
/// Partitioned by <c>fileId</c>, so listing a file's comments is a single-partition query — the
/// operation SC-003 puts a 300 ms budget on.
/// <para>
/// Comment TTLs are derived from the parent file's expiry at creation. Cosmos has no cascading
/// delete, so on a retention extension the comments' TTLs have to be moved too; that happens in
/// the reconciliation job rather than synchronously, because a file may have many comments and
/// FR-027 must not turn into a slow operation. A stale comment TTL can never surface content
/// that should be gone, because the read path validates the parent file first.
/// </para>
/// </remarks>
public sealed class CommentRepository(CosmosContext context, IClock clock) : ICommentRepository
{
    private readonly Container _container = context.Comments;
    private readonly IClock _clock = clock;

    public async Task<IReadOnlyList<Comment>> ListByFileAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        if (!Identifiers.IsValid(fileId))
        {
            return [];
        }

        var query = new QueryDefinition("SELECT * FROM c WHERE c.fileId = @fileId ORDER BY c.createdAt ASC")
            .WithParameter("@fileId", fileId);

        var results = new List<Comment>();
        using var iterator = _container.GetItemQueryIterator<Comment>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(fileId) });

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            results.AddRange(page);
        }

        return results;
    }

    public async Task<Comment?> GetAsync(
        string fileId,
        string commentId,
        CancellationToken cancellationToken = default)
    {
        if (!Identifiers.IsValid(fileId) || !Identifiers.IsValid(commentId))
        {
            return null;
        }

        try
        {
            var response = await _container
                .ReadItemAsync<Comment>(commentId, new PartitionKey(fileId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Comment> CreateAsync(Comment comment, CancellationToken cancellationToken = default)
    {
        var response = await _container
            .CreateItemAsync(comment, new PartitionKey(comment.FileId), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Resource;
    }

    public async Task<Comment> ReplaceAsync(Comment comment, CancellationToken cancellationToken = default)
    {
        var response = await _container
            .ReplaceItemAsync(
                comment,
                comment.Id,
                new PartitionKey(comment.FileId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Resource;
    }

    public async Task<int> DeleteByFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var comments = await ListByFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        var deleted = 0;

        foreach (var comment in comments)
        {
            try
            {
                await _container
                    .DeleteItemAsync<Comment>(
                        comment.Id,
                        new PartitionKey(fileId),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                deleted++;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                // Already swept by TTL.
            }
        }

        return deleted;
    }

    public async Task<int> SyncTtlAsync(
        string fileId,
        int ttlSeconds,
        CancellationToken cancellationToken = default)
    {
        var comments = await ListByFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        var updated = 0;

        foreach (var comment in comments)
        {
            if (comment.Ttl == ttlSeconds)
            {
                continue;
            }

            // A patch rather than a replace: it moves one field and cannot accidentally
            // overwrite a body or an anchor written concurrently.
            await _container
                .PatchItemAsync<Comment>(
                    comment.Id,
                    new PartitionKey(fileId),
                    [PatchOperation.Set("/ttl", ttlSeconds)],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            updated++;
        }

        return updated;
    }

    /// <summary>Marks a comment deleted without removing it, so replies survive (FR-025).</summary>
    public async Task<Comment> SoftDeleteAsync(
        string fileId,
        string commentId,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(fileId, commentId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Comment '{commentId}' was not found.");

        var deleted = existing with
        {
            DeletedAt = _clock.UtcNow,
            // The body goes with it. A "deleted" comment whose text is still readable through the
            // API is not deleted, it is merely hidden.
            Body = string.Empty,
        };

        return await ReplaceAsync(deleted, cancellationToken).ConfigureAwait(false);
    }
}
