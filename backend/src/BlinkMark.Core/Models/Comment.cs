using System.Text.Json.Serialization;

namespace BlinkMark.Core.Models;

/// <summary>
/// A review comment. Cosmos container <c>comments</c>, partition key <c>/fileId</c>.
/// </summary>
/// <remarks>
/// Partitioning by file makes "load every comment on this file" a single-partition query, which
/// is the operation SC-003 puts a 300 ms budget on.
/// </remarks>
public sealed record Comment
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("fileId")]
    public required string FileId { get; init; }

    /// <summary>Equals <see cref="Id"/> for a root comment; replies inherit the root's value.</summary>
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    /// <summary>Direct parent, for reply nesting. Null on a root comment.</summary>
    [JsonPropertyName("parentId")]
    public string? ParentId { get; init; }

    /// <summary>
    /// The comment text, stored and returned as literal text.
    /// </summary>
    /// <remarks>
    /// Never rendered as markup anywhere (FR-024). A comment body is untrusted input from an
    /// authenticated user, and the fact that the author is authenticated says nothing about
    /// whether the text is safe to interpret.
    /// </remarks>
    [JsonPropertyName("body")]
    public required string Body { get; init; }

    /// <summary>
    /// Entra object id of the author, taken from the authenticated principal.
    /// </summary>
    /// <remarks>
    /// A client-supplied author is ignored, always (FR-005, FR-023). This is the field an
    /// attacker would most like to control.
    /// </remarks>
    [JsonPropertyName("authorId")]
    public required string AuthorId { get; init; }

    [JsonPropertyName("authorDisplayName")]
    public required string AuthorDisplayName { get; init; }

    /// <summary>
    /// Set when an AI agent created this comment on the author's behalf.
    /// </summary>
    /// <remarks>
    /// Drives the visible attribution FR-048 requires. A reader must be able to tell that a
    /// human's name on a comment does not mean a human wrote it.
    /// </remarks>
    [JsonPropertyName("actingAgentId")]
    public string? ActingAgentId { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("editedAt")]
    public DateTimeOffset? EditedAt { get; init; }

    [JsonPropertyName("anchor")]
    public required Anchor Anchor { get; init; }

    [JsonPropertyName("anchorState")]
    public AnchorState AnchorState { get; init; } = AnchorState.Anchored;

    /// <summary>
    /// Soft delete, so replies to a deleted comment survive.
    /// </summary>
    /// <remarks>
    /// Hard-deleting a comment mid-thread would silently destroy other people's replies. The
    /// audit trail records the deletion (FR-025).
    /// </remarks>
    [JsonPropertyName("deletedAt")]
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>Cosmos per-item TTL in seconds, derived from the parent file's expiry.</summary>
    [JsonPropertyName("ttl")]
    public int Ttl { get; init; }

    [JsonIgnore]
    public bool IsDeleted => DeletedAt is not null;

    [JsonIgnore]
    public bool IsRoot => ParentId is null;
}
