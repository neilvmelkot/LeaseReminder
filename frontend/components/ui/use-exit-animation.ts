"use client";

import { useCallback, useEffect, useRef, useState } from "react";

/** Matches the exit animations in globals.css. */
export const exitDurationMs = 150;

/**
 * Lets a mount-to-open surface animate on the way out.
 *
 * <h3>The problem it solves</h3>
 *
 * Every dialog and drawer here is rendered only while it is open, which is
 * what guarantees its fields start empty. The cost is that closing is an
 * unmount, and an unmount is instantaneous: the surface animates in, then
 * simply ceases to exist. That reads as a glitch rather than as a dismissal —
 * the eye tracked something arriving and then lost it.
 *
 * So a dismissal sets <code>isClosing</code>, which swaps the entry animation
 * for its reverse, and the caller's close handler runs once that has played.
 * The surface's contract with its parent does not change.
 *
 * <h3>Reduced motion</h3>
 *
 * Closes immediately. Somebody who has asked for less movement should not be
 * made to wait out an animation they are not being shown — a delay with no
 * animation is just latency.
 */
export function useExitAnimation(onClose: () => void) {
  const [isClosing, setIsClosing] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const requestClose = useCallback(() => {
    // Guarded: Escape held down, or a second click on the backdrop, would
    // otherwise queue a second close after the first has already run.
    if (timer.current !== null) {
      return;
    }

    const reducedMotion =
      typeof window !== "undefined"
      && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    if (reducedMotion) {
      onClose();

      return;
    }

    setIsClosing(true);
    timer.current = setTimeout(onClose, exitDurationMs);
  }, [onClose]);

  useEffect(
    () => () => {
      if (timer.current !== null) {
        clearTimeout(timer.current);
      }
    },
    [],
  );

  return { isClosing, requestClose };
}
