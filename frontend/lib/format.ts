import type { Lease } from "@/lib/api";
import type { StatusTone } from "@/components/ui/primitives";

/**
 * Display helpers.
 *
 * <h3>Why dates are formatted without a time zone conversion</h3>
 *
 * A lease start date, a rent due date, and a lease end date are calendar dates
 * in the property's own time zone, not instants. They arrive as "2026-04-01".
 * Passing that through `new Date()` would interpret it as midnight UTC and
 * then render it in the viewer's zone, which displays 31 March to anybody west
 * of Greenwich. For a rent due date that is not a cosmetic bug: it is the
 * application telling a tenant the wrong day.
 *
 * So date-only values are parsed into their parts and formatted directly,
 * never routed through a time zone. Instants (an approval timestamp, a send
 * time) are different and are converted deliberately.
 */

const monthNames = [
  "January",
  "February",
  "March",
  "April",
  "May",
  "June",
  "July",
  "August",
  "September",
  "October",
  "November",
  "December",
] as const;

/** Formats an ISO date-only string as "1 April 2026", with no zone conversion. */
export function formatDate(isoDate: string | null | undefined): string {
  if (!isoDate) {
    return "Not set";
  }

  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(isoDate);

  if (!match) {
    return isoDate;
  }

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);

  const monthName = monthNames[month - 1] ?? String(month);

  return `${day} ${monthName} ${year}`;
}

/** Formats a date-only string as "2026-04-01" for compact, sortable columns. */
export function formatDateIso(isoDate: string | null | undefined): string {
  if (!isoDate) {
    return "—";
  }

  return /^(\d{4}-\d{2}-\d{2})/.exec(isoDate)?.[1] ?? isoDate;
}

/**
 * Formats a currency amount.
 *
 * Intl handles the symbol and the grouping, and `currencyDisplay: "narrowSymbol"`
 * keeps it as "$1,200.00" rather than "US$1,200.00" inside an application that
 * already knows which currency it is showing.
 */
export function formatMoney(
  amount: number | null | undefined,
  currency: string,
): string {
  if (amount === null || amount === undefined) {
    return "—";
  }

  try {
    return new Intl.NumberFormat("en-US", {
      style: "currency",
      currency,
      currencyDisplay: "narrowSymbol",
    }).format(amount);
  } catch {
    // An unrecognised currency code should not blank the whole row.
    return `${currency} ${amount.toFixed(2)}`;
  }
}

/** Days from today until a date-only value. Negative when past. */
export function daysUntil(isoDate: string | null | undefined): number | null {
  if (!isoDate) {
    return null;
  }

  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(isoDate);

  if (!match) {
    return null;
  }

  // Both sides are compared as UTC midnights, so the subtraction is a whole
  // number of days and no partial-day rounding creeps in.
  const target = Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  const now = new Date();
  const today = Date.UTC(now.getFullYear(), now.getMonth(), now.getDate());

  return Math.round((target - today) / 86_400_000);
}

/* -------------------------------------------------------------------------- */
/* Status presentation                                                         */
/*                                                                            */
/* Three separate functions for three separate state machines (section 13.E).  */
/* There is deliberately no combined "lease status", because combining them    */
/* would hide the case that matters most: approved, active, and paused.        */
/* -------------------------------------------------------------------------- */

export function approvalStatusLabel(
  status: Lease["approvalStatus"],
): { label: string; tone: StatusTone } {
  switch (status) {
    case "approved":
      return { label: "Approved", tone: "success" };
    case "pendingReview":
      return { label: "Pending review", tone: "warning" };
    case "rejected":
      return { label: "Rejected", tone: "danger" };
    default:
      return { label: status, tone: "neutral" };
  }
}

export function lifecycleStatusLabel(
  status: Lease["lifecycleStatus"],
): { label: string; tone: StatusTone } {
  switch (status) {
    case "active":
      return { label: "Active", tone: "success" };
    case "upcoming":
      return { label: "Upcoming", tone: "info" };
    case "draft":
      return { label: "Draft", tone: "neutral" };
    case "expired":
      return { label: "Expired", tone: "neutral" };
    case "terminated":
      return { label: "Terminated", tone: "neutral" };
    case "archived":
      return { label: "Archived", tone: "neutral" };
    default:
      return { label: status, tone: "neutral" };
  }
}

export function automationStatusLabel(
  status: Lease["automationStatus"],
): { label: string; tone: StatusTone } {
  switch (status) {
    case "active":
      return { label: "Reminders on", tone: "success" };
    case "paused":
      return { label: "Reminders paused", tone: "warning" };
    case "pendingActivation":
      return { label: "Reminders not started", tone: "warning" };
    case "notConfigured":
      return { label: "No reminders set up", tone: "neutral" };
    case "error":
      return { label: "Reminder error", tone: "danger" };
    default:
      return { label: status, tone: "neutral" };
  }
}

/**
 * One sentence explaining whether reminders will actually go out.
 *
 * Worth its own function because the answer depends on all three statuses at
 * once, and the combination is exactly what a user misreads. "Approved" next
 * to "Active" looks like everything is fine even when automation is paused and
 * nothing will be sent.
 */
export function reminderSummary(lease: Lease): string {
  if (lease.approvalStatus !== "approved") {
    return "No reminders will be sent: this lease has not been approved yet.";
  }

  if (lease.lifecycleStatus === "expired" || lease.lifecycleStatus === "terminated") {
    return `No reminders will be sent: this lease is ${lease.lifecycleStatus}.`;
  }

  if (lease.automationStatus === "paused") {
    return lease.automationPausedReason
      ? `Reminders are paused: ${lease.automationPausedReason}`
      : "Reminders are paused for this lease.";
  }

  if (lease.automationStatus === "pendingActivation") {
    return "Reminders are configured but have not been switched on yet.";
  }

  if (lease.automationStatus === "notConfigured") {
    return "No reminder rules have been set up for this lease.";
  }

  if (lease.canSendReminders) {
    return "Reminders are active for this lease.";
  }

  return "Reminders are not currently being sent for this lease.";
}

/** Formats a count with its noun, so "1 lease" rather than "1 leases". */
export function pluralize(count: number, singular: string, plural?: string): string {
  return `${count} ${count === 1 ? singular : (plural ?? `${singular}s`)}`;
}

/** "multiFamily" becomes "Multi family", without a lookup table to maintain. */
export function formatPropertyType(propertyType: string): string {
  const spaced = propertyType.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();

  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

/**
 * A one-line address, or null when nothing has been filled in.
 *
 * Null rather than an empty string: the caller then decides how to say "not
 * set", instead of rendering a blank cell that looks like a rendering bug.
 */
export function formatAddress(parts: {
  addressLine1: string | null;
  locality: string | null;
  region: string | null;
  postalCode: string | null;
}): string | null {
  const present = [
    parts.addressLine1,
    parts.locality,
    parts.region,
    parts.postalCode,
  ].filter((part): part is string => Boolean(part));

  return present.length > 0 ? present.join(", ") : null;
}
