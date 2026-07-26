namespace BlinkMark.Core.Models;

/// <summary>
/// System-generated identifiers.
/// </summary>
/// <remarks>
/// ULIDs rather than GUIDs, for two reasons that both matter here.
/// <para>
/// They are lexicographically sortable by creation time, which is what lets the comments
/// container return a thread in creation order without a secondary sort or an index on
/// <c>createdAt</c>.
/// </para>
/// <para>
/// They are also opaque and system-generated, which is what FR-008 requires: a stored file is
/// never addressed by anything derived from its uploaded filename, so a hostile name cannot
/// become a path, a header, or a link.
/// </para>
/// </remarks>
public static class Identifiers
{
    /// <summary>Creates a new sortable identifier.</summary>
    public static string New() => Ulid.NewUlid().ToString();

    /// <summary>Creates a new sortable identifier stamped with an explicit time.</summary>
    public static string New(DateTimeOffset timestamp) => Ulid.NewUlid(timestamp).ToString();

    /// <summary>
    /// Returns true when <paramref name="value"/> is a well-formed identifier.
    /// </summary>
    /// <remarks>
    /// Every route that accepts an identifier validates it here before it reaches storage. A
    /// blob path is built from this value, so accepting an arbitrary string would reintroduce
    /// path traversal through the back door.
    /// </remarks>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && Ulid.TryParse(value, out _);

    /// <summary>Extracts the creation time encoded in an identifier.</summary>
    public static DateTimeOffset? CreatedAt(string value) =>
        Ulid.TryParse(value, out var ulid) ? ulid.Time : null;
}
