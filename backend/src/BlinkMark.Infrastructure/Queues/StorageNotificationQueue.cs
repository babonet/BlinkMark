using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using BlinkMark.Core.Abstractions;
using BlinkMark.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlinkMark.Infrastructure.Queues;

/// <summary>
/// The notification queue, backed by Azure Storage Queues.
/// </summary>
/// <remarks>
/// FR-037 requires that a notification failure never affects the user action that triggered it.
/// Enqueue-and-forget is the only shape that guarantees that, and it is why
/// <see cref="EnqueueAsync"/> swallows its own failures: a comment that was successfully created
/// must not be reported as failed because the queue was unreachable.
/// </remarks>
public sealed class StorageNotificationQueue : INotificationQueue
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly QueueClient _queue;
    private readonly ILogger<StorageNotificationQueue> _logger;

    public StorageNotificationQueue(
        QueueServiceClient queueService,
        IOptions<BlinkMarkOptions> options,
        ILogger<StorageNotificationQueue> logger)
    {
        _queue = queueService.GetQueueClient(options.Value.Notifications.QueueName);
        _logger = logger;
    }

    public async Task EnqueueAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(message, SerializerOptions);
            await _queue.SendMessageAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Deliberately not rethrown. The comment is already created and the user is already
            // finished; failing their request now would trade a missed email for lost work.
            _logger.LogError(
                exception,
                "Could not enqueue a notification for comment {CommentId} on file {FileId}. The comment itself was unaffected.",
                message.CommentId,
                message.FileId);
        }
    }

    public async Task<IReadOnlyList<QueuedNotification>> ReceiveAsync(
        int maxMessages,
        CancellationToken cancellationToken = default)
    {
        var response = await _queue.ReceiveMessagesAsync(
            maxMessages,
            visibilityTimeout: TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);

        var results = new List<QueuedNotification>();

        foreach (var raw in response.Value)
        {
            var message = TryDeserialize(raw);
            if (message is null)
            {
                // Unreadable message. Remove it rather than let it block the queue forever.
                await _queue.DeleteMessageAsync(raw.MessageId, raw.PopReceipt, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            results.Add(new QueuedNotification
            {
                Message = message,
                MessageId = raw.MessageId,
                PopReceipt = raw.PopReceipt,
                DequeueCount = raw.DequeueCount,
            });
        }

        return results;
    }

    public async Task CompleteAsync(QueuedNotification message, CancellationToken cancellationToken = default)
    {
        await _queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken)
            .ConfigureAwait(false);
    }

    private NotificationMessage? TryDeserialize(QueueMessage raw)
    {
        try
        {
            return JsonSerializer.Deserialize<NotificationMessage>(raw.Body.ToString(), SerializerOptions);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Discarding an unreadable notification message {MessageId}.", raw.MessageId);
            return null;
        }
    }
}
