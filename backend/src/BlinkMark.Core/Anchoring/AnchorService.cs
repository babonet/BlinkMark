using BlinkMark.Core.Models;

namespace BlinkMark.Core.Anchoring;

/// <summary>How an anchor was resolved.</summary>
public enum AnchorMatchQuality
{
    /// <summary>The quote was found exactly once. The best case.</summary>
    Exact,

    /// <summary>The quote appears several times; the position hint chose between them.</summary>
    DisambiguatedByPosition,

    /// <summary>Found by approximate match. Usable, but the render has drifted.</summary>
    Approximate,

    /// <summary>Not found. The comment becomes orphaned rather than moving or vanishing.</summary>
    NotFound,
}

/// <summary>The outcome of resolving an anchor against a projection.</summary>
public sealed record AnchorResolution
{
    public required AnchorMatchQuality Quality { get; init; }

    public int Start { get; init; }

    public int End { get; init; }

    public bool IsResolved => Quality != AnchorMatchQuality.NotFound;

    public AnchorState State => IsResolved ? AnchorState.Anchored : AnchorState.Orphaned;

    public static readonly AnchorResolution NotFound = new() { Quality = AnchorMatchQuality.NotFound };
}

/// <summary>
/// Computes and resolves anchors against the text projection (T054).
/// </summary>
/// <remarks>
/// Follows the W3C Web Annotation selector model (research.md R2). Each anchor carries both a
/// quote selector — the exact passage plus surrounding context — and a position selector, and
/// each covers the other's weakness. The quote survives re-rendering but cannot tell two
/// identical passages apart; the position tells them apart but shifts if anything upstream
/// changes.
/// <para>
/// Resolution therefore goes quote first, position as a tiebreak, approximate match, then
/// orphan. Never the other way around: trusting the offset first would silently move a comment
/// to whatever text now sits at that character position, which is the exact failure Principle III
/// forbids.
/// </para>
/// <para>
/// The client resolves anchors too, against the rendered DOM. This server-side implementation
/// exists for the paths that have no DOM: computing an anchor an agent supplied, and detecting
/// orphaning without a browser.
/// </para>
/// </remarks>
public sealed class AnchorService
{
    /// <summary>Characters of context stored either side of the quote.</summary>
    public const int ContextLength = 32;

    /// <summary>
    /// The proportion of characters that may differ in an approximate match.
    /// </summary>
    /// <remarks>
    /// Deliberately tight. A loose threshold finds a match for almost anything, which produces
    /// confidently wrong anchors — worse than an honest orphan, because nobody reviews them.
    /// </remarks>
    private const double ApproximateTolerance = 0.15;

    /// <summary>Builds an anchor for a selection over the projection.</summary>
    public Anchor CreateTextAnchor(string projection, int start, int end, string renderVersion)
    {
        ArgumentNullException.ThrowIfNull(projection);

        if (start < 0 || end > projection.Length || start >= end)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                "The selection does not lie inside the text projection.");
        }

        var prefixStart = Math.Max(0, start - ContextLength);
        var suffixEnd = Math.Min(projection.Length, end + ContextLength);

        return new Anchor
        {
            Kind = AnchorKind.Text,
            Exact = projection[start..end],
            Prefix = projection[prefixStart..start],
            Suffix = projection[end..suffixEnd],
            Start = start,
            End = end,
            RenderVersion = renderVersion,
        };
    }

    /// <summary>Resolves an anchor against a projection.</summary>
    public AnchorResolution Resolve(Anchor anchor, string projection)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        if (string.IsNullOrEmpty(projection) || string.IsNullOrEmpty(anchor.Exact))
        {
            return AnchorResolution.NotFound;
        }

        var occurrences = FindAll(projection, anchor.Exact);

        if (occurrences.Count == 1)
        {
            return new AnchorResolution
            {
                Quality = AnchorMatchQuality.Exact,
                Start = occurrences[0],
                End = occurrences[0] + anchor.Exact.Length,
            };
        }

        if (occurrences.Count > 1)
        {
            // The duplicate-passage edge case. Context is tried before the position hint,
            // because surrounding text is content-derived and therefore survives a re-render,
            // whereas an offset does not.
            var byContext = occurrences
                .Where(index => ContextMatches(projection, index, anchor))
                .ToList();

            var candidates = byContext.Count > 0 ? byContext : occurrences;

            var best = candidates
                .OrderBy(index => Math.Abs(index - anchor.Start))
                .First();

            return new AnchorResolution
            {
                Quality = candidates.Count == 1 && byContext.Count == 1
                    ? AnchorMatchQuality.Exact
                    : AnchorMatchQuality.DisambiguatedByPosition,
                Start = best,
                End = best + anchor.Exact.Length,
            };
        }

        return ResolveApproximately(anchor, projection);
    }

    /// <summary>
    /// Searches near the stored offset for a close-enough match.
    /// </summary>
    /// <remarks>
    /// Only near the offset, never across the whole document. A global fuzzy search would find
    /// something for almost any input, and "found the wrong passage" is a worse outcome than
    /// "orphaned" — an orphan is visible and reviewable, a mis-anchor is silent.
    /// </remarks>
    private static AnchorResolution ResolveApproximately(Anchor anchor, string projection)
    {
        var length = anchor.Exact.Length;
        if (length == 0 || length > projection.Length)
        {
            return AnchorResolution.NotFound;
        }

        var searchRadius = Math.Max(256, length * 4);
        var from = Math.Max(0, anchor.Start - searchRadius);
        var to = Math.Min(projection.Length - length, anchor.Start + searchRadius);

        var maxDistance = (int)Math.Floor(length * ApproximateTolerance);
        if (maxDistance == 0)
        {
            return AnchorResolution.NotFound;
        }

        var bestIndex = -1;
        var bestDistance = int.MaxValue;

        for (var index = from; index <= to; index++)
        {
            var candidate = projection.Substring(index, length);
            var distance = BoundedEditDistance(anchor.Exact, candidate, maxDistance);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;

                if (distance == 0)
                {
                    break;
                }
            }
        }

        if (bestIndex < 0 || bestDistance > maxDistance)
        {
            return AnchorResolution.NotFound;
        }

        return new AnchorResolution
        {
            Quality = AnchorMatchQuality.Approximate,
            Start = bestIndex,
            End = bestIndex + length,
        };
    }

    private static bool ContextMatches(string projection, int index, Anchor anchor)
    {
        if (anchor.Prefix.Length > 0)
        {
            var prefixStart = index - anchor.Prefix.Length;
            if (prefixStart < 0
                || !projection.AsSpan(prefixStart, anchor.Prefix.Length).SequenceEqual(anchor.Prefix))
            {
                return false;
            }
        }

        if (anchor.Suffix.Length > 0)
        {
            var suffixStart = index + anchor.Exact.Length;
            if (suffixStart + anchor.Suffix.Length > projection.Length
                || !projection.AsSpan(suffixStart, anchor.Suffix.Length).SequenceEqual(anchor.Suffix))
            {
                return false;
            }
        }

        return true;
    }

    private static List<int> FindAll(string haystack, string needle)
    {
        var results = new List<int>();
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            results.Add(index);

            // Overlapping occurrences count, so the search advances by one rather than by the
            // length of the match.
            index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal);
        }

        return results;
    }

    /// <summary>Levenshtein distance, abandoned once it exceeds <paramref name="maxDistance"/>.</summary>
    private static int BoundedEditDistance(string left, string right, int maxDistance)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowMinimum = current[0];

            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = left[i - 1] == right[j - 1] ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitution);

                rowMinimum = Math.Min(rowMinimum, current[j]);
            }

            // No later row can improve on the best in this one, so an already-too-large row
            // means the answer exceeds the bound.
            if (rowMinimum > maxDistance)
            {
                return int.MaxValue;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
