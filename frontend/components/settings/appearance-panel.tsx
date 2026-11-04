"use client";

import { useCallback, useSyncExternalStore } from "react";
import { Monitor, Moon, Sun, type LucideIcon } from "lucide-react";

import { cx } from "@/components/ui/primitives";

/**
 * Light, dark, or follow the machine.
 *
 * <h3>Why three choices and not a switch</h3>
 *
 * A two-state toggle cannot express "whatever my laptop is doing", which is
 * what most people actually want — and it is the state somebody is in before
 * they have touched this at all. A switch would have to pick a side on their
 * behalf and then disagree with every other application on their screen at
 * sunset.
 *
 * <h3>Why this writes an attribute rather than holding state</h3>
 *
 * The theme has to be applied before the first paint, or every page load
 * flashes white. That is done by a small script in app/layout.tsx reading
 * localStorage and stamping <code>data-theme</code> on the document. This
 * component therefore does not own the value: it writes the same attribute
 * and subscribes to it, so there is one source of truth and nothing to keep
 * in step. See app/globals.css for how the tokens respond.
 */

type Theme = "light" | "dark" | "system";

const options: Array<{ value: Theme; label: string; icon: LucideIcon }> = [
  { value: "light", label: "Light", icon: Sun },
  { value: "dark", label: "Dark", icon: Moon },
  { value: "system", label: "System", icon: Monitor },
];

/**
 * Re-renders when the attribute changes.
 *
 * Module scope, because useSyncExternalStore resubscribes whenever this
 * function's identity changes and an inline arrow would rebuild the observer
 * on every render.
 */
function subscribe(onChange: () => void): () => void {
  const observer = new MutationObserver(onChange);

  observer.observe(document.documentElement, {
    attributes: true,
    attributeFilter: ["data-theme"],
  });

  return () => observer.disconnect();
}

function readTheme(): Theme {
  const value = document.documentElement.getAttribute("data-theme");

  return value === "dark" || value === "light" ? value : "system";
}

export function AppearancePanel() {
  // "system" on the server, which is what the server rendered: it has no way
  // to know, and guessing would mean a hydration mismatch on every load.
  const theme = useSyncExternalStore(subscribe, readTheme, () => "system" as Theme);

  const choose = useCallback((next: Theme) => {
    if (next === "system") {
      document.documentElement.removeAttribute("data-theme");
    } else {
      document.documentElement.setAttribute("data-theme", next);
    }

    try {
      if (next === "system") {
        localStorage.removeItem("theme");
      } else {
        localStorage.setItem("theme", next);
      }
    } catch {
      // A browser blocking site data still gets the theme for this visit;
      // it just will not be remembered.
    }
  }, []);

  return (
    <div
      role="radiogroup"
      aria-label="Colour theme"
      className="flex flex-wrap gap-2"
    >
      {options.map(({ value, label, icon: Icon }) => {
        const selected = theme === value;

        return (
          <button
            key={value}
            type="button"
            role="radio"
            aria-checked={selected}
            onClick={() => choose(value)}
            className={cx(
              "flex min-w-[7.5rem] flex-1 items-center gap-2.5 rounded-control border",
              "px-3 py-2.5 text-left text-[13px] transition-colors",
              selected
                ? "border-accent bg-accent-tint font-medium text-accent"
                : "border-line bg-surface text-ink-secondary hover:border-line-strong hover:text-ink",
            )}
          >
            <Icon className="size-4 shrink-0" aria-hidden="true" />
            {label}
          </button>
        );
      })}
    </div>
  );
}
