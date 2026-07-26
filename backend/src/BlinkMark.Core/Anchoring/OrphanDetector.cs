using BlinkMark.Core.Models;

namespace BlinkMark.Core.Anchoring;

/// <summary>
/// Decides whether a comment's anchor still resolves (T060).
/// </summary>
/// <remarks>
/// Orphaning is not an error path. Principle III forbids two outcomes that are far worse than an
/// honest one: silently binding a comment to a different passage, and hiding it. Both destroy
/// review work without telling anyone, and the first does so while looking correct.
/// <para>
/// It is reachable even though files are immutable, which is not obvious. Anchors resolve against
/// the <em>sanitized render</em>, not the upload, so a change in sanitizer or renderer version
/// changes the text the anchor was computed against. That is why <c>renderVersion</c> is pinned
/// per file — and why this class treats a version mismatch as a reason for caution rather than as
/// a reason to give up.
/// </para>
/// </remarks>
public sealed class OrphanDetector(AnchorService anchors)
{
    private readonly AnchorService _anchors = anchors;

    /// <summary>Returns the comment with its anchor state brought up to date.</summary>
    public Comment Evaluate(Comment comment, string projection, string currentRenderVersion)
    {
        var resolution = _anchors.Resolve(comment.Anchor, projection);

        var state = resolution.IsResolved ? AnchorState.Anchored : AnchorState.Orphaned;

        if (state == comment.AnchorState)
        {
            return comment;
        }

        return comment with { AnchorState = state };
    }

    /// <summary>Evaluates a whole file's comments in one pass.</summary>
    public IReadOnlyList<Comment> EvaluateAll(
        IEnumerable<Comment> comments,
        string projection,
        string currentRenderVersion) =>
        comments.Select(comment => Evaluate(comment, projection, currentRenderVersion)).ToList();

    /// <summary>
    /// True when the anchor was computed against a different render than the one in force.
    /// </summary>
    /// <remarks>
    /// A mismatch means resolution must be treated as approximate rather than authoritative. In
    /// normal operation it never happens, because the render version is pinned for the life of a
    /// file — it appears when a file is re-uploaded, which is the path orphaning primarily exists
    /// to serve.
    /// </remarks>
    public static bool IsStale(Anchor anchor, string currentRenderVersion) =>
        !string.Equals(anchor.RenderVersion, currentRenderVersion, StringComparison.Ordinal);
}
