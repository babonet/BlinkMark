import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { apiClient, type FileListResponse } from '../services/apiClient';

/**
 * Formats the time remaining before a file deletes itself.
 *
 * Shown as a duration rather than a timestamp because that is the question people actually ask.
 * "Expires 2026-03-02T09:00:00Z" requires arithmetic; "in 3 hours" does not, and the difference
 * decides whether someone remembers to extend it.
 */
export function formatRemaining(expiresAt: string, now: Date = new Date()): string {
  const remainingMs = new Date(expiresAt).getTime() - now.getTime();

  if (remainingMs <= 0) {
    return 'Expired';
  }

  const minutes = Math.floor(remainingMs / 60_000);
  if (minutes < 60) {
    return `in ${minutes} minute${minutes === 1 ? '' : 's'}`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 48) {
    return `in ${hours} hour${hours === 1 ? '' : 's'}`;
  }

  const days = Math.floor(hours / 24);
  return `in ${days} day${days === 1 ? '' : 's'}`;
}

/** The caller's live files, with their quota position (T047). */
export function FileListPage() {
  const [data, setData] = useState<FileListResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    apiClient
      .listFiles()
      .then(setData)
      .catch(() => setError('Your files could not be loaded.'));
  }, []);

  if (error) {
    return (
      <section>
        <h1>My files</h1>
        <p className="error" role="alert">
          {error}
        </p>
      </section>
    );
  }

  if (!data) {
    return (
      <section>
        <h1>My files</h1>
        <p aria-live="polite">Loading…</p>
      </section>
    );
  }

  return (
    <section>
      <h1>My files</h1>

      <p>
        {data.quota.liveFiles} of {data.quota.maxLiveFiles} live files. Expired files disappear on
        their own and free up capacity.
      </p>

      {data.files.length === 0 ? (
        <p>
          Nothing here yet. <Link to="/upload">Upload a draft</Link> to get a link you can share.
        </p>
      ) : (
        <table>
          <caption className="visually-hidden">Your live files and when each one deletes itself</caption>
          <thead>
            <tr>
              <th scope="col">Name</th>
              <th scope="col">Type</th>
              <th scope="col">Uploaded</th>
              <th scope="col">Deletes itself</th>
            </tr>
          </thead>
          <tbody>
            {data.files.map((file) => (
              <tr key={file.id}>
                <td>
                  <Link to={`/files/${file.id}`}>{file.displayName}</Link>
                </td>
                <td>{file.contentType}</td>
                <td>
                  <time dateTime={file.uploadedAt}>{new Date(file.uploadedAt).toLocaleString()}</time>
                </td>
                <td>
                  {/* The machine-readable timestamp is in the attribute; the human-readable
                      duration is the visible text. Both audiences are served without either one
                      having to do arithmetic. */}
                  <time dateTime={file.expiresAt}>{formatRemaining(file.expiresAt)}</time>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
