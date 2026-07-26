using System.Net;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;
using Microsoft.Azure.Cosmos;

namespace BlinkMark.Infrastructure.Cosmos;

/// <summary>User preferences in Cosmos. Point read by user id, no TTL.</summary>
public sealed class UserPreferencesRepository(CosmosContext context) : IUserPreferencesRepository
{
    private readonly Container _container = context.UserPreferences;

    public async Task<UserPreferences?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _container
                .ReadItemAsync<UserPreferences>(userId, new PartitionKey(userId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return response.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Absent means "never changed anything", which is the default, not an error.
            return null;
        }
    }

    public async Task<UserPreferences> UpsertAsync(
        UserPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        var response = await _container
            .UpsertItemAsync(preferences, new PartitionKey(preferences.Id), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Resource;
    }
}

/// <summary>In-app notifications in Cosmos, partitioned by recipient.</summary>
public sealed class NotificationRepository(CosmosContext context) : INotificationRepository
{
    private readonly Container _container = context.Notifications;

    public async Task<NotificationRecord> CreateAsync(
        NotificationRecord notification,
        CancellationToken cancellationToken = default)
    {
        var response = await _container
            .CreateItemAsync(
                notification,
                new PartitionKey(notification.RecipientId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Resource;
    }

    public async Task<IReadOnlyList<NotificationRecord>> ListUnreadAsync(
        string recipientId,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.recipientId = @recipientId AND c.state = 'Unread' ORDER BY c.createdAt DESC")
            .WithParameter("@recipientId", recipientId);

        return await ReadAllAsync(query, recipientId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationRecord?> FindByCoalesceKeyAsync(
        string recipientId,
        string coalesceKey,
        CancellationToken cancellationToken = default)
    {
        // Unread only. Once someone has read a notification, a new comment deserves a new one
        // rather than silently reviving something they have already dealt with.
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.recipientId = @recipientId AND c.coalesceKey = @coalesceKey AND c.state = 'Unread'")
            .WithParameter("@recipientId", recipientId)
            .WithParameter("@coalesceKey", coalesceKey);

        var results = await ReadAllAsync(query, recipientId, cancellationToken).ConfigureAwait(false);
        return results.Count > 0 ? results[0] : null;
    }

    public async Task<NotificationRecord> ReplaceAsync(
        NotificationRecord notification,
        CancellationToken cancellationToken = default)
    {
        var response = await _container
            .ReplaceItemAsync(
                notification,
                notification.Id,
                new PartitionKey(notification.RecipientId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Resource;
    }

    public async Task MarkReadAsync(
        string recipientId,
        string notificationId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _container
                .PatchItemAsync<NotificationRecord>(
                    notificationId,
                    new PartitionKey(recipientId),
                    [
                        PatchOperation.Set("/state", NotificationState.Read.ToString()),
                        PatchOperation.Set("/readAt", readAt),
                    ],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Already expired with its parent file. Marking a gone notification read is a no-op,
            // not an error.
        }
    }

    private async Task<IReadOnlyList<NotificationRecord>> ReadAllAsync(
        QueryDefinition query,
        string recipientId,
        CancellationToken cancellationToken)
    {
        var results = new List<NotificationRecord>();
        using var iterator = _container.GetItemQueryIterator<NotificationRecord>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(recipientId) });

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            results.AddRange(page);
        }

        return results;
    }
}
