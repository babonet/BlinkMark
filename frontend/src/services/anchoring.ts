import type { Anchor } from './apiClient';

/**
 * Anchor computation and resolution over the text projection (T062).
 *
 * This deliberately does **not** use `dom-anchor-text-quote` or the DOM at all. The preview is
 * framed with `sandbox` and no `allow-same-origin` on a separate origin (Principle IV), so the
 * SPA cannot read the rendered document, cannot observe a selection inside it, and cannot receive
 * a message from it. Anchoring therefore happens against the normalized text projection — the
 * same artifact the server resolves against and agents read (research.md R2, corrected).
 *
 * The algorithm mirrors `AnchorService` on the server exactly: quote first, surrounding context to
 * disambiguate, position as a hint, approximate match, then orphan. Two implementations of the
 * same rules is a real risk, which is why the ordering and the tolerance below are stated in the
 * same terms as the C# — if one changes, the other has to.
 */

/** Characters of context stored either side of the quote. Must match `AnchorService`. */
export const CONTEXT_LENGTH = 32;

/** Proportion of characters that may differ in an approximate match. Must match `AnchorService`. */
const APPROXIMATE_TOLERANCE = 0.15;

export type AnchorMatchQuality = 'exact' | 'disambiguated' | 'approximate' | 'not-found';

export interface AnchorResolution {
  quality: AnchorMatchQuality;
  start: number;
  end: number;
  resolved: boolean;
}

const NOT_FOUND: AnchorResolution = { quality: 'not-found', start: 0, end: 0, resolved: false };

/**
 * Builds an anchor for a selection over the projection.
 *
 * The quote and its context are captured now because they cannot be recovered later. An orphaned
 * comment shows `exact`, and if it were derived rather than stored there would be nothing to show
 * at precisely the moment it is needed (FR-022).
 */
export function createTextAnchor(
  projection: string,
  start: number,
  end: number,
  renderVersion: string,
): Anchor {
  if (start < 0 || end > projection.length || start >= end) {
    throw new RangeError('The selection does not lie inside the text projection.');
  }

  return {
    kind: 'text',
    exact: projection.slice(start, end),
    prefix: projection.slice(Math.max(0, start - CONTEXT_LENGTH), start),
    suffix: projection.slice(end, Math.min(projection.length, end + CONTEXT_LENGTH)),
    start,
    end,
    renderVersion,
  };
}

/** Builds a region anchor for a structural block, for content with no useful quotable text. */
export function createRegionAnchor(
  projection: string,
  start: number,
  end: number,
  containerPath: string,
  renderVersion: string,
): Anchor {
  return {
    ...createTextAnchor(projection, start, end, renderVersion),
    kind: 'region',
    containerPath,
  };
}

/**
 * Resolves an anchor against a projection.
 *
 * Never returns a match it is not reasonably confident in. "Found the wrong passage" is a worse
 * outcome than "orphaned": an orphan is visible and reviewable, a mis-anchor is silent and looks
 * correct (Principle III).
 */
export function resolveAnchor(anchor: Anchor, projection: string): AnchorResolution {
  if (!projection || !anchor.exact) {
    return NOT_FOUND;
  }

  const occurrences = findAll(projection, anchor.exact);

  if (occurrences.length === 1) {
    return {
      quality: 'exact',
      start: occurrences[0],
      end: occurrences[0] + anchor.exact.length,
      resolved: true,
    };
  }

  if (occurrences.length > 1) {
    // Context before position: surrounding text is content-derived and survives a re-render,
    // whereas an offset does not.
    const byContext = occurrences.filter((index) => contextMatches(projection, index, anchor));
    const candidates = byContext.length > 0 ? byContext : occurrences;

    const best = candidates.reduce((closest, index) =>
      Math.abs(index - anchor.start) < Math.abs(closest - anchor.start) ? index : closest,
    );

    return {
      quality: byContext.length === 1 ? 'exact' : 'disambiguated',
      start: best,
      end: best + anchor.exact.length,
      resolved: true,
    };
  }

  return resolveApproximately(anchor, projection);
}

/**
 * Searches near the stored offset for a close-enough match.
 *
 * Only near the offset. A document-wide fuzzy search finds something for almost any input, which
 * is how confident mis-anchoring happens.
 */
function resolveApproximately(anchor: Anchor, projection: string): AnchorResolution {
  const length = anchor.exact.length;
  if (length === 0 || length > projection.length) {
    return NOT_FOUND;
  }

  const radius = Math.max(256, length * 4);
  const from = Math.max(0, anchor.start - radius);
  const to = Math.min(projection.length - length, anchor.start + radius);
  const maxDistance = Math.floor(length * APPROXIMATE_TOLERANCE);

  if (maxDistance === 0) {
    return NOT_FOUND;
  }

  let bestIndex = -1;
  let bestDistance = Number.MAX_SAFE_INTEGER;

  for (let index = from; index <= to; index++) {
    const distance = boundedEditDistance(anchor.exact, projection.substr(index, length), maxDistance);

    if (distance < bestDistance) {
      bestDistance = distance;
      bestIndex = index;
      if (distance === 0) {
        break;
      }
    }
  }

  if (bestIndex < 0 || bestDistance > maxDistance) {
    return NOT_FOUND;
  }

  return { quality: 'approximate', start: bestIndex, end: bestIndex + length, resolved: true };
}

function contextMatches(projection: string, index: number, anchor: Anchor): boolean {
  if (anchor.prefix) {
    const prefixStart = index - anchor.prefix.length;
    if (prefixStart < 0 || projection.slice(prefixStart, index) !== anchor.prefix) {
      return false;
    }
  }

  if (anchor.suffix) {
    const suffixStart = index + anchor.exact.length;
    if (projection.slice(suffixStart, suffixStart + anchor.suffix.length) !== anchor.suffix) {
      return false;
    }
  }

  return true;
}

function findAll(haystack: string, needle: string): number[] {
  const results: number[] = [];
  let index = haystack.indexOf(needle);

  while (index >= 0) {
    results.push(index);
    // Advance by one, not by the match length: overlapping occurrences count.
    index = haystack.indexOf(needle, index + 1);
  }

  return results;
}

/** Levenshtein distance, abandoned once it exceeds `maxDistance`. */
function boundedEditDistance(left: string, right: string, maxDistance: number): number {
  if (left.length === 0) return right.length;
  if (right.length === 0) return left.length;

  let previous = Array.from({ length: right.length + 1 }, (_, index) => index);
  let current = new Array<number>(right.length + 1);

  for (let i = 1; i <= left.length; i++) {
    current[0] = i;
    let rowMinimum = current[0];

    for (let j = 1; j <= right.length; j++) {
      const substitution = left[i - 1] === right[j - 1] ? 0 : 1;
      current[j] = Math.min(current[j - 1] + 1, previous[j] + 1, previous[j - 1] + substitution);
      rowMinimum = Math.min(rowMinimum, current[j]);
    }

    // No later row can beat the best in this one.
    if (rowMinimum > maxDistance) {
      return Number.MAX_SAFE_INTEGER;
    }

    [previous, current] = [current, previous];
  }

  return previous[right.length];
}

/**
 * Splits the projection into addressable blocks.
 *
 * The projection separates block elements with newlines, so a block is a paragraph, heading, list
 * item, or table cell. These are the units the keyboard path moves through (FR-078) and the units
 * region selection targets (FR-082) — which is what lets both interaction paths produce the same
 * anchor data as a pointer drag rather than a second format.
 */
export interface ProjectionBlock {
  index: number;
  text: string;
  start: number;
  end: number;
}

export function splitIntoBlocks(projection: string): ProjectionBlock[] {
  const blocks: ProjectionBlock[] = [];
  let offset = 0;
  let index = 0;

  for (const line of projection.split('\n')) {
    const trimmed = line.trim();
    if (trimmed.length > 0) {
      // Offsets refer to the untrimmed line so they stay valid against the stored projection.
      const leading = line.length - line.trimStart().length;
      blocks.push({
        index: index++,
        text: trimmed,
        start: offset + leading,
        end: offset + leading + trimmed.length,
      });
    }
    offset += line.length + 1;
  }

  return blocks;
}
