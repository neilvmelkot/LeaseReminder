import Link from "next/link";
import { redirect } from "next/navigation";

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
import {
  api,
  ApiError,
  permissions,
  type Lease,
  type Property,
  type Tenant,
  type Unit,
} from "@/lib/api";
import { AddLease } from "@/components/leases/add-lease";
import { PillTabs } from "@/components/ui/pill-tabs";
import { getCurrentUser } from "@/lib/supabase/server";
import {
  approvalStatusLabel,
  automationStatusLabel,
  daysUntil,
  formatDateIso,
  formatMoney,
  lifecycleStatusLabel,
  reminderSummary,
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
 * Lease list.
 *
 * The filter set comes from section 3.D: active, pending review, expiring
 * soon, expired, draft, archived.
 *
 * The important design decision is the three status columns. Section 13.E
 * requires approval, lifecycle, and automation to be separate state machines,
 * and the example it gives is precisely the case a single column would hide:
 * "A lease can be approved and active while its notification automation is
 * paused." So all three are shown, side by side, and a fourth column says in
 * plain words whether anything will actually be sent.
 */

const filters = [
  { key: "all", label: "All" },
  { key: "active", label: "Active" },
  { key: "pendingReview", label: "Pending review" },
  { key: "expiring", label: "Expiring soon" },
  { key: "attention", label: "Needs attention" },
  { key: "expired", label: "Expired" },
  { key: "draft", label: "Draft" },
  { key: "archived", label: "Archived" },
] as const;

type FilterKey = (typeof filters)[number]["key"];

export default async function LeasesPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  const params = await searchParams;
  const active = resolveFilter(params);

  // The dashboard links here with ?add=1 rather than carrying the dialog
  // itself, which would mean fetching properties, units and tenants on the
  // dashboard to power a button most visits never press.
  const openAddLease = params.add === "1";

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let access = permissions(null);
  let leases: Lease[] = [];
  let properties: Property[] = [];
  let tenants: Tenant[] = [];
  let unitsByProperty: Record<string, Unit[]> = {};
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;
      access = permissions(organization.callerRole);

      // Fetched unfiltered and narrowed below. The API supports server-side
      // filtering, which is what a large portfolio would use; for one
      // landlord's leases a single request and a local filter keeps the tab
      // bar instant and avoids a round trip per click.
      //
      // Properties and tenants are for the create form, and are allowed to
      // fail on their own: a lease list that will not render because the
      // tenant list could not load would be worse than a form that opens
      // with nothing to pick.
      const [leaseResult, propertyResult, tenantResult] = await Promise.all([
        api.leases(organization.id),
        api.properties(organization.id).catch(() => []),
        api.tenants(organization.id).catch(() => []),
      ]);

      leases = leaseResult;
      properties = propertyResult;
      tenants = tenantResult;

      // One request per property, in parallel.
      //
      // Done here rather than on demand in the browser so choosing a property
      // in the form switches its unit list instantly, with no round trip and
      // no loading state inside a dialog. That trade stops being right at a
      // portfolio of hundreds; the marker for when is this loop, not the
      // form.
      const unitLists = await Promise.all(
        properties.map(async (property) => [
          property.id,
          await api.units(organization.id, property.id).catch(() => []),
        ] as const),
      );

      unitsByProperty = Object.fromEntries(unitLists);
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    loadError =
      error instanceof ApiError
        ? error.message
        : "The API could not be reached.";
  }

  const visible = applyFilter(leases, active);

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <PageHeader
        title="Leases"
        description="A lease sends no reminders until it has been reviewed and approved."
        primaryAction={
          organizationId ? (
            <AddLease
              organizationId={organizationId}
              properties={properties}
              unitsByProperty={unitsByProperty}
              tenants={tenants}
              canWrite={access.canWrite}
              autoOpen={openAddLease}
            />
          ) : null
        }
      />

      {/* Links rather than client state, so a filtered view is shareable
          and survives a refresh. */}
      <div className="mb-5">
        <PillTabs
          ariaLabel="Filter leases"
          activeKey={active}
          tabs={filters.map((filter) => ({
            key: filter.key,
            label: filter.label,
            count: applyFilter(leases, filter.key).length,
            href:
              filter.key === "all" ? "/leases" : `/leases?filter=${filter.key}`,
          }))}
        />
      </div>

      <Panel>
        {loadError ? (
          <ErrorState description={loadError} />
        ) : visible.length === 0 ? (
          <EmptyState
            title={leases.length === 0 ? "No leases yet" : "No leases match this filter"}
            description={
              leases.length === 0
                ? "Add a lease by hand, or upload a lease document once phase 2 is in place."
                : "Try a different filter."
            }
          />
        ) : (
          <TableFrame>
            <thead>
              <tr>
                <Th>Lease</Th>
                <Th>Property</Th>
                <Th className="text-right">Rent</Th>
                <Th>Term</Th>
                {/* Three independent statuses (section 13.E). Shown separately
                    because combining them would hide the approved-and-paused
                    case, which is exactly the one worth noticing. */}
                <Th>Approval</Th>
                <Th>Lifecycle</Th>
                <Th>Reminders</Th>
                <Th />
              </tr>
            </thead>
            <tbody>
              {visible.map((lease) => {
                const approval = approvalStatusLabel(lease.approvalStatus);
                const lifecycle = lifecycleStatusLabel(lease.lifecycleStatus);
                const automation = automationStatusLabel(lease.automationStatus);
                const endsIn = daysUntil(lease.endDate);

                return (
                  <tr key={lease.id} className="hover:bg-surface-subtle">
                    <Td className="font-medium">
                      <Link href={`/leases/${lease.id}`} className="hover:text-accent">
                        {lease.reference ?? "Untitled lease"}
                      </Link>
                      {lease.tenants.length > 0 ? (
                        <p className="mt-0.5 text-[12px] font-normal text-ink-secondary">
                          {lease.tenants
                            .map((tenant) => tenant.preferredName ?? tenant.legalName)
                            .join(", ")}
                        </p>
                      ) : null}
                    </Td>

                    <Td className="text-ink-secondary">
                      {lease.propertyName}
                      {lease.unitNumber ? (
                        <span className="text-ink-muted"> · {lease.unitNumber}</span>
                      ) : null}
                    </Td>

                    <Td className="tabular text-right font-medium">
                      {formatMoney(lease.rentAmount, lease.currency)}
                    </Td>

                    <Td className="tabular whitespace-nowrap text-[13px] text-ink-secondary">
                      {formatDateIso(lease.startDate)}
                      {" to "}
                      {lease.endDate ? (
                        formatDateIso(lease.endDate)
                      ) : (
                        // Open-ended is a real lease term, not missing data.
                        <span title="Open ended, month to month">open</span>
                      )}
                      {endsIn !== null && endsIn >= 0 && endsIn <= 90 ? (
                        <span className="ml-1.5 text-warning-text">
                          ({endsIn}d)
                        </span>
                      ) : null}
                    </Td>

                    <Td>
                      <Status tone={approval.tone}>{approval.label}</Status>
                    </Td>
                    <Td>
                      <Status tone={lifecycle.tone}>{lifecycle.label}</Status>
                    </Td>
                    <Td>
                      {/* The title is the one-sentence explanation, because the
                          three chips together still need interpreting. */}
                      <span title={reminderSummary(lease)}>
                        <Status tone={automation.tone}>{automation.label}</Status>
                      </span>
                    </Td>

                    {/* Offered only once the lease is approved. A rent schedule
                        decides when a tenant is told to pay, so setting one up
                        before anybody has approved the terms is the sequence
                        section 13.C exists to prevent. */}
                    <Td className="text-right">
                      {lease.approvalStatus === "approved" ? (
                        <Link
                          href={`/leases/${lease.id}/schedule`}
                          className="inline-flex items-center rounded-control border
                                     border-line-strong bg-surface px-2.5 py-1
                                     text-[12.5px] font-medium hover:bg-surface-subtle"
                        >
                          Rent schedule
                        </Link>
                      ) : null}
                    </Td>
                  </tr>
                );
              })}
            </tbody>
          </TableFrame>
        )}
      </Panel>

      <p className="mt-4 text-[13px] text-ink-secondary">
        Approval, lifecycle, and reminders are tracked separately. A lease can be
        approved and active while its reminders are paused, and in that state nothing
        is sent.
      </p>
    </Shell>
  );
}

function resolveFilter(params: Record<string, string | string[] | undefined>): FilterKey {
  const raw = params["filter"] ?? params["lifecycle"] ?? params["approval"];
  const value = Array.isArray(raw) ? raw[0] : raw;

  if (params["attention"] === "1") {
    return "attention";
  }

  const match = filters.find((filter) => filter.key === value);

  return match?.key ?? "all";
}

function applyFilter(leases: Lease[], filter: FilterKey): Lease[] {
  switch (filter) {
    case "active":
      return leases.filter(
        (lease) =>
          lease.approvalStatus === "approved" && lease.lifecycleStatus === "active",
      );

    case "pendingReview":
      return leases.filter((lease) => lease.approvalStatus === "pendingReview");

    case "expiring":
      return leases.filter((lease) => {
        const days = daysUntil(lease.endDate);

        return (
          days !== null &&
          days >= 0 &&
          days <= 90 &&
          (lease.lifecycleStatus === "active" || lease.lifecycleStatus === "upcoming")
        );
      });

    case "attention":
      // Approved and live, but nothing is going out. The case that is easiest
      // to miss and most expensive to miss.
      return leases.filter(
        (lease) =>
          lease.approvalStatus === "approved" &&
          (lease.lifecycleStatus === "active" || lease.lifecycleStatus === "upcoming") &&
          !lease.canSendReminders,
      );

    case "expired":
      return leases.filter(
        (lease) =>
          lease.lifecycleStatus === "expired" || lease.lifecycleStatus === "terminated",
      );

    case "draft":
      return leases.filter((lease) => lease.lifecycleStatus === "draft");

    case "archived":
      return leases.filter((lease) => lease.lifecycleStatus === "archived");

    case "all":
    default:
      return leases;
  }
}
