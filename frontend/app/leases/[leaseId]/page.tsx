import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { CalendarClock, ChevronLeft, Users } from "lucide-react";

import { Shell } from "@/components/shell";
import { LeaseControls } from "@/components/leases/lease-controls";
import {
  EmptyState,
  ErrorState,
  PageHeader,
  Panel,
  Status,
} from "@/components/ui/primitives";
import { PermissionNotice } from "@/components/ui/permission-notice";
import { api, ApiError, permissions, type Lease } from "@/lib/api";
import {
  approvalStatusLabel,
  automationStatusLabel,
  formatDate,
  formatMoney,
  lifecycleStatusLabel,
  reminderSummary,
} from "@/lib/format";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * One lease.
 *
 * <h3>Why the three statuses are shown separately</h3>
 *
 * They are independent, and the combination is what people misread. "Approved"
 * beside "Active" looks like everything is working even when the reminders are
 * paused and nothing will be sent. So all three appear, and a sentence
 * underneath says what the combination actually means for sending — which is
 * the only thing anybody came here to find out.
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Lease" };

export default async function LeasePage({
  params,
}: {
  params: Promise<{ leaseId: string }>;
}) {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  const { leaseId } = await params;

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let access = permissions(null);
  let lease: Lease | null = null;
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;
      access = permissions(organization.callerRole);

      lease = await api.lease(organization.id, leaseId);
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    if (error instanceof ApiError && error.status === 404) {
      notFound();
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  const approval = lease ? approvalStatusLabel(lease.approvalStatus) : null;
  const lifecycle = lease ? lifecycleStatusLabel(lease.lifecycleStatus) : null;
  const automation = lease ? automationStatusLabel(lease.automationStatus) : null;

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <div className="mb-4">
        <Link
          href="/leases"
          className="inline-flex items-center gap-1 text-[12.5px] text-ink-secondary
                     transition-colors hover:text-ink"
        >
          <ChevronLeft className="size-3.5" aria-hidden="true" />
          All leases
        </Link>
      </div>

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !lease || !organizationId ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace before opening a lease."
          />
        </Panel>
      ) : (
        <div className="space-y-5">
          <PageHeader
            title={lease.reference ?? "Untitled lease"}
            description={`${lease.propertyName}${
              lease.unitNumber ? ` · ${lease.unitNumber}` : ""
            }`}
            primaryAction={
              <LeaseControls
                organizationId={organizationId}
                lease={lease}
                canWrite={access.canWrite}
                canAdminister={access.canAdminister}
              />
            }
          />

          {!access.canWrite ? (
            <PermissionNotice reason={access.reason} action="Changing this lease" />
          ) : null}

          {/* The three statuses, and then what they add up to. The sentence is
              the part that matters: the badges alone are what people
              misread. */}
          <Panel padded>
            <div className="flex flex-wrap items-center gap-2">
              {approval ? (
                <Status tone={approval.tone}>{approval.label}</Status>
              ) : null}
              {lifecycle ? (
                <Status tone={lifecycle.tone}>{lifecycle.label}</Status>
              ) : null}
              {automation ? (
                <Status tone={automation.tone}>{automation.label}</Status>
              ) : null}
            </div>

            <p className="mt-2.5 text-[13px] leading-relaxed">
              {reminderSummary(lease)}
            </p>

            {lease.rejectionReason ? (
              <p
                className="mt-2 rounded-control border border-danger/30 bg-danger/10
                           px-3 py-2 text-[12.5px] leading-relaxed text-danger-text"
              >
                Rejected: {lease.rejectionReason}
              </p>
            ) : null}
          </Panel>

          <div className="grid gap-5 lg:grid-cols-2">
            <Panel title="Term and rent" padded>
              <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
                <Detail label="Starts">{formatDate(lease.startDate)}</Detail>
                <Detail label="Ends">{formatDate(lease.endDate)}</Detail>
                <Detail label="Rent">
                  {formatMoney(lease.rentAmount, lease.currency)}
                </Detail>
                <Detail label="First payment">
                  {formatDate(lease.firstPaymentDate)}
                </Detail>
                <Detail label="Deposit">
                  {formatMoney(lease.securityDepositAmount, lease.currency)}
                </Detail>
                <Detail label="Grace period">
                  {lease.gracePeriodDays === null
                    ? "—"
                    : `${lease.gracePeriodDays} days`}
                </Detail>
                <Detail label="Late fee">
                  {lease.lateFeeAmount === null
                    ? "—"
                    : lease.lateFeeIsPercentage
                      ? `${lease.lateFeeAmount}% of rent`
                      : formatMoney(lease.lateFeeAmount, lease.currency)}
                </Detail>
                <Detail label="Charged after">
                  {lease.lateFeeAfterDays === null
                    ? "—"
                    : `${lease.lateFeeAfterDays} days late`}
                </Detail>
              </dl>

              {lease.paymentInstructions ? (
                <div className="mt-4 border-t border-line pt-3">
                  <p className="text-[12px] font-medium tracking-wide text-ink-secondary uppercase">
                    Payment instructions
                  </p>
                  <p className="mt-1 text-[13px] leading-relaxed whitespace-pre-line">
                    {lease.paymentInstructions}
                  </p>
                  <p className="mt-1 text-[11.5px] text-ink-muted">
                    Included in reminders, so a tenant reads this.
                  </p>
                </div>
              ) : null}
            </Panel>

            <div className="space-y-5">
              <Panel
                title="Tenants"
                description="The primary tenant is who a rent reminder is addressed to."
              >
                {lease.tenants.length === 0 ? (
                  <EmptyState
                    compact
                    title="Nobody is named on this lease"
                    description="A reminder has nowhere to go until a tenant is named."
                  />
                ) : (
                  <ul className="divide-y divide-line">
                    {lease.tenants.map((tenant) => (
                      <li
                        key={tenant.tenantId}
                        className="flex items-center justify-between gap-3 px-5 py-2.5"
                      >
                        <div className="min-w-0">
                          <p className="truncate text-[13px] font-medium">
                            {tenant.preferredName ?? tenant.legalName}
                          </p>
                          <p className="text-[11.5px] text-ink-secondary">
                            {tenant.role === "primary" ? "Primary" : "Co-tenant"}
                          </p>
                        </div>

                        {/* A tenant who has opted out receives nothing,
                            whatever a rule says — so it is shown on the
                            lease rather than discovered when nothing
                            arrives. */}
                        <Status
                          tone={tenant.notificationsOptedIn ? "success" : "warning"}
                        >
                          {tenant.notificationsOptedIn ? "Opted in" : "Opted out"}
                        </Status>
                      </li>
                    ))}
                  </ul>
                )}
              </Panel>

              <Panel title="Rent schedule" padded>
                <p className="text-[13px] leading-relaxed text-ink-secondary">
                  The schedule decides which dates reminders are generated for.
                  It is set up per lease, and read in the property&apos;s own
                  time zone.
                </p>

                <div className="mt-3 flex flex-wrap gap-2">
                  <Link
                    href={`/leases/${lease.id}/schedule`}
                    className="inline-flex items-center gap-1.5 rounded-control border
                               border-line-strong bg-surface px-3 py-1.5 text-[13px]
                               font-medium transition-colors hover:bg-surface-subtle"
                  >
                    <CalendarClock className="size-3.5" aria-hidden="true" />
                    {lease.approvalStatus === "approved"
                      ? "Set up the schedule"
                      : "View the schedule"}
                  </Link>

                  <Link
                    href="/reminders"
                    className="inline-flex items-center gap-1.5 rounded-control border
                               border-line-strong bg-surface px-3 py-1.5 text-[13px]
                               font-medium transition-colors hover:bg-surface-subtle"
                  >
                    <Users className="size-3.5" aria-hidden="true" />
                    All reminders
                  </Link>
                </div>
              </Panel>
            </div>
          </div>

          {lease.notes ? (
            <Panel title="Notes" description="Internal. Never sent to a tenant." padded>
              <p className="text-[13px] leading-relaxed whitespace-pre-line">
                {lease.notes}
              </p>
            </Panel>
          ) : null}
        </div>
      )}
    </Shell>
  );
}

function Detail({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <dt className="text-[12px] font-medium tracking-wide text-ink-secondary uppercase">
        {label}
      </dt>
      <dd className="mt-0.5 text-[13.5px]">{children}</dd>
    </div>
  );
}
