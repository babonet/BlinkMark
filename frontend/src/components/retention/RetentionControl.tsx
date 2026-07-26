import { useState } from 'react';
import { apiClient } from '../../services/apiClient';

interface RetentionControlProps {
  fileId: string;
  expiresAt: string;
  maxExpiresAt: string;
  retentionNotice: string;
  onChanged: (expiresAt: string, retentionNotice: string) => void;
}

/**
 * Remaining lifetime, and the control to extend it (T076, FR-026 to FR-029, FR-075).
 *
 * The ceiling is presented as a fact about the file rather than as an error waiting to happen:
 * the date input is bounded by `maxExpiresAt`, and the ceiling is stated in words above it. A
 * user should find out that 30 days is the limit before they pick day 45, not afterwards.
 *
 * The server still refuses an out-of-range value, and that refusal is the real enforcement — this
 * bound is a courtesy. Treating a disabled input as a security control is how bypasses happen.
 */
export function RetentionControl({
  fileId,
  expiresAt,
  maxExpiresAt,
  retentionNotice,
  onChanged,
}: RetentionControlProps) {
  const [target, setTarget] = useState(() => toDateInputValue(expiresAt));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  const atCeiling = new Date(expiresAt).getTime() >= new Date(maxExpiresAt).getTime();

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    setStatus(null);

    try {
      // End of the chosen day, so "keep it until Friday" means all of Friday.
      const chosen = new Date(`${target}T23:59:59Z`).toISOString();
      const result = await apiClient.extendRetention(fileId, chosen);

      onChanged(result.expiresAt, result.retentionNotice);
      setStatus(`This file now deletes itself on ${formatDate(result.expiresAt)}.`);
    } catch {
      setError(
        'That expiry could not be applied. A file can never be kept longer than 30 days from upload.',
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="retention-heading">
      <h2 id="retention-heading">Retention</h2>

      {/* FR-075. Restated here, next to the control that changes it. */}
      <p className="retention-notice">{retentionNotice}</p>

      {atCeiling ? (
        <p>
          This file is already set to its latest possible expiry,{' '}
          <time dateTime={maxExpiresAt}>{formatDate(maxExpiresAt)}</time>. It cannot be kept any
          longer — download a copy if you need one after that.
        </p>
      ) : (
        <form onSubmit={handleSubmit}>
          <label htmlFor="retention-date">Keep this file until</label>
          <input
            id="retention-date"
            type="date"
            value={target}
            min={toDateInputValue(new Date().toISOString())}
            max={toDateInputValue(maxExpiresAt)}
            onChange={(event) => setTarget(event.target.value)}
            aria-describedby="retention-ceiling"
          />

          <p id="retention-ceiling">
            The latest this file can be kept is{' '}
            <time dateTime={maxExpiresAt}>{formatDate(maxExpiresAt)}</time>, 30 days after it was
            uploaded. That limit cannot be raised by anyone.
          </p>

          <button type="submit" disabled={busy}>
            {busy ? 'Updating…' : 'Update expiry'}
          </button>
        </form>
      )}

      <p aria-live="polite">{status}</p>

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}

function toDateInputValue(iso: string): string {
  return new Date(iso).toISOString().slice(0, 10);
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    dateStyle: 'long',
    timeStyle: 'short',
  });
}
