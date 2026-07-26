/**
 * The access-scope and no-backup notices (T049).
 *
 * These carry FR-057, FR-075, and SC-016, and they are the entire mitigation for a decision the
 * spec took deliberately. Clarification Q1 chose "any authenticated organization member holding
 * the link may view and comment", which means link possession equals access. There is no access
 * list to get wrong and no invitation to forget — and no way to take it back either.
 *
 * The residual risk was accepted on the condition that uploaders are told before they share, not
 * after something goes somewhere they did not expect. Deleting this component removes the
 * mitigation, not just some text.
 */
export function ScopeNotice() {
  return (
    <aside className="scope-notice" aria-labelledby="scope-notice-heading">
      <h2 id="scope-notice-heading">Before you upload</h2>
      <ul>
        <li>
          <strong>Anyone in your organization with the link can open this file and comment on it.</strong>{' '}
          There is no per-person access list — the link is the access grant, and a forwarded link works
          just as well as the original.
        </li>
        <li>
          <strong>It deletes itself after 24 hours</strong>, and can never be kept longer than 30 days
          from upload. You can extend it up to that limit, or delete it sooner.
        </li>
        <li>
          <strong>There is no backup.</strong> Once a file expires it is gone, along with its comments.
          Download a copy if you need to keep the review.
        </li>
        <li>Uploads, views, and comments are recorded in an audit trail that outlives the file.</li>
      </ul>
    </aside>
  );
}
