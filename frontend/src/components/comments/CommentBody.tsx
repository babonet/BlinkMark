interface CommentBodyProps {
  body: string;
  deleted: boolean;
}

/**
 * Renders a comment body as literal text (T067, FR-024).
 *
 * The whole of this component is the point: it puts the string in a text node and does nothing
 * else. No Markdown, no autolinking, no `dangerouslySetInnerHTML`, no "just let them bold things".
 *
 * A comment body is untrusted input. That the author is authenticated says something about who
 * they are and nothing at all about whether their text is safe to interpret — and unlike uploaded
 * files, comments render on the *application* origin, where there is no sandbox and no separate
 * host to contain a mistake. The lint config forbids `dangerouslySetInnerHTML` repo-wide for this
 * reason.
 *
 * `white-space: pre-wrap` preserves the author's line breaks without interpreting anything.
 */
export function CommentBody({ body, deleted }: CommentBodyProps) {
  if (deleted) {
    return (
      <p className="comment-body comment-body--deleted">
        <em>This comment was deleted by its author.</em>
      </p>
    );
  }

  return <p className="comment-body">{body}</p>;
}
