import { useEffect, useRef, useState } from 'react';
import { apiClient } from '../../services/apiClient';

interface PreviewFrameProps {
  fileId: string;
  displayName: string;
  previewUrl: string;
  /** Called when the URL is re-minted, so the parent can keep its copy current. */
  onPreviewUrlChanged?: (previewUrl: string) => void;
}

/**
 * The sandboxed preview host (T048, T123, T127).
 *
 * Three separate concerns meet in this component, and all three are requirements rather than
 * polish.
 *
 * **Isolation (Principle IV).** `sandbox` carries no tokens at all: no scripts, no same-origin,
 * no forms, no top-level navigation. Adding `allow-scripts` together with `allow-same-origin`
 * would collapse the isolation entirely and must never happen — the framed document would regain
 * access to the very origin the separate hostname exists to keep it away from.
 *
 * **Silent token renewal (T127).** The preview token lives fifteen minutes. That never
 * constrains a reviewer, because the token gates the document *fetch*, not the session — but it
 * does mean a frame reloaded after a laptop wake, a back-navigation, or a network blip will get a
 * 401. The contract requires that to be treated as "re-mint and retry" rather than as an error,
 * so the reader never sees an authentication failure for content they are still allowed to read.
 *
 * **Keyboard access (T123, FR-081).** An iframe is a focus trap by default. Without a documented
 * way in, a documented way out, and a skip link past it, a keyboard user reading a long document
 * cannot reach the comment list at all.
 */
export function PreviewFrame({ fileId, displayName, previewUrl, onPreviewUrlChanged }: PreviewFrameProps) {
  const frameRef = useRef<HTMLIFrameElement>(null);
  const wrapperRef = useRef<HTMLDivElement>(null);
  const mintedAtRef = useRef<number>(Date.now());

  const [currentUrl, setCurrentUrl] = useState(previewUrl);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');

  useEffect(() => {
    setCurrentUrl(previewUrl);
    mintedAtRef.current = Date.now();
  }, [previewUrl]);

  /**
   * Requests a fresh preview URL and reloads the frame.
   *
   * Deliberately silent. A reader who is still authorized must never be shown an authentication
   * error, so this path reports nothing unless the re-mint itself fails — which means the user
   * genuinely has lost access, or the file has expired.
   */
  async function reMintAndReload(): Promise<void> {
    try {
      const detail = await apiClient.getFile(fileId);
      mintedAtRef.current = Date.now();
      setCurrentUrl(detail.previewUrl);
      setStatus('ready');
      onPreviewUrlChanged?.(detail.previewUrl);
    } catch {
      setStatus('error');
    }
  }

  /**
   * Pre-emptive renewal, as the contract recommends.
   *
   * Waiting for the 401 works, but it costs a visible round trip at exactly the moment the user
   * is trying to read something. Ten minutes leaves five minutes of headroom inside the token's
   * fifteen-minute life.
   */
  useEffect(() => {
    function handleVisibilityChange() {
      const ageMinutes = (Date.now() - mintedAtRef.current) / 60_000;
      if (document.visibilityState === 'visible' && ageMinutes > 10) {
        void reMintAndReload();
      }
    }

    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => document.removeEventListener('visibilitychange', handleVisibilityChange);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fileId]);

  /**
   * The frame is cross-origin, so `load` fires for a 401 exactly as it does for a 200 and the
   * status code is unreadable from here. A probe request against the same URL is the only way to
   * tell the two apart — and it is cheap, because a successful probe is served from cache.
   */
  async function handleFrameLoad(): Promise<void> {
    try {
      const probe = await fetch(currentUrl, { method: 'GET', mode: 'no-cors', credentials: 'omit' });

      // An opaque response tells us nothing, which is the normal case for a healthy frame.
      if (probe.type !== 'opaque' && probe.status === 401) {
        await reMintAndReload();
        return;
      }
    } catch {
      // A blocked probe is not evidence of a problem. Only an explicit 401 triggers a re-mint.
    }

    setStatus('ready');
  }

  /*
   * There was an `Escape returns focus to the application` handler here. It was removed because
   * it could not work and the instruction it advertised was false.
   *
   * The preview is a cross-origin sandboxed iframe, so key events inside it belong to that
   * document and never bubble out to this one. The handler could only ever fire when the wrapper
   * itself held focus — that is, when the user was *not* in the preview — so it did nothing in
   * precisely the situation it claimed to rescue.
   *
   * Nothing replaces it, because nothing needs to: browsers include iframe content in the tab
   * sequence and continue past it, so the preview was never a trap. The skip link above is the
   * real affordance for jumping over it.
   */

  return (
    <section aria-labelledby="preview-heading">
      <h2 id="preview-heading">Preview of {displayName}</h2>

      <p id="preview-instructions" className="visually-hidden">
        This preview is a separate document. Tab moves into it and continues out the other side; the skip link
        above jumps straight to the comments. Nothing here can be selected — the document above this panel
        holds the same content as selectable prose, and is where comments are written.
      </p>

      <a className="skip-link" href="#comments">
        Skip preview and go to comments
      </a>

      {status === 'error' ? (
        <p className="error" role="alert">
          This preview is no longer available. The file may have expired — files delete themselves, and an
          expired file cannot be recovered.
        </p>
      ) : (
        <div
          ref={wrapperRef}
          className="preview-region"
          role="group"
          aria-label={`Preview of ${displayName}`}
          aria-describedby="preview-instructions"
        >
          <iframe
            ref={frameRef}
            src={currentUrl}
            title={`Preview of ${displayName}`}
            /*
             * No sandbox tokens. Not "none configured" — deliberately empty, which is the most
             * restrictive setting there is.
             */
            sandbox=""
            referrerPolicy="no-referrer"
            loading="eager"
            onLoad={() => void handleFrameLoad()}
          />
        </div>
      )}
    </section>
  );
}
