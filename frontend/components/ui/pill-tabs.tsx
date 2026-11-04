"use client";

import { useLayoutEffect, useRef, useState } from "react";
import Link from "next/link";

/**
 * A segmented control whose selection slides between options.
 *
 * <h3>Why the indicator is a measured element rather than a border</h3>
 *
 * Moving a background between tabs is the whole point: it shows which one you
 * came from, so the change reads as one control changing state rather than two
 * unrelated things repainting. A border or a background colour on the active
 * item cannot animate between siblings — only a single element that moves can.
 *
 * So the indicator is one absolutely positioned div, and its position is
 * measured from the active tab's own geometry. That survives tabs of different
 * widths, which labels like "All properties" and "Oak Avenue" certainly are.
 *
 * <h3>On motion</h3>
 *
 * The transition is disabled until after the first measurement, so the
 * indicator does not fly in from the left on mount. And it honours
 * prefers-reduced-motion through the `motion-safe:` variant: somebody who has
 * asked for less movement gets an instant change, which is the correct
 * response to that preference rather than a slower animation.
 */
export function PillTabs<T extends string>({
  tabs,
  activeKey,
  onSelect,
  size = "md",
  ariaLabel,
}: {
  tabs: ReadonlyArray<{
    key: T;
    label: string;
    count?: number;
    /** Optional leading glyph. Decorative: the label carries the meaning. */
    icon?: React.ReactNode;
    /** When present the tab is a link rather than a button. */
    href?: string;
  }>;
  activeKey: T;
  onSelect?: (key: T) => void;
  size?: "sm" | "md";
  ariaLabel: string;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [indicator, setIndicator] = useState<{ left: number; width: number } | null>(
    null,
  );
  const [hasMeasured, setHasMeasured] = useState(false);

  // Layout effect, not effect: measuring after paint would show the indicator
  // in the wrong place for a frame.
  useLayoutEffect(() => {
    const container = containerRef.current;

    if (!container) {
      return;
    }

    function measure() {
      const element = container?.querySelector<HTMLElement>('[data-active="true"]');

      if (!element || !container) {
        return;
      }

      setIndicator({
        left: element.offsetLeft,
        width: element.offsetWidth,
      });
    }

    measure();

    // Labels reflow when the container resizes, and a count can change from
    // "9" to "10" and widen its tab. Without this the indicator drifts.
    const observer = new ResizeObserver(measure);
    observer.observe(container);

    for (const child of container.children) {
      observer.observe(child);
    }

    // Deferred so the first paint is already correct before transitions are
    // allowed, which is what stops the mount animation.
    const frame = requestAnimationFrame(() => setHasMeasured(true));

    return () => {
      observer.disconnect();
      cancelAnimationFrame(frame);
    };
  }, [tabs, activeKey]);

  const padding = size === "sm" ? "px-2.5 py-1" : "px-3 py-1.5";
  const text = size === "sm" ? "text-[12px]" : "text-[12.5px]";

  return (
    <div
      ref={containerRef}
      role="tablist"
      aria-label={ariaLabel}
      className="relative inline-flex flex-wrap gap-1 rounded-control bg-surface-sunken p-1"
    >
      {/* The moving part. aria-hidden because it carries no meaning a screen
          reader needs: aria-selected on the tabs already says which is which. */}
      {indicator ? (
        <span
          aria-hidden="true"
          className={`absolute top-1 bottom-1 rounded-control bg-surface shadow-sm
                      ${hasMeasured ? "motion-safe:transition-all motion-safe:duration-200 motion-safe:ease-out" : ""}`}
          style={{ left: indicator.left, width: indicator.width }}
        />
      ) : null}

      {tabs.map((tab) => {
        const isActive = tab.key === activeKey;

        const content = (
          <>
            {tab.icon ? (
              <span className="shrink-0" aria-hidden="true">
                {tab.icon}
              </span>
            ) : null}
            {tab.label}
            {tab.count !== undefined ? (
              <span
                className={`tabular ml-1.5 ${
                  isActive ? "text-ink-secondary" : "text-ink-muted"
                }`}
              >
                {tab.count}
              </span>
            ) : null}
          </>
        );

        const className = `relative z-10 inline-flex items-center gap-1.5 rounded-control
                           ${padding} ${text} font-medium transition-colors
                           motion-safe:duration-150 ${
                             isActive
                               ? "text-ink"
                               : "text-ink-secondary hover:text-ink"
                           }`;

        return tab.href ? (
          <Link
            key={tab.key}
            href={tab.href}
            role="tab"
            aria-selected={isActive}
            data-active={isActive}
            className={className}
          >
            {content}
          </Link>
        ) : (
          <button
            key={tab.key}
            type="button"
            role="tab"
            aria-selected={isActive}
            data-active={isActive}
            onClick={() => onSelect?.(tab.key)}
            className={className}
          >
            {content}
          </button>
        );
      })}
    </div>
  );
}
