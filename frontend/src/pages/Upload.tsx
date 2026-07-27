import { useEffect, useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { ApiError, apiClient, type QuotaStatus } from '../services/apiClient';
import { ScopeNotice } from '../components/upload/ScopeNotice';

const ACCEPTED_EXTENSIONS = '.html,.htm,.md,.markdown';
const MAX_SIZE_BYTES = 20 * 1024 * 1024;

/**
 * The upload screen (T046).
 *
 * Validation runs client-side purely so the user finds out quickly. The server validates
 * independently and is the only opinion that counts — client-side checks are a courtesy, and the
 * extension-versus-content check in particular can only be done where the bytes are read.
 */
export function UploadPage() {
  const navigate = useNavigate();

  const [file, setFile] = useState<File | null>(null);
  const [quota, setQuota] = useState<QuotaStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isUploading, setIsUploading] = useState(false);

  useEffect(() => {
    apiClient
      .listFiles()
      .then((response) => setQuota(response.quota))
      .catch(() => setQuota(null));
  }, []);

  function validateLocally(candidate: File): string | null {
    const extension = candidate.name.slice(candidate.name.lastIndexOf('.')).toLowerCase();

    if (!ACCEPTED_EXTENSIONS.split(',').includes(extension)) {
      return 'BlinkMark accepts .html, .htm, .md, and .markdown files.';
    }

    if (candidate.size > MAX_SIZE_BYTES) {
      return 'That file is larger than the 20 MB limit.';
    }

    if (candidate.size === 0) {
      return 'That file is empty.';
    }

    return null;
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();

    if (!file) {
      setError('Choose a file first.');
      return;
    }

    const localError = validateLocally(file);
    if (localError) {
      setError(localError);
      return;
    }

    setIsUploading(true);
    setError(null);

    try {
      const created = await apiClient.uploadFile(file);
      navigate(`/files/${created.id}`);
    } catch (uploadError) {
      if (uploadError instanceof ApiError) {
        // The server's message is the useful one: it names the extension-versus-content
        // disagreement, or the exact quota that was reached.
        setError(uploadError.problem.detail ?? uploadError.problem.title);
      } else {
        setError('The upload failed. Try again.');
      }
    } finally {
      setIsUploading(false);
    }
  }

  const isAtQuota = quota !== null && quota.liveFiles >= quota.maxLiveFiles;

  return (
    <section>
      <h1>Upload a draft</h1>

      <ScopeNotice />

      <form onSubmit={handleSubmit} noValidate>
        <div>
          <label htmlFor="file-input">Choose an HTML or Markdown file</label>
          <input
            id="file-input"
            type="file"
            accept={ACCEPTED_EXTENSIONS}
            aria-describedby="file-input-help"
            aria-invalid={error !== null}
            onChange={(event) => {
              setFile(event.target.files?.[0] ?? null);
              setError(null);
            }}
          />
          <p id="file-input-help">Up to 20 MB. The file is rendered safely — scripts never run.</p>
        </div>

        {error && (
          // role="alert" so the message is announced rather than only shown. A validation error a
          // screen-reader user cannot hear is a validation error that has not been reported.
          <p className="error" role="alert">
            {error}
          </p>
        )}

        {quota && (
          <p aria-live="polite">
            You have {quota.liveFiles} of {quota.maxLiveFiles} live files.
            {isAtQuota &&
              ' You have reached the limit. Delete one, or wait for one to expire — capacity frees itself as files reach their expiry.'}
          </p>
        )}

        <button type="submit" disabled={isUploading || isAtQuota}>
          {isUploading ? 'Uploading…' : 'Upload'}
        </button>
      </form>
    </section>
  );
}
