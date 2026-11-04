import Link from "next/link";
import { redirect } from "next/navigation";
import { Archive } from "lucide-react";

import { Shell } from "@/components/shell";
import { MaintenanceWorkspace } from "@/components/kanban/maintenance-workspace";
import { ErrorState, PageHeader, Panel, Status } from "@/components/ui/primitives";
import { PropertyFilter } from "@/components/ui/property-filter";
import { api, ApiError, maintenanceApi, membershipApi, roleLabel } from "@/lib/api";
import type { Assignable } from "@/components/kanban/assignee-picker";
import { getCurrentUser } from "@/lib/supabase/server";
import type { Board } from "@/lib/maintenance";

/**
 * Maintenance and tasks.
 *
 * <p>
 * Rendered per request. The board is per-user data behind a session, so there
 * is no correct build-time output and nothing here may be cached.
 * </p>
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Maintenance" };

export default async function MaintenancePage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  const params = await searchParams;
  const propertyFilter = readParam(params, "propertyId");

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let board: Board | null = null;
  let properties: Array<{ id: string; name: string }> = [];
  let tenants: Array<{ id: string; name: string }> = [];
  let assignable: Assignable[] = [];
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;

      // Requested together rather than in sequence. The board needs the
      // property and tenant lists for the new-task dialog, and three
      // dependent round trips would be three times the latency for no gain.
      // Members and vendors are for the assignee picker, and are allowed to
      // fail on their own: a board that will not render because the vendor
      // list is unavailable would be worse than a picker with fewer names.
      const [
        boardResult,
        propertyResult,
        tenantResult,
        memberResult,
        vendorResult,
      ] = await Promise.all([
        maintenanceApi.board(
          organization.id,
          propertyFilter ? { propertyId: propertyFilter } : undefined,
        ),
        api.properties(organization.id),
        api.tenants(organization.id),
        membershipApi.members(organization.id).catch(() => []),
        maintenanceApi.vendors(organization.id).catch(() => []),
      ]);

      board = boardResult;

      properties = propertyResult.map((property) => ({
        id: property.id,
        name: property.name,
      }));

      // One list, because "who is dealing with this" does not care whether
      // the answer is a colleague or a plumber. The database keeps them
      // apart; the picker sends whichever id applies.
      assignable = [
        ...memberResult.map((member) => ({
          userId: member.userId,
          vendorId: null,
          name: member.displayName ?? member.email,
          detail: member.displayName ? member.email : roleLabel(member.role),
        })),
        ...vendorResult
          .filter((vendor) => vendor.active)
          .map((vendor) => ({
            userId: null,
            vendorId: vendor.id,
            name: vendor.company ?? vendor.name,
            detail: vendor.company ? vendor.name : null,
          })),
      ];

      tenants = tenantResult
        // Only tenants who can actually be notified are offered as reporters.
        // Offering somebody who has opted out would set up a report whose
        // acknowledgement silently never arrives.
        .filter((tenant) => tenant.primaryEmail && tenant.notificationsOptedIn)
        .map((tenant) => ({
          id: tenant.id,
          name: tenant.preferredName ?? tenant.legalName,
        }));
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  const propertyName = propertyFilter
    ? (properties.find((property) => property.id === propertyFilter)?.name ?? null)
    : null;

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <PageHeader
        title="Maintenance & tasks"
        {...(propertyName ? { description: propertyName } : {})}
        meta={
          board && board.overdueTaskCount > 0 ? (
            <Status tone="danger">{board.overdueTaskCount} overdue</Status>
          ) : null
        }
        secondaryAction={
          <Link
            href="/maintenance/archive"
            className="inline-flex h-9 items-center gap-2 rounded-control border
                       border-line-strong bg-surface px-4 text-sm font-medium
                       hover:bg-page"
          >
            <Archive className="size-4" aria-hidden="true" />
            Archive
          </Link>
        }
      />

      {/* The portfolio board and a single-property board are two filters
          over one set of columns, so switching is a link. */}
      {properties.length > 1 ? (
        <div className="mb-4">
          <PropertyFilter
            properties={properties}
            selectedId={propertyFilter}
            basePath="/maintenance"
          />
        </div>
      ) : null}

      {loadError ? (
        <Panel>
          <ErrorState description={loadError} />
        </Panel>
      ) : !organizationId || !board ? (
        <Panel>
          <ErrorState
            title="No workspace yet"
            description="Create a workspace before logging maintenance tasks."
          />
        </Panel>
      ) : (
        <MaintenanceWorkspace
          assignable={assignable}
          board={board}
          organizationId={organizationId}
          properties={properties}
          tenants={tenants}
        />
      )}
    </Shell>
  );
}

function readParam(
  params: Record<string, string | string[] | undefined>,
  key: string,
): string | null {
  const value = params[key];

  if (Array.isArray(value)) {
    return value[0] ?? null;
  }

  return value ?? null;
}
