import "server-only";

import { api, ApiError, notificationApi } from "@/lib/api";
import { getCurrentUser } from "@/lib/supabase/server";
import type { NotificationItem } from "@/lib/maintenance";
import type { OrgRole } from "@/lib/api";

/**
 * Everything the application shell needs, fetched once per page render.
 *
 * <h3>Why this exists</h3>
 *
 * Every page was independently calling `getCurrentUser` and `api.currentUser`
 * and then threading the organization name into the shell. That duplication
 * was tolerable until the notification badge needed initial data too: the
 * alternative was a fetch on mount inside the bell, which means the badge
 * flashes 0 on every navigation and the React lint rule against setting state
 * in an effect body is correct to object.
 *
 * Fetching it here means the badge is right on first paint, with no effect and
 * no flash, and a page needs one call instead of three.
 *
 * <h3>Why a failure returns a context rather than throwing</h3>
 *
 * The shell has to render even when the API is unreachable, because the
 * alternative is a blank page with no navigation and no way to sign out. So a
 * failed notification fetch yields an empty badge and the page carries on; a
 * failed identity fetch is reported so the page can show it.
 */
export interface ShellContext {
  userEmail: string | null;
  organizationId: string | null;
  organizationName: string | null;
  callerRole: OrgRole | null;
  unreadNotificationCount: number;
  notifications: NotificationItem[];

  /** Set when the organization lookup failed, so a page can show the reason. */
  error: string | null;

  /** True when the session has gone, so the page should redirect to sign-in. */
  isUnauthenticated: boolean;
}

export async function getShellContext(): Promise<ShellContext> {
  const user = await getCurrentUser();

  if (!user) {
    return {
      userEmail: null,
      organizationId: null,
      organizationName: null,
      callerRole: null,
      unreadNotificationCount: 0,
      notifications: [],
      error: null,
      isUnauthenticated: true,
    };
  }

  const base = {
    userEmail: user.email ?? null,
    isUnauthenticated: false,
  };

  try {
    // Both at once. The notification list does not depend on the organization
    // lookup, so running them in sequence would double the latency of every
    // page for no reason.
    const [currentUser, notifications] = await Promise.all([
      api.currentUser(),
      // Only a short preview: the bell shows the most recent few and the full
      // history has its own page. Fetching fifty on every page render would be
      // paying for data almost nobody opens.
      notificationApi.list(false, 8).catch(() => ({
        unreadCount: 0,
        notifications: [] as NotificationItem[],
      })),
    ]);

    const organization = currentUser.organizations[0] ?? null;

    return {
      ...base,
      organizationId: organization?.id ?? null,
      organizationName: organization?.name ?? null,
      callerRole: organization?.callerRole ?? null,
      unreadNotificationCount: notifications.unreadCount,
      notifications: notifications.notifications,
      error: null,
    };
  } catch (error) {
    if (error instanceof ApiError && error.isUnauthenticated) {
      return {
        ...base,
        organizationId: null,
        organizationName: null,
        callerRole: null,
        unreadNotificationCount: 0,
        notifications: [],
        error: null,
        isUnauthenticated: true,
      };
    }

    return {
      ...base,
      organizationId: null,
      organizationName: null,
      callerRole: null,
      unreadNotificationCount: 0,
      notifications: [],
      error:
        error instanceof ApiError
          ? error.message
          : "The API could not be reached. Check that it is running and that " +
            "NEXT_PUBLIC_API_BASE_URL points at it.",
      isUnauthenticated: false,
    };
  }
}
