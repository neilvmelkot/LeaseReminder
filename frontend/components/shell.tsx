import { AppShell } from "@/components/app-shell";
import { getShellContext } from "@/lib/shell";

/**
 * Server-side wrapper around the application shell.
 *
 * <p>
 * Exists so the notification badge is correct on first paint without every
 * page having to fetch and thread the data through. A server component can
 * render a client component and pass it data, so this does the fetch and
 * <see cref="AppShell"/> stays a client component for the drawer, the menus,
 * and the bell's own interactivity.
 * </p>
 *
 * <p>
 * The caller still passes userEmail and organizationName, because a page has
 * usually already loaded them for its own content and fetching them twice
 * would be wasteful. Only the notification data is fetched here.
 * </p>
 */
export async function Shell({
  children,
  userEmail,
  organizationName,
}: {
  children: React.ReactNode;
  userEmail: string | null;
  organizationName: string | null;
}) {
  const context = await getShellContext();

  return (
    <AppShell
      userEmail={userEmail ?? context.userEmail}
      organizationName={organizationName ?? context.organizationName}
      unreadNotificationCount={context.unreadNotificationCount}
      notifications={context.notifications}
    >
      {children}
    </AppShell>
  );
}
