import Link from "next/link";
import { redirect } from "next/navigation";
import { AlertTriangle, CheckCircle2, Clock, MailWarning } from "lucide-react";

import { Shell } from "@/components/shell";
import {
  EmptyState,
  ErrorState,
  PageHeader,
  Panel,
  Status,
  TableFrame,
  Td,
  Th,
} from "@/components/ui/primitives";
import { api, ApiError, type ReminderBoard } from "@/lib/api";
import { formatDate, formatMoney } from "@/lib/format";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * Rent reminders, across the whole workspace.
 *
 * <h3>What this page is for</h3>
 *
 * The per-lease schedule screen answers "when is rent due on this lease". This
 * answers the question somebody asks when they want to trust the system rather
 * than configure it: is anything actually going to be sent, and did the last
 * batch go out.
 *
 * <h3>Why it leads with what is missing</h3>
 *
 * Sending a rent reminder needs four things to be true at once — an approved
 * lease, a rent schedule, a reminder rule, and a connected mailbox — and any
 * one of them missing produces the same symptom: silence. A page that showed
 * only an empty upcoming list would be indistinguishable from a working
 * system with nothing due this week, which is the single most misleading
 * thing it could do. So the gaps are named first and the list comes second.
 *
 * <h3>Read-only, deliberately</h3>
 *
 * Nothing here schedules, cancels or re-sends. Generation belongs to the
 * worker, and a reminder that could be conjured from a screen would be a
 * second path around the queue — whose idempotency key is the only thing
 * stopping a tenant being emailed the same reminder twice.
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Rent reminders" };

export default async function RemindersPage() {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  let organizationName: string | null = null;
  let board: ReminderBoard | null = null;
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationName = organization.name;
      board = await api.reminders(organization.id);
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  const summary = board?.summary ?? null;
  const blockers = summary ? describeBlockers(summary) : [];

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <PageHeader
        title="Rent reminders"
        description="Every reminder this workspace is going to send, and what happened to the ones it already did."
        meta={
          summary && summary.failedCount > 0 ? (
            <Status tone="danger">{summary.failedCount} failed</Status>
          ) : summary && summary.scheduledCount > 0 ? (
            <Status tone="success">{summary.scheduledCount} scheduled</Status>
          ) : null
        }
      />

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !board || !summary ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace before setting up reminders."
          />
        </Panel>
      ) : (
        <div className="space-y-5">
          {/* What is stopping anything being sent. First, because every one of
              these looks like "nothing is due" from the list below. */}
          {blockers.length > 0 ? (
            <Panel
              title="Why nothing is being sent"
              description="A reminder needs all of these. Any one missing produces silence, not an error."
              padded
            >
              <ul className="space-y-2.5">
                {blockers.map((blocker) => (
                  <li key={blocker.title} className="flex items-start gap-2.5">
                    <AlertTriangle
                      className="mt-0.5 size-4 shrink-0 text-warning"
                      aria-hidden="true"
                    />
                    <div className="min-w-0">
                      <p className="text-[13px] font-medium">{blocker.title}</p>
                      <p className="mt-0.5 text-[12.5px] leading-relaxed text-ink-secondary">
                        {blocker.detail}
                      </p>
                      {blocker.href ? (
                        <Link
                          href={blocker.href}
                          className="mt-1 inline-block text-[12.5px] font-medium text-accent
                                     transition-colors hover:underline"
                        >
                          {blocker.action}
                        </Link>
                      ) : null}
                    </div>
                  </li>
                ))}
              </ul>
            </Panel>
          ) : (
            <Panel padded>
              <p className="flex items-start gap-2 text-[13px] leading-relaxed">
                <CheckCircle2
                  className="mt-0.5 size-4 shrink-0 text-success"
                  aria-hidden="true"
                />
                <span>
                  A mailbox is connected and {summary.scheduleCount} rent{" "}
                  {summary.scheduleCount === 1 ? "schedule" : "schedules"}{" "}
                  {summary.scheduleCount === 1 ? "is" : "are"} set up.{" "}
                  {summary.scheduledCount > 0
                    ? `${summary.scheduledCount} reminder${
                        summary.scheduledCount === 1 ? "" : "s"
                      } ${summary.scheduledCount === 1 ? "is" : "are"} queued.`
                    : "Nothing is queued at the moment."}
                </span>
              </p>
            </Panel>
          )}

          <Panel
            title="Coming up"
            description="The next 90 days, in the order the dispatcher will send them."
          >
            {board.upcoming.length === 0 ? (
              <EmptyState
                compact
                title="Nothing is queued"
                description="Reminders appear here once a lease is approved and its rent schedule has been generated."
              />
            ) : (
              <TableFrame minWidth="56rem">
                <thead>
                  <tr>
                    <Th>When</Th>
                    <Th>Property</Th>
                    <Th>About</Th>
                    <Th className="text-right">Amount</Th>
                    <Th className="text-right">Recipients</Th>
                  </tr>
                </thead>
                <tbody>
                  {board.upcoming.map((reminder) => (
                    <tr key={reminder.id} className="hover:bg-surface-subtle">
                      <Td>
                        <span className="tabular text-[13px] font-medium">
                          {new Date(reminder.scheduledAtUtc).toLocaleString()}
                        </span>
                        <p className="mt-0.5 text-[11.5px] text-ink-muted">
                          {reminder.localTimeZone}
                          {/* Worth saying: the local send time either did not
                              exist that day or existed twice, so the instant
                              was chosen rather than read off a clock. */}
                          {reminder.dstAdjusted
                            ? " · moved for a clock change"
                            : ""}
                        </p>
                      </Td>

                      <Td className="text-[13px]">
                        <Link
                          href={`/leases/${reminder.leaseId}`}
                          className="font-medium transition-colors hover:text-accent"
                        >
                          {reminder.propertyName}
                        </Link>
                        {reminder.unitNumber ? (
                          <span className="text-ink-secondary">
                            {" "}
                            · {reminder.unitNumber}
                          </span>
                        ) : null}
                      </Td>

                      <Td className="text-[13px] text-ink-secondary">
                        {eventLabel(reminder.eventType)}
                        <p className="mt-0.5 text-[11.5px] text-ink-muted">
                          {reminder.dueDate
                            ? `Rent due ${formatDate(reminder.dueDate)}`
                            : formatDate(reminder.eventLocalDate)}
                        </p>
                      </Td>

                      <Td className="tabular text-right text-[13px]">
                        {reminder.amount === null
                          ? "—"
                          : formatMoney(reminder.amount, reminder.currency ?? "USD")}
                      </Td>

                      <Td className="tabular text-right text-[13px]">
                        {reminder.recipientCount === 0 ? (
                          <Status tone="warning">Nobody</Status>
                        ) : (
                          reminder.recipientCount
                        )}
                      </Td>
                    </tr>
                  ))}
                </tbody>
              </TableFrame>
            )}
          </Panel>

          <Panel
            title="Already handled"
            description="Accepted means Google took the message. Delivery to an inbox is not something any sender can guarantee."
          >
            {board.recent.length === 0 ? (
              <EmptyState
                compact
                title="Nothing has been sent yet"
                description="Once a reminder goes out, its result appears here either way."
              />
            ) : (
              <TableFrame minWidth="52rem">
                <thead>
                  <tr>
                    <Th>When</Th>
                    <Th>Property</Th>
                    <Th>About</Th>
                    <Th>Result</Th>
                  </tr>
                </thead>
                <tbody>
                  {board.recent.map((reminder) => (
                    <tr key={reminder.id} className="hover:bg-surface-subtle">
                      <Td className="tabular text-[13px]">
                        {reminder.sentAt
                          ? new Date(reminder.sentAt).toLocaleString()
                          : formatDate(reminder.eventLocalDate)}
                      </Td>

                      <Td className="text-[13px]">
                        <Link
                          href={`/leases/${reminder.leaseId}`}
                          className="font-medium transition-colors hover:text-accent"
                        >
                          {reminder.propertyName}
                        </Link>
                        {reminder.unitNumber ? (
                          <span className="text-ink-secondary">
                            {" "}
                            · {reminder.unitNumber}
                          </span>
                        ) : null}
                      </Td>

                      <Td className="text-[13px] text-ink-secondary">
                        {eventLabel(reminder.eventType)}
                      </Td>

                      <Td>
                        <Status tone={resultTone(reminder.status)}>
                          {resultLabel(reminder.status)}
                        </Status>

                        {/* The operator-safe reason, which is the only kind
                            stored. Shown because a failure with no reason is
                            a dead end. */}
                        {reminder.lastErrorMessage ? (
                          <p className="mt-0.5 flex items-start gap-1 text-[11.5px] text-danger-text">
                            <MailWarning
                              className="mt-0.5 size-3 shrink-0"
                              aria-hidden="true"
                            />
                            {reminder.lastErrorMessage}
                          </p>
                        ) : null}

                        {reminder.attemptCount > 1 ? (
                          <p className="mt-0.5 text-[11.5px] text-ink-muted">
                            {reminder.attemptCount} attempts
                          </p>
                        ) : null}
                      </Td>
                    </tr>
                  ))}
                </tbody>
              </TableFrame>
            )}
          </Panel>

          <p className="flex items-start gap-1.5 text-[12px] leading-relaxed text-ink-secondary">
            <Clock className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            Reminders are generated by the background worker and sent from the
            one connected mailbox. Nothing on this page sends anything — a
            reminder that could be triggered from a screen would be a second
            path around the queue that stops a tenant being emailed twice.
          </p>
        </div>
      )}
    </Shell>
  );
}

/**
 * What is missing, in the order it has to be fixed.
 *
 * Ordered deliberately: there is no point telling somebody to connect a
 * mailbox when they have no approved lease to send about, and no point
 * mentioning schedules before there is a lease to attach one to.
 */
function describeBlockers(summary: {
  liveLeaseCount: number;
  scheduleCount: number;
  scheduledCount: number;
  mailboxConnected: boolean;
}): Array<{ title: string; detail: string; href?: string; action?: string }> {
  const blockers: Array<{
    title: string;
    detail: string;
    href?: string;
    action?: string;
  }> = [];

  if (summary.liveLeaseCount === 0) {
    blockers.push({
      title: "No approved lease is live",
      detail:
        "A lease sends nothing until it has been approved and its term has begun. "
        + "A lease still pending review is not one of these.",
      href: "/leases",
      action: "Go to leases",
    });
  }

  if (summary.liveLeaseCount > 0 && summary.scheduleCount === 0) {
    blockers.push({
      title: "No rent schedule has been set up",
      detail:
        "The schedule is what produces the dates a reminder can be attached to. "
        + "It is set per lease, and read in the property's own time zone.",
      href: "/leases",
      action: "Open a lease to set one up",
    });
  }

  if (!summary.mailboxConnected) {
    blockers.push({
      title: "No mailbox is connected",
      detail:
        "Reminders are generated and queued, but nothing is sent. There is no "
        + "fallback sender, by design.",
      href: "/settings/email",
      action: "Connect a Gmail account",
    });
  }

  if (
    summary.liveLeaseCount > 0
    && summary.scheduleCount > 0
    && summary.mailboxConnected
    && summary.scheduledCount === 0
  ) {
    blockers.push({
      title: "Nothing is queued",
      detail:
        "The leases and the mailbox are in place, so this is either a quiet "
        + "period or no reminder rule is switched on for these events.",
      href: "/settings/notifications",
      action: "Check the reminder rules",
    });
  }

  return blockers;
}

/** "rent_due_soon" becomes "Rent due soon", without a table to maintain. */
function eventLabel(eventType: string): string {
  const spaced = eventType.replace(/_/g, " ").toLowerCase();

  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function resultLabel(status: string): string {
  switch (status) {
    case "sent":
      return "Accepted by Google";
    case "failed":
      return "Failed";
    case "cancelled":
      return "Cancelled";
    case "suppressed":
      return "Not sent";
    default:
      return eventLabel(status);
  }
}

function resultTone(status: string): "success" | "danger" | "neutral" | "warning" {
  switch (status) {
    case "sent":
      return "success";
    case "failed":
      return "danger";
    case "suppressed":
      return "warning";
    default:
      return "neutral";
  }
}
