import Link from "next/link";
import { redirect } from "next/navigation";
import { CalendarDays, ChevronLeft, ChevronRight } from "lucide-react";

import { Shell } from "@/components/shell";
import { ErrorState, cx } from "@/components/ui/primitives";
import { api, ApiError, apiFetch, maintenanceApi, type Lease } from "@/lib/api";
import { formatMoney } from "@/lib/format";
import {
  buildMonthGrid,
  eventKindLabels,
  formatDayLong,
  groupByDate,
  isInMonth,
  monthKeyOf,
  parseMonthKey,
  todayIso,
  weekdayLabels,
  type CalendarEvent,
  type CalendarEventKind,
} from "@/lib/calendar";
import type { SavedOccurrence } from "@/lib/rent";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * What is happening, and when.
 *
 * <h3>Why this is not the reminders page</h3>
 *
 * The reminders page answers "will anything be sent, and did the last batch
 * go out" — it is about the health of the dispatcher. This answers a
 * different question: what is due. The two differ most in the case that
 * matters, because rent is due on the first whether or not a reminder rule
 * exists, whether or not a mailbox is connected, and whether or not the
 * lease's automation is paused. A calendar built from the send queue would
 * show an empty month for a fully-let building with no mailbox set up, which
 * is precisely backwards.
 *
 * So this is built from the rent schedules and the leases themselves, which
 * are facts about the tenancy rather than facts about the mail.
 *
 * <h3>Why a small grid beside a day, rather than a large grid</h3>
 *
 * A month of a small portfolio is mostly empty. Rent lands on one or two days
 * and everything else is scattered, so a grid with cells big enough to hold
 * the detail is five hundred pixels of nothing wrapped around four facts —
 * and it still cannot show a day with five things on it.
 *
 * The grid here is sized to do the one job a grid is good at: showing which
 * days have something on them, as a shape you take in at a glance. Each day
 * is a link, and the detail for the chosen one is read next to it at full
 * size, where there is room for the amount, the property and the unit.
 *
 * <h3>Why the state is in the URL</h3>
 *
 * The month and the selected day are both navigations, not local state: they
 * should be linkable, go in the browser history, and survive a refresh.
 * Keeping them there also keeps this a server component with no client
 * JavaScript at all.
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Calendar" };

/**
 * How many leases are asked for their schedule.
 *
 * <p>
 * Each schedule is its own request, so this is the one place the page could
 * become slow on a large portfolio. The cap is high enough to be invisible
 * for the portfolios this is built for and low enough that nothing
 * pathological happens on a big one; the page says so when it bites, rather
 * than quietly drawing an incomplete month.
 * </p>
 */
const MaximumLeasesCharted = 60;

export default async function CalendarPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  const params = await searchParams;
  const today = todayIso();

  const selectedDate = readDate(params.date);

  // A selected day decides the month, so a link to a date is enough on its
  // own and the two can never point at different months.
  const { year, month } = parseMonthKey(
    selectedDate ? monthKeyOf(selectedDate) : readParam(params.month),
  );

  const grid = buildMonthGrid(year, month);

  let organizationName: string | null = null;
  let events: CalendarEvent[] = [];
  let loadError: string | null = null;
  let truncated = false;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationName = organization.name;

      const [leases, board] = await Promise.all([
        api.leases(organization.id),
        // Allowed to fail on its own. A calendar missing its maintenance
        // items is still a useful calendar; one that refuses to render
        // because the board was unavailable is not.
        maintenanceApi.board(organization.id).catch(() => null),
      ]);

      // Only leases that have dates worth charting. A rejected or draft lease
      // has no agreed rent day, and showing one would put a date on the grid
      // that nobody has committed to.
      const chartable = leases.filter(
        (lease) =>
          lease.approvalStatus === "approved"
          && lease.lifecycleStatus !== "terminated"
          && lease.lifecycleStatus !== "archived",
      );

      truncated = chartable.length > MaximumLeasesCharted;

      const schedules = await Promise.all(
        chartable.slice(0, MaximumLeasesCharted).map(async (lease) => {
          try {
            const result = await apiFetch<{ occurrences: SavedOccurrence[] }>(
              `/api/v1/organizations/${organization.id}/leases/${lease.id}/rent-schedule`,
            );

            return { lease, occurrences: result.occurrences ?? [] };
          } catch {
            // A lease with no schedule yet answers 404 or an empty body.
            // That is an ordinary state, not an error worth a banner.
            return { lease, occurrences: [] as SavedOccurrence[] };
          }
        }),
      );

      events = [
        ...schedules.flatMap(({ lease, occurrences }) =>
          occurrences.map((occurrence) => ({
            date: occurrence.dueDate,
            kind: "rentDue" as const,
            title: `Rent ${formatMoney(occurrence.amount, occurrence.currency)}`,
            location: describeLease(lease),
            href: `/leases/${lease.id}/schedule`,
            amount: occurrence.amount,
            currency: occurrence.currency,
          })),
        ),

        ...chartable.flatMap((lease) => {
          const dated: CalendarEvent[] = [];

          if (lease.startDate) {
            dated.push({
              date: lease.startDate,
              kind: "leaseStart",
              title: "Lease starts",
              location: describeLease(lease),
              href: `/leases/${lease.id}`,
            });
          }

          if (lease.endDate) {
            dated.push({
              date: lease.endDate,
              kind: "leaseEnd",
              title: "Lease ends",
              location: describeLease(lease),
              href: `/leases/${lease.id}`,
            });
          }

          return dated;
        }),

        ...(board?.columns ?? [])
          .flatMap((column) => column.tasks)
          // Only work that is still outstanding. A finished task is not due,
          // and the board keeps settled cards for a fortnight, so including
          // them would leave last week looking busier than this week.
          .filter(
            (task) =>
              task.dueDate !== null
              && task.statusCategory !== "completed"
              && task.statusCategory !== "cancelled",
          )
          .map((task) => ({
            date: task.dueDate!,
            kind: "taskDue" as const,
            title: task.title,
            location: task.unitNumber
              ? `${task.propertyName} · Unit ${task.unitNumber}`
              : task.propertyName,
            href: "/maintenance",
          })),
      ];
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  const byDate = groupByDate(events);

  const monthEvents = events
    .filter((event) => isInMonth(event.date, grid.key))
    .sort((left, right) => left.date.localeCompare(right.date));

  const rentThisMonth = monthEvents.filter((event) => event.kind === "rentDue");

  const rentTotal = rentThisMonth.reduce(
    (total, event) => total + (event.amount ?? 0),
    0,
  );

  const rentCurrency = rentThisMonth[0]?.currency ?? "USD";

  const selectedEvents = selectedDate ? (byDate.get(selectedDate) ?? []) : [];


  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      {/* ------------------------------------------------------------------ */}
      {/* Month bar                                                           */}
      {/* ------------------------------------------------------------------ */}
      <div className="mb-3 flex flex-wrap items-center gap-x-3 gap-y-2">
        <h1 className="text-[20px] font-semibold tracking-tight">{grid.label}</h1>

        <div className="flex items-center gap-0.5">
          <MonthStep
            monthKey={grid.previousKey}
            label="Previous month"
            icon={<ChevronLeft className="size-4" aria-hidden="true" />}
          />
          <MonthStep
            monthKey={grid.nextKey}
            label="Next month"
            icon={<ChevronRight className="size-4" aria-hidden="true" />}
          />
        </div>

        <Link
          href="/calendar"
          className="rounded-control border border-line-strong bg-surface px-2.5 py-1
                     text-[12.5px] font-medium text-ink-secondary transition-colors
                     hover:bg-page hover:text-ink"
        >
          Today
        </Link>

        <div className="ml-auto flex flex-wrap items-center gap-x-4 gap-y-1">
          {rentThisMonth.length > 0 ? (
            <p className="text-[12.5px] text-ink-secondary">
              <span className="tabular font-semibold text-ink">
                {formatMoney(rentTotal, rentCurrency)}
              </span>{" "}
              rent due
            </p>
          ) : null}

          <Legend />
        </div>
      </div>

      {loadError ? (
        <div className="surface p-6">
          <ErrorState description={loadError} />
        </div>
      ) : (
        <>
          {truncated ? (
            <p
              role="status"
              className="mb-3 rounded-control border border-line bg-warning-tint px-3 py-2
                         text-[12.5px] text-warning-text"
            >
              Showing rent for the first {MaximumLeasesCharted} leases. Filter by
              property from the leases page to chart the rest.
            </p>
          ) : null}

          {/* ---------------------------------------------------------- */}
          {/* The month grid                                              */}
          {/*                                                             */}
          {/* Six rows of equal height filling the window, so paging      */}
          {/* through months never moves the grid under the pointer and a */}
          {/* cell is tall enough to hold its entries rather than hiding  */}
          {/* them behind a count.                                        */}
          {/* ---------------------------------------------------------- */}
          <div className="overflow-x-auto">
            <div
              className="surface flex min-w-[52rem] flex-col overflow-hidden
                         lg:h-[calc(100dvh-13rem)] lg:min-h-[34rem]"
            >
              <div className="grid shrink-0 grid-cols-7 border-b border-line">
                {weekdayLabels.map((weekday) => (
                  <div
                    key={weekday}
                    className="px-2 py-1.5 text-[11.5px] font-medium text-ink-secondary"
                  >
                    {weekday}
                  </div>
                ))}
              </div>

              <div className="grid flex-1 grid-cols-7 grid-rows-6">
                {grid.days.map((day, index) => {
                  const dayEvents = byDate.get(day.date) ?? [];
                  const isToday = day.date === today;
                  const isSelected = day.date === selectedDate;

                  return (
                    <div
                      key={day.date}
                      className={cx(
                        "relative flex min-h-0 flex-col border-line",
                        // Interior rules only, so the grid does not double up
                        // against the panel's own edge.
                        index % 7 !== 6 && "border-r",
                        index < 35 && "border-b",
                        !day.isCurrentMonth && "bg-surface-subtle/50",
                        isSelected && "bg-accent-tint/50",
                      )}
                    >
                      {/* The whole cell chooses the day, and it sits behind
                          the entries rather than wrapping them: an entry is
                          its own link, and nesting one anchor in another is
                          invalid HTML that browsers resolve unpredictably. */}
                      <Link
                        href={`/calendar?date=${day.date}`}
                        aria-label={`Show ${formatDayLong(day.date)}`}
                        aria-current={isSelected ? "date" : undefined}
                        className="absolute inset-0 transition-colors
                                   hover:bg-surface-sunken/60
                                   focus-visible:bg-surface-sunken/60"
                      />

                      <div className="pointer-events-none relative px-1.5 pt-1.5">
                        <span
                          className={cx(
                            "tabular inline-flex h-6 min-w-6 items-center justify-center",
                            "rounded-full px-1.5 text-[12.5px]",
                            isToday
                              ? "bg-accent font-semibold text-white"
                              : isSelected
                                ? "font-semibold text-accent"
                                : day.isCurrentMonth
                                  ? "text-ink-secondary"
                                  : "text-ink-muted/70",
                          )}
                        >
                          {day.dayOfMonth}
                        </span>
                      </div>

                      <ul className="relative mt-0.5 min-h-0 flex-1 space-y-[2px] overflow-hidden px-1 pb-1">
                        {dayEvents.slice(0, 3).map((event, eventIndex) => (
                          <li key={`${event.kind}-${event.href}-${eventIndex}`}>
                            <Link
                              href={event.href}
                              title={`${eventKindLabels[event.kind]}: ${event.title} — ${event.location}`}
                              className={cx(
                                "flex items-center gap-1.5 rounded-[4px] px-1.5 py-[3px]",
                                "text-[11.5px] leading-tight transition-colors",
                                kindChip[event.kind],
                              )}
                            >
                              <span
                                className={cx(
                                  "size-1.5 shrink-0 rounded-full",
                                  kindDot[event.kind],
                                )}
                                aria-hidden="true"
                              />
                              <span className="truncate">{event.title}</span>
                            </Link>
                          </li>
                        ))}

                        {dayEvents.length > 3 ? (
                          <li>
                            <Link
                              href={`/calendar?date=${day.date}`}
                              className="relative block px-1.5 text-[11px] font-medium
                                         text-ink-secondary hover:text-accent hover:underline"
                            >
                              {dayEvents.length - 3} more
                            </Link>
                          </li>
                        ) : null}
                      </ul>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>

          {/* ---------------------------------------------------------- */}
          {/* The chosen day                                              */}
          {/*                                                             */}
          {/* Only once a day has been chosen. With nothing selected the  */}
          {/* grid is the whole answer, and a permanent panel below it    */}
          {/* would be the same month written out twice.                  */}
          {/* ---------------------------------------------------------- */}
          {selectedDate ? (
            <section className="surface mt-4 overflow-hidden">
              <header className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1.5 border-b border-line px-4 py-2.5">
                <h2 className="text-[14px] font-semibold tracking-tight">
                  {formatDayLong(selectedDate)}
                </h2>

                <Link
                  href={`/calendar?month=${grid.key}`}
                  className="text-[12px] font-medium text-accent hover:underline"
                >
                  Clear selection
                </Link>
              </header>

              <EntryList
                entries={selectedEvents}
                emptyTitle="Nothing on this day"
                emptyBody="Pick another day, or clear the selection to see the month."
              />
            </section>
          ) : null}
        </>
      )}
    </Shell>
  );
}

/**
 * The entries for one day, at full size.
 *
 * <p>
 * The kind is carried by a coloured rule and by its name in the row, never by
 * colour alone.
 * </p>
 */
function EntryList({
  entries,
  emptyTitle,
  emptyBody,
}: {
  entries: CalendarEvent[];
  emptyTitle: string;
  emptyBody: string;
}) {
  if (entries.length === 0) {
    return (
      <div className="px-6 py-10 text-center">
        <div
          className="mx-auto mb-3 grid size-10 place-items-center rounded-full
                     bg-surface-sunken text-ink-muted"
          aria-hidden="true"
        >
          <CalendarDays className="size-5" />
        </div>
        <h3>{emptyTitle}</h3>
        <p className="mx-auto mt-1.5 max-w-sm text-[13px] leading-relaxed text-ink-secondary">
          {emptyBody}
        </p>
      </div>
    );
  }

  return (
    <ul className="divide-y divide-line">
      {entries.map((entry, index) => (
        <li key={`${entry.date}-${entry.kind}-${index}`}>
          <Link
            href={entry.href}
            className="flex items-center gap-3 px-4 py-2.5 transition-colors
                       hover:bg-surface-subtle"
          >
            <span
              className={cx("h-8 w-[3px] shrink-0 rounded-full", kindDot[entry.kind])}
              aria-hidden="true"
            />

            <span className="min-w-0 flex-1">
              <span className="block truncate text-[13.5px] font-medium">
                {entry.title}
              </span>
              <span className="block truncate text-[12px] text-ink-secondary">
                {eventKindLabels[entry.kind]} · {entry.location}
              </span>
            </span>
          </Link>
        </li>
      ))}
    </ul>
  );
}

/** One colour per kind, always paired with the kind's name somewhere nearby. */
const kindDot: Record<CalendarEventKind, string> = {
  rentDue: "bg-success",
  leaseEnd: "bg-danger",
  leaseStart: "bg-accent",
  taskDue: "bg-warning",
};

/** The chip a kind wears inside a grid cell. */
const kindChip: Record<CalendarEventKind, string> = {
  rentDue: "bg-success-tint text-success-text hover:bg-success/15",
  leaseEnd: "bg-danger-tint text-danger-text hover:bg-danger/15",
  leaseStart: "bg-accent-tint text-accent hover:bg-accent/15",
  taskDue: "bg-warning-tint text-warning-text hover:bg-warning/15",
};

function Legend() {
  return (
    <ul className="flex flex-wrap items-center gap-x-3 gap-y-1">
      {(Object.keys(eventKindLabels) as CalendarEventKind[]).map((kind) => (
        <li
          key={kind}
          className="flex items-center gap-1.5 text-[11.5px] text-ink-secondary"
        >
          <span
            className={cx("size-1.5 rounded-full", kindDot[kind])}
            aria-hidden="true"
          />
          {eventKindLabels[kind]}
        </li>
      ))}
    </ul>
  );
}

/**
 * A step to the next or previous month.
 *
 * Carries no date: a selected day belongs to the month it is in, so stepping
 * away from it clears the selection rather than dragging the 31st into a
 * month with thirty days.
 */
function MonthStep({
  monthKey,
  label,
  icon,
}: {
  monthKey: string;
  label: string;
  icon: React.ReactNode;
}) {
  return (
    <Link
      href={`/calendar?month=${monthKey}`}
      aria-label={label}
      title={label}
      className="inline-flex size-7 items-center justify-center rounded-control
                 text-ink-secondary transition-colors
                 hover:bg-surface-sunken hover:text-ink"
    >
      {icon}
    </Link>
  );
}

function readParam(value: string | string[] | undefined): string | null {
  return (Array.isArray(value) ? value[0] : value) ?? null;
}

/** Only a well-formed date is honoured; anything else selects nothing. */
function readDate(value: string | string[] | undefined): string | null {
  const raw = readParam(value);

  return raw && /^\d{4}-\d{2}-\d{2}$/.test(raw) ? raw : null;
}

/** "87 Winding Way · Unit 2", or just the property when there is no unit. */
function describeLease(lease: Lease): string {
  return lease.unitNumber
    ? `${lease.propertyName} · Unit ${lease.unitNumber}`
    : lease.propertyName;
}
