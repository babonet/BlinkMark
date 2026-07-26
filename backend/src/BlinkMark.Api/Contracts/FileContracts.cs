using BlinkMark.Core.Models;
using BlinkMark.Core.Rendering;

namespace BlinkMark.Api.Contracts;

/// <summary>A file as it appears in a list.</summary>
public record FileSummaryResponse
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string ContentType { get; init; }

    public required long SizeBytes { get; init; }

    public required string OwnerId { get; init; }

    public required string OwnerDisplayName { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset MaxExpiresAt { get; init; }

    public static FileSummaryResponse From(FileRecord file) => new()
    {
        Id = file.Id,
        DisplayName = file.DisplayName,
        ContentType = file.ContentType.ToString().ToLowerInvariant(),
        SizeBytes = file.SizeBytes,
        OwnerId = file.OwnerId,
        OwnerDisplayName = file.OwnerDisplayName,
        UploadedAt = file.UploadedAt,
        ExpiresAt = file.ExpiresAt,
        MaxExpiresAt = file.MaxExpiresAt,
    };
}

/// <summary>A file, with everything needed to display and preview it.</summary>
public sealed record FileDetailResponse : FileSummaryResponse
{
    /// <summary>
    /// The preview URL, carrying a freshly minted preview token.
    /// </summary>
    /// <remarks>
    /// Minted per request rather than stored. The token lives fifteen minutes at most, and a
    /// client that caches this URL and reuses it after expiry is expected to re-request the
    /// metadata rather than show the user an error (contracts/preview-origin.md).
    /// </remarks>
    public required string PreviewUrl { get; init; }

    /// <summary>
    /// FR-057, SC-016 — who can see this.
    /// </summary>
    /// <remarks>
    /// Clarification Q1 accepted that the link is the access grant within the organization. The
    /// residual risk is mitigated by telling people, at the point of upload, rather than by an
    /// access control that does not exist. Removing this text removes the mitigation.
    /// </remarks>
    public required string AccessScopeNotice { get; init; }

    /// <summary>FR-075 — when it disappears, and that there is no backup.</summary>
    public required string RetentionNotice { get; init; }

    public required bool IsOwner { get; init; }
}

/// <summary>A user's live-file position against their cap.</summary>
public sealed record QuotaStatusResponse
{
    public required int LiveFiles { get; init; }

    public required int MaxLiveFiles { get; init; }
}

/// <summary>The response to listing a user's files.</summary>
public sealed record FileListResponse
{
    public required IReadOnlyList<FileSummaryResponse> Files { get; init; }

    public required QuotaStatusResponse Quota { get; init; }
}

/// <summary>The body of a retention change.</summary>
public sealed record RetentionUpdateRequest
{
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>The result of a retention change.</summary>
public sealed record RetentionUpdateResponse
{
    public required string FileId { get; init; }

    /// <summary>
    /// What the expiry was before this change.
    /// </summary>
    /// <remarks>
    /// Returned, not just audited, so the owner can see what they actually changed rather than
    /// having to remember.
    /// </remarks>
    public required DateTimeOffset PreviousExpiresAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The ceiling. The client uses this to bound its own picker (FR-028).</summary>
    public required DateTimeOffset MaxExpiresAt { get; init; }

    public required string RetentionNotice { get; init; }
}

/// <summary>The structured content projection an agent reads (FR-051).</summary>
public sealed record FileContentResponse
{
    public required string FileId { get; init; }

    public required string DisplayName { get; init; }

    public required string ContentType { get; init; }

    public required string RenderVersion { get; init; }

    /// <summary>
    /// The same normalized projection anchors resolve against.
    /// </summary>
    /// <remarks>
    /// Deliberately the same artifact, not a second rendering. If they diverged, an agent could
    /// comment on text no human ever saw.
    /// </remarks>
    public required string Text { get; init; }

    /// <summary>
    /// The same document, described as structure the client can render.
    /// </summary>
    /// <remarks>
    /// Data, not markup. Each block carries a type from a closed set, a level, and text — never
    /// an element, an attribute, or a URL — so a reviewer can read and comment on a document that
    /// looks like a document without any uploaded HTML reaching the application origin
    /// (Principle IV). Offsets index into <see cref="Text"/>, so a comment made here and a comment
    /// made by an agent reading the flat text address the same characters.
    /// </remarks>
    public required IReadOnlyList<DocumentBlockResponse> Blocks { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>One block of a document.</summary>
public sealed record DocumentBlockResponse
{
    /// <summary>One of: paragraph, heading, listItem, quote, code, tableCell.</summary>
    public required string Type { get; init; }

    /// <summary>Heading level 1-6, or zero.</summary>
    public required int Level { get; init; }

    public required bool Ordered { get; init; }

    public required string Text { get; init; }

    public required int Start { get; init; }

    public required int End { get; init; }

    public static DocumentBlockResponse From(DocumentBlock block) => new()
    {
        Type = char.ToLowerInvariant(block.Type.ToString()[0]) + block.Type.ToString()[1..],
        Level = block.Level,
        Ordered = block.Ordered,
        Text = block.Text,
        Start = block.Start,
        End = block.End,
    };
}
