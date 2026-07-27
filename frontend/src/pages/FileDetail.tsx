import { useCallback, useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useMsal } from '@azure/msal-react';
import {
  ApiError,
  apiClient,
  type Anchor,
  type Comment,
  type DocumentBlock,
  type FileDetail,
} from '../services/apiClient';
import { PreviewFrame } from '../components/preview/PreviewFrame';
import { PassageSelector } from '../components/comments/PassageSelector';
import { CommentSidebar } from '../components/comments/CommentSidebar';
import { RetentionControl } from '../components/retention/RetentionControl';
import { DownloadButton } from '../components/retention/DownloadButton';
import { ShareBar } from '../components/share/ShareBar';
import { CommentIcon } from '../components/icons/Icons';
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
  const [blocks, setBlocks] = useState<DocumentBlock[]>([]);
  const [renderVersion, setRenderVersion] = useState<string>('');
  const [comments, setComments] = useState<Comment[]>([]);
  const [pendingAnchor, setPendingAnchor] = useState<Anchor | null>(null);
  const [error, setError] = useState<string | null>(null);

  /** The exact rendering is opt-in; it costs a preview token and a cross-origin frame to show. */
  const [showRendering, setShowRendering] = useState(false);

  /** Comments can be put away so the document has the full width. */
  const [showComments, setShowComments] = useState(true);

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
      setBlocks(content.blocks);
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

  /**
   * Selecting a passage always brings the comments back.
   *
   * Without this, choosing a passage while comments are hidden would open a composer the user
   * cannot see, and the selection would look as though it had done nothing.
   */
  function handleSelectPassage(anchor: Anchor) {
    setPendingAnchor(anchor);
    setShowComments(true);
  }

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

  const liveComments = comments.filter((comment) => comment.deletedAt === null).length;

  return (
    <section className="file-page">
      <header className="file-header">
        <div className="file-header__title">
          <h1>{file.displayName}</h1>

          <p className="file-meta">
            Uploaded by {file.ownerDisplayName}. Deletes itself{' '}
            <time dateTime={file.expiresAt}>{formatRemaining(file.expiresAt)}</time>.
          </p>
        </div>

        <div className="file-header__actions">
          <ShareBar accessScopeNotice={file.accessScopeNotice} />

          {/*
            Hiding the comments gives the document the whole width, which is the point: reading a
            long draft and reading the discussion about it are different activities, and the
            second does not need to be on screen during the first.

            aria-expanded rather than aria-pressed. The button controls the visibility of a region
            that exists in the document, which is what aria-expanded describes; aria-pressed would
            say this is a setting rather than a disclosure.
          */}
          <button
            type="button"
            className="compact"
            aria-expanded={showComments}
            aria-controls="comments"
            onClick={() => setShowComments(!showComments)}
          >
            <CommentIcon />
            {showComments ? 'Hide comments' : 'Show comments'}
            {liveComments > 0 && <span className="count-badge">{liveComments}</span>}
          </button>
        </div>
      </header>

      <div className={showComments ? 'file-layout' : 'file-layout file-layout--wide'}>
        <div className="file-main">
          {/*
            One document, not two views of it. An earlier version put a "Text" tab beside the
            rendered preview, which exposed an implementation detail — the normalized text
            projection — as though it were a feature. A reviewer wants to read the document and
            comment on it; that is now the only thing on offer, and it renders with its headings,
            lists and quotations intact.

            The rendered preview is still available below for the one job it is uniquely good at:
            showing exactly how the file renders. It cannot be commented on, because it is a
            sandboxed cross-origin frame whose DOM this application deliberately cannot reach.
          */}
          <PassageSelector
            title={file.displayName}
            projection={projection}
            blocks={blocks}
            renderVersion={renderVersion}
            onSelect={handleSelectPassage}
            highlights={highlights}
          />

          <details
            className="exact-rendering"
            open={showRendering}
            onToggle={(event) => setShowRendering(event.currentTarget.open)}
          >
            <summary>See exactly how this file renders</summary>
            <p className="view-note">
              Isolated in a sandbox so that nothing in it can reach your session, which also means text here
              cannot be selected. Comment on the document above.
            </p>
            {showRendering && (
              <PreviewFrame
                fileId={file.id}
                displayName={file.displayName}
                previewUrl={file.previewUrl}
                onPreviewUrlChanged={(previewUrl) => setFile({ ...file, previewUrl })}
              />
            )}
          </details>
        </div>

        {/*
          Kept in the DOM when hidden, rather than unmounted. The skip link at the top of the
          preview points at #comments, and a target that disappears turns that link into a dead
          end. Hidden with the `hidden` attribute, so it is out of the tab order and out of the
          accessibility tree too — not merely invisible.
        */}
        <div id="comments" tabIndex={-1} hidden={!showComments}>
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
