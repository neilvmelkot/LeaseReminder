/**
 * Types and helpers for rent schedules.
 */

export type RentFrequency =
  | "monthly"
  | "weekly"
  | "biweekly"
  | "every_30_days"
  | "one_time"
  | "custom";

export type DueDayRule =
  | "first_of_month"
  | "last_of_month"
  | "specific_day"
  | "first_business_day"
  | "custom_sequence";

export type ShortMonthFallback = "last_day_of_month" | "skip_month" | "manual_exception";

export type BusinessDayShift = "none" | "previous_business_day" | "next_business_day";

export interface RentScheduleInput {
  frequency: RentFrequency;
  dueDayRule: DueDayRule;
  dueDayOfMonth: number | null;
  startDate: string;
  endDate: string | null;
  amount: number;
  currency: string;
  timeZone: string;
  shortMonthFallback: ShortMonthFallback;
  businessDayShift: BusinessDayShift;
  holidayCalendarId: string | null;
  gracePeriodDays: number | null;
  lateFeeAfterDays: number | null;
}

export interface PreviewOccurrence {
  sequenceNumber: number;
  nominalDueDate: string;
  dueDate: string;
  gracePeriodEndDate: string | null;
  lateFeeDate: string | null;
  amount: number;
  currency: string;
  adjustmentReason: string | null;
  /** The lease says one date, the reminder goes on another. */
  wasShifted: boolean;
}

export interface UnresolvedMonth {
  monthStart: string;
  requestedDayOfMonth: number;
  daysInMonth: number;
}

export interface SchedulePreview {
  occurrences: PreviewOccurrence[];
  unresolvedMonths: UnresolvedMonth[];
  totalGenerated: number;
}

export interface SavedSchedule {
  id: string;
  frequency: RentFrequency;
  dueDayRule: DueDayRule;
  dueDayOfMonth: number | null;
  startDate: string;
  endDate: string | null;
  amount: number | null;
  currency: string | null;
  timeZone: string;
  shortMonthFallback: ShortMonthFallback;
  businessDayShift: BusinessDayShift;
  holidayCalendarId: string | null;
  generationVersion: number;
}

export interface SavedOccurrence {
  id: string;
  sequenceNumber: number;
  nominalDueDate: string;
  dueDate: string;
  gracePeriodEndDate: string | null;
  lateFeeDate: string | null;
  amount: number;
  currency: string;
  adjustmentReason: string | null;
}

/* -------------------------------------------------------------------------- */
/* Presentation                                                                */
/* -------------------------------------------------------------------------- */

export const frequencyOptions: Array<{ value: RentFrequency; label: string; hint: string }> = [
  { value: "monthly", label: "Monthly", hint: "The usual case" },
  { value: "weekly", label: "Weekly", hint: "Every seven days from the start" },
  { value: "biweekly", label: "Every two weeks", hint: "Twenty-six payments a year" },
  {
    value: "every_30_days",
    label: "Every 30 days",
    hint: "Drifts against the calendar on purpose; not the same as monthly",
  },
  { value: "one_time", label: "One payment", hint: "A single due date" },
];

export const dueDayOptions: Array<{ value: DueDayRule; label: string; hint: string }> = [
  { value: "first_of_month", label: "The 1st", hint: "" },
  { value: "last_of_month", label: "The last day", hint: "28th, 29th, 30th, or 31st as the month runs" },
  { value: "specific_day", label: "A particular day", hint: "Choose below" },
  {
    value: "first_business_day",
    label: "The first working day",
    hint: "Skips weekends and holidays",
  },
];

export const shortMonthOptions: Array<{
  value: ShortMonthFallback;
  label: string;
  hint: string;
}> = [
  {
    value: "last_day_of_month",
    label: "Use the last day of that month",
    hint: "The 31st becomes the 28th in February",
  },
  {
    value: "skip_month",
    label: "Skip that month",
    hint: "No rent falls due at all. Rarely what a lease means",
  },
  {
    value: "manual_exception",
    label: "Ask me",
    hint: "Nothing is generated and the month is flagged for you to decide",
  },
];

export const businessDayOptions: Array<{
  value: BusinessDayShift;
  label: string;
  hint: string;
}> = [
  { value: "none", label: "Leave it", hint: "Rent is due on the date, weekend or not" },
  {
    value: "previous_business_day",
    label: "Move earlier",
    hint: "A Sunday due date becomes the Friday before",
  },
  {
    value: "next_business_day",
    label: "Move later",
    hint: "A Sunday due date becomes the Monday after",
  },
];

/**
 * Why a date moved.
 *
 * The adjustment reason is the most useful thing in a preview: it is the
 * difference between "the schedule is wrong" and "the schedule is doing what
 * you asked and the calendar is awkward".
 */
export function adjustmentLabel(reason: string | null): string | null {
  if (!reason) {
    return null;
  }

  switch (reason) {
    case "ShortMonth":
      return "That month is too short for this day";
    case "BusinessDayShift":
      return "Moved off a weekend or holiday";
    case "ManualOverride":
      return "Changed by hand";
    case "Skipped":
      return "Skipped";
    default:
      return reason;
  }
}

/** "2026-06-01" as "1 Jun 2026", without touching a time zone. */
export function formatDate(iso: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);

  if (!match) {
    return iso;
  }

  const months = [
    "Jan", "Feb", "Mar", "Apr", "May", "Jun",
    "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
  ] as const;

  // Parsed from its parts rather than through `new Date()`, which treats a
  // date-only string as midnight UTC and renders the day before for any
  // viewer west of Greenwich.
  return `${Number(match[3])} ${months[Number(match[2]) - 1] ?? match[2]} ${match[1]}`;
}

export function formatMonth(iso: string): string {
  const match = /^(\d{4})-(\d{2})/.exec(iso);

  if (!match) {
    return iso;
  }

  const months = [
    "January", "February", "March", "April", "May", "June",
    "July", "August", "September", "October", "November", "December",
  ] as const;

  return `${months[Number(match[2]) - 1] ?? match[2]} ${match[1]}`;
}

export function formatMoney(amount: number, currency: string): string {
  return `${amount.toLocaleString(undefined, {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })} ${currency}`;
}

/** Today, as an ISO date, in the viewer's own zone. */
export function todayIso(): string {
  const now = new Date();

  return [
    now.getFullYear(),
    String(now.getMonth() + 1).padStart(2, "0"),
    String(now.getDate()).padStart(2, "0"),
  ].join("-");
}
