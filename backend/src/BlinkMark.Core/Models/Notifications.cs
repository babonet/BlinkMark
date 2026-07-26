using System.Text.Json.Serialization;

namespace BlinkMark.Core.Models;

/// <summary>Why a notification was raised.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotificationTrigger>))]
public enum NotificationTrigger
{
    /// <summary>Someone commented on a file you own.</summary>
    CommentOnOwnedFile,

    /// <summary>Someone replied in a thread you are part of.</summary>
    ReplyInThread,
}

/// <summary>Delivery state.</summary>
/// <remarks>
/// In-app delivery has no "sending" step and no transport that can fail: the record's existence
/// <em>is</em> the delivery (research.md R9). The states below describe what the recipient has done
/// with it, not what a mail server did.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<NotificationState>))]
public enum NotificationState
{
    /// <summary>Visible in the recipient's notification list and not yet opened.</summary>
    Unread,

    Read,

    /// <summary>Not raised on purpose — self-notification, or the recipient opted out.</summary>
    Suppressed,
}

/// <summary>
/// A notification in a user's in-app list. Cosmos container <c>notifications</c>, partition
/// <c>/recipientId</c>.
/// </summary>
/// <remarks>
/// Deliberately carries no channel. Notifications are delivered in-app (research.md R9), and
/// adding email or a Teams activity feed later should extend the dispatcher rather than reshape
/// this record.
/// </remarks>
public sealed record NotificationRecord
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("recipientId")]
    public required string RecipientId { get; init; }

    [JsonPropertyName("fileId")]
    public required string FileId { get; init; }

    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    [JsonPropertyName("triggerKind")]
    public required NotificationTrigger TriggerKind { get; init; }

    /// <summary>
    /// <c>recipientId + fileId + window</c>.
    /// </summary>
    /// <remarks>
    /// Consolidation (FR-038) becomes a grouping operation over this key rather than bespoke
    /// batching logic. Five comments on one file in one window produce one notification because
    /// they share a coalesce key, not because a rule says so.
    /// </remarks>
    [JsonPropertyName("coalesceKey")]
    public required string CoalesceKey { get; init; }

    /// <summary>How many comment events this notification stands for (FR-038).</summary>
    [JsonPropertyName("eventCount")]
    public int EventCount { get; init; } = 1;

    [JsonPropertyName("state")]
    public NotificationState State { get; init; } = NotificationState.Unread;

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("readAt")]
    public DateTimeOffset? ReadAt { get; init; }

    [JsonPropertyName("ttl")]
    public int Ttl { get; init; }
}

/// <summary>
/// Per-user preferences. Cosmos container <c>userPrefs</c>, partition <c>/id</c>. No TTL.
/// </summary>
public sealed record UserPreferences
{
    /// <summary>Entra object id.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>FR-040. Defaults to on, because a review tool nobody hears from is not one.</summary>
    [JsonPropertyName("notificationsEnabled")]
    public bool NotificationsEnabled { get; init; } = true;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    public static UserPreferences Default(string userId, DateTimeOffset now) => new()
    {
        Id = userId,
        NotificationsEnabled = true,
        UpdatedAt = now,
    };
}
