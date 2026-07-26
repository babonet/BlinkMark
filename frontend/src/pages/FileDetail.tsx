import { useCallback, useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useMsal } from '@azure/msal-react';
import { ApiError, apiClient, type Anchor, type Comment, type FileDetail } from '../services/apiClient';
import { PreviewFrame } from '../components/preview/PreviewFrame';
import { PassageSelector } from '../components/comments/PassageSelector';
import { CommentSidebar } from '../components/comments/CommentSidebar';
import { RetentionControl } from '../components/retention/RetentionControl';
import { DownloadButton } from '../components/retention/DownloadButton';
import { ShareBar } from '../components/share/ShareBar';
import { isLocalDevAuth, localAccount } from '../services/localAuth';
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

  /** Which representation of the document is on screen. Text is the one you can work in. */
  const [view, setView] = useState<'text' | 'rendered'>('text');

  /** Owner-only: hide the owner controls to see what a reviewer sees. */
  const [asReviewer, setAsReviewer] = useState(false);

  const currentUserId = isLocalDevAuth
    ? (localAccount().idTokenClaims.oid ?? '')
    : ((instance.getActiveAccount()?.idTokenClaims as { oid?: string } | undefined)?.oid ?? '');

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
      <header className="file-header">
        <h1>{file.displayName}</h1>

        <p className="file-meta">
          Uploaded by {file.ownerDisplayName}. Deletes itself{' '}
          <time dateTime={file.expiresAt}>{formatRemaining(file.expiresAt)}</time>.
        </p>
      </header>

      <ShareBar accessScopeNotice={file.accessScopeNotice} />

      <div className="file-layout">
        <div className="file-main">
          {/*
            The text comes first, and the rendered preview is something you switch to.
            That ordering is the opposite of the obvious one, and it is deliberate: the preview
            is a cross-origin sandboxed iframe, so nothing in it can be selected or commented on.
            Leading with it puts the one surface you cannot work in at the top of the page and
            hides the one you can (research.md R2).
          */}
          <div className="view-switch" role="group" aria-label="How to view this document">
            <button
              type="button"
              className={view === 'text' ? 'view-switch__option is-selected' : 'view-switch__option'}
              aria-pressed={view === 'text'}
              onClick={() => setView('text')}
            >
              Text
              <span className="view-switch__hint">select passages and comment</span>
            </button>
            <button
              type="button"
              className={view === 'rendered' ? 'view-switch__option is-selected' : 'view-switch__option'}
              aria-pressed={view === 'rendered'}
              onClick={() => setView('rendered')}
            >
              Rendered
              <span className="view-switch__hint">check formatting, read-only</span>
            </button>
          </div>

          {view === 'text' ? (
            <PassageSelector
              projection={projection}
              renderVersion={renderVersion}
              onSelect={setPendingAnchor}
              highlights={highlights}
            />
          ) : (
            <>
              <p className="view-note">
                This is how the document renders. It is isolated in a sandbox so that nothing in it can reach
                your session, which also means you cannot select text here — switch back to{' '}
                <strong>Text</strong> to comment.
              </p>
              <PreviewFrame
                fileId={file.id}
                displayName={file.displayName}
                previewUrl={file.previewUrl}
                onPreviewUrlChanged={(previewUrl) => setFile({ ...file, previewUrl })}
              />
            </>
          )}
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
        <section className="owner-actions" aria-labelledby="owner-actions-heading">
          <div className="owner-actions__bar">
            <h2 id="owner-actions-heading">Owner actions</h2>

            {/*
              Answers "what does a reviewer see?" without needing a second account. It hides the
              owner-only controls and nothing else — it does not change permissions, and saying so
              on the button matters, because a toggle that looked like impersonation would invite
              exactly the wrong conclusion about what BlinkMark can enforce.
            */}
            <button
              type="button"
              className="secondary"
              aria-pressed={asReviewer}
              onClick={() => setAsReviewer(!asReviewer)}
            >
              {asReviewer ? 'Back to owner view' : 'See the reviewer view'}
            </button>
          </div>

          {asReviewer ? (
            <p className="view-note">
              Owner controls are hidden. This is what everyone else sees: the document, the comments, and no
              way to change how long the file lives or to delete it. Their permissions are unchanged by this
              toggle — it only hides the buttons.
            </p>
          ) : (
            <>
              <RetentionControl
                fileId={file.id}
                expiresAt={file.expiresAt}
                maxExpiresAt={file.maxExpiresAt}
                retentionNotice={file.retentionNotice}
                onChanged={(expiresAt, retentionNotice) => setFile({ ...file, expiresAt, retentionNotice })}
              />

              <DownloadButton fileId={file.id} displayName={file.displayName} />

              <button type="button" className="danger" onClick={() => void handleDelete()}>
                Delete now
              </button>
            </>
          )}
        </section>
      )}
    </section>
  );
}
