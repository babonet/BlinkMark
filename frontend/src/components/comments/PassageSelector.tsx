import { useEffect, useRef, useState } from 'react';
import { createRegionAnchor, createTextAnchor, splitIntoBlocks, type ProjectionBlock } from '../../services/anchoring';
import type { Anchor } from '../../services/apiClient';

interface PassageSelectorProps {
  projection: string;
  renderVersion: string;
  onSelect: (anchor: Anchor) => void;
  highlights?: { start: number; end: number; commentCount: number }[];
}

type Mode = 'pointer' | 'keyboard';

/**
 * The commenting surface (T063, T064, T065, FR-017, FR-018, FR-078, FR-082).
 *
 * Three selection paths live in one component on purpose. The build-order warning in tasks.md was
 * that retrofitting keyboard-accessible passage selection means rewriting the interaction model —
 * so pointer, keyboard, and region selection are built together and, critically, **all three emit
 * the same `Anchor`**. There is no second anchor format for accessible selection, which is what
 * stops the keyboard path becoming a lesser experience that drifts.
 *
 * They operate over the text projection rather than the preview iframe, because the iframe is
 * sandboxed cross-origin and its DOM is unreachable by design (research.md R2, corrected).
 *
 * - **Pointer** — ordinary drag-select, read back as offsets into the projection.
 * - **Keyboard** — move through blocks with the arrow keys, extend with Shift, commit with Enter.
 *   No dragging, no pointer, and no modifier gymnastics.
 * - **Region** — select a whole structural block. This is FR-082's non-dragging alternative: a
 *   figure or table gets commented on by choosing it, not by drawing a rectangle around it.
 */
export function PassageSelector({ projection, renderVersion, onSelect, highlights = [] }: PassageSelectorProps) {
  const [blocks, setBlocks] = useState<ProjectionBlock[]>([]);
  const [mode, setMode] = useState<Mode>('pointer');
  const [focusedBlock, setFocusedBlock] = useState(0);
  const [anchorBlock, setAnchorBlock] = useState<number | null>(null);
  const [status, setStatus] = useState('');

  const containerRef = useRef<HTMLDivElement>(null);
  const blockRefs = useRef<(HTMLElement | null)[]>([]);

  useEffect(() => {
    setBlocks(splitIntoBlocks(projection));
  }, [projection]);

  /** Reads a pointer selection back as offsets into the projection. */
  function handlePointerSelection() {
    const selection = window.getSelection();
    if (!selection || selection.isCollapsed || !containerRef.current) {
      return;
    }

    const range = selection.getRangeAt(0);
    const startBlock = findBlockFor(range.startContainer);
    const endBlock = findBlockFor(range.endContainer);

    if (startBlock === null || endBlock === null) {
      return;
    }

    const start = startBlock.start + range.startOffset;
    const end = endBlock.start + range.endOffset;

    if (end <= start) {
      return;
    }

    setMode('pointer');
    onSelect(createTextAnchor(projection, start, end, renderVersion));
    selection.removeAllRanges();
    setStatus('Passage selected.');
  }

  function findBlockFor(node: Node): ProjectionBlock | null {
    let element: HTMLElement | null =
      node.nodeType === Node.TEXT_NODE ? node.parentElement : (node as HTMLElement);

    while (element && element !== containerRef.current) {
      const index = element.dataset?.blockIndex;
      if (index !== undefined) {
        return blocks[Number(index)] ?? null;
      }
      element = element.parentElement;
    }

    return null;
  }

  /**
   * The keyboard path (FR-078).
   *
   * Arrow keys move, Shift extends, Enter commits, Escape clears. Deliberately the same keys a
   * reader would guess, and deliberately not dependent on a pointer selection existing first.
   */
  function handleKeyDown(event: React.KeyboardEvent<HTMLDivElement>) {
    if (blocks.length === 0) {
      return;
    }

    switch (event.key) {
      case 'ArrowDown':
      case 'ArrowRight': {
        event.preventDefault();
        const next = Math.min(focusedBlock + 1, blocks.length - 1);
        moveTo(next, event.shiftKey);
        break;
      }

      case 'ArrowUp':
      case 'ArrowLeft': {
        event.preventDefault();
        const previous = Math.max(focusedBlock - 1, 0);
        moveTo(previous, event.shiftKey);
        break;
      }

      case 'Home':
        event.preventDefault();
        moveTo(0, event.shiftKey);
        break;

      case 'End':
        event.preventDefault();
        moveTo(blocks.length - 1, event.shiftKey);
        break;

      case 'Enter':
        event.preventDefault();
        commitKeyboardSelection();
        break;

      case 'Escape':
        event.preventDefault();
        setAnchorBlock(null);
        setStatus('Selection cleared.');
        break;

      default:
        break;
    }
  }

  function moveTo(index: number, extending: boolean) {
    setMode('keyboard');
    setFocusedBlock(index);
    blockRefs.current[index]?.focus();

    if (extending) {
      // Extending from wherever the selection began, so Shift+Arrow behaves the way it does in a
      // text editor rather than resetting on every keystroke.
      setAnchorBlock((current) => current ?? focusedBlock);
      const from = anchorBlock ?? focusedBlock;
      const count = Math.abs(index - from) + 1;
      setStatus(`${count} block${count === 1 ? '' : 's'} selected.`);
    } else {
      setAnchorBlock(null);
      setStatus(`Block ${index + 1} of ${blocks.length}.`);
    }
  }

  function commitKeyboardSelection() {
    const from = Math.min(anchorBlock ?? focusedBlock, focusedBlock);
    const to = Math.max(anchorBlock ?? focusedBlock, focusedBlock);

    const start = blocks[from].start;
    const end = blocks[to].end;

    // Identical shape to the pointer path. This is the property that matters.
    onSelect(createTextAnchor(projection, start, end, renderVersion));
    setAnchorBlock(null);
    setStatus('Passage selected. Write your comment.');
  }

  /** FR-082 — comment on a whole block without dragging anything. */
  function selectRegion(block: ProjectionBlock) {
    onSelect(
      createRegionAnchor(projection, block.start, block.end, `block:${block.index}`, renderVersion),
    );
    setStatus('Region selected. Write your comment.');
  }

  function isSelected(index: number): boolean {
    if (anchorBlock === null) {
      return false;
    }
    const from = Math.min(anchorBlock, focusedBlock);
    const to = Math.max(anchorBlock, focusedBlock);
    return index >= from && index <= to;
  }

  function highlightFor(block: ProjectionBlock) {
    return highlights.find((h) => h.start < block.end && h.end > block.start);
  }

  return (
    <section aria-labelledby="passages-heading">
      <h2 id="passages-heading">Document text</h2>

      <p id="passage-instructions">
        Select a passage to comment on it. With a keyboard: move with the arrow keys, hold Shift to
        extend the selection across blocks, and press Enter to comment. Each block also has a
        “Comment on this block” button, so nothing here needs a drag.
      </p>

      {/*
        A polite live region. Selection changes are worth announcing and never worth interrupting
        with, and moving focus on every arrow press would fight the user (FR-080's reasoning).
      */}
      <p aria-live="polite" className="visually-hidden">
        {status}
      </p>

      <div
        ref={containerRef}
        className="passage-list"
        aria-describedby="passage-instructions"
        onMouseUp={handlePointerSelection}
        onKeyDown={handleKeyDown}
      >
        {blocks.map((block) => {
          const highlight = highlightFor(block);
          const selected = isSelected(block.index);

          return (
            <div key={block.index} className="passage-row">
              <p
                ref={(element) => {
                  blockRefs.current[block.index] = element;
                }}
                data-block-index={block.index}
                tabIndex={block.index === focusedBlock ? 0 : -1}
                className={[
                  'passage',
                  selected ? 'passage--selected' : '',
                  highlight ? 'passage--commented' : '',
                ]
                  .filter(Boolean)
                  .join(' ')}
                /*
                  The commented state is announced, not just shown. FR-079 forbids conveying it by
                  visual highlight alone, and a highlight is exactly what a sighted user gets.
                */
                aria-current={block.index === focusedBlock && mode === 'keyboard' ? 'true' : undefined}
                aria-label={
                  highlight
                    ? `${block.text} — ${highlight.commentCount} comment${highlight.commentCount === 1 ? '' : 's'} on this passage`
                    : undefined
                }
              >
                {block.text}
              </p>

              <button
                type="button"
                className="secondary passage-action"
                onClick={() => selectRegion(block)}
              >
                Comment on this block
                <span className="visually-hidden"> — {block.text.slice(0, 40)}</span>
              </button>
            </div>
          );
        })}
      </div>
    </section>
  );
}
