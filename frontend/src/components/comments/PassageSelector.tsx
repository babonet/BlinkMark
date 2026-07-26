import { useEffect, useRef, useState } from 'react';
import { createRegionAnchor, createTextAnchor, splitIntoBlocks } from '../../services/anchoring';
import type { Anchor, DocumentBlock } from '../../services/apiClient';

interface PassageSelectorProps {
  projection: string;
  blocks: DocumentBlock[];
  renderVersion: string;
  title: string;
  onSelect: (anchor: Anchor) => void;
  highlights?: { start: number; end: number; commentCount: number }[];
}

/** A block plus its position in the list, which the roving tabindex needs. */
interface PositionedBlock extends DocumentBlock {
  index: number;
}

type Mode = 'pointer' | 'keyboard';

/**
 * The document, as something you can read and comment on (T063, T064, T065, FR-017, FR-018,
 * FR-078, FR-082).
 *
 * This renders the document itself — headings as headings, lists as lists — rather than the flat
 * text projection it used to show. That earlier version worked and nobody wanted to read it: a
 * document stripped of its structure is materially harder to review than the document.
 *
 * What makes that safe is that it renders **data, not markup**. The server sends a block type from
 * a closed set, a level, and text; the components below decide what element that becomes. No
 * uploaded element, attribute, style, or URL reaches the application origin, so a sanitizer bypass
 * still cannot touch the session — which is the whole of Principle IV. `dangerouslySetInnerHTML`
 * is banned by lint here for the same reason.
 *
 * Three selection paths live in one component on purpose. The build-order warning in tasks.md was
 * that retrofitting keyboard-accessible passage selection means rewriting the interaction model —
 * so pointer, keyboard, and region selection are built together and, critically, **all three emit
 * the same `Anchor`**. There is no second anchor format for accessible selection, which is what
 * stops the keyboard path becoming a lesser experience that drifts.
 *
 * - **Pointer** — ordinary drag-select, read back as offsets into the projection.
 * - **Keyboard** — move through blocks with the arrow keys, extend with Shift, commit with Enter.
 *   No dragging, no pointer, and no modifier gymnastics.
 * - **Region** — select a whole block. This is FR-082's non-dragging alternative: a table or a
 *   code sample gets commented on by choosing it, not by drawing a rectangle around it.
 */
export function PassageSelector({
  projection,
  blocks: documentBlocks,
  renderVersion,
  title,
  onSelect,
  highlights = [],
}: PassageSelectorProps) {
  const [blocks, setBlocks] = useState<PositionedBlock[]>([]);
  const [mode, setMode] = useState<Mode>('pointer');
  const [focusedBlock, setFocusedBlock] = useState(0);
  const [anchorBlock, setAnchorBlock] = useState<number | null>(null);
  const [status, setStatus] = useState('');

  const containerRef = useRef<HTMLDivElement>(null);
  const blockRefs = useRef<(HTMLElement | null)[]>([]);

  useEffect(() => {
    // The server's structured blocks are preferred. Splitting the flat projection is the fallback
    // for a document that produced none — an empty file, or one stored before the structured
    // projection existed — so commenting never depends on it having worked.
    setBlocks(
      documentBlocks.length > 0
        ? documentBlocks.map((block, index) => ({ ...block, index }))
        : splitIntoBlocks(projection).map((block) => ({
            ...block,
            type: 'paragraph' as const,
            level: 0,
            ordered: false,
          })),
    );
  }, [documentBlocks, projection]);

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

  function findBlockFor(node: Node): PositionedBlock | null {
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
  function selectRegion(block: PositionedBlock) {
    onSelect(createRegionAnchor(projection, block.start, block.end, `block:${block.index}`, renderVersion));
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

  function highlightFor(block: PositionedBlock) {
    return highlights.find((h) => h.start < block.end && h.end > block.start);
  }

  /**
   * Renders one block as the element its type calls for.
   *
   * The mapping is exhaustive over a closed enum, so there is no path by which an uploaded
   * document chooses its own element. A type this component does not recognise becomes a
   * paragraph rather than anything clever.
   */
  function renderBlockContent(block: PositionedBlock, props: Record<string, unknown>) {
    const text = block.text;

    switch (block.type) {
      case 'heading':
        return (
          // Not a real <h1>-<h6>. The page already owns its heading outline, and injecting a
          // document's h1 into it would produce two competing document structures for a screen
          // reader. role + aria-level keeps the semantics while nesting the document's outline
          // beneath the page's.
          <div {...props} role="heading" aria-level={Math.min(6, block.level + 2)} data-level={block.level}>
            {text}
          </div>
        );

      case 'code':
        return <pre {...props}>{text}</pre>;

      case 'quote':
        return <blockquote {...props}>{text}</blockquote>;

      case 'listItem':
        return (
          <div {...props} role="listitem">
            <span aria-hidden="true" className="doc-bullet">
              {block.ordered ? `${listNumberFor(block)}.` : '•'}
            </span>
            {text}
          </div>
        );

      case 'tableCell':
        return <div {...props}>{text}</div>;

      default:
        return <p {...props}>{text}</p>;
    }
  }

  /** Position of a list item within its run of consecutive items. */
  function listNumberFor(block: PositionedBlock): number {
    let number = 1;
    for (let index = block.index - 1; index >= 0; index--) {
      if (blocks[index].type !== 'listItem') break;
      number++;
    }
    return number;
  }

  return (
    <section aria-labelledby="passages-heading">
      {/*
        Visually hidden: the page heading above already names the file, and repeating it here
        would give a screen-reader user the same title twice for no additional information. The
        element stays because the landmark needs a name.
      */}
      <h2 id="passages-heading" className="visually-hidden">
        {title}
      </h2>

      <p id="passage-instructions" className="document-hint">
        Select any passage to comment on it, or use the button beside a block. With a keyboard: arrow keys to
        move, Shift to extend across blocks, Enter to comment.
      </p>

      {/*
        A polite live region. Selection changes are worth announcing and never worth interrupting
        with, and moving focus on every arrow press would fight the user (FR-080's reasoning).
      */}
      <p aria-live="polite" className="visually-hidden">
        {status}
      </p>

      {/*
        The rule below wants an interactive role on this container, and the obvious candidate is
        `listbox`. That would be the wrong answer: this is the document's prose, and announcing
        someone's draft as "listbox, 42 items" with each paragraph as "option" misrepresents what
        a screen-reader user is actually reading. Correct semantics for prose beat a clean lint
        run.

        What the rule protects against — interaction a keyboard cannot reach — does not apply
        here. Every action has a keyboard path: the blocks carry a roving tabindex and handle
        arrow keys, Shift extends, Enter commits, and each block also has a real button. The
        handlers on this element delegate for children that are already focusable; they are not a
        hidden click target.
      */}
      {/* eslint-disable-next-line jsx-a11y/no-static-element-interactions */}
      <div
        ref={containerRef}
        className="passage-list document"
        aria-describedby="passage-instructions"
        onMouseUp={handlePointerSelection}
        onKeyDown={handleKeyDown}
      >
        {blocks.map((block) => {
          const highlight = highlightFor(block);
          const selected = isSelected(block.index);

          return (
            <div key={block.index} className="passage-row">
              {renderBlockContent(block, {
                ref: (element: HTMLElement | null) => {
                  blockRefs.current[block.index] = element;
                },
                'data-block-index': block.index,
                tabIndex: block.index === focusedBlock ? 0 : -1,
                className: [
                  'passage',
                  `passage--${block.type}`,
                  selected ? 'passage--selected' : '',
                  highlight ? 'passage--commented' : '',
                ]
                  .filter(Boolean)
                  .join(' '),
                /*
                  The commented state is announced, not just shown. FR-079 forbids conveying it by
                  visual highlight alone, and a highlight is exactly what a sighted user gets.
                */
                'aria-current': block.index === focusedBlock && mode === 'keyboard' ? 'true' : undefined,
                'aria-label': highlight
                  ? `${block.text} — ${highlight.commentCount} comment${highlight.commentCount === 1 ? '' : 's'} on this passage`
                  : undefined,
              })}

              <button type="button" className="secondary passage-action" onClick={() => selectRegion(block)}>
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
