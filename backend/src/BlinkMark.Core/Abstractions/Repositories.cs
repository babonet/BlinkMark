using BlinkMark.Core.Models;

namespace BlinkMark.Core.Abstractions;

/// <summary>Reads and writes file documents.</summary>
public interface IFileRepository
{
    /// <summary>Point read by id. The preview hot path (SC-002).</summary>
    Task<FileRecord?> GetAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>Every live file owned by a user, newest first.</summary>
    Task<IReadOnlyList<FileRecord>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>Counts a user's live files, for the quota check (FR-083).</summary>
    Task<int> CountLiveByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);

    Task<FileRecord> CreateAsync(FileRecord file, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a file's expiry.
    /// </summary>
    /// <remarks>
    /// Implementations validate against the stored <c>maxExpiresAt</c> before writing, so the
    /// 30-day ceiling cannot be bypassed by a caller that forgot to check (FR-028).
    /// </remarks>
    Task<FileRecord> UpdateExpiryAsync(string fileId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    Task DeleteAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>Files whose expiry has passed, for the reconciliation sweep.</summary>
    Task<IReadOnlyList<FileRecord>> ListExpiredAsync(int maxItems, CancellationToken cancellationToken = default);

    /// <summary>
    /// Live files whose expiry has been moved past the default, for the comment TTL sweep.
    /// </summary>
    /// <remarks>
    /// Derived from stored state — an expiry later than the default for the file's upload time —
    /// rather than from an "extended" flag. A flag would have to be set by the write path, and
    /// then correctness would depend on that write happening; this way the sweep can be scheduled
    /// blindly (research.md R7).
    /// </remarks>
    Task<IReadOnlyList<FileRecord>> ListExtendedAsync(int maxItems, CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes comments.</summary>
public interface ICommentRepository
{
    /// <summary>Single-partition list for one file (SC-003).</summary>
    Task<IReadOnlyList<Comment>> ListByFileAsync(string fileId, CancellationToken cancellationToken = default);

    Task<Comment?> GetAsync(string fileId, string commentId, CancellationToken cancellationToken = default);

    Task<Comment> CreateAsync(Comment comment, CancellationToken cancellationToken = default);

    Task<Comment> ReplaceAsync(Comment comment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a comment deleted without removing it.
    /// </summary>
    /// <remarks>
    /// Soft, so that replies to a deleted comment survive. Hard-deleting a comment mid-thread
    /// would silently destroy other people's work (FR-025).
    /// </remarks>
    Task<Comment> SoftDeleteAsync(string fileId, string commentId, CancellationToken cancellationToken = default);

    /// <summary>Removes every comment on a file. Used by the reconciliation sweep only.</summary>
    Task<int> DeleteByFileAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>Re-derives comment TTLs after a retention extension (research.md R7).</summary>
    Task<int> SyncTtlAsync(string fileId, int ttlSeconds, CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes user preferences.</summary>
public interface IUserPreferencesRepository
{
    Task<UserPreferences?> GetAsync(string userId, CancellationToken cancellationToken = default);

    Task<UserPreferences> UpsertAsync(UserPreferences preferences, CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes in-app notifications.</summary>
public interface INotificationRepository
{
    Task<NotificationRecord> CreateAsync(NotificationRecord notification, CancellationToken cancellationToken = default);

    /// <summary>The recipient's unread notifications, newest first.</summary>
    Task<IReadOnlyList<NotificationRecord>> ListUnreadAsync(string recipientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds an existing unread notification sharing a coalesce key.
    /// </summary>
    /// <remarks>
    /// This is how FR-038 consolidation happens: a second comment in the same window finds the
    /// first notification and increments it, rather than adding a row.
    /// </remarks>
    Task<NotificationRecord?> FindByCoalesceKeyAsync(string recipientId, string coalesceKey, CancellationToken cancellationToken = default);

    Task<NotificationRecord> ReplaceAsync(NotificationRecord notification, CancellationToken cancellationToken = default);

    Task MarkReadAsync(string recipientId, string notificationId, DateTimeOffset readAt, CancellationToken cancellationToken = default);
}
