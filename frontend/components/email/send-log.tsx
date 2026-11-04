"use client";

import { useMemo, useState } from "react";
import { ChevronLeft, ChevronRight, Search } from "lucide-react";

import {
  Button,
  EmptyState,
  Panel,
  Status,
  TableFrame,
  Td,
  Th,
} from "@/components/ui/primitives";
import type { EmailLogEntry } from "@/lib/api";

/**
 * What was actually sent.
 *
 * <h3>Why failures are shown at the same weight as successes</h3>
 *
 * A log that mostly shows successes answers none of the questions it would
 * actually be opened for. People come here when a tenant says they did not
 * receive something, so the useful rows are the ones that did not work, and
 * the reason has to be on the row rather than a click away.
 *
 * <h3>What this screen does not claim</h3>
 *
 * "Accepted" means Google took the message, not that it reached an inbox.
 * Section 4 is explicit that inbox delivery must not be promised, and the
 * wording here says accepted rather than delivered for exactly that reason.
 *
 * <h3>Why the filtering is client-side</h3>
 *
 * The page fetches the most recent two hundred entries in one request, which
 * is a small payload and the whole of the history anybody scrolls. Filtering
 * and paging that in the browser is instant and costs no round trip, where a
 * server-side query per keystroke would feel worse and answer no better.
 *
 * It does mean the filters search what has been fetched rather than all of
 * history, so the panel says when it is sitting on the cap instead of
 * implying the search was exhaustive.
 */
const PageSize = 20;

export function SendLog({ entries }: { entries: EmailLogEntry[] }) {
  const [query, setQuery] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [status, setStatus] = useState<"all" | "accepted" | "failed" | "suppressed">("all");
  const [page, setPage] = useState(0);

  const filtered = useMemo(() => {
    const needle = query.trim().toLowerCase();

    return entries.filter((entry) => {
      if (status !== "all" && entry.status !== status) {
        return false;
      }

      // The instant, read as a date in the viewer's own zone — the same zone
      // the When column is formatted in, so a row shown as the 3rd is found
      // by searching for the 3rd.
      const day = localDay(entry.createdAt);

      if (from && day < from) {
        return false;
      }

      if (to && day > to) {
        return false;
      }

      if (!needle) {
        return true;
      }

      return (
        entry.subject.toLowerCase().includes(needle)
        || entry.senderEmail.toLowerCase().includes(needle)
        || parseRecipients(entry.recipientsJson)
          .some((recipient) => recipient.toLowerCase().includes(needle))
      );
    });
  }, [entries, query, from, to, status]);

  const pageCount = Math.max(1, Math.ceil(filtered.length / PageSize));

  // Clamped rather than reset in an effect. Narrowing the filters while on
  // page five would otherwise render one empty page before the effect moved
  // it, which is the cascading render the lint rule objects to — and a blank
  // table that fills itself in reads as a bug.
  const current = Math.min(page, pageCount - 1);
  const visible = filtered.slice(current * PageSize, current * PageSize + PageSize);

  const isFiltered = Boolean(query || from || to || status !== "all");

  return (
    <Panel
      title="Sent mail"
      description="Accepted means Google took the message. Delivery to an inbox is not
                   something any sender can guarantee."
    >
      {entries.length === 0 ? (
        <EmptyState
          title="Nothing has been sent yet"
          description="Once a mailbox is connected and a reminder falls due, every
                       attempt appears here — including the ones that failed."
        />
      ) : (
        <>
          {/* ------------------------------------------------------------ */}
          {/* Filters                                                       */}
          {/* ------------------------------------------------------------ */}
          <div className="flex flex-wrap items-end gap-3 border-b border-line px-4 py-3">
            <label className="min-w-[13rem] flex-1">
              <span className="mb-1 block text-[12px] font-medium">Search</span>
              <span className="relative block">
                <Search
                  className="pointer-events-none absolute left-2.5 top-1/2 size-3.5
                             -translate-y-1/2 text-ink-muted"
                  aria-hidden="true"
                />
                <input
                  type="search"
                  value={query}
                  onChange={(event) => {
                    setQuery(event.target.value);
                    setPage(0);
                  }}
                  placeholder="Subject or address"
                  className="h-8 w-full rounded-control border border-line-strong bg-surface
                             pl-8 pr-2.5 text-[13px] transition-colors focus:border-accent
                             focus:outline-none focus:ring-2 focus:ring-accent/20"
                />
              </span>
            </label>

            <label>
              <span className="mb-1 block text-[12px] font-medium">From</span>
              <input
                type="date"
                value={from}
                max={to || undefined}
                onChange={(event) => {
                  setFrom(event.target.value);
                  setPage(0);
                }}
                className="h-8 rounded-control border border-line-strong bg-surface px-2.5
                           text-[13px] transition-colors focus:border-accent
                           focus:outline-none focus:ring-2 focus:ring-accent/20"
              />
            </label>

            <label>
              <span className="mb-1 block text-[12px] font-medium">To</span>
              <input
                type="date"
                value={to}
                min={from || undefined}
                onChange={(event) => {
                  setTo(event.target.value);
                  setPage(0);
                }}
                className="h-8 rounded-control border border-line-strong bg-surface px-2.5
                           text-[13px] transition-colors focus:border-accent
                           focus:outline-none focus:ring-2 focus:ring-accent/20"
              />
            </label>

            <label>
              <span className="mb-1 block text-[12px] font-medium">Result</span>
              <select
                value={status}
                onChange={(event) => {
                  setStatus(event.target.value as typeof status);
                  setPage(0);
                }}
                className="h-8 rounded-control border border-line-strong bg-surface px-2
                           text-[13px] transition-colors focus:border-accent
                           focus:outline-none focus:ring-2 focus:ring-accent/20"
              >
                <option value="all">All</option>
                <option value="accepted">Accepted</option>
                <option value="failed">Failed</option>
                <option value="suppressed">Suppressed</option>
              </select>
            </label>

            {isFiltered ? (
              <Button
                variant="ghost"
                size="sm"
                onClick={() => {
                  setQuery("");
                  setFrom("");
                  setTo("");
                  setStatus("all");
                  setPage(0);
                }}
              >
                Clear
              </Button>
            ) : null}
          </div>

          {/* ------------------------------------------------------------ */}
          {/* Rows                                                          */}
          {/* ------------------------------------------------------------ */}
          {filtered.length === 0 ? (
            <EmptyState
              compact
              title="Nothing matches"
              description="No sent message matches those filters. Widen the dates, or clear them."
            />
          ) : (
            <TableFrame minWidth="52rem">
              <thead>
                <tr>
                  <Th>When</Th>
                  <Th>To</Th>
                  <Th>Subject</Th>
                  <Th>Result</Th>
                </tr>
              </thead>
              <tbody>
                {visible.map((entry) => {
                  const recipients = parseRecipients(entry.recipientsJson);

                  return (
                    <tr key={entry.id} className="hover:bg-surface-subtle">
                      <Td className="tabular whitespace-nowrap text-[13px] text-ink-secondary">
                        {new Date(entry.createdAt).toLocaleString()}
                      </Td>

                      <Td className="text-[13px]">
                        {recipients.length === 0 ? (
                          <span className="text-ink-muted">—</span>
                        ) : (
                          <>
                            {recipients[0]}
                            {recipients.length > 1 ? (
                              <span className="text-ink-secondary">
                                {" "}
                                and {recipients.length - 1} more
                              </span>
                            ) : null}
                          </>
                        )}
                      </Td>

                      <Td className="text-[13px] font-medium">{entry.subject}</Td>

                      <Td>
                        {entry.status === "accepted" ? (
                          <Status tone="success">Accepted</Status>
                        ) : entry.status === "suppressed" ? (
                          <Status tone="neutral">Suppressed</Status>
                        ) : (
                          <div>
                            <Status tone="danger">
                              {categoryLabel(entry.errorCategory)}
                            </Status>
                            {entry.errorMessage ? (
                              <p className="mt-0.5 max-w-sm text-[12px] leading-relaxed text-ink-secondary">
                                {entry.errorMessage}
                              </p>
                            ) : null}
                          </div>
                        )}

                        {entry.attemptNumber > 1 ? (
                          <p className="mt-0.5 text-[11.5px] text-ink-muted">
                            Attempt {entry.attemptNumber}
                          </p>
                        ) : null}
                      </Td>
                    </tr>
                  );
                })}
              </tbody>
            </TableFrame>
          )}

          {/* ------------------------------------------------------------ */}
          {/* Pager                                                         */}
          {/* ------------------------------------------------------------ */}
          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-line px-4 py-2.5">
            <p className="text-[12.5px] text-ink-secondary">
              {filtered.length === 0 ? (
                "No messages"
              ) : (
                <>
                  <span className="tabular font-medium text-ink">
                    {current * PageSize + 1}–{current * PageSize + visible.length}
                  </span>{" "}
                  of{" "}
                  <span className="tabular font-medium text-ink">{filtered.length}</span>
                  {isFiltered ? " matching" : null}
                  {/* Said plainly rather than implied. The filters search what
                      was fetched, and at the cap there is older mail they do
                      not reach. */}
                  {!isFiltered && entries.length >= 200
                    ? " (most recent 200)"
                    : null}
                </>
              )}
            </p>

            {/* Always rendered, disabled rather than hidden on a single page.
                Controls that appear only once there is enough data are
                controls nobody knows exist — and their absence reads as
                pagination being broken rather than as there being one page. */}
            <div className="flex items-center gap-1.5">
              <Button
                variant="secondary"
                size="sm"
                disabled={current === 0}
                onClick={() => setPage(current - 1)}
              >
                <ChevronLeft className="size-3.5" aria-hidden="true" />
                Previous
              </Button>

              <span className="tabular px-1 text-[12.5px] text-ink-secondary">
                {current + 1} / {pageCount}
              </span>

              <Button
                variant="secondary"
                size="sm"
                disabled={current >= pageCount - 1}
                onClick={() => setPage(current + 1)}
              >
                Next
                <ChevronRight className="size-3.5" aria-hidden="true" />
              </Button>
            </div>
          </div>
        </>
      )}
    </Panel>
  );
}

/**
 * The calendar day an instant falls on, where the reader is.
 *
 * The date inputs are plain calendar dates, and the When column is formatted
 * in the viewer's zone, so the comparison has to happen in that zone too —
 * otherwise a message sent at 7pm local is filed under tomorrow and cannot be
 * found by the date it is displayed under.
 */
function localDay(isoInstant: string): string {
  const when = new Date(isoInstant);

  if (Number.isNaN(when.getTime())) {
    return "";
  }

  const month = String(when.getMonth() + 1).padStart(2, "0");
  const day = String(when.getDate()).padStart(2, "0");

  return `${when.getFullYear()}-${month}-${day}`;
}

/**
 * Turns an error category into something a landlord can act on.
 *
 * The categories are the engine's vocabulary, not a user's. "authorization"
 * is precise and means nothing to the person reading it; "reconnect needed"
 * tells them what to do.
 */
function categoryLabel(category: string): string {
  switch (category) {
    case "authorization":
      return "Reconnect needed";
    case "quota":
      return "Daily limit reached";
    case "invalid_recipient":
      return "Bad address";
    case "transient":
      return "Temporary failure";
    case "configuration":
      return "Not configured";
    case "permanent":
      return "Refused";
    default:
      return "Failed";
  }
}

/**
 * Recipients are stored as a JSON array.
 *
 * Parsed defensively: this is a column rather than a typed field, and a
 * malformed value should cost one row's detail rather than the whole page.
 */
function parseRecipients(json: string): string[] {
  try {
    const parsed: unknown = JSON.parse(json);

    return Array.isArray(parsed) ? parsed.map(String) : [];
  } catch {
    return [];
  }
}
