/**
 * The operations calendar.
 *
 * <h3>Why the date maths here never touches `Date` for a calendar date</h3>
 *
 * Everything this module arranges is a date, not an instant. A rent due date,
 * a lease end date and a task due date are all calendar dates in the
 * property's own time zone, and they arrive as "2026-04-01". Passing that
 * through `new Date()` reads it as midnight UTC and renders 31 March for
 * anybody west of Greenwich — which would put rent on the wrong day of the
 * grid, in the wrong week, and occasionally in the wrong month.
 *
 * So dates are parsed into their parts and compared as strings or as UTC
 * midnights, and the only place the viewer's clock is consulted at all is
 * {@link todayIso}, which is asking a genuinely local question: what day is
 * it where the person looking at the screen is.
 */

export type CalendarEventKind =
  | "rentDue"
  | "leaseStart"
  | "leaseEnd"
  | "taskDue";

export interface CalendarEvent {
  /** "YYYY-MM-DD", in the property's zone. */
  date: string;
  kind: CalendarEventKind;
  /** The headline: "Rent $1,850" or "Lease ends". */
  title: string;
  /** Where: "87 Winding Way · Unit 2". */
  location: string;
  /** Where clicking it goes. */
  href: string;
  /** Set for rent, so a month can be totalled. */
  amount?: number;
  currency?: string;
}

export const eventKindLabels: Record<CalendarEventKind, string> = {
  rentDue: "Rent due",
  leaseStart: "Lease starts",
  leaseEnd: "Lease ends",
  taskDue: "Task due",
};

/**
 * One month, as the grid draws it.
 *
 * Weeks start on Monday. Six rows always, so the grid does not change height
 * between months — a calendar that reflows as you page through it makes the
 * day you were looking at move under the cursor.
 */
export interface MonthGrid {
  /** "YYYY-MM". */
  key: string;
  year: number;
  /** 1-12. */
  month: number;
  label: string;
  previousKey: string;
  nextKey: string;
  /** 42 cells, Monday first. */
  days: Array<{ date: string; dayOfMonth: number; isCurrentMonth: boolean }>;
}

const monthNames = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December",
] as const;

export const weekdayLabels = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"] as const;

/** Today where the viewer is, as "YYYY-MM-DD". */
export function todayIso(): string {
  const now = new Date();

  return isoDate(now.getFullYear(), now.getMonth() + 1, now.getDate());
}

function isoDate(year: number, month: number, day: number): string {
  return `${year}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`;
}

/**
 * Parses "YYYY-MM" into a month, falling back to the current one.
 *
 * Validated rather than trusted: this comes off the query string, and
 * `?month=lol` should show this month rather than a grid of NaN.
 */
export function parseMonthKey(value: string | null | undefined): {
  year: number;
  month: number;
} {
  const match = /^(\d{4})-(\d{2})$/.exec(value ?? "");

  if (match) {
    const year = Number(match[1]);
    const month = Number(match[2]);

    if (year >= 1970 && year <= 2200 && month >= 1 && month <= 12) {
      return { year, month };
    }
  }

  const now = new Date();

  return { year: now.getFullYear(), month: now.getMonth() + 1 };
}

export function buildMonthGrid(year: number, month: number): MonthGrid {
  // Date.UTC is used purely as a calendar calculator here — for "what weekday
  // is the first", and for stepping day by day across a month boundary. No
  // value derived from it is ever rendered in the viewer's zone.
  const firstOfMonth = new Date(Date.UTC(year, month - 1, 1));

  // getUTCDay is 0 for Sunday; the grid starts on Monday, so Sunday becomes 6.
  const leadingBlanks = (firstOfMonth.getUTCDay() + 6) % 7;

  const start = new Date(Date.UTC(year, month - 1, 1 - leadingBlanks));

  const days = Array.from({ length: 42 }, (_, index) => {
    const cell = new Date(start.getTime() + index * 86_400_000);
    const cellYear = cell.getUTCFullYear();
    const cellMonth = cell.getUTCMonth() + 1;
    const cellDay = cell.getUTCDate();

    return {
      date: isoDate(cellYear, cellMonth, cellDay),
      dayOfMonth: cellDay,
      isCurrentMonth: cellMonth === month && cellYear === year,
    };
  });

  const previous = month === 1 ? { year: year - 1, month: 12 } : { year, month: month - 1 };
  const next = month === 12 ? { year: year + 1, month: 1 } : { year, month: month + 1 };

  return {
    key: `${year}-${String(month).padStart(2, "0")}`,
    year,
    month,
    label: `${monthNames[month - 1]} ${year}`,
    previousKey: `${previous.year}-${String(previous.month).padStart(2, "0")}`,
    nextKey: `${next.year}-${String(next.month).padStart(2, "0")}`,
    days,
  };
}

/** Groups events by their date, so a grid cell is one dictionary lookup. */
export function groupByDate(
  events: CalendarEvent[],
): Map<string, CalendarEvent[]> {
  const byDate = new Map<string, CalendarEvent[]>();

  for (const event of events) {
    const existing = byDate.get(event.date);

    if (existing) {
      existing.push(event);
    } else {
      byDate.set(event.date, [event]);
    }
  }

  // Within a day, the order is the order of the kinds below rather than the
  // order they happened to be fetched in, so two days read the same way.
  const kindOrder: CalendarEventKind[] = [
    "rentDue",
    "leaseEnd",
    "leaseStart",
    "taskDue",
  ];

  for (const list of byDate.values()) {
    list.sort(
      (left, right) =>
        kindOrder.indexOf(left.kind) - kindOrder.indexOf(right.kind)
        || left.title.localeCompare(right.title),
    );
  }

  return byDate;
}

/** "2026-04-01" is in "2026-04". A plain prefix test, which is all it needs. */
export function isInMonth(date: string, monthKey: string): boolean {
  return date.startsWith(monthKey);
}

/** The month a date belongs to, for a link that jumps to it. */
export function monthKeyOf(date: string): string {
  return date.slice(0, 7);
}

/**
 * "Thursday 5 November", for the heading over a chosen day.
 *
 * The weekday is derived with Date.UTC rather than by parsing the string into
 * a local Date, for the reason at the top of this file: a local parse of
 * "2026-11-05" is midnight UTC, which is the 4th — and a Wednesday — for
 * anybody west of Greenwich.
 */
export function formatDayLong(isoDate: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(isoDate);

  if (!match) {
    return isoDate;
  }

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);

  const weekdays = [
    "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday",
  ] as const;

  const months = [
    "January", "February", "March", "April", "May", "June",
    "July", "August", "September", "October", "November", "December",
  ] as const;

  const weekday = weekdays[new Date(Date.UTC(year, month - 1, day)).getUTCDay()];

  return `${weekday} ${day} ${months[month - 1]}`;
}
