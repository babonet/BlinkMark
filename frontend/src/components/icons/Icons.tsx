interface IconProps {
  /** Size in pixels. Defaults to 16, which pairs with the surrounding 0.9rem text. */
  size?: number;
}

/**
 * The icon set.
 *
 * Inline SVG rather than an icon font or a package. A font would need a network request before
 * anything is legible and renders as a wrong glyph when it fails; a package would be another
 * dependency to audit for the sake of six shapes.
 *
 * Every icon is `aria-hidden` and `focusable="false"`, without exception. An icon is decoration —
 * the accessible name always comes from the button that contains it, never from here. That is the
 * rule that keeps an icon-only button from announcing itself as "graphic" and nothing else, and
 * it is why every caller below pairs its icon with visually hidden text.
 */
const base = (size: number) => ({
  width: size,
  height: size,
  viewBox: '0 0 16 16',
  fill: 'none' as const,
  stroke: 'currentColor',
  strokeWidth: 1.6,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
  'aria-hidden': true,
  focusable: false as const,
});

export function ReplyIcon({ size = 16 }: IconProps) {
  return (
    <svg {...base(size)}>
      <polyline points="6.5,3.5 2.5,7 6.5,10.5" />
      <path d="M2.5 7h6.2a4 4 0 0 1 4 4v1.5" />
    </svg>
  );
}

export function EditIcon({ size = 16 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M11.2 2.4a1.4 1.4 0 0 1 2 2L6 11.6l-2.7.8.8-2.7z" />
      <line x1="10.2" y1="3.4" x2="12.6" y2="5.8" />
    </svg>
  );
}

export function DeleteIcon({ size = 16 }: IconProps) {
  return (
    <svg {...base(size)}>
      <polyline points="2.5,4 13.5,4" />
      <path d="M5.5 4V2.8h5V4" />
      <path d="M4 4l.7 9.2h6.6L12 4" />
      <line x1="6.6" y1="6.4" x2="6.8" y2="11" />
      <line x1="9.4" y1="6.4" x2="9.2" y2="11" />
    </svg>
  );
}

export function CopyIcon({ size = 16 }: IconProps) {
  return (
    <svg {...base(size)}>
      <rect x="5.5" y="5.5" width="8" height="8" rx="1.3" />
      <path d="M10.5 5.5V3.8a1.3 1.3 0 0 0-1.3-1.3H3.8a1.3 1.3 0 0 0-1.3 1.3v5.4a1.3 1.3 0 0 0 1.3 1.3h1.7" />
    </svg>
  );
}

export function CheckIcon({ size = 16 }: IconProps) {
  return (
    <svg {...base(size)}>
      <polyline points="3,8.4 6.4,11.6 13,4.6" />
    </svg>
  );
}

export function CommentIcon({ size = 16 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M13.5 9.4a1.6 1.6 0 0 1-1.6 1.6H5.8L2.5 13.6V4.1A1.6 1.6 0 0 1 4.1 2.5h7.8a1.6 1.6 0 0 1 1.6 1.6z" />
    </svg>
  );
}
