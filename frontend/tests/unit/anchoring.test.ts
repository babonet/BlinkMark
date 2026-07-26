import { describe, expect, it } from 'vitest';
import {
  createRegionAnchor,
  createTextAnchor,
  resolveAnchor,
  splitIntoBlocks,
} from '../../src/services/anchoring';

/**
 * Client-side anchoring.
 *
 * These mirror the server's `AnchorServiceTests` case for case, on purpose. The same rules are
 * implemented twice — once in C# for the write path and agent-supplied anchors, once here for
 * selection and display — and two implementations of one algorithm is a real risk. Matching tests
 * are what turn a silent divergence into a failing build.
 */

const PROJECTION = [
  'The ingest path is the bottleneck.',
  'Retention is enforced by the platform.',
  'The ingest path is the bottleneck.',
  'The audit trail outlives the content.',
].join('\n');

const RENDER_VERSION = 'test-1';

describe('createTextAnchor', () => {
  it('captures the quote and its surrounding context', () => {
    const start = PROJECTION.indexOf('Retention');
    const anchor = createTextAnchor(PROJECTION, start, start + 9, RENDER_VERSION);

    // The quote is stored, not derived. An orphaned comment has nothing else to show.
    expect(anchor.exact).toBe('Retention');
    expect(anchor.prefix.length).toBeGreaterThan(0);
    expect(anchor.suffix.length).toBeGreaterThan(0);
    expect(anchor.renderVersion).toBe(RENDER_VERSION);
  });

  it('refuses a selection outside the projection', () => {
    expect(() => createTextAnchor(PROJECTION, 0, PROJECTION.length + 10, RENDER_VERSION)).toThrow(RangeError);
    expect(() => createTextAnchor(PROJECTION, 5, 5, RENDER_VERSION)).toThrow(RangeError);
  });

  it('marks a region anchor with its container', () => {
    const anchor = createRegionAnchor(PROJECTION, 0, 33, 'block:0', RENDER_VERSION);

    expect(anchor.kind).toBe('region');
    expect(anchor.containerPath).toBe('block:0');
    // Region anchors still carry a quote, so they orphan as informatively as text ones.
    expect(anchor.exact.length).toBeGreaterThan(0);
  });
});

describe('resolveAnchor', () => {
  it('resolves a unique passage exactly', () => {
    const start = PROJECTION.indexOf('Retention is enforced');
    const anchor = createTextAnchor(PROJECTION, start, start + 21, RENDER_VERSION);

    const resolution = resolveAnchor(anchor, PROJECTION);

    expect(resolution.quality).toBe('exact');
    expect(resolution.start).toBe(start);
  });

  it('disambiguates a duplicated passage by its context', () => {
    const second = PROJECTION.lastIndexOf('The ingest path is the bottleneck.');
    const anchor = createTextAnchor(PROJECTION, second, second + 34, RENDER_VERSION);

    const resolution = resolveAnchor(anchor, PROJECTION);

    // The listed edge case. Without context it would land on the first occurrence and move the
    // comment to a different paragraph without telling anyone.
    expect(resolution.resolved).toBe(true);
    expect(resolution.start).toBe(second);
  });

  it('orphans rather than binding a vanished passage somewhere else', () => {
    const resolution = resolveAnchor(
      {
        kind: 'text',
        exact: 'A sentence that is not in this document at all.',
        prefix: 'context ',
        suffix: ' context',
        start: 10,
        end: 57,
        renderVersion: RENDER_VERSION,
      },
      PROJECTION,
    );

    expect(resolution.resolved).toBe(false);
    expect(resolution.quality).toBe('not-found');
  });

  it('still resolves a lightly edited passage approximately', () => {
    const start = PROJECTION.indexOf('The audit trail outlives');
    const anchor = createTextAnchor(PROJECTION, start, start + 24, RENDER_VERSION);

    const drifted = PROJECTION.replace('audit trail outlives', 'audit trail outlasts');
    const resolution = resolveAnchor(anchor, drifted);

    expect(resolution.resolved).toBe(true);
    expect(resolution.quality).toBe('approximate');
  });

  it('orphans an empty projection rather than throwing', () => {
    const anchor = createTextAnchor(PROJECTION, 0, 10, RENDER_VERSION);
    expect(resolveAnchor(anchor, '').resolved).toBe(false);
  });
});

describe('splitIntoBlocks', () => {
  it('produces one addressable block per non-empty line', () => {
    const blocks = splitIntoBlocks(PROJECTION);

    expect(blocks).toHaveLength(4);
    expect(blocks[1].text).toBe('Retention is enforced by the platform.');
  });

  it('gives offsets that index back into the original projection', () => {
    // The keyboard and region paths build anchors from these offsets, so if they did not line up
    // with the stored projection every keyboard-made comment would orphan immediately.
    for (const block of splitIntoBlocks(PROJECTION)) {
      expect(PROJECTION.slice(block.start, block.end)).toBe(block.text);
    }
  });

  it('ignores blank lines', () => {
    expect(splitIntoBlocks('One\n\n\nTwo')).toHaveLength(2);
  });
});
