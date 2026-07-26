import { useEffect, useRef, useState, type FormEvent } from 'react';
import type { Anchor, Comment } from '../../services/apiClient';
import { DeleteIcon, EditIcon, ReplyIcon } from '../icons/Icons';
import { CommentBody } from './CommentBody';

interface CommentSidebarProps {
  comments: Comment[];
  currentUserId: string;
  pendingAnchor: Anchor | null;
  onCreate: (body: string, anchor: Anchor, parentId?: string) => Promise<void>;
  onEdit: (commentId: string, body: string) => Promise<void>;
  onDelete: (commentId: string) => Promise<void>;
  onCancelPending: () => void;
}

interface Thread {
  threadId: string;
  root: Comment;
  replies: Comment[];
  orphaned: boolean;
}

function buildThreads(comments: Comment[]): Thread[] {
  const byThread = new Map<string, Comment[]>();

  for (const comment of comments) {
    const existing = byThread.get(comment.threadId);
    if (existing) {
      existing.push(comment);
    } else {
      byThread.set(comment.threadId, [comment]);
    }
  }

  return [...byThread.entries()]
    .map(([threadId, all]) => {
      const ordered = [...all].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
      const root = ordered.find((comment) => comment.id === threadId) ?? ordered[0];
      return {
        threadId,
        root,
        replies: ordered.filter((comment) => comment.id !== root.id),
        orphaned: root.anchorState === 'orphaned',
      };
    })
    .sort((a, b) => a.root.createdAt.localeCompare(b.root.createdAt));
}

/**
 * The comment sidebar (T066, T122).
 *
 * The orphaned section is the part that matters. Principle III forbids a comment that lost its
 * passage from being hidden or from silently moving, so orphans get their own clearly labelled
 * group, keep their quoted context, and stay fully readable and repliable. The failure is
 * surfaced rather than smoothed over — a reviewer can see that a comment refers to something the
 * document no longer contains, and decide what to do about it.
 *
 * Orphaned state is conveyed three ways, not one (FR-079): a heading that says so, a per-comment
 * text label, and only *then* the dashed border. A state carried by border style alone does not
 * exist for a screen-reader user, and does not exist for anyone at all in high-contrast mode.
 */
export function CommentSidebar({
  comments,
  currentUserId,
  pendingAnchor,
  onCreate,
  onEdit,
  onDelete,
  onCancelPending,
}: CommentSidebarProps) {
  const [draft, setDraft] = useState('');
  const [replyTo, setReplyTo] = useState<string | null>(null);
  const [replyDraft, setReplyDraft] = useState('');
  const [editing, setEditing] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState('');
  const [confirmingDelete, setConfirmingDelete] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const composerRef = useRef<HTMLTextAreaElement>(null);

  /*
   * Focus the composer when a passage is selected.
   *
   * This replaces `autoFocus`, which jsx-a11y flags for good reason: it moves focus because a
   * thing rendered, which from the user's side is focus moving on its own. Here focus moves
   * because the user just selected a passage and asked to comment on it, and it moves to the
   * field they asked for. That is the distinction the rule is really drawing, and it matters
   * most for the keyboard path — without it, committing a selection would leave the user in the
   * transcript with no indication that a composer had appeared somewhere else on the page.
   */
  useEffect(() => {
    if (pendingAnchor) {
      composerRef.current?.focus();
    }
  }, [pendingAnchor]);

  const threads = buildThreads(comments);
  const anchored = threads.filter((thread) => !thread.orphaned);
  const orphaned = threads.filter((thread) => thread.orphaned);

  async function handleCreate(event: FormEvent) {
    event.preventDefault();
    if (!pendingAnchor || draft.trim().length === 0) {
      return;
    }

    setBusy(true);
    try {
      await onCreate(draft.trim(), pendingAnchor);
      setDraft('');
    } finally {
      setBusy(false);
    }
  }

  async function handleReply(event: FormEvent, thread: Thread) {
    event.preventDefault();
    if (replyDraft.trim().length === 0) {
      return;
    }

    setBusy(true);
    try {
      await onCreate(replyDraft.trim(), thread.root.anchor, thread.root.id);
      setReplyDraft('');
      setReplyTo(null);
    } finally {
      setBusy(false);
    }
  }

  async function handleEdit(event: FormEvent, comment: Comment) {
    event.preventDefault();
    setBusy(true);
    try {
      await onEdit(comment.id, editDraft.trim());
      setEditing(null);
    } finally {
      setBusy(false);
    }
  }

  function renderComment(comment: Comment, orphanedThread: boolean) {
    const mine = comment.authorId === currentUserId;

    return (
      <article
        key={comment.id}
        className={`comment${orphanedThread ? ' orphaned' : ''}`}
        aria-label={
          orphanedThread
            ? `Comment by ${comment.authorDisplayName}, orphaned — the passage it referred to can no longer be found`
            : `Comment by ${comment.authorDisplayName}`
        }
      >
        <header>
          <strong>{comment.authorDisplayName}</strong>
          <time dateTime={comment.createdAt}>{new Date(comment.createdAt).toLocaleString()}</time>
          {comment.editedAt && <span className="comment-edited"> (edited)</span>}
          {comment.actingAgentId && (
            // FR-048. A human's name on a comment must not imply a human wrote it.
            <span
              className="agent-badge"
              title={`Written by an agent on behalf of ${comment.authorDisplayName}`}
            >
              via agent
            </span>
          )}
        </header>

        {editing === comment.id ? (
          <form onSubmit={(event) => void handleEdit(event, comment)}>
            <label htmlFor={`edit-${comment.id}`} className="visually-hidden">
              Edit your comment
            </label>
            <textarea
              id={`edit-${comment.id}`}
              value={editDraft}
              onChange={(event) => setEditDraft(event.target.value)}
              rows={3}
            />
            <button type="submit" disabled={busy}>
              Save
            </button>
            <button type="button" className="secondary" onClick={() => setEditing(null)}>
              Cancel
            </button>
          </form>
        ) : (
          <CommentBody body={comment.body} deleted={comment.deletedAt !== null} />
        )}

        {mine && !comment.deletedAt && editing !== comment.id && (
          <div className="comment-actions">
            {confirmingDelete === comment.id ? (
              /*
                A two-step delete, and only for the icon.
                
                An icon-only destructive control is precisely where a mis-click happens: there is
                no word to read before the pointer lands. The confirmation is worded rather than
                iconic for the same reason — the moment something is about to be destroyed is the
                wrong moment to make somebody interpret a picture.
              */
              <>
                <span className="comment-actions__prompt">Delete this comment?</span>
                <button
                  type="button"
                  className="danger compact"
                  onClick={() => {
                    setConfirmingDelete(null);
                    void onDelete(comment.id);
                  }}
                >
                  Delete
                </button>
                <button type="button" className="secondary compact" onClick={() => setConfirmingDelete(null)}>
                  Cancel
                </button>
              </>
            ) : (
              <>
                <button
                  type="button"
                  className="icon-button"
                  title="Edit your comment"
                  onClick={() => {
                    setEditing(comment.id);
                    setEditDraft(comment.body);
                  }}
                >
                  <EditIcon />
                  <span className="visually-hidden">Edit your comment</span>
                </button>
                <button
                  type="button"
                  className="icon-button icon-button--danger"
                  title="Delete your comment"
                  onClick={() => setConfirmingDelete(comment.id)}
                >
                  <DeleteIcon />
                  <span className="visually-hidden">Delete your comment</span>
                </button>
              </>
            )}
          </div>
        )}
      </article>
    );
  }

  function renderThread(thread: Thread) {
    return (
      <li key={thread.threadId} className="thread">
        <blockquote className="comment-quote">{thread.root.anchor.exact}</blockquote>

        {thread.orphaned && (
          <p className="orphan-note">
            The passage this refers to can no longer be found in the document. The comment is kept with the
            text it was written about.
          </p>
        )}

        {renderComment(thread.root, thread.orphaned)}

        {/*
          Replies are a nested list, not a flat continuation. The indentation is the cheap part;
          the reason it is a <ul> is that a screen-reader user gets "list, 3 items" and knows the
          shape of the conversation without having to infer it from the order, which is exactly
          what the visual indent gives everyone else.
        */}
        {thread.replies.length > 0 && (
          <ul
            className="reply-list"
            aria-label={`${thread.replies.length} repl${thread.replies.length === 1 ? 'y' : 'ies'}`}
          >
            {thread.replies.map((reply) => (
              <li key={reply.id}>{renderComment(reply, thread.orphaned)}</li>
            ))}
          </ul>
        )}

        {replyTo === thread.threadId ? (
          <form className="reply-form" onSubmit={(event) => void handleReply(event, thread)}>
            <label htmlFor={`reply-${thread.threadId}`} className="visually-hidden">
              Reply to this thread
            </label>
            <textarea
              id={`reply-${thread.threadId}`}
              value={replyDraft}
              onChange={(event) => setReplyDraft(event.target.value)}
              rows={2}
              placeholder="Reply…"
            />
            <button type="submit" disabled={busy}>
              Reply
            </button>
            <button type="button" className="secondary" onClick={() => setReplyTo(null)}>
              Cancel
            </button>
          </form>
        ) : (
          /*
            Reply keeps its word alongside the icon. It is the action a reviewer is most likely to
            want and least likely to look for, and an unlabelled arrow beneath a comment reads as
            ambiguous in a way that a pencil beside your own words does not.
          */
          <button
            type="button"
            className="secondary compact reply-button"
            onClick={() => setReplyTo(thread.threadId)}
          >
            <ReplyIcon />
            Reply
            <span className="visually-hidden">
              {' '}
              to the thread about “{thread.root.anchor.exact.slice(0, 40)}”
            </span>
          </button>
        )}
      </li>
    );
  }

  return (
    <aside className="comment-sidebar" aria-labelledby="comments-heading">
      <h2 id="comments-heading">Comments</h2>

      {pendingAnchor && (
        <form onSubmit={(event) => void handleCreate(event)} className="comment-compose">
          <p>
            Commenting on: <q>{pendingAnchor.exact}</q>
          </p>
          <label htmlFor="new-comment">Your comment</label>
          <textarea
            id="new-comment"
            ref={composerRef}
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            rows={3}
          />
          <button type="submit" disabled={busy || draft.trim().length === 0}>
            Comment
          </button>
          <button type="button" className="secondary" onClick={onCancelPending}>
            Cancel
          </button>
        </form>
      )}

      <section aria-labelledby="anchored-heading">
        <h3 id="anchored-heading">On the document ({anchored.length})</h3>
        {anchored.length === 0 ? (
          <p>No comments yet. Select a passage to start one.</p>
        ) : (
          <ul className="thread-list">{anchored.map(renderThread)}</ul>
        )}
      </section>

      {orphaned.length > 0 && (
        <section aria-labelledby="orphaned-heading">
          <h3 id="orphaned-heading">Orphaned ({orphaned.length})</h3>
          <p>
            These comments refer to passages that can no longer be found. They are kept here rather than
            deleted or moved.
          </p>
          <ul className="thread-list">{orphaned.map(renderThread)}</ul>
        </section>
      )}
    </aside>
  );
}
