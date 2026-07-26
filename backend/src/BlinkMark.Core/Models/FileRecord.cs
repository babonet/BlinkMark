using System.Text.Json.Serialization;

namespace BlinkMark.Core.Models;

/// <summary>The two content types BlinkMark accepts.</summary>
/// <remarks>
/// Determined from validated content, never from the claimed extension (FR-007). A file named
/// <c>notes.md</c> containing HTML is an HTML file, and the upload validator rejects the
/// disagreement rather than trusting either side.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<FileContentType>))]
public enum FileContentType
{
    Html,
    Markdown,
}

/// <summary>
/// A file in the working set. Cosmos container <c>files</c>, partition key <c>/id</c>.
/// </summary>
/// <remarks>
/// The partition key is the id because the preview path is a point read, which is the cheapest
/// and lowest-latency Cosmos operation and the one SC-002's one-second budget depends on.
/// </remarks>
public sealed record FileRecord
{
    /// <summary>System-generated ULID. Also the blob name (FR-008).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// The original filename, kept for display only (FR-009).
    /// </summary>
    /// <remarks>
    /// Never used as a path, a header value, or a link target. It is attacker-controlled text
    /// that happens to be useful to show a human.
    /// </remarks>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("contentType")]
    public required FileContentType ContentType { get; init; }

    [JsonPropertyName("sizeBytes")]
    public required long SizeBytes { get; init; }

    /// <summary>Entra object id of the uploader.</summary>
    [JsonPropertyName("ownerId")]
    public required string OwnerId { get; init; }

    /// <summary>Denormalized so listing a user's files needs no directory lookup per row.</summary>
    [JsonPropertyName("ownerDisplayName")]
    public required string OwnerDisplayName { get; init; }

    [JsonPropertyName("uploadedAt")]
    public required DateTimeOffset UploadedAt { get; init; }

    /// <summary>When the file becomes unreadable. Mutable, but only within the ceiling.</summary>
    [JsonPropertyName("expiresAt")]
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// <c>uploadedAt + 30 days</c>, written once at upload and never recomputed.
    /// </summary>
    /// <remarks>
    /// The 30-day ceiling is data, not validation logic (FR-028). Storing it means a future
    /// endpoint that writes <see cref="ExpiresAt"/> is checked against a value it did not
    /// compute and cannot argue with — which is the difference between a rule that holds and a
    /// rule that holds until someone adds a code path.
    /// </remarks>
    [JsonPropertyName("maxExpiresAt")]
    public required DateTimeOffset MaxExpiresAt { get; init; }

    /// <summary>
    /// Sanitizer and renderer version pinned at upload.
    /// </summary>
    /// <remarks>
    /// Anchors resolve against the sanitized render, not the raw upload. If the sanitizer
    /// changed under a live file, its text would change and every comment on it would orphan at
    /// once. Pinning the version guarantees a file renders identically for its whole life
    /// (research.md R2).
    /// </remarks>
    [JsonPropertyName("renderVersion")]
    public required string RenderVersion { get; init; }

    [JsonPropertyName("blobPath")]
    public required string BlobPath { get; init; }

    /// <summary>Cosmos per-item TTL in seconds, kept in step with <see cref="ExpiresAt"/>.</summary>
    [JsonPropertyName("ttl")]
    public int Ttl { get; init; }

    /// <summary>
    /// True once <see cref="ExpiresAt"/> has passed.
    /// </summary>
    /// <remarks>
    /// Derived, never stored. An expired file must read as gone the instant it expires, whatever
    /// the reconciliation job has or has not swept yet (FR-032).
    /// </remarks>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}
