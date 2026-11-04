"use client";

import { useEffect, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { Check, ListFilter, X } from "lucide-react";

import { Button, cx } from "@/components/ui/primitives";

/**
 * Narrows a board to one property, from a list that may be long.
 *
 * <h3>Why a button rather than a row of tabs</h3>
 *
 * One tab per property reads fine with three properties and becomes the page
 * with thirty: the filter wraps onto four lines and pushes the thing being
 * filtered below the fold. The cost scales with the portfolio, which is
 * exactly backwards — the bigger the portfolio, the more the filter matters
 * and the worse it gets.
 *
 * A button costs one line at any size, and the list it opens can scroll.
 *
 * <h3>Why the current filter is named on the button</h3>
 *
 * A filter you cannot see is how somebody concludes their tasks have
 * disappeared. The button reads "Oak Avenue", not "Filter", whenever one is
 * applied — so the state is legible without opening anything.
 *
 * <h3>Why selecting is still a navigation</h3>
 *
 * The board is server-rendered per property, so the filter is a URL. That
 * keeps a filtered board shareable and surviving a refresh, which client state
 * would not.
 */
export function PropertyFilter({
  properties,
  selectedId,
  basePath,
}: {
  properties: Array<{ id: string; name: string }>;
  /** Null for every property. */
  selectedId: string | null;
  /** Where the links point, e.g. "/maintenance". */
  basePath: string;
}) {
  const router = useRouter();
  const [isOpen, setIsOpen] = useState(false);
  const [query, setQuery] = useState("");
  const containerRef = useRef<HTMLDivElement>(null);

  const selected = properties.find((property) => property.id === selectedId);

  // A search box only earns its space once scanning the list is work.
  const isSearchable = properties.length > 8;

  const visible = query.trim()
    ? properties.filter((property) =>
        property.name.toLowerCase().includes(query.trim().toLowerCase()),
      )
    : properties;

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    function onPointerDown(event: MouseEvent) {
      if (!containerRef.current?.contains(event.target as Node)) {
        setIsOpen(false);
      }
    }

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setIsOpen(false);
      }
    }

    document.addEventListener("mousedown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);

    return () => {
      document.removeEventListener("mousedown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [isOpen]);

  function choose(propertyId: string | null) {
    setIsOpen(false);
    setQuery("");
    router.push(propertyId ? `${basePath}?propertyId=${propertyId}` : basePath);
  }

  return (
    <div ref={containerRef} className="relative inline-flex items-center gap-2">
      <Button
        variant={selected ? "secondary" : "subtle"}
        size="sm"
        aria-expanded={isOpen}
        aria-haspopup="true"
        onClick={() => setIsOpen((open) => !open)}
      >
        <ListFilter className="size-3.5" aria-hidden="true" />
        {selected ? selected.name : "All properties"}
      </Button>

      {/* A way out that does not involve opening the list again. */}
      {selected ? (
        <button
          type="button"
          onClick={() => choose(null)}
          aria-label="Show every property"
          className="rounded-control p-1 text-ink-muted transition-colors hover:text-ink"
        >
          <X className="size-3.5" aria-hidden="true" />
        </button>
      ) : null}

      {isOpen ? (
        <div
          role="menu"
          className="absolute top-full left-0 z-30 mt-1.5 w-64 overflow-hidden
                     rounded-surface border border-line bg-surface shadow-floating
                     motion-safe:animate-[dialog-in_150ms_cubic-bezier(0.16,1,0.3,1)]"
        >
          {isSearchable ? (
            <div className="border-b border-line p-2">
              <input
                autoFocus
                type="search"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder="Search properties"
                className="w-full rounded-control border border-line-strong bg-surface
                           px-2 py-1.5 text-[13px] transition-colors focus:border-accent
                           focus:outline-none focus:ring-2 focus:ring-accent/20"
              />
            </div>
          ) : null}

          <ul className="thin-scrollbar max-h-72 overflow-y-auto p-1">
            <li>
              <FilterOption
                label="All properties"
                isSelected={selectedId === null}
                onSelect={() => choose(null)}
              />
            </li>

            {visible.map((property) => (
              <li key={property.id}>
                <FilterOption
                  label={property.name}
                  isSelected={property.id === selectedId}
                  onSelect={() => choose(property.id)}
                />
              </li>
            ))}

            {visible.length === 0 ? (
              <li className="px-2.5 py-3 text-center text-[12.5px] text-ink-secondary">
                Nothing matches that.
              </li>
            ) : null}
          </ul>
        </div>
      ) : null}
    </div>
  );
}

function FilterOption({
  label,
  isSelected,
  onSelect,
}: {
  label: string;
  isSelected: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      role="menuitemradio"
      aria-checked={isSelected}
      onClick={onSelect}
      className={cx(
        "flex w-full items-center gap-2 rounded-control px-2.5 py-1.5 text-left",
        "text-[13px] transition-colors",
        isSelected
          ? "bg-accent-tint font-medium text-accent"
          : "text-ink-secondary hover:bg-surface-subtle hover:text-ink",
      )}
    >
      {/* A fixed-width slot, so the labels line up whether ticked or not. */}
      <span className="flex size-3.5 shrink-0 items-center justify-center">
        {isSelected ? <Check className="size-3.5" aria-hidden="true" /> : null}
      </span>
      <span className="truncate">{label}</span>
    </button>
  );
}
