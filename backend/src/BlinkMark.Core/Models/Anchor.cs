using System.Text.Json.Serialization;

namespace BlinkMark.Core.Models;

/// <summary>What an anchor points at.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnchorKind>))]
public enum AnchorKind
{
    /// <summary>A run of text, selected by pointer or by keyboard.</summary>
    Text,

    /// <summary>A structural region, for a figure or table that has no useful quotable text.</summary>
    Region,
}

/// <summary>Whether an anchor still resolves.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnchorState>))]
public enum AnchorState
{
    Anchored,

    /// <summary>
    /// The passage could not be located. The comment stays visible with its quoted context.
    /// </summary>
    /// <remarks>
    /// Orphaned is a first-class state, not an error. Principle III forbids two failure modes
    /// that are far worse than an honest one: silently binding the comment to a different
    /// passage, and hiding it. Both lose review work without telling anyone.
    /// </remarks>
    Orphaned,
}

/// <summary>
/// A content-derived pointer to a passage, embedded in a comment.
/// </summary>
/// <remarks>
/// Modelled on the W3C Web Annotation selectors (research.md R2). Both a quote selector and a
/// position selector are stored, because each covers the other's weakness: the quote survives
/// re-rendering but cannot distinguish two identical passages, and the position distinguishes
/// them but shifts if anything upstream changes.
/// <para>
/// Resolution order is quote, then position as a tiebreak, then approximate match, then orphan.
/// </para>
/// </remarks>
public sealed record Anchor
{
    [JsonPropertyName("kind")]
    public required AnchorKind Kind { get; init; }

    /// <summary>
    /// The quoted passage.
    /// </summary>
    /// <remarks>
    /// This is what an orphaned comment displays, which is why it is stored rather than
    /// recomputed. A reviewer whose passage vanished still sees what they were talking about
    /// (FR-022).
    /// </remarks>
    [JsonPropertyName("exact")]
    public required string Exact { get; init; }

    /// <summary>Roughly 32 characters before the passage. Disambiguates repeated text.</summary>
    [JsonPropertyName("prefix")]
    public string Prefix { get; init; } = string.Empty;

    /// <summary>Roughly 32 characters after the passage.</summary>
    [JsonPropertyName("suffix")]
    public string Suffix { get; init; } = string.Empty;

    /// <summary>Character offset into the normalized text projection. A hint, not the truth.</summary>
    [JsonPropertyName("start")]
    public int Start { get; init; }

    /// <summary>Exclusive end offset into the normalized text projection.</summary>
    [JsonPropertyName("end")]
    public int End { get; init; }

    /// <summary>Region anchors only: the nearest stable containing element.</summary>
    [JsonPropertyName("containerPath")]
    public string? ContainerPath { get; init; }

    /// <summary>
    /// Region anchors only: normalized offsets within the container, never pixels.
    /// </summary>
    /// <remarks>
    /// Pixel coordinates are forbidden by Principle III and would break on any viewport the
    /// author did not have. Fractions of the container survive reflow.
    /// </remarks>
    [JsonPropertyName("fractionalRect")]
    public FractionalRect? FractionalRect { get; init; }

    /// <summary>
    /// The render this anchor was computed against.
    /// </summary>
    /// <remarks>
    /// A mismatch against the file's current render version means resolution must be treated as
    /// approximate rather than authoritative.
    /// </remarks>
    [JsonPropertyName("renderVersion")]
    public required string RenderVersion { get; init; }

    /// <summary>Length of the anchored passage in characters.</summary>
    [JsonIgnore]
    public int Length => Math.Max(0, End - Start);
}

/// <summary>Normalized offsets within a container element. Values are 0..1.</summary>
public sealed record FractionalRect
{
    [JsonPropertyName("x")]
    public required double X { get; init; }

    [JsonPropertyName("y")]
    public required double Y { get; init; }

    [JsonPropertyName("width")]
    public required double Width { get; init; }

    [JsonPropertyName("height")]
    public required double Height { get; init; }
}
