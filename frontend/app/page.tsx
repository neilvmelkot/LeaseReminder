import Link from "next/link";
import { redirect } from "next/navigation";
import { AlertTriangle, CalendarClock, Plus } from "lucide-react";

import { Shell } from "@/components/shell";
import { CreateWorkspace } from "@/components/create-workspace";
import {
  EmptyState,
  ErrorState,
  Metric,
  PageHeader,
  Panel,
  Status,
  TableFrame,
  Td,
  Th,
} from "@/components/ui/primitives";
import { api, ApiError, type Lease } from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";
import {
  automationStatusLabel,
  daysUntil,
  formatDateIso,
  formatMoney,
  pluralize,
} from "@/lib/format";

/**
 * Rendered per request, never prerendered.
 *
 * Two reasons, and either alone would be sufficient. The page depends on the
 * caller session, so there is no correct build-time output to produce. And the
 * content is one landlord lease and tenant data, which must never be baked
 * into a static asset or held in a shared cache.
 */
export const dynamic = "force-dynamic";

/**
 * Dashboard.
 *
 * Section 3.A lists what belongs here and section 5 says how it should look.
 * Two constraints shaped the result more than anything else:
 *
 *   * "Every dashboard metric should link to the relevant filtered page. Avoid
 *     decorative metrics that do not help users take action." So the summary
 *     row is six links, not six cards, and `Metric` requires an `href`.
 *
 *   * "Avoid turning every metric into a large colourful card." So the numbers
 *     are quiet by default, and colour appears only where something actually
 *     needs attention.
 *
 * Phase 1 has no reminder schedule and no email history yet, so the sections
 * that would show them say so plainly rather than displaying an empty chart.
 * An empty widget with no explanation is how a user concludes the application
 * is broken.
 */
export default async function DashboardPage() {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  let data: Awaited<ReturnType<typeof loadDashboard>>;

  try {
    data = await loadDashboard();
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    return (
      <Shell userEmail={user.email ?? null} organizationName={null}>
        <PageHeader title="Dashboard" />
        <Panel>
          <ErrorState
            description={
              error instanceof ApiError
                ? error.message
                : "The API could not be reached. Check that it is running and that " +
                  "NEXT_PUBLIC_API_BASE_URL points at it."
            }
          />
        </Panel>
      </Shell>
    );
  }

  const { organization, leases, propertyCount, tenantCount } = data;

  if (!organization) {
    return (
      <Shell userEmail={user.email ?? null} organizationName={null}>
        <PageHeader title="Welcome" />
        <Panel padded>
          {/* The form itself, not a link to one. This used to point at
              /settings, which has nothing that creates a workspace — so the
              only route into the application dead-ended here. */}
          <div className="py-2">
            <div className="mb-5 text-center">
              <h2 className="text-[15px] font-semibold">Create your workspace</h2>
              <p className="mx-auto mt-1 max-w-sm text-[13px] leading-relaxed text-ink-secondary">
                A workspace holds your properties, leases, tenants, and reminder
                rules. You can add other people to it later.
              </p>
            </div>

            <CreateWorkspace />
          </div>
        </Panel>
      </Shell>
    );
  }

  const activeLeases = leases.filter(
    (lease) => lease.approvalStatus === "approved" && lease.lifecycleStatus === "active",
  );

  const pendingReview = leases.filter((lease) => lease.approvalStatus === "pendingReview");

  // "Needs attention" is deliberately not the same as "pending review". An
  // approved, active lease whose reminders are paused or erroring sends
  // nothing, and nobody will notice unless it is surfaced here.
  const needsAttention = leases.filter(
    (lease) =>
      lease.approvalStatus === "approved" &&
      (lease.lifecycleStatus === "active" || lease.lifecycleStatus === "upcoming") &&
      !lease.canSendReminders,
  );

  const expiringSoon = leases
    .filter((lease) => {
      const days = daysUntil(lease.endDate);

      return (
        days !== null &&
        days >= 0 &&
        days <= 90 &&
        (lease.lifecycleStatus === "active" || lease.lifecycleStatus === "upcoming")
      );
    })
    .sort((left, right) => (daysUntil(left.endDate) ?? 0) - (daysUntil(right.endDate) ?? 0));

  // Monthly scheduled rent counts only leases that are actually live. Including
  // pending ones would overstate expected income, which is the sort of number
  // somebody makes a decision on.
  const monthlyRent = activeLeases.reduce(
    (total, lease) => total + (lease.rentAmount ?? 0),
    0,
  );

  const currency = activeLeases[0]?.currency ?? "USD";

  return (
    <Shell userEmail={user.email ?? null} organizationName={organization.name}>
      <PageHeader
        title="Dashboard"
        description={new Date().toLocaleDateString("en-US", {
          weekday: "long",
          day: "numeric",
          month: "long",
          year: "numeric",
        })}
        primaryAction={
          // A link to the leases page rather than the dialog itself. The
          // dialog needs the property, unit and tenant lists to open onto,
          // and fetching three more collections on the dashboard to power a
          // button most visits do not press is the wrong trade. The leases
          // page already has them and opens the same chooser.
          <Link
            href="/leases?add=1"
            className="inline-flex h-9 items-center gap-2 rounded-control bg-accent px-4
                       text-sm font-medium text-white transition-colors hover:bg-accent-hover"
          >
            <Plus className="size-4" aria-hidden="true" />
            Add lease
          </Link>
        }
        secondaryAction={
          <Link
            href="/properties"
            className="inline-flex h-9 items-center gap-2 rounded-control border
                       border-line-strong bg-surface px-4 text-sm font-medium
                       hover:bg-page"
          >
            <Plus className="size-4" aria-hidden="true" />
            Add property
          </Link>
        }
      />

      {/* ------------------------------------------------------------------ */}
      {/* Summary. Every figure is a link to the page that lets you act on it. */}
      {/* ------------------------------------------------------------------ */}
      <div className="mb-6 grid grid-cols-2 gap-3 lg:grid-cols-3 xl:grid-cols-6">
        <Metric
          label="Active leases"
          value={activeLeases.length}
          href="/leases?lifecycle=active"
        />
        <Metric
          label="Monthly rent"
          value={formatMoney(monthlyRent, currency)}
          hint="Active leases only"
          href="/leases?lifecycle=active"
        />
        <Metric
          label="Properties"
          value={propertyCount}
          href="/properties"
        />
        <Metric label="Tenants" value={tenantCount} href="/tenants" />
        <Metric
          label="Pending review"
          value={pendingReview.length}
          hint={pendingReview.length > 0 ? "Not sending reminders" : "Nothing waiting"}
          tone={pendingReview.length > 0 ? "warning" : "neutral"}
          href="/leases?approval=pendingReview"
        />
        <Metric
          label="Needs attention"
          value={needsAttention.length}
          hint={needsAttention.length > 0 ? "Reminders not going out" : "All clear"}
          tone={needsAttention.length > 0 ? "danger" : "neutral"}
          href="/leases?attention=1"
        />
      </div>

      <div className="grid gap-5 xl:grid-cols-3">
        {/* ---------------------------------------------------------------- */}
        {/* Leases needing attention. First, because it is the only section   */}
        {/* that means something is currently wrong.                          */}
        {/* ---------------------------------------------------------------- */}
        <div className="space-y-5 xl:col-span-2">
          <Panel
            title="Needs attention"
            description="Approved leases whose reminders are not currently being sent."
          >
            {needsAttention.length === 0 ? (
              <EmptyState
                title="Nothing needs attention"
                description="Every approved lease either has its reminders running or is
                             outside its term."
              />
            ) : (
              <TableFrame>
                <thead>
                  <tr>
                    <Th>Lease</Th>
                    <Th>Property</Th>
                    <Th>Reminders</Th>
                    <Th>Why</Th>
                  </tr>
                </thead>
                <tbody>
                  {needsAttention.map((lease) => {
                    const automation = automationStatusLabel(lease.automationStatus);

                    return (
                      <tr key={lease.id} className="hover:bg-surface-subtle">
                        <Td className="font-medium">
                          <Link href={`/leases/${lease.id}`} className="hover:text-accent">
                            {lease.reference ?? "Untitled lease"}
                          </Link>
                        </Td>
                        <Td className="text-ink-secondary">
                          {lease.propertyName}
                          {lease.unitNumber ? ` · ${lease.unitNumber}` : ""}
                        </Td>
                        <Td>
                          <Status tone={automation.tone}>{automation.label}</Status>
                        </Td>
                        <Td className="max-w-[20rem] text-[13px] text-ink-secondary">
                          {lease.automationPausedReason ??
                            "Reminders have not been switched on."}
                        </Td>
                      </tr>
                    );
                  })}
                </tbody>
              </TableFrame>
            )}
          </Panel>

          {/* -------------------------------------------------------------- */}
          {/* Expiring leases, with the renewal notice point called out.      */}
          {/* -------------------------------------------------------------- */}
          <Panel
            title="Leases expiring within 90 days"
            description="A renewal notice deadline is usually earlier than the end date."
          >
            {expiringSoon.length === 0 ? (
              <EmptyState
                title="No leases expiring soon"
                description="Nothing in the next 90 days."
              />
            ) : (
              <TableFrame>
                <thead>
                  <tr>
                    <Th>Lease</Th>
                    <Th>Property</Th>
                    <Th>Ends</Th>
                    <Th>In</Th>
                    <Th>Notice due by</Th>
                  </tr>
                </thead>
                <tbody>
                  {expiringSoon.map((lease) => {
                    const days = daysUntil(lease.endDate) ?? 0;

                    // The renewal notice deadline is NOT the end date
                    // (section 6.F). Shown separately, and only when the lease
                    // actually states a notice period.
                    const noticeDeadline =
                      lease.endDate && lease.renewalNoticeDays
                        ? shiftIsoDate(lease.endDate, -lease.renewalNoticeDays)
                        : null;

                    return (
                      <tr key={lease.id} className="hover:bg-surface-subtle">
                        <Td className="font-medium">
                          <Link href={`/leases/${lease.id}`} className="hover:text-accent">
                            {lease.reference ?? "Untitled lease"}
                          </Link>
                        </Td>
                        <Td className="text-ink-secondary">{lease.propertyName}</Td>
                        <Td className="tabular">{formatDateIso(lease.endDate)}</Td>
                        <Td>
                          <Status tone={days <= 30 ? "warning" : "neutral"}>
                            {pluralize(days, "day")}
                          </Status>
                        </Td>
                        <Td className="tabular text-ink-secondary">
                          {noticeDeadline ? (
                            formatDateIso(noticeDeadline)
                          ) : (
                            <span className="text-ink-muted">Not stated</span>
                          )}
                        </Td>
                      </tr>
                    );
                  })}
                </tbody>
              </TableFrame>
            )}
          </Panel>
        </div>

        {/* ---------------------------------------------------------------- */}
        {/* Right column: what is waiting on a person, and what is not built  */}
        {/* yet. Saying so beats an empty widget.                             */}
        {/* ---------------------------------------------------------------- */}
        <div className="space-y-5">
          <Panel
            title="Waiting for review"
            description="A lease sends no reminders until it is approved."
          >
            {pendingReview.length === 0 ? (
              <EmptyState
                title="Nothing to review"
                description="Every lease has been reviewed."
              />
            ) : (
              <ul className="divide-y divide-line">
                {pendingReview.slice(0, 6).map((lease) => (
                  <li key={lease.id} className="px-5 py-3">
                    <Link
                      href={`/leases/${lease.id}`}
                      className="block text-sm font-medium hover:text-accent"
                    >
                      {lease.reference ?? "Untitled lease"}
                    </Link>
                    <p className="mt-0.5 text-[12px] text-ink-secondary">
                      {lease.propertyName}
                      {lease.unitNumber ? ` · ${lease.unitNumber}` : ""}
                    </p>
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          <Panel title="Upcoming reminders">
            <div className="flex items-start gap-3 px-5 py-5">
              <CalendarClock
                className="mt-0.5 size-5 shrink-0 text-ink-muted"
                aria-hidden="true"
              />
              <div>
                <p className="text-sm font-medium">Nothing scheduled yet</p>
                <p className="mt-1 text-[13px] text-ink-secondary">
                  A reminder needs four things at once: an approved lease, a
                  rent schedule, a reminder rule, and a connected mailbox.
                  Rent reminders lists which of them is missing.
                </p>
              </div>
            </div>
          </Panel>

          <Panel title="Recent email activity">
            <div className="flex items-start gap-3 px-5 py-5">
              <AlertTriangle
                className="mt-0.5 size-5 shrink-0 text-warning"
                aria-hidden="true"
              />
              <div>
                <p className="text-sm font-medium">No Gmail account connected</p>
                <p className="mt-1 text-[13px] text-ink-secondary">
                  Nothing can be sent until an account is connected. While it is
                  disconnected the dispatcher will not pick up any reminder, and it
                  will never fall back to another sender.
                </p>
              </div>
            </div>
          </Panel>
        </div>
      </div>
    </Shell>
  );
}

/**
 * Loads everything the dashboard needs.
 *
 * The first release has one workspace (section 13.A), so the first membership
 * is the one to show. The schema and these calls already carry the
 * organization id, so adding a workspace switcher later changes this function
 * and nothing else.
 */
async function loadDashboard() {
  const currentUser = await api.currentUser();
  const organization = currentUser.organizations[0] ?? null;

  if (!organization) {
    return { organization: null, leases: [] as Lease[], propertyCount: 0, tenantCount: 0 };
  }

  // Requested together rather than in sequence: three dependent round trips to
  // the API would be three times the latency for no benefit.
  const [leases, properties, tenants] = await Promise.all([
    api.leases(organization.id),
    api.properties(organization.id),
    api.tenants(organization.id),
  ]);

  return {
    organization,
    leases,
    propertyCount: properties.length,
    tenantCount: tenants.length,
  };
}

/**
 * Shifts an ISO date-only string by a number of days, staying in date space.
 *
 * Uses Date.UTC rather than the local constructor, so the result does not
 * depend on the server's time zone. A renewal notice deadline that moved by a
 * day depending on where the container ran would be worse than not showing it.
 */
function shiftIsoDate(isoDate: string, days: number): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(isoDate);

  if (!match) {
    return isoDate;
  }

  const shifted = new Date(
    Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]) + days),
  );

  return shifted.toISOString().slice(0, 10);
}
