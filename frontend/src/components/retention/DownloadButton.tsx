import { useState } from 'react';
import { apiClient } from '../../services/apiClient';

interface DownloadButtonProps {
  fileId: string;
  displayName: string;
}

/**
 * Downloads the file with all its comments (T077, FR-031, FR-076).
 *
 * The notice below the button is the part that is easy to cut and should not be. Everything else
 * in BlinkMark is governed by retention — the file deletes itself, and that promise is what makes
 * it safe to share a draft. A download is the one action that breaks that promise on purpose, and
 * the person taking it becomes responsible for a copy that BlinkMark will never delete.
 *
 * Saying so before the click, not in a confirmation dialog afterwards, is the whole control. The
 * same text is written into the bundle itself, so it survives the moment.
 */
export function DownloadButton({ fileId, displayName }: DownloadButtonProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleDownload() {
    setBusy(true);
    setError(null);

    try {
      const blob = await apiClient.downloadBundle(fileId);
      const url = URL.createObjectURL(blob);

      try {
        const link = document.createElement('a');
        link.href = url;
        link.download = `${displayName.replace(/[/\\]/g, '_')}-blinkmark.zip`;
        link.click();
      } finally {
        // Released immediately; the blob is already handed to the browser's download manager.
        URL.revokeObjectURL(url);
      }
    } catch {
      setError('The download could not be prepared. The file may have expired.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="download-heading">
      <h2 id="download-heading">Download</h2>

      <p id="download-notice">
        Downloading gives you the file and every comment on it, including comments whose passage
        has since been edited away. <strong>Your copy is not covered by BlinkMark&rsquo;s
        retention</strong> — the file here still deletes itself on schedule, but your download will
        not. Store it according to your organization&rsquo;s data policy.
      </p>

      <button
        type="button"
        onClick={() => void handleDownload()}
        disabled={busy}
        aria-describedby="download-notice"
      >
        {busy ? 'Preparing…' : 'Download file and comments'}
      </button>

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}
