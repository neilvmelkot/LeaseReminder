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
import { api, ApiError, permissions, type Tenant } from "@/lib/api";
import { AddTenant } from "@/components/portfolio/add-tenant";
import { PermissionNotice } from "@/components/ui/permission-notice";
import { getCurrentUser } from "@/lib/supabase/server";

/**
 * Rendered per request, never prerendered.
 *
 * Two reasons, and either alone would be sufficient. The page depends on the
 * caller's session, so there is no correct build-time output to produce. And
 * the content is tenant personal data, which must never be baked into a static
 * asset or held in a shared cache.
 */
export const dynamic = "force-dynamic";

/**
 * Tenant list.
 *
 * The consent column matters more than it looks. A reminder rule targeting
 * tenants has to honour each tenant's notification preference (section 6.C),
 * so somebody who has opted out receives nothing however the rule is
 * configured. Surfacing it here is what stops a user configuring a rule and
 * then wondering why one tenant never gets an email.
 *
 * These records are visible only to members of the workspace. Tenants are not
 * users of this system, and there is no tenant-facing sign-in, which is the
 * structural reason one tenant can never see another's details.
 */
export default async function TenantsPage() {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  let organizationId: string | null = null;
  let organizationName: string | null = null;
  let access = permissions(null);
  let propertyCount = 0;
  let tenants: Tenant[] = [];
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    const organization = currentUser.organizations[0] ?? null;

    if (organization) {
      organizationId = organization.id;
      organizationName = organization.name;
      access = permissions(organization.callerRole);

      // Both, because the Add control is unavailable until a property
      // exists: a tenant with nowhere to be named is a contact, not a tenant.
      const [tenantResult, propertyResult] = await Promise.all([
        api.tenants(organization.id),
        api.properties(organization.id),
      ]);

      tenants = tenantResult;
      propertyCount = propertyResult.length;
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
        title="Tenants"
        description="A tenant who has opted out of notifications receives nothing, whatever a rule says."
        primaryAction={
          organizationId ? (
            <AddTenant
              organizationId={organizationId}
              propertyCount={propertyCount}
              canWrite={access.canWrite}
            />
          ) : null
        }
      />

      {organizationId && !access.canWrite ? (
        <div className="mb-4">
          <PermissionNotice reason={access.reason} action="Adding a tenant" />
        </div>
      ) : null}

      <Panel>
        {loadError ? (
          <ErrorState description={loadError} />
        ) : tenants.length === 0 ? (
          <EmptyState
            title="No tenants yet"
            description="Add a tenant, then name them on a lease."
          />
        ) : (
          <TableFrame>
            <thead>
              <tr>
                <Th>Name</Th>
                <Th>Email</Th>
                <Th>Status</Th>
                <Th>Notifications</Th>
                <Th className="text-right">Leases</Th>
              </tr>
            </thead>
            <tbody>
              {tenants.map((tenant) => (
                <tr key={tenant.id} className="hover:bg-surface-subtle">
                  <Td className="font-medium">
                    {tenant.preferredName ?? tenant.legalName}
                    {tenant.preferredName && tenant.preferredName !== tenant.legalName ? (
                      <p className="mt-0.5 text-[12px] font-normal text-ink-secondary">
                        Legal name: {tenant.legalName}
                      </p>
                    ) : null}
                  </Td>

                  <Td className="text-[13px] text-ink-secondary">
                    {tenant.primaryEmail ?? (
                      <span className="text-ink-muted">Not set</span>
                    )}
                  </Td>

                  <Td>
                    <Status tone={tenant.status === "active" ? "success" : "neutral"}>
                      {capitalize(tenant.status)}
                    </Status>
                  </Td>

                  <Td>
                    {/* Both states get a word, so the meaning never rests on
                        the colour alone (section 5). */}
                    <Status tone={tenant.notificationsOptedIn ? "success" : "warning"}>
                      {tenant.notificationsOptedIn ? "Opted in" : "Opted out"}
                    </Status>
                  </Td>

                  <Td className="tabular text-right">{tenant.leaseCount}</Td>
                </tr>
              ))}
            </tbody>
          </TableFrame>
        )}
      </Panel>
    </Shell>
  );
}

function capitalize(value: string): string {
  return value.charAt(0).toUpperCase() + value.slice(1);
}
