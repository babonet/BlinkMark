import { useCallback, useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useMsal } from '@azure/msal-react';
import { ApiError, apiClient, type Anchor, type Comment, type FileDetail } from '../services/apiClient';
import { PreviewFrame } from '../components/preview/PreviewFrame';
import { PassageSelector } from '../components/comments/PassageSelector';
import { CommentSidebar } from '../components/comments/CommentSidebar';
import { RetentionControl } from '../components/retention/RetentionControl';
import { DownloadButton } from '../components/retention/DownloadButton';
import { formatRemaining } from './FileList';

/**
 * A single file: its preview, its text, its comments, and its retention.
 *
 * The two-surface layout is a consequence of Principle IV rather than a design preference. The
 * iframe shows the document as it really renders, and cannot be read from here because it is
 * sandboxed cross-origin. The transcript beside it is the text projection — the same artifact the
 * server anchors against — and is where selection and commenting happen.
 */
export function FileDetailPage() {
  const { fileId } = useParams<{ fileId: string }>();
  const navigate = useNavigate();
  const { instance } = useMsal();

  const [file, setFile] = useState<FileDetail | null>(null);
  const [projection, setProjection] = useState<string>('');
  const [renderVersion, setRenderVersion] = useState<string>('');
  const [comments, setComments] = useState<Comment[]>([]);
  const [pendingAnchor, setPendingAnchor] = useState<Anchor | null>(null);
  const [error, setError] = useState<string | null>(null);

  const currentUserId =
    (instance.getActiveAccount()?.idTokenClaims as { oid?: string } | undefined)?.oid ?? '';

  const load = useCallback(async () => {
    if (!fileId) {
      return;
    }

    try {
      const [detail, content, existing] = await Promise.all([
        apiClient.getFile(fileId),
        apiClient.getFileContent(fileId),
        apiClient.listComments(fileId),
      ]);

      setFile(detail);
      setProjection(content.text);
      setRenderVersion(content.renderVersion);
      setComments(existing);
    } catch (loadError) {
      if (loadError instanceof ApiError && loadError.status === 404) {
        // Expired and never-existed are the same answer by design, so the message covers both
        // without claiming to know which happened.
        setError(
          'That file is not available. It may have expired — files delete themselves, and an expired file cannot be recovered.',
        );
      } else {
        setError('That file could not be loaded.');
      }
    }
  }, [fileId]);

  useEffect(() => {
    void load();
  }, [load]);

  async function handleCreate(body: string, anchor: Anchor, parentId?: string) {
    if (!fileId) return;
    await apiClient.createComment(fileId, body, anchor, parentId);
    setPendingAnchor(null);
    setComments(await apiClient.listComments(fileId));
  }

  async function handleEdit(commentId: string, body: string) {
    if (!fileId) return;
    await apiClient.editComment(fileId, commentId, body);
    setComments(await apiClient.listComments(fileId));
  }

  async function handleDeleteComment(commentId: string) {
    if (!fileId) return;
    await apiClient.deleteComment(fileId, commentId);
    setComments(await apiClient.listComments(fileId));
  }

  async function handleDelete() {
    if (!file) {
      return;
    }

    await apiClient.deleteFile(file.id);
    navigate('/files');
  }

  if (error) {
    return (
      <section>
        <h1>File</h1>
        <p className="error" role="alert">
          {error}
        </p>
      </section>
    );
  }

  if (!file) {
    return (
      <section>
        <h1>File</h1>
        <p aria-live="polite">Loading…</p>
      </section>
    );
  }

  // Only anchored comments highlight a passage; an orphan has no passage left to point at.
  const highlights = comments
    .filter((comment) => comment.anchorState === 'anchored' && comment.deletedAt === null)
    .map((comment) => ({
      start: comment.anchor.start,
      end: comment.anchor.end,
      commentCount: comments.filter((other) => other.threadId === comment.threadId).length,
    }));

  return (
    <section>
      <h1>{file.displayName}</h1>

      <p className="retention-notice">{file.retentionNotice}</p>

      <p>
        Uploaded by {file.ownerDisplayName}. Deletes itself{' '}
        <time dateTime={file.expiresAt}>{formatRemaining(file.expiresAt)}</time>.
      </p>

      <p className="scope-notice">{file.accessScopeNotice}</p>

      <div className="file-layout">
        <div className="file-main">
          <PreviewFrame
            fileId={file.id}
            displayName={file.displayName}
            previewUrl={file.previewUrl}
            onPreviewUrlChanged={(previewUrl) => setFile({ ...file, previewUrl })}
          />

          <PassageSelector
            projection={projection}
            renderVersion={renderVersion}
            onSelect={setPendingAnchor}
            highlights={highlights}
          />
        </div>

        <div id="comments" tabIndex={-1}>
          <CommentSidebar
            comments={comments}
            currentUserId={currentUserId}
            pendingAnchor={pendingAnchor}
            onCreate={handleCreate}
            onEdit={handleEdit}
            onDelete={handleDeleteComment}
            onCancelPending={() => setPendingAnchor(null)}
          />
        </div>
      </div>

      {file.isOwner && (
        <section aria-labelledby="owner-actions-heading">
          <h2 id="owner-actions-heading">Owner actions</h2>

          <RetentionControl
            fileId={file.id}
            expiresAt={file.expiresAt}
            maxExpiresAt={file.maxExpiresAt}
            retentionNotice={file.retentionNotice}
            onChanged={(expiresAt, retentionNotice) => setFile({ ...file, expiresAt, retentionNotice })}
          />

          <DownloadButton fileId={file.id} displayName={file.displayName} />

          <button type="button" className="secondary" onClick={() => void handleDelete()}>
            Delete now
          </button>
        </section>
      )}
    </section>
  );
}
