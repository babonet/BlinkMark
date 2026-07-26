using System.Collections.Concurrent;
using BlinkMark.Core.Abstractions;
using BlinkMark.Core.Models;
using BlinkMark.Core.Retention;

namespace BlinkMark.TestHost.Fakes;

/// <summary>
/// An in-memory file repository that keeps the rules the real one keeps.
/// </summary>
/// <remarks>
/// The retention validation below is not scaffolding — it is the point. If the fake accepted an
/// expiry the real repository would refuse, the mandatory retention tests would pass against
/// behaviour that does not exist in production, which is worse than having no tests.
/// </remarks>
public sealed class InMemoryFileRepository(IClock clock) : IFileRepository
{
    private readonly ConcurrentDictionary<string, FileRecord> _files = new(StringComparer.Ordinal);
    private readonly IClock _clock = clock;

    public IReadOnlyCollection<FileRecord> All => _files.Values.ToList();

    public Task<FileRecord?> GetAsync(string fileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_files.TryGetValue(fileId, out var file) ? file : null);

    public Task<IReadOnlyList<FileRecord>> ListByOwnerAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        IReadOnlyList<FileRecord> results = _files.Values
            .Where(file => file.OwnerId == ownerId && !file.IsExpired(now))
            .OrderByDescending(file => file.UploadedAt)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<int> CountLiveByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        return Task.FromResult(_files.Values.Count(file => file.OwnerId == ownerId && !file.IsExpired(now)));
    }

    public Task<FileRecord> CreateAsync(FileRecord file, CancellationToken cancellationToken = default)
    {
        var validation = RetentionPolicy.Validate(file.ExpiresAt, file.MaxExpiresAt, _clock.UtcNow);
        if (!validation.IsValid)
        {
            throw new RetentionViolationException(validation.Violation, validation.Message!);
        }

        _files[file.Id] = file;
        return Task.FromResult(file);
    }

    public Task<FileRecord> UpdateExpiryAsync(
        string fileId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        if (!_files.TryGetValue(fileId, out var existing))
        {
            throw new FileNotFoundException($"File '{fileId}' was not found.");
        }

        var validation = RetentionPolicy.Validate(expiresAt, existing.MaxExpiresAt, _clock.UtcNow);
        if (!validation.IsValid)
        {
            throw new RetentionViolationException(validation.Violation, validation.Message!);
        }

        var updated = existing with
        {
            ExpiresAt = expiresAt,
            Ttl = RetentionPolicy.TtlSeconds(expiresAt, _clock.UtcNow),
        };

        _files[fileId] = updated;
        return Task.FromResult(updated);
    }

    public Task DeleteAsync(string fileId, CancellationToken cancellationToken = default)
    {
        _files.TryRemove(fileId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FileRecord>> ListExpiredAsync(
        int maxItems,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        IReadOnlyList<FileRecord> results = _files.Values
            .Where(file => file.IsExpired(now))
            .Take(maxItems)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<IReadOnlyList<FileRecord>> ListExtendedAsync(
        int maxItems,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        IReadOnlyList<FileRecord> results = _files.Values
            .Where(file => !file.IsExpired(now)
                && file.ExpiresAt > RetentionPolicy.DefaultExpiresAt(file.UploadedAt))
            .Take(maxItems)
            .ToList();

        return Task.FromResult(results);
    }
}

/// <summary>In-memory comments, partitioned by file exactly as Cosmos is.</summary>
public sealed class InMemoryCommentRepository(IClock clock) : ICommentRepository
{
    private readonly ConcurrentDictionary<string, Comment> _comments = new(StringComparer.Ordinal);
    private readonly IClock _clock = clock;

    public IReadOnlyCollection<Comment> All => _comments.Values.ToList();

    public Task<IReadOnlyList<Comment>> ListByFileAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Comment> results = _comments.Values
            .Where(comment => comment.FileId == fileId)
            .OrderBy(comment => comment.CreatedAt)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<Comment?> GetAsync(
        string fileId,
        string commentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            _comments.TryGetValue(commentId, out var comment) && comment.FileId == fileId ? comment : null);

    public Task<Comment> CreateAsync(Comment comment, CancellationToken cancellationToken = default)
    {
        _comments[comment.Id] = comment;
        return Task.FromResult(comment);
    }

    public Task<Comment> ReplaceAsync(Comment comment, CancellationToken cancellationToken = default)
    {
        _comments[comment.Id] = comment;
        return Task.FromResult(comment);
    }

    public Task<int> DeleteByFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var comment in _comments.Values.Where(c => c.FileId == fileId))
        {
            if (_comments.TryRemove(comment.Id, out _))
            {
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    public Task<int> SyncTtlAsync(
        string fileId,
        int ttlSeconds,
        CancellationToken cancellationToken = default)
    {
        var updated = 0;
        foreach (var comment in _comments.Values.Where(c => c.FileId == fileId && c.Ttl != ttlSeconds))
        {
            _comments[comment.Id] = comment with { Ttl = ttlSeconds };
            updated++;
        }

        return Task.FromResult(updated);
    }

    public Task<Comment> SoftDeleteAsync(
        string fileId,
        string commentId,
        CancellationToken cancellationToken = default)
    {
        if (!_comments.TryGetValue(commentId, out var existing) || existing.FileId != fileId)
        {
            throw new KeyNotFoundException($"Comment '{commentId}' was not found.");
        }

        var deleted = existing with { DeletedAt = _clock.UtcNow, Body = string.Empty };
        _comments[commentId] = deleted;
        return Task.FromResult(deleted);
    }
}

/// <summary>In-memory preferences.</summary>
public sealed class InMemoryUserPreferencesRepository : IUserPreferencesRepository
{
    private readonly ConcurrentDictionary<string, UserPreferences> _preferences = new(StringComparer.Ordinal);

    public Task<UserPreferences?> GetAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_preferences.TryGetValue(userId, out var value) ? value : null);

    public Task<UserPreferences> UpsertAsync(
        UserPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        _preferences[preferences.Id] = preferences;
        return Task.FromResult(preferences);
    }
}

/// <summary>In-memory in-app notifications.</summary>
public sealed class InMemoryNotificationRepository : INotificationRepository
{
    private readonly ConcurrentDictionary<string, NotificationRecord> _records = new(StringComparer.Ordinal);

    public IReadOnlyCollection<NotificationRecord> All => _records.Values.ToList();

    public Task<NotificationRecord> CreateAsync(
        NotificationRecord notification,
        CancellationToken cancellationToken = default)
    {
        _records[notification.Id] = notification;
        return Task.FromResult(notification);
    }

    public Task<IReadOnlyList<NotificationRecord>> ListUnreadAsync(
        string recipientId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NotificationRecord> results = _records.Values
            .Where(record => record.RecipientId == recipientId && record.State == NotificationState.Unread)
            .OrderByDescending(record => record.CreatedAt)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<NotificationRecord?> FindByCoalesceKeyAsync(
        string recipientId,
        string coalesceKey,
        CancellationToken cancellationToken = default)
    {
        var match = _records.Values.FirstOrDefault(record =>
            record.RecipientId == recipientId
            && record.CoalesceKey == coalesceKey
            && record.State == NotificationState.Unread);

        return Task.FromResult(match);
    }

    public Task<NotificationRecord> ReplaceAsync(
        NotificationRecord notification,
        CancellationToken cancellationToken = default)
    {
        _records[notification.Id] = notification;
        return Task.FromResult(notification);
    }

    public Task MarkReadAsync(
        string recipientId,
        string notificationId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default)
    {
        if (_records.TryGetValue(notificationId, out var existing) && existing.RecipientId == recipientId)
        {
            _records[notificationId] = existing with { State = NotificationState.Read, ReadAt = readAt };
        }

        return Task.CompletedTask;
    }
}
