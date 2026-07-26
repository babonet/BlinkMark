using System.Net;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;
using BlinkMark.Core.Retention;
using Microsoft.Azure.Cosmos;

namespace BlinkMark.Infrastructure.Cosmos;

/// <summary>
/// File documents in Cosmos (T016).
/// </summary>
/// <remarks>
/// Two things here are load-bearing rather than incidental.
/// <para>
/// Reads go through <see cref="Container.ReadItemAsync{T}"/> with the id as both key and
/// partition key. That is a point read — the cheapest and lowest-latency Cosmos operation, and
/// the reason SC-002's one-second preview budget is achievable at all.
/// </para>
/// <para>
/// <see cref="UpdateExpiryAsync"/> validates against the file's own stored
/// <c>maxExpiresAt</c> before writing. Putting the check here rather than in the endpoint means
/// a future code path that moves an expiry cannot skip it (FR-028).
/// </para>
/// </remarks>
public sealed class FileRepository(CosmosContext context, IClock clock) : IFileRepository
{
    private readonly Container _container = context.Files;
    private readonly IClock _clock = clock;

    public async Task<FileRecord?> GetAsync(string fileId, CancellationToken cancellationToken = default)
    {
        if (!Identifiers.IsValid(fileId))
        {
            return null;
        }

        try
        {
            var response = await _container
                .ReadItemAsync<FileRecord>(fileId, new PartitionKey(fileId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<FileRecord>> ListByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        // Cross-partition, and deliberately so. The live population is small by construction —
        // a 50-file cap per user, everything expiring continuously — which makes this
        // affordable in serverless, whereas partitioning by owner would have made the preview
        // path a cross-partition read instead. The hot path wins.
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.ownerId = @ownerId AND c.expiresAt > @now ORDER BY c.uploadedAt DESC")
            .WithParameter("@ownerId", ownerId)
            .WithParameter("@now", _clock.UtcNow);

        return await ReadAllAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountLiveByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition(
            "SELECT VALUE COUNT(1) FROM c WHERE c.ownerId = @ownerId AND c.expiresAt > @now")
            .WithParameter("@ownerId", ownerId)
            .WithParameter("@now", _clock.UtcNow);

        using var iterator = _container.GetItemQueryIterator<int>(query);
        var total = 0;
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var value in page)
            {
                total += value;
            }
        }

        return total;
    }

    public async Task<FileRecord> CreateAsync(FileRecord file, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        // The ceiling is validated on creation too, not only on extension. An upload that asked
        // for an unreasonable expiry is the same violation as an extension that did.
        var validation = RetentionPolicy.Validate(file.ExpiresAt, file.MaxExpiresAt, now);
        if (!validation.IsValid)
        {
            throw new RetentionViolationException(validation.Violation, validation.Message!);
        }

        var withTtl = file with { Ttl = RetentionPolicy.TtlSeconds(file.ExpiresAt, now) };

        var response = await _container
            .CreateItemAsync(withTtl, new PartitionKey(withTtl.Id), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Resource;
    }

    public async Task<FileRecord> UpdateExpiryAsync(
        string fileId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(fileId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"File '{fileId}' was not found.");

        var now = _clock.UtcNow;

        // Validated against the value stored at upload, never against a recomputed one. A
        // ceiling recomputed from "now" would let a file be extended forever, 30 days at a time.
        var validation = RetentionPolicy.Validate(expiresAt, existing.MaxExpiresAt, now);
        if (!validation.IsValid)
        {
            throw new RetentionViolationException(validation.Violation, validation.Message!);
        }

        var updated = existing with
        {
            ExpiresAt = expiresAt,
            Ttl = RetentionPolicy.TtlSeconds(expiresAt, now),
        };

        var response = await _container
            .ReplaceItemAsync(updated, fileId, new PartitionKey(fileId), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Resource;
    }

    public async Task DeleteAsync(string fileId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _container
                .DeleteItemAsync<FileRecord>(fileId, new PartitionKey(fileId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone. Deletion is idempotent because the reconciliation job and an owner's
            // explicit delete race by design.
        }
    }

    public async Task<IReadOnlyList<FileRecord>> ListExpiredAsync(
        int maxItems,
        CancellationToken cancellationToken = default)
    {
        // Cosmos hides TTL-expired items from queries before physically removing them, so this
        // returns only the window where expiresAt has passed but the TTL sweep has not yet run.
        // That window is exactly what the reconciliation job exists to clean up in blob storage
        // and in the comments container, neither of which Cosmos cascades to.
        var query = new QueryDefinition(
            "SELECT TOP @limit * FROM c WHERE c.expiresAt <= @now")
            .WithParameter("@limit", maxItems)
            .WithParameter("@now", _clock.UtcNow);

        return await ReadAllAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FileRecord>> ListExtendedAsync(
        int maxItems,
        CancellationToken cancellationToken = default)
    {
        // "Extended" means the expiry sits later than the default would have put it. Comparing
        // against DateTimeAdd rather than storing a flag keeps this true for files extended by
        // any code path, including ones that do not exist yet.
        var query = new QueryDefinition(
            "SELECT TOP @limit * FROM c WHERE c.expiresAt > @now " +
            "AND c.expiresAt > DateTimeAdd('hh', @defaultHours, c.uploadedAt)")
            .WithParameter("@limit", maxItems)
            .WithParameter("@now", _clock.UtcNow)
            .WithParameter("@defaultHours", (int)RetentionPolicy.DefaultLifetime.TotalHours);

        return await ReadAllAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<FileRecord>> ReadAllAsync(
        QueryDefinition query,
        CancellationToken cancellationToken)
    {
        var results = new List<FileRecord>();
        using var iterator = _container.GetItemQueryIterator<FileRecord>(query);

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            results.AddRange(page);
        }

        return results;
    }
}
