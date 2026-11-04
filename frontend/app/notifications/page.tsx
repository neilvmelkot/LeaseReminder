import { redirect } from "next/navigation";

import { Shell } from "@/components/shell";
import { NotificationEmptyState } from "@/components/notification-bell";
import { NotificationHistory } from "@/components/notification-history";
import { ErrorState, PageHeader, Panel } from "@/components/ui/primitives";
import { PillTabs } from "@/components/ui/pill-tabs";
import { api, ApiError, notificationApi } from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";
import type { NotificationItem } from "@/lib/maintenance";

/**
 * The full notification history.
 *
 * The bell shows what is unread; this shows everything, which is the view
 * somebody uses to answer "was I told about that, and when". It is the reason
 * the notification centre is worth having over email alone.
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Notifications" };

export default async function NotificationsPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const user = await getCurrentUser();

  if (!user) {
    redirect("/sign-in");
  }

  const params = await searchParams;
  const showAll = params["filter"] === "all";

  let organizationName: string | null = null;
  let unreadCount = 0;
  let notifications: NotificationItem[] = [];
  let loadError: string | null = null;

  try {
    const currentUser = await api.currentUser();
    organizationName = currentUser.organizations[0]?.name ?? null;

    const result = await notificationApi.list(showAll, 100);

    unreadCount = result.unreadCount;
    notifications = result.notifications;
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
        title="Notifications"
        description="Rent reminders, lease dates, and maintenance updates, in one place."
      />

      <div className="mb-4">
        <PillTabs
          ariaLabel="Notification filter"
          activeKey={showAll ? "all" : "unread"}
          tabs={[
            { key: "unread", label: "Unread", href: "/notifications", count: unreadCount },
            { key: "all", label: "Everything", href: "/notifications?filter=all" },
          ]}
        />
      </div>

      <Panel>
        {loadError ? (
          <ErrorState description={loadError} />
        ) : notifications.length === 0 ? (
          <NotificationEmptyState />
        ) : (
          <NotificationHistory notifications={notifications} />
        )}
      </Panel>
    </Shell>
  );
}
