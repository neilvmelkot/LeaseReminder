/**
 * The product mark.
 *
 * <h3>Why a glyph rather than initials</h3>
 *
 * "LR" in a rounded square is the placeholder every unfinished product wears,
 * and two capitals carry no meaning: they are an abbreviation of a name the
 * user can already read on the line beside them. A mark should say what the
 * thing is about, and at 32px that has to be one silhouette with no interior
 * detail to lose.
 *
 * So: a roof over a door. Property, in the fewest strokes that still read at
 * the size this is actually used — and the one place the mark appears at all
 * is the top of the sidebar, where it sits beside the wordmark and only has
 * to be recognisable, not legible.
 *
 * <h3>Why solid shapes rather than strokes</h3>
 *
 * A 1.5px stroke is a grey smear at 32px on a non-retina display and
 * disappears entirely when the rail collapses. Filled forms with a knocked-out
 * door hold their shape at any size, and the knockout lets the badge colour
 * show through rather than needing a second fill.
 */
export function Logo({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      className={className}
      fill="none"
      aria-hidden="true"
      focusable="false"
    >
      {/* The roof: a single chevron, overhanging the walls on both sides so
          the silhouette reads as a building rather than as an arrow. */}
      <path
        d="M12 3.2 1.8 11.1a1.2 1.2 0 0 0 1.48 1.89L12 6.24l8.72 6.75a1.2 1.2 0 0 0 1.48-1.89Z"
        fill="currentColor"
      />
      {/* The walls, with the doorway knocked out of the bottom edge.
          evenodd is what makes the inner rectangle a hole rather than a
          second filled shape sitting on top. */}
      <path
        fillRule="evenodd"
        clipRule="evenodd"
        d="M12 8.6l7 5.42V21.4H5v-7.38ZM10.2 19.6h3.6v-4.2h-3.6Z"
        fill="currentColor"
      />
    </svg>
  );
}
