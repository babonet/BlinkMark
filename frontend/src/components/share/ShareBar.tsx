import { useEffect, useRef, useState } from 'react';
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

  const fallbackRef = useRef<HTMLInputElement>(null);

  const link = window.location.href;

  /*
   * Focus and select the fallback link the moment it appears.
   *
   * This replaces `autoFocus`, which jsx-a11y flags because it moves focus merely because
   * something rendered. Here focus moves because the user pressed Copy and the browser refused;
   * they asked for the link, so putting the cursor on it and selecting it is finishing the job
   * they started rather than hijacking their place on the page.
   */
  useEffect(() => {
    if (error) {
      fallbackRef.current?.select();
    }
  }, [error]);

  async function handleCopy() {
    setError(null);

    try {
      await navigator.clipboard.writeText(link);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 4000);
    } catch {
      // Clipboard access can be refused by browser policy or by a permissions prompt, and hiding
      // the URL removed the fallback that used to exist. So the failure path puts it back: the
      // link appears, selected, to be copied by hand. A share button that can silently fail with
      // no way through is worse than one that never looked tidy.
      setError('Copying was blocked by your browser. Here is the link — copy it manually.');
    }
  }

  return (
    <section className="share-bar" aria-labelledby="share-heading">
      {/*
        Visually hidden. The button says what it does, so a "Share" heading above it spends a line
        of vertical space on something the layout already conveys. It stays in the markup because
        the landmark needs a name.
      */}
      <h2 id="share-heading" className="visually-hidden">
        Share
      </h2>

      <button type="button" className="compact" onClick={() => void handleCopy()}>
        {copied ? <CheckIcon /> : <CopyIcon />}
        {copied ? 'Link copied' : 'Copy link'}
      </button>

      {/*
        FR-057 and SC-016. The URL itself is not shown — it is a ULID nobody reads and nobody
        retypes, and putting it on screen cost a third of the bar for no benefit. What replaces it
        is not nothing: this notice stays visible rather than moving behind a tooltip, because it
        is the mitigation Clarification Q1 accepted for having no per-person access list, and a
        control nobody reads is not a control.
      */}
      <p className="scope-notice share-scope">{accessScopeNotice}</p>

      {/* Polite, not assertive: confirming a copy should never interrupt what someone is doing. */}
      <p aria-live="polite" className="visually-hidden">
        {copied ? 'Link copied to the clipboard.' : ''}
      </p>

      {error && (
        <div className="share-fallback">
          <p className="error" role="alert">
            {error}
          </p>
          <label htmlFor="share-link" className="visually-hidden">
            Link to this file
          </label>
          <input
            id="share-link"
            ref={fallbackRef}
            type="text"
            value={link}
            readOnly
            onFocus={(event) => event.currentTarget.select()}
          />
        </div>
      )}
    </section>
  );
}
