using BlinkMark.Core.Models;

namespace BlinkMark.Core.Comments;

/// <summary>A root comment together with its replies, in creation order.</summary>
public sealed record CommentThread
{
    public required string ThreadId { get; init; }

    public required Comment Root { get; init; }

    public required IReadOnlyList<Comment> Replies { get; init; }

    /// <summary>The anchor the whole thread hangs off — the root's.</summary>
    public Anchor Anchor => Root.Anchor;

    public AnchorState AnchorState => Root.AnchorState;

    /// <summary>Every comment in the thread, root first.</summary>
    public IEnumerable<Comment> All => new[] { Root }.Concat(Replies);
}

/// <summary>
/// Reply threading (T058).
/// </summary>
/// <remarks>
/// A thread is identified by its root comment's id, which every reply inherits. Storing
/// <c>threadId</c> as well as <c>parentId</c> looks redundant and is not: it makes "everything in
/// this conversation" a single grouping over one field, rather than a recursive walk up a parent
/// chain that would have to run per comment.
/// <para>
/// Replies do not carry their own anchor. A thread is a conversation about one passage, so the
/// root's anchor governs the lot — which also means a thread orphans as a unit rather than
/// fragmenting into replies that still resolve and a root that does not.
/// </para>
/// </remarks>
public sealed class ThreadService
{
    /// <summary>
    /// Works out the thread and parent for a new comment.
    /// </summary>
    /// <param name="parentId">The comment being replied to, or null for a new thread.</param>
    /// <param name="existing">Every comment already on the file.</param>
    /// <returns>The thread id to store, and the parent id after validation.</returns>
    /// <exception cref="KeyNotFoundException">The named parent is not on this file.</exception>
    public (string ThreadId, string? ParentId) Resolve(
        string newCommentId,
        string? parentId,
        IReadOnlyList<Comment> existing)
    {
        if (string.IsNullOrWhiteSpace(parentId))
        {
            // A root comment is its own thread. This is why threadId can be assigned without
            // waiting to see whether anyone ever replies.
            return (newCommentId, null);
        }

        var parent = existing.FirstOrDefault(comment => comment.Id == parentId)
            ?? throw new KeyNotFoundException($"Comment '{parentId}' is not on this file.");

        // Replies to replies join the existing thread rather than nesting further. Reviewers
        // reply to a conversation, not to a position in a tree, and unbounded nesting produces a
        // structure nobody can read on a narrow screen.
        return (parent.ThreadId, parent.Id);
    }

    /// <summary>Groups a file's comments into threads, newest thread last.</summary>
    public IReadOnlyList<CommentThread> BuildThreads(IReadOnlyList<Comment> comments)
    {
        var byThread = comments
            .GroupBy(comment => comment.ThreadId, StringComparer.Ordinal)
            .ToList();

        var threads = new List<CommentThread>(byThread.Count);

        foreach (var group in byThread)
        {
            var ordered = group.OrderBy(comment => comment.CreatedAt).ToList();

            // The root is the comment whose id is the thread id. Falling back to the earliest
            // comment covers the case where a root was hard-deleted by retention sweeping while
            // its replies were still in flight — rare, but a missing root should not lose the
            // conversation.
            var root = ordered.FirstOrDefault(comment => comment.Id == group.Key) ?? ordered[0];

            threads.Add(new CommentThread
            {
                ThreadId = group.Key,
                Root = root,
                Replies = ordered.Where(comment => comment.Id != root.Id).ToList(),
            });
        }

        return threads.OrderBy(thread => thread.Root.CreatedAt).ToList();
    }

    /// <summary>
    /// Everyone who has taken part in a thread, for notification purposes (FR-035).
    /// </summary>
    /// <remarks>
    /// Authors of deleted comments are still participants. Someone who joined a conversation and
    /// then withdrew a remark has not stopped caring how it ends.
    /// </remarks>
    public IReadOnlyCollection<string> Participants(IReadOnlyList<Comment> comments, string threadId) =>
        comments
            .Where(comment => comment.ThreadId == threadId)
            .Select(comment => comment.AuthorId)
            .ToHashSet(StringComparer.Ordinal);
}
