"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import { Search, X } from "lucide-react";

import {
  EmptyState,
  Status,
  TableFrame,
  Td,
  Th,
} from "@/components/ui/primitives";
import { PillTabs } from "@/components/ui/pill-tabs";
import type { Property } from "@/lib/api";
import { formatAddress, formatPropertyType, pluralize } from "@/lib/format";

type View = "active" | "archived" | "all";

/**
 * The portfolio, filtered and searchable.
 *
 * <h3>Why archived is a filter rather than a separate page</h3>
 *
 * Somebody looking for an archived property does not know it is archived —
 * that is usually what they are trying to find out. A separate page means
 * searching twice and concluding the property was deleted when the first
 * search comes back empty.
 *
 * So all three views share one list and one search box, and the counts on the
 * tabs say what is where. Active is the default, because the portfolio is what
 * the page is for.
 *
 * <h3>Why the search filters in the browser</h3>
 *
 * The list is already here — the page fetched it to render the table — so
 * filtering is a comparison over data in hand. A request per keystroke would
 * be slower, would need debouncing, and would leave an empty result ambiguous
 * between "no match" and "still loading".
 *
 * It searches the address as well as the name, because "the Oak Avenue one" and
 * "14 Maple Street" are both things people remember.
 */
export function PropertyTable({ properties }: { properties: Property[] }) {
  const [view, setView] = useState<View>("active");
  const [query, setQuery] = useState("");

  const counts = useMemo(
    () => ({
      active: properties.filter((property) => property.archivedAt == null).length,
      archived: properties.filter((property) => property.archivedAt != null).length,
      all: properties.length,
    }),
    [properties],
  );

  const visible = useMemo(() => {
    const byView = properties.filter((property) => {
      if (view === "active") {
        return property.archivedAt == null;
      }

      if (view === "archived") {
        return property.archivedAt != null;
      }

      return true;
    });

    const needle = query.trim().toLowerCase();

    if (!needle) {
      return byView;
    }

    return byView.filter((property) =>
      `${property.name} ${formatAddress(property) ?? ""} ${property.timeZone}`
        .toLowerCase()
        .includes(needle),
    );
  }, [properties, view, query]);

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-3 px-5 pt-4 pb-3">
        <PillTabs
          ariaLabel="Property status"
          activeKey={view}
          onSelect={setView}
          size="sm"
          tabs={[
            { key: "active", label: "Active", count: counts.active },
            { key: "archived", label: "Archived", count: counts.archived },
            { key: "all", label: "All", count: counts.all },
          ]}
        />

        <label className="relative w-full sm:w-64">
          <span className="sr-only">Search properties</span>
          <Search
            className="pointer-events-none absolute top-1/2 left-2.5 size-3.5
                       -translate-y-1/2 text-ink-muted"
            aria-hidden="true"
          />
          <input
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Search name or address"
            className="w-full rounded-control border border-line-strong bg-surface py-1.5
                       pr-8 pl-8 text-[13px] transition-colors focus:border-accent
                       focus:outline-none focus:ring-2 focus:ring-accent/20"
          />
          {query ? (
            <button
              type="button"
              onClick={() => setQuery("")}
              aria-label="Clear search"
              className="absolute top-1/2 right-2 -translate-y-1/2 rounded-control p-0.5
                         text-ink-muted transition-colors hover:text-ink"
            >
              <X className="size-3.5" aria-hidden="true" />
            </button>
          ) : null}
        </label>
      </div>

      {visible.length === 0 ? (
        query ? (
          <EmptyState
            compact
            title="Nothing matches that"
            description="Search looks at the name, the address, and the time zone."
          />
        ) : view === "archived" ? (
          <EmptyState
            compact
            title="Nothing is archived"
            description="A property with history is archived rather than deleted, and would appear here."
          />
        ) : (
          <EmptyState
            title="No properties yet"
            description="A property is where everything else hangs off: units, leases,
                         tenants, and the time zone rent dates are read in. Add one to
                         begin."
          />
        )
      ) : (
        <TableFrame>
          <thead>
            <tr>
              <Th>Property</Th>
              <Th>Address</Th>
              <Th>Time zone</Th>
              <Th className="text-right">Units</Th>
              <Th className="text-right">Active leases</Th>
            </tr>
          </thead>
          <tbody>
            {visible.map((property) => (
              <tr key={property.id} className="hover:bg-surface-subtle">
                <Td className="font-medium">
                  <span className="flex flex-wrap items-center gap-1.5">
                    <Link
                      href={`/properties/${property.id}`}
                      className="transition-colors hover:text-accent"
                    >
                      {property.name}
                    </Link>

                    {/* Shown on the row, not only on the Archived tab: the
                        All view mixes both, and a row that looks active but
                        is not is the one thing this must never do. */}
                    {property.archivedAt != null ? (
                      <Status tone="warning">Archived</Status>
                    ) : null}
                  </span>

                  <p className="mt-0.5 text-[12px] font-normal text-ink-secondary">
                    {formatPropertyType(property.propertyType)}
                  </p>
                </Td>

                <Td className="text-[13px] text-ink-secondary">
                  {formatAddress(property) ?? (
                    <span className="text-ink-muted">Not set</span>
                  )}
                </Td>

                <Td className="text-[13px] text-ink-secondary">
                  {property.timeZone}
                </Td>

                <Td className="tabular text-right">
                  {pluralize(property.unitCount, "unit")}
                </Td>

                <Td className="tabular text-right">{property.activeLeaseCount}</Td>
              </tr>
            ))}
          </tbody>
        </TableFrame>
      )}
    </div>
  );
}
