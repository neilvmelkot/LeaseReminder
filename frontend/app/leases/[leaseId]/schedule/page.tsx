import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { ChevronLeft } from "lucide-react";

import { Shell } from "@/components/shell";
import { ScheduleEditor } from "@/components/rent/schedule-editor";
import { ErrorState, PageHeader, Panel, Status } from "@/components/ui/primitives";
import { api, ApiError, apiFetch, type Lease } from "@/lib/api";
import type { SavedOccurrence, SavedSchedule } from "@/lib/rent";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * The rent schedule for one lease.
 *
 * <p>
 * Reachable only for an approved lease. A schedule is what reminders are built
 * from, so offering to configure one for a lease nobody has approved would
 * invite exactly the sequence section 13.C forbids. The database refuses to
 * generate for an unapproved lease regardless; this page says so rather than
 * letting someone fill in a form that will quietly do nothing.
 * </p>
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Rent schedule" };

export default async function RentSchedulePage({
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
  let canEdit = false;
  let lease: Lease | null = null;
  let schedule: SavedSchedule | null = null;
  let occurrences: SavedOccurrence[] = [];
  let propertyTimeZone = "UTC";
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;

      const role = organization.callerRole;
      canEdit = role === "owner" || role === "admin" || role === "propertyManager";

      const leases = await api.leases(organization.id);
      lease = leases.find((candidate) => candidate.id === leaseId) ?? null;

      if (!lease) {
        notFound();
      }

      const properties = await api.properties(organization.id);
      propertyTimeZone =
        properties.find((property) => property.id === lease!.propertyId)?.timeZone
        ?? organization.timeZone;

      const result = await apiFetch<{
        schedule: SavedSchedule | null;
        occurrences: SavedOccurrence[];
      }>(`/api/v1/organizations/${organization.id}/leases/${leaseId}/rent-schedule`);

      schedule = result.schedule;
      occurrences = result.occurrences;
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  const isApproved = lease?.approvalStatus === "approved";

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <div className="mb-4">
        <Link
          href="/leases"
          className="inline-flex items-center gap-1 text-[12.5px] text-ink-secondary
                     hover:text-ink"
        >
          <ChevronLeft className="size-3.5" aria-hidden="true" />
          All leases
        </Link>
      </div>

      <PageHeader
        title="Rent schedule"
        // Spread rather than passed as undefined: under
        // exactOptionalPropertyTypes an explicit undefined is not the same as
        // an omitted property, and PageHeader declares this one optional.
        {...(lease
          ? {
              description: `${lease.propertyName}${
                lease.unitNumber ? ` · Unit ${lease.unitNumber}` : ""
              }`,
            }
          : {})}
        meta={
          occurrences.length > 0 ? (
            <Status tone="success">
              {occurrences.length} upcoming payment
              {occurrences.length === 1 ? "" : "s"} generated
            </Status>
          ) : schedule ? (
            <Status tone="warning">Dates are being generated</Status>
          ) : null
        }
      />

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !organizationId || !lease ? (
        <Panel>
          <ErrorState
            title="Lease not found"
            description="It may have been removed, or belong to another workspace."
          />
        </Panel>
      ) : !isApproved ? (
        <Panel>
          <ErrorState
            title="This lease has not been approved yet"
            description="A rent schedule decides when a tenant is told to pay, so it can
                         only be set up once somebody has approved the lease terms.
                         Review the lease first."
          />
        </Panel>
      ) : (
        <ScheduleEditor
          organizationId={organizationId}
          leaseId={leaseId}
          existing={schedule}
          propertyTimeZone={schedule?.timeZone ?? propertyTimeZone}
          leaseStartDate={lease.startDate}
          leaseEndDate={lease.endDate}
          leaseRentAmount={lease.rentAmount}
          leaseCurrency={lease.currency}
          gracePeriodDays={lease.gracePeriodDays}
          lateFeeAfterDays={lease.lateFeeAfterDays}
          canEdit={canEdit}
        />
      )}
    </Shell>
  );
}
