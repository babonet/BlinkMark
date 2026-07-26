import { useState } from 'react';
import { CheckIcon, CopyIcon } from '../icons/Icons';

interface ShareBarProps {
  accessScopeNotice: string;
}

/**
 * Sharing (FR-004, FR-057).
 *
 * BlinkMark has no invite flow, no per-person access list, and no "add reviewer" dialog — and
 * this component is where that becomes obvious rather than confusing. Clarification Q1 decided
 * that the link *is* the access grant for anyone in the organization, which makes "copy the link
 * and send it to someone" the entire sharing model.
 *
 * That is a real trade-off, not an omission, so the scope notice sits next to the button rather
 * than buried further down the page. Someone about to paste a link into a channel of two hundred
 * people should find out what that means before they paste it, not afterwards.
 */
export function ShareBar({ accessScopeNotice }: ShareBarProps) {
  const [copied, setCopied] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const link = window.location.href;

  async function handleCopy() {
    setError(null);

    try {
      await navigator.clipboard.writeText(link);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 4000);
    } catch {
      // Clipboard access can be refused by policy or by a permissions prompt. The input below is
      // always present and selectable, so there is a way through that does not depend on it.
      setError('Copying was blocked. Select the link below and copy it manually.');
    }
  }

  return (
    <section className="share-bar" aria-labelledby="share-heading">
      {/*
        Visually hidden. The row below is self-evident to anyone who can see it — a link and a
        copy button — so a "Share" heading above it spends a line of vertical space on something
        the layout already says. It stays in the markup because the landmark needs a name.
      */}
      <h2 id="share-heading" className="visually-hidden">
        Share
      </h2>

      <div className="share-row">
        <label htmlFor="share-link" className="visually-hidden">
          Link to this file
        </label>
        <input
          id="share-link"
          type="text"
          value={link}
          readOnly
          onFocus={(event) => event.currentTarget.select()}
        />
        <button
          type="button"
          className="compact"
          title="Copy the link to this file"
          onClick={() => void handleCopy()}
        >
          {copied ? <CheckIcon /> : <CopyIcon />}
          {copied ? 'Copied' : 'Copy'}
        </button>

        {/*
          FR-057 and SC-016. This stays on screen rather than moving behind a tooltip or a
          disclosure: it is the mitigation for the residual risk that Clarification Q1 accepted,
          and a control nobody reads is not a control. It is compressed onto one line, not hidden.
        */}
        <p className="scope-notice share-scope">{accessScopeNotice}</p>
      </div>

      {/* Polite, not assertive: confirming a copy should never interrupt what someone is doing. */}
      <p aria-live="polite" className="visually-hidden">
        {copied ? 'Link copied to the clipboard.' : ''}
      </p>

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}
