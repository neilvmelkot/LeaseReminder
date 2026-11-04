import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { ChevronLeft, MapPin } from "lucide-react";

import { Shell } from "@/components/shell";
import { EditProperty } from "@/components/portfolio/edit-property";
import { PropertyLifecycleControls } from "@/components/portfolio/remove-property";
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
import { PermissionNotice } from "@/components/ui/permission-notice";
import { api, ApiError, permissions, type Property, type Unit } from "@/lib/api";
import { formatAddress, formatPropertyType, pluralize } from "@/lib/format";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * One property.
 *
 * <p>
 * Rendered per request: this is lease and tenant data, so there is no correct
 * build-time output and none of it may sit in a shared cache.
 * </p>
 *
 * <h3>Why the time zone is given its own line rather than a table cell</h3>
 *
 * It is the one field here that changes behaviour rather than describing it.
 * Every rent date rule for this property is read in that zone, so it decides
 * what calendar date a tenant is told to pay — and this is the only screen
 * where somebody is likely to notice it was set up wrong.
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Property" };

export default async function PropertyPage({
  params,
}: {
  params: Promise<{ propertyId: string }>;
}) {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  const { propertyId } = await params;

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let access = permissions(null);
  let property: Property | null = null;
  let units: Unit[] = [];
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;
      access = permissions(organization.callerRole);

      property = await api.property(organization.id, propertyId);

      // Allowed to fail on its own. A property page that will not render
      // because the unit list could not load is worse than a unit list that
      // says it is empty.
      units = await api.units(organization.id, propertyId).catch(() => []);
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

  const address = property ? formatAddress(property) : null;

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <div className="mb-4">
        <Link
          href="/properties"
          className="inline-flex items-center gap-1 text-[12.5px] text-ink-secondary
                     transition-colors hover:text-ink"
        >
          <ChevronLeft className="size-3.5" aria-hidden="true" />
          All properties
        </Link>
      </div>

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !property || !organizationId ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace before opening a property."
          />
        </Panel>
      ) : (
        <div className="space-y-5">
          <PageHeader
            title={property.name}
            description={formatPropertyType(property.propertyType)}
            meta={
              property.archivedAt ? (
                <Status tone="warning">Archived</Status>
              ) : (
                <Status tone="neutral">
                  {pluralize(property.unitCount, "unit")}
                </Status>
              )
            }
            primaryAction={
              <div className="flex flex-wrap items-start justify-end gap-2">
                {access.canWrite && !property.archivedAt ? (
                  <EditProperty organizationId={organizationId} property={property} />
                ) : null}

                {/* Archive while it is in use; Restore and Delete once it is
                    archived. Deleting is never offered beside an archive
                    button, because one is reversible and the other is not. */}
                {access.canAdminister ? (
                  <PropertyLifecycleControls
                    organizationId={organizationId}
                    property={property}
                  />
                ) : null}
              </div>
            }
          />

          {property.archivedAt ? (
            <p
              className="rounded-control border border-warning/40 bg-warning/10 px-3
                         py-2.5 text-[12.5px] leading-relaxed text-warning-text"
            >
              This property was archived on{" "}
              {new Date(property.archivedAt).toLocaleDateString()}. It is hidden
              from the portfolio, and its leases, rent history and audit trail
              are unchanged. Restoring it puts it back.
            </p>
          ) : null}

          {!access.canWrite ? (
            <PermissionNotice
              reason={access.reason}
              action="Editing this property"
            />
          ) : null}

          <Panel title="Details" padded>
            <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
              <Detail label="Address">
                {address ? (
                  <span className="flex items-start gap-1.5">
                    <MapPin
                      className="mt-0.5 size-3.5 shrink-0 text-ink-muted"
                      aria-hidden="true"
                    />
                    {address}
                  </span>
                ) : (
                  <span className="text-ink-muted">Not set</span>
                )}
              </Detail>

              <Detail label="Time zone">
                {property.timeZone}
                <p className="mt-0.5 text-[12px] leading-relaxed text-ink-secondary">
                  Rent due dates for this property are read in this zone.
                </p>
              </Detail>

              <Detail label="Active leases">
                <span className="tabular">{property.activeLeaseCount}</span>
              </Detail>

              <Detail label="Country">{property.countryCode}</Detail>

              {property.notes ? (
                <div className="sm:col-span-2">
                  <Detail label="Notes">
                    <span className="whitespace-pre-line">{property.notes}</span>
                  </Detail>
                </div>
              ) : null}
            </dl>
          </Panel>

          <Panel
            title="Units"
            description="A lease is signed against a unit, so a single-home property still has one."
          >
            {units.length === 0 ? (
              <EmptyState
                title="No units yet"
                description="Every lease is signed against a unit. A single-family house has
                             one; a building has one per door."
              />
            ) : (
              <TableFrame minWidth="40rem">
                <thead>
                  <tr>
                    <Th>Unit</Th>
                    <Th>Status</Th>
                    <Th>Beds</Th>
                    <Th>Baths</Th>
                    <Th>Current tenant</Th>
                  </tr>
                </thead>
                <tbody>
                  {units.map((unit) => (
                    <tr key={unit.id} className="hover:bg-surface-subtle">
                      <Td className="font-medium">{unit.unitNumber}</Td>

                      <Td>
                        <Status
                          tone={unit.activeLeaseId ? "success" : "neutral"}
                        >
                          {formatPropertyType(unit.status)}
                        </Status>
                      </Td>

                      <Td className="tabular text-[13px] text-ink-secondary">
                        {unit.bedrooms ?? "—"}
                      </Td>

                      <Td className="tabular text-[13px] text-ink-secondary">
                        {unit.bathrooms ?? "—"}
                      </Td>

                      <Td className="text-[13px] text-ink-secondary">
                        {unit.activeLeaseId && unit.currentTenantName ? (
                          <Link
                            href={`/leases/${unit.activeLeaseId}`}
                            className="transition-colors hover:text-accent"
                          >
                            {unit.currentTenantName}
                          </Link>
                        ) : (
                          <span className="text-ink-muted">Vacant</span>
                        )}
                      </Td>
                    </tr>
                  ))}
                </tbody>
              </TableFrame>
            )}
          </Panel>
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
