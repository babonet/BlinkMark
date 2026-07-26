using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlinkMark.Core.Models;

namespace BlinkMark.Core.Export;

/// <summary>
/// Builds the download bundle: a file plus every comment made on it (T074, FR-031).
/// </summary>
/// <remarks>
/// This is the only way review work survives expiry, which makes completeness the requirement
/// rather than a nicety. Three things are easy to leave out and all three are required:
/// <list type="bullet">
/// <item><description>
/// <strong>Orphaned comments.</strong> A comment whose passage was edited away still says
/// something, and dropping it from the export would be the silent data loss Principle III
/// forbids. Orphans are included, labelled as orphaned, and keep the quoted passage they were
/// originally attached to — which is often the only remaining record of what that passage said.
/// </description></item>
/// <item><description>
/// <strong>Deleted comments.</strong> Included as tombstones, without their bodies. The body was
/// withdrawn deliberately and restoring it in an export would undo the author's decision; but
/// removing the tombstone would break the thread structure and make replies look like they
/// answer nothing.
/// </description></item>
/// <item><description>
/// <strong>Agent attribution.</strong> If an agent wrote a comment on someone's behalf, the
/// export says so. Attribution that exists in the product but vanishes in the export is
/// attribution that fails exactly when someone is reconstructing what happened (FR-049).
/// </description></item>
/// </list>
/// <para>
/// The bundle is a JSON sidecar plus the original content, not a rendered document. The reviewer
/// downloading it wants their colleagues' words in a form they can search and process, and
/// re-rendering here would mean a fourth rendering path to keep consistent with the other three.
/// </para>
/// </remarks>
public sealed class DownloadBundleBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Builds the comment sidecar for <paramref name="file"/>.</summary>
    public DownloadBundle Build(
        FileRecord file,
        IReadOnlyList<Comment> comments,
        DateTimeOffset downloadedAt)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(comments);

        // Chronological, so a thread reads in the order it happened regardless of how storage
        // returned it.
        var ordered = comments.OrderBy(comment => comment.CreatedAt).ToList();

        var exported = ordered
            .Select(comment => new ExportedComment
            {
                Id = comment.Id,
                ThreadId = comment.ThreadId,
                ParentId = comment.ParentId,
                Author = comment.AuthorDisplayName,
                AuthorId = comment.AuthorId,
                ActingAgentId = comment.ActingAgentId,
                CreatedAt = comment.CreatedAt,
                EditedAt = comment.EditedAt,
                IsDeleted = comment.DeletedAt is not null,

                // The body of a withdrawn comment stays withdrawn.
                Body = comment.DeletedAt is null ? comment.Body : null,

                AnchorState = comment.AnchorState.ToString().ToLowerInvariant(),
                IsOrphaned = comment.AnchorState == AnchorState.Orphaned,

                // The quoted passage travels with the comment even when orphaned — especially
                // when orphaned, since the document no longer contains it.
                AnchoredPassage = comment.Anchor.Exact,
                AnchorPrefix = string.IsNullOrEmpty(comment.Anchor.Prefix) ? null : comment.Anchor.Prefix,
                AnchorSuffix = string.IsNullOrEmpty(comment.Anchor.Suffix) ? null : comment.Anchor.Suffix,
            })
            .ToList();

        return new DownloadBundle
        {
            FileId = file.Id,
            DisplayName = file.DisplayName,
            ContentType = file.ContentType.ToString().ToLowerInvariant(),
            UploadedAt = file.UploadedAt,
            ExpiresAt = file.ExpiresAt,
            DownloadedAt = downloadedAt,
            OwnerDisplayName = file.OwnerDisplayName,
            CommentCount = exported.Count,
            OrphanedCommentCount = exported.Count(comment => comment.IsOrphaned),
            Comments = exported,

            // FR-076. The copy being downloaded is not covered by BlinkMark's retention, and the
            // person downloading it becomes responsible for it. Saying so is the control.
            Notice =
                "This copy is yours to keep and is no longer governed by BlinkMark's retention. " +
                "The file in BlinkMark still deletes itself on the date above; this download will not. " +
                "Handle it according to your organization's data policy.",
        };
    }

    /// <summary>Serializes a bundle to the sidecar bytes written into the archive.</summary>
    public byte[] Serialize(DownloadBundle bundle) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bundle, SerializerOptions));
}

/// <summary>The comment sidecar accompanying a downloaded file.</summary>
public sealed record DownloadBundle
{
    public required string FileId { get; init; }

    public required string DisplayName { get; init; }

    public required string ContentType { get; init; }

    public required string OwnerDisplayName { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset DownloadedAt { get; init; }

    public required int CommentCount { get; init; }

    public required int OrphanedCommentCount { get; init; }

    public required IReadOnlyList<ExportedComment> Comments { get; init; }

    public required string Notice { get; init; }
}

/// <summary>One comment as it appears in an export.</summary>
public sealed record ExportedComment
{
    public required string Id { get; init; }

    public required string ThreadId { get; init; }

    public string? ParentId { get; init; }

    public required string Author { get; init; }

    public required string AuthorId { get; init; }

    /// <summary>Present only when an agent wrote this on the author's behalf (FR-049).</summary>
    public string? ActingAgentId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? EditedAt { get; init; }

    public required bool IsDeleted { get; init; }

    /// <summary>Null for a deleted comment; the tombstone remains so threads stay intact.</summary>
    public string? Body { get; init; }

    public required string AnchorState { get; init; }

    public required bool IsOrphaned { get; init; }

    /// <summary>The passage this comment was written about, as it read at the time.</summary>
    public required string AnchoredPassage { get; init; }

    public string? AnchorPrefix { get; init; }

    public string? AnchorSuffix { get; init; }
}
