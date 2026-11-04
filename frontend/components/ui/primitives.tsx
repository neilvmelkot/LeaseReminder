import type { ComponentProps, ReactNode } from "react";

/**
 * The shared component vocabulary.
 *
 * Section 5 asks for consistent typography, spacing, radius, and icon style,
 * and warns against inconsistent spacing and purposeless cards. That drift
 * comes from composing utility strings per page, so the shapes live here once
 * and pages compose them.
 */

export function cx(...parts: Array<string | false | null | undefined>): string {
  return parts.filter(Boolean).join(" ");
}

/* ========================================================================== */
/* Button                                                                      */
/* ========================================================================== */

type ButtonVariant = "primary" | "secondary" | "ghost" | "danger" | "subtle";
type ButtonSize = "xs" | "sm" | "md";

const buttonBase =
  "inline-flex items-center justify-center gap-1.5 rounded-control font-medium " +
  "whitespace-nowrap transition-[background-color,border-color,color,box-shadow] " +
  "duration-100 disabled:cursor-not-allowed disabled:opacity-50 " +
  "active:translate-y-px";

const buttonVariants: Record<ButtonVariant, string> = {
  // White on #2F52C8 clears 7:1.
  primary:
    "bg-accent text-white shadow-raised hover:bg-accent-hover active:bg-accent-active",
  secondary:
    "bg-surface text-ink border border-line-strong shadow-raised " +
    "hover:bg-surface-subtle hover:border-ink-muted/50",
  subtle: "bg-surface-sunken text-ink hover:bg-line",
  ghost: "text-ink-secondary hover:bg-surface-sunken hover:text-ink",
  // White on #C0393A is 5.40:1.
  danger: "bg-danger text-white shadow-raised hover:bg-danger-hover",
};

const buttonSizes: Record<ButtonSize, string> = {
  xs: "h-7 px-2 text-[12.5px]",
  sm: "h-8 px-3 text-[13px]",
  md: "h-9 px-3.5 text-[13.5px]",
};

export function Button({
  variant = "secondary",
  size = "md",
  className,
  ...rest
}: ComponentProps<"button"> & { variant?: ButtonVariant; size?: ButtonSize }) {
  return (
    <button
      // A button inside a form defaults to submit, which has caused more
      // accidental submissions than it has ever saved keystrokes. Callers that
      // want a submit button say so.
      type={rest.type ?? "button"}
      className={cx(buttonBase, buttonVariants[variant], buttonSizes[size], className)}
      {...rest}
    />
  );
}

/**
 * A link styled as a button.
 *
 * Separate from `Button` because nesting an anchor inside a button is invalid
 * HTML and gives the element two conflicting roles. If it navigates, it is a
 * link; if it acts, it is a button.
 */
export function LinkButton({
  variant = "secondary",
  size = "md",
  className,
  ...rest
}: ComponentProps<"a"> & { variant?: ButtonVariant; size?: ButtonSize }) {
  return (
    <a
      className={cx(buttonBase, buttonVariants[variant], buttonSizes[size], className)}
      {...rest}
    />
  );
}

/** A square icon-only button. Requires an accessible label, since it has no text. */
export function IconButton({
  label,
  size = "md",
  variant = "ghost",
  className,
  children,
  ...rest
}: ComponentProps<"button"> & {
  label: string;
  size?: ButtonSize;
  variant?: ButtonVariant;
}) {
  const box = size === "xs" ? "size-7" : size === "sm" ? "size-8" : "size-9";

  return (
    <button
      type={rest.type ?? "button"}
      // Both, deliberately: aria-label names it for a screen reader, title
      // gives a sighted mouse user the same information on hover.
      aria-label={label}
      title={label}
      className={cx(
        "inline-flex shrink-0 items-center justify-center rounded-control",
        "transition-colors duration-100 disabled:cursor-not-allowed disabled:opacity-50",
        box,
        buttonVariants[variant],
        className,
      )}
      {...rest}
    >
      {children}
    </button>
  );
}

/* ========================================================================== */
/* Page and panel structure                                                    */
/* ========================================================================== */

export function PageHeader({
  title,
  description,
  primaryAction,
  secondaryAction,
  meta,
}: {
  title: string;
  description?: string;
  primaryAction?: ReactNode;
  secondaryAction?: ReactNode;
  meta?: ReactNode;
}) {
  return (
    <header className="mb-6 flex flex-wrap items-start justify-between gap-x-6 gap-y-3">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2.5">
          {/* Section 5 warns against huge headings pushing useful information
              below the fold. This is a heading, not a banner. */}
          <h1>{title}</h1>
          {meta}
        </div>
        {description ? (
          <p className="mt-1 max-w-2xl text-[13.5px] leading-relaxed text-ink-secondary">
            {description}
          </p>
        ) : null}
      </div>
      {primaryAction || secondaryAction ? (
        <div className="flex shrink-0 items-center gap-2">
          {secondaryAction}
          {primaryAction}
        </div>
      ) : null}
    </header>
  );
}

/**
 * A titled surface.
 *
 * <h3>Where the body padding comes from</h3>
 *
 * The body is unpadded by default, because most panels here hold something
 * that must reach the panel's edges — a table, or a `divide-y` list whose
 * separators should span the full width. Those children pad their own rows
 * (`px-5 py-3`), which is the only way the separator can run edge to edge
 * while the text still lines up with the header above it.
 *
 * Anything else — a form, prose, a stack of banners — needs `padded`, or its
 * content sits flush against the border. If you are adding a panel and its
 * contents are not a table or a row list, you want `padded`.
 */
export function Panel({
  title,
  description,
  action,
  children,
  className,
  bodyClassName,
  padded = false,
}: {
  title?: string;
  description?: string;
  action?: ReactNode;
  children: ReactNode;
  className?: string;
  bodyClassName?: string;
  /** Pads the body. See the note above: tables and row lists pad themselves. */
  padded?: boolean;
}) {
  return (
    <section className={cx("surface overflow-hidden", className)}>
      {title ? (
        <header className="flex items-start justify-between gap-4 border-b border-line px-5 py-3.5">
          <div className="min-w-0">
            <h2>{title}</h2>
            {description ? (
              <p className="mt-0.5 text-[12.5px] text-ink-secondary">{description}</p>
            ) : null}
          </div>
          {action ? <div className="shrink-0">{action}</div> : null}
        </header>
      ) : null}
      <div className={cx(padded && "px-5 py-4", bodyClassName)}>{children}</div>
    </section>
  );
}

/** A horizontal rule with a caption, for separating groups inside a panel. */
export function SectionDivider({ label }: { label: string }) {
  return (
    <div className="flex items-center gap-3 border-y border-line bg-surface-subtle px-5 py-2">
      <span className="eyebrow">{label}</span>
    </div>
  );
}

/* ========================================================================== */
/* Status and priority                                                         */
/* ========================================================================== */

export type StatusTone = "neutral" | "info" | "success" | "warning" | "danger";

const statusTones: Record<StatusTone, string> = {
  neutral: "bg-surface-sunken text-ink-secondary ring-line-strong",
  info: "bg-accent-tint text-accent ring-accent/20",
  // The darkened variants, because these are read as text.
  success: "bg-success-tint text-success-text ring-success/20",
  warning: "bg-warning-tint text-warning-text ring-warning/25",
  danger: "bg-danger-tint text-danger-text ring-danger/20",
};

/**
 * A status label.
 *
 * Always renders words, never colour alone. Section 5 requires that status is
 * not communicated by colour alone, and a coloured dot with no text is
 * unreadable both to a colour-blind user and to anyone who has not memorised
 * the legend.
 *
 * `ring` rather than `border`, so the chip's box size does not change between
 * toned and untoned variants and rows stay aligned.
 */
export function Status({
  tone,
  children,
  className,
}: {
  tone: StatusTone;
  children: ReactNode;
  className?: string;
}) {
  return (
    <span
      className={cx(
        "inline-flex items-center whitespace-nowrap rounded-full px-2 py-0.5",
        "text-[11.5px] font-medium ring-1 ring-inset",
        statusTones[tone],
        className,
      )}
    >
      {children}
    </span>
  );
}

export type TaskPriority = "urgent" | "high" | "medium" | "low";

const priorityStyles: Record<TaskPriority, { chip: string; label: string }> = {
  urgent: {
    chip: "bg-priority-urgent-tint text-priority-urgent ring-priority-urgent/25",
    label: "Urgent",
  },
  high: {
    chip: "bg-priority-high-tint text-warning-text ring-priority-high/25",
    label: "High",
  },
  medium: {
    chip: "bg-priority-medium-tint text-priority-medium ring-priority-medium/20",
    label: "Medium",
  },
  low: {
    chip: "bg-priority-low-tint text-priority-low ring-priority-low/20",
    label: "Low",
  },
};

/**
 * A priority chip.
 *
 * Low priority renders nothing by default. On a board of forty cards, a chip
 * on every one that says "Low" is forty pieces of noise competing with the two
 * that say "Urgent"; absence is the clearer signal. Pass `alwaysShow` where the
 * field is being edited and the current value has to be visible.
 */
export function Priority({
  priority,
  alwaysShow = false,
}: {
  priority: TaskPriority;
  alwaysShow?: boolean;
}) {
  if (priority === "low" && !alwaysShow) {
    return null;
  }

  const style = priorityStyles[priority];

  return (
    <span
      className={cx(
        "inline-flex items-center gap-1 whitespace-nowrap rounded px-1.5 py-0.5",
        "text-[11px] font-semibold ring-1 ring-inset",
        style.chip,
      )}
    >
      {/* Decorative: the word next to it carries the meaning. */}
      <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />
      {style.label}
    </span>
  );
}

/* ========================================================================== */
/* Avatar                                                                      */
/* ========================================================================== */

/**
 * Initials avatar.
 *
 * The colour is derived from the name rather than stored, so the same person is
 * always the same colour on every board without a palette assignment to
 * maintain. The hash is deliberately trivial; it only has to be stable.
 */
export function Avatar({
  name,
  size = "md",
  className,
}: {
  name: string | null;
  size?: "xs" | "sm" | "md";
  className?: string;
}) {
  const box =
    size === "xs"
      ? "size-5 text-[9.5px]"
      : size === "sm"
        ? "size-6 text-[10.5px]"
        : "size-7 text-[11.5px]";

  if (!name) {
    return (
      <span
        className={cx(
          "inline-flex shrink-0 items-center justify-center rounded-full",
          "bg-surface-sunken text-ink-muted ring-1 ring-inset ring-line-strong",
          box,
          className,
        )}
        // Unassigned is a meaningful state on a board, so it is announced
        // rather than left as an unlabelled grey circle.
        title="Unassigned"
      >
        <span aria-hidden="true">?</span>
        <span className="sr-only">Unassigned</span>
      </span>
    );
  }

  // The palette is muted on purpose. Avatars sit next to priority chips, and a
  // saturated avatar would out-shout the chip that actually matters.
  const palette = [
    "bg-[#dbe4fd] text-[#28408f]",
    "bg-[#d7ece3] text-[#145c40]",
    "bg-[#f8e3d2] text-[#8a4a16]",
    "bg-[#e6ddf6] text-[#4a2f80]",
    "bg-[#fadfe0] text-[#8f2b2c]",
    "bg-[#d4e9ef] text-[#17566a]",
  ] as const;

  let hash = 0;
  for (let index = 0; index < name.length; index++) {
    hash = (hash * 31 + name.charCodeAt(index)) % 1_000_003;
  }

  const tone = palette[hash % palette.length];

  return (
    <span
      className={cx(
        "inline-flex shrink-0 items-center justify-center rounded-full font-semibold",
        box,
        tone,
        className,
      )}
      title={name}
    >
      <span aria-hidden="true">{initialsOf(name)}</span>
      <span className="sr-only">{name}</span>
    </span>
  );
}

function initialsOf(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);

  if (words.length === 0) {
    return "?";
  }

  const first = words[0]?.[0] ?? "";
  // The LAST word, not the second, so "Maria del Carmen Souza" gives MS.
  const last = words.length > 1 ? (words[words.length - 1]?.[0] ?? "") : "";

  return (first + last).toUpperCase();
}

/** Overlapping avatars for a group, with an overflow count. */
export function AvatarStack({
  names,
  max = 3,
}: {
  names: string[];
  max?: number;
}) {
  if (names.length === 0) {
    return <Avatar name={null} size="sm" />;
  }

  const shown = names.slice(0, max);
  const overflow = names.length - shown.length;

  return (
    <span className="flex items-center">
      {shown.map((name, index) => (
        <Avatar
          key={`${name}-${index}`}
          name={name}
          size="sm"
          // Negative margin on all but the first, plus a ring, so the stack
          // reads as overlapping rather than as a smudge.
          className={index > 0 ? "-ml-1.5 ring-2 ring-surface" : "ring-2 ring-surface"}
        />
      ))}
      {overflow > 0 ? (
        <span
          className="-ml-1.5 inline-flex size-6 items-center justify-center rounded-full
                     bg-surface-sunken text-[10px] font-semibold text-ink-secondary
                     ring-2 ring-surface"
          title={names.slice(max).join(", ")}
        >
          +{overflow}
        </span>
      ) : null}
    </span>
  );
}

/* ========================================================================== */
/* Metric                                                                      */
/* ========================================================================== */

/**
 * A compact summary block.
 *
 * Section 5 is explicit: do not turn every metric into a large colourful card.
 * So this is a quiet block with a label, a number, and a destination. `href` is
 * required rather than optional, because the specification also asks that every
 * dashboard metric link to the relevant filtered page; a metric with nowhere to
 * go is a decoration.
 */
export function Metric({
  label,
  value,
  hint,
  href,
  tone = "neutral",
  icon,
}: {
  label: string;
  value: string | number;
  hint?: string;
  href: string;
  tone?: StatusTone;
  icon?: ReactNode;
}) {
  const valueTone =
    tone === "warning"
      ? "text-warning-text"
      : tone === "danger"
        ? "text-danger-text"
        : tone === "success"
          ? "text-success-text"
          : "text-ink";

  return (
    <a
      href={href}
      className="surface group flex flex-col justify-between gap-2 px-4 py-3.5
                 transition-[border-color,box-shadow] duration-100
                 hover:border-line-strong hover:shadow-floating"
    >
      <span className="flex items-start justify-between gap-2">
        <span className="text-[12.5px] font-medium text-ink-secondary">{label}</span>
        {icon ? (
          <span className="shrink-0 text-ink-muted transition-colors group-hover:text-accent">
            {icon}
          </span>
        ) : null}
      </span>
      <span>
        <span
          className={cx(
            "block text-[26px] font-semibold leading-none tracking-tight tabular",
            valueTone,
          )}
        >
          {value}
        </span>
        {hint ? (
          <span className="mt-1.5 block text-[11.5px] text-ink-secondary">{hint}</span>
        ) : null}
      </span>
    </a>
  );
}

/* ========================================================================== */
/* Table                                                                       */
/* ========================================================================== */

/**
 * Table wrapper.
 *
 * The overflow container is the point. Section 5 requires wide content to
 * scroll inside its own region and the page body never to scroll
 * horizontally, which is what keeps a wide lease table usable on a narrow
 * screen without breaking the layout around it.
 */
export function TableFrame({
  children,
  minWidth = "46rem",
}: {
  children: ReactNode;
  minWidth?: string;
}) {
  return (
    <div className="thin-scrollbar overflow-x-auto">
      <table
        className="w-full border-collapse text-[13.5px]"
        style={{ minWidth }}
      >
        {children}
      </table>
    </div>
  );
}

export function Th({ children, className, ...rest }: ComponentProps<"th">) {
  return (
    <th
      // Explicit scope, so a screen reader associates the column with its
      // cells. Without it a data table is read as a flat list of values.
      scope="col"
      className={cx(
        "sticky top-0 z-10 border-b border-line bg-surface-subtle px-4 py-2.5 text-left",
        "text-[11px] font-semibold uppercase tracking-[0.055em] text-ink-secondary",
        className,
      )}
      {...rest}
    >
      {children}
    </th>
  );
}

export function Td({ children, className, ...rest }: ComponentProps<"td">) {
  return (
    <td
      className={cx("border-b border-line px-4 py-2.5 align-middle", className)}
      {...rest}
    >
      {children}
    </td>
  );
}

export function Tr({ children, className, ...rest }: ComponentProps<"tr">) {
  return (
    <tr
      className={cx("transition-colors hover:bg-surface-subtle", className)}
      {...rest}
    >
      {children}
    </tr>
  );
}

/* ========================================================================== */
/* Empty, loading, and error states                                            */
/* ========================================================================== */

/**
 * The empty state.
 *
 * Section 5 requires empty, loading, error, and success states throughout. An
 * empty table with no explanation is the single most common way a new user
 * concludes the application is broken, so this always says what the thing is
 * and offers the action that fills it.
 */
export function EmptyState({
  title,
  description,
  action,
  icon,
  compact = false,
}: {
  title: string;
  description: string;
  action?: ReactNode;
  icon?: ReactNode;
  compact?: boolean;
}) {
  return (
    <div className={cx("text-center", compact ? "px-5 py-8" : "px-6 py-14")}>
      {icon ? (
        <div
          className="mx-auto mb-3 grid size-10 place-items-center rounded-full
                     bg-surface-sunken text-ink-muted"
          aria-hidden="true"
        >
          {icon}
        </div>
      ) : null}
      <h3>{title}</h3>
      <p className="mx-auto mt-1.5 max-w-md text-[13px] leading-relaxed text-ink-secondary">
        {description}
      </p>
      {action ? <div className="mt-5 flex justify-center">{action}</div> : null}
    </div>
  );
}

export function ErrorState({
  title = "Something went wrong",
  description,
  retry,
}: {
  title?: string;
  description: string;
  retry?: ReactNode;
}) {
  return (
    <div
      // role="alert" so the message is announced rather than silently
      // appearing. Section 5 asks for accessible validation messages, and the
      // same reasoning applies to a failed load.
      role="alert"
      className="px-6 py-10 text-center"
    >
      <h3 className="text-danger-text">{title}</h3>
      <p className="mx-auto mt-1.5 max-w-md text-[13px] leading-relaxed text-ink-secondary">
        {description}
      </p>
      {retry ? <div className="mt-5 flex justify-center">{retry}</div> : null}
    </div>
  );
}

/**
 * Skeleton rows.
 *
 * Used only where it genuinely improves perceived responsiveness (section 5):
 * a table whose shape is already known. A skeleton standing in for content
 * whose shape is unknown just flashes and then reflows, which is worse than a
 * brief blank.
 */
export function SkeletonRows({
  rows = 4,
  columns = 4,
}: {
  rows?: number;
  columns?: number;
}) {
  return (
    <tbody aria-hidden="true">
      {Array.from({ length: rows }, (_, rowIndex) => (
        <tr key={rowIndex}>
          {Array.from({ length: columns }, (_, columnIndex) => (
            <Td key={columnIndex}>
              <span
                className="block h-3.5 animate-pulse rounded bg-line"
                // Varied widths, so the placeholder reads as text rather than
                // as a progress bar.
                style={{ width: `${55 + ((rowIndex + columnIndex) % 4) * 12}%` }}
              />
            </Td>
          ))}
        </tr>
      ))}
    </tbody>
  );
}

/* ========================================================================== */
/* Form controls                                                               */
/* ========================================================================== */

const fieldBase =
  "w-full rounded-control border border-line-strong bg-surface px-2.5 text-[13.5px] " +
  "shadow-raised transition-[border-color,box-shadow] placeholder:text-ink-muted " +
  "focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/25 " +
  "disabled:cursor-not-allowed disabled:bg-surface-sunken disabled:text-ink-muted";

export function Field({
  label,
  hint,
  error,
  required,
  htmlFor,
  children,
}: {
  label: string;
  hint?: string;
  error?: string;
  required?: boolean;
  htmlFor: string;
  children: ReactNode;
}) {
  return (
    <div>
      <label htmlFor={htmlFor} className="block text-[12.5px] font-medium">
        {label}
        {required ? (
          <span className="ml-0.5 text-danger-text" aria-hidden="true">
            *
          </span>
        ) : null}
      </label>
      {hint ? (
        <p id={`${htmlFor}-hint`} className="mt-0.5 text-[11.5px] text-ink-secondary">
          {hint}
        </p>
      ) : null}
      <div className="mt-1.5">{children}</div>
      {error ? (
        // role="alert" so a validation message is announced when it appears.
        <p id={`${htmlFor}-error`} role="alert" className="mt-1 text-[11.5px] text-danger-text">
          {error}
        </p>
      ) : null}
    </div>
  );
}

export function Input({ className, ...rest }: ComponentProps<"input">) {
  return <input className={cx(fieldBase, "h-9", className)} {...rest} />;
}

export function Textarea({ className, ...rest }: ComponentProps<"textarea">) {
  return <textarea className={cx(fieldBase, "py-2 leading-relaxed", className)} {...rest} />;
}

export function Select({ className, children, ...rest }: ComponentProps<"select">) {
  return (
    <select className={cx(fieldBase, "h-9 pr-8", className)} {...rest}>
      {children}
    </select>
  );
}

/* ========================================================================== */
/* Toolbar and tabs                                                            */
/* ========================================================================== */

/** A filter and action bar. Wraps on narrow screens instead of scrolling. */
export function Toolbar({ children }: { children: ReactNode }) {
  return (
    <div className="mb-4 flex flex-wrap items-center gap-2">{children}</div>
  );
}

/**
 * Segmented tabs rendered as links.
 *
 * Links rather than client state, so a filtered view is shareable, survives a
 * refresh, and works with the browser's back button.
 */
export function TabLinks({
  tabs,
  activeKey,
}: {
  tabs: ReadonlyArray<{ key: string; label: string; href: string; count?: number }>;
  activeKey: string;
}) {
  return (
    <nav
      className="inline-flex flex-wrap gap-1 rounded-control bg-surface-sunken p-1"
      aria-label="Filter"
    >
      {tabs.map((tab) => {
        const active = tab.key === activeKey;

        return (
          <a
            key={tab.key}
            href={tab.href}
            aria-current={active ? "page" : undefined}
            className={cx(
              "inline-flex items-center gap-1.5 rounded-[4px] px-2.5 py-1.5",
              "text-[12.5px] transition-colors",
              active
                ? "bg-surface font-semibold text-ink shadow-raised"
                : "text-ink-secondary hover:text-ink",
            )}
          >
            {tab.label}
            {tab.count !== undefined ? (
              <span
                className={cx(
                  "tabular rounded px-1 text-[11px] font-semibold",
                  active ? "bg-accent-tint text-accent" : "text-ink-muted",
                )}
              >
                {tab.count}
              </span>
            ) : null}
          </a>
        );
      })}
    </nav>
  );
}
