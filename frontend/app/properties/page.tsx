import { redirect } from "next/navigation";

import { Shell } from "@/components/shell";
import { ErrorState, PageHeader, Panel } from "@/components/ui/primitives";
import { PropertyTable } from "@/components/portfolio/property-table";
import { api, ApiError, permissions, type Property } from "@/lib/api";
import { AddProperty } from "@/components/portfolio/add-property";
import { PermissionNotice } from "@/components/ui/permission-notice";
import { getCurrentUser } from "@/lib/supabase/server";

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
 * Property list.
 *
 * The time zone column is not padding. Every rent date rule for a property is
 * interpreted in that zone (section 6.B), so it is a term of the schedule
 * rather than a formatting preference, and this is the only place a user can
 * notice a property was set up in the wrong one.
 */
export default async function PropertiesPage() {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let organizationTimeZone = "UTC";
  let access = permissions(null);
  let properties: Property[] = [];
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;
      organizationTimeZone = organization.timeZone;
      access = permissions(organization.callerRole);
      // Including archived ones, because this page can filter to them. The
      // API excludes them by default, which is right for every other caller.
      properties = await api.properties(organization.id, {
        includeArchived: true,
      });
    }
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      redirect("/sign-in");
    }

    loadError =
      error instanceof ApiError ? error.message : "The API could not be reached.";
  }

  return (
    <Shell userEmail={user.email ?? null} organizationName={organizationName}>
      <PageHeader
        title="Properties"
        description="Each property carries the time zone its rent dates are read in."
        primaryAction={
          organizationId ? (
            <AddProperty
              organizationId={organizationId}
              defaultTimeZone={organizationTimeZone}
              canWrite={access.canWrite}
            />
          ) : null
        }
      />

      {/* Said once, here, rather than also in place of the button: somebody
          who cannot add a property should learn why before they go looking
          for a control that is not there. */}
      {organizationId && !access.canWrite ? (
        <div className="mb-4">
          <PermissionNotice reason={access.reason} action="Adding a property" />
        </div>
      ) : null}

      <Panel>
        {loadError ? (
          <ErrorState description={loadError} />
        ) : (
          <PropertyTable properties={properties} />
        )}
      </Panel>
    </Shell>
  );
}
