import { describe, expect, it } from 'vitest';
import { formatRemaining } from '../../src/pages/FileList';

/**
 * Remaining-time display.
 *
 * Shown as a duration rather than a timestamp because that is the question people actually ask,
 * and because the answer decides whether someone remembers to extend a file before it deletes
 * itself.
 */
describe('formatRemaining', () => {
  const now = new Date('2026-03-01T09:00:00Z');

  it('reports minutes for the last hour', () => {
    expect(formatRemaining('2026-03-01T09:45:00Z', now)).toBe('in 45 minutes');
  });

  it('reports hours up to two days', () => {
    expect(formatRemaining('2026-03-02T00:00:00Z', now)).toBe('in 15 hours');
  });

  it('reports days beyond that', () => {
    expect(formatRemaining('2026-03-08T09:00:00Z', now)).toBe('in 7 days');
  });

  it('says a file has expired rather than showing a negative duration', () => {
    expect(formatRemaining('2026-03-01T08:00:00Z', now)).toBe('Expired');
  });

  it('treats the exact moment of expiry as expired', () => {
    // Not "in 0 minutes". An expired file reads as gone the instant it expires (FR-032).
    expect(formatRemaining('2026-03-01T09:00:00Z', now)).toBe('Expired');
  });
});
