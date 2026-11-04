"use client";

import { useCallback, useEffect, useRef, useState, useTransition } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Bell, Check, CheckCheck, X } from "lucide-react";

import {
  dismissNotificationAction,
  markNotificationsReadAction,
} from "@/app/maintenance/actions";
import { cx } from "@/components/ui/primitives";
import { relativeTime, type NotificationItem } from "@/lib/maintenance";

/**
 * The unified notification centre.
 *
 * <p>
 * Rent reminders, lease expiries, and maintenance updates in one list. The
 * reason they can share one list is that the database holds every channel in
 * one table, so an in-app notification is a delivery row rather than a
 * separate concept.
 * </p>
 *
 * <h3>Why this exists alongside email</h3>
 *
 * Email is the channel somebody reads once and then loses. A notification
 * centre answers "what has happened since I last looked" without archaeology
 * in an inbox, and it means the noisy rules can be set to in-app only instead
 * of being switched off entirely. A board where every status change emails
 * five people is a board whose emails get filtered.
 *
 * <h3>Polling, not sockets</h3>
 *
 * The badge polls on an interval and refreshes when the tab regains focus.
 * A realtime subscription would be better and is the natural next step, but
 * polling has no connection to keep alive, no reconnect logic, and no failure
 * mode where the badge silently stops updating because a socket died quietly.
 * The interval is deliberately slow, and the poll stops entirely while the tab
 * is hidden so a background tab costs nothing.
 */
export function NotificationBell({
  initialUnreadCount,
  initialNotifications,
}: {
  initialUnreadCount: number;
  initialNotifications: NotificationItem[];
}) {
  const router = useRouter();
  const [isOpen, setIsOpen] = useState(false);
  const [unreadCount, setUnreadCount] = useState(initialUnreadCount);
  const [notifications, setNotifications] =
    useState<NotificationItem[]>(initialNotifications);
  const [, startTransition] = useTransition();
  const panelRef = useRef<HTMLDivElement | null>(null);
  const buttonRef = useRef<HTMLButtonElement | null>(null);

  // --- Badge polling ------------------------------------------------------

  const refreshCount = useCallback(async () => {
    try {
      const response = await fetch("/api/notifications/count", {
        cache: "no-store",
      });

      if (response.ok) {
        const data = (await response.json()) as { unreadCount: number };
        setUnreadCount(data.unreadCount);
      }
    } catch {
      // A failed poll is not worth reporting. The next one will succeed, and
      // an error toast every sixty seconds on a flaky connection would be
      // worse than a slightly stale badge.
    }
  }, []);

  useEffect(() => {
    // No fetch on mount: the initial count is server-rendered by
    // getShellContext, so the badge is already correct on first paint. Doing
    // it here instead would flash a zero on every navigation, and setting
    // state from an effect body is what the React lint rule objects to.
    //
    // Nothing polls while the tab is hidden either: a background tab left open
    // for a day would otherwise make a request a minute for a badge nobody can
    // see.
    let timer: ReturnType<typeof setInterval> | null = null;

    function start() {
      if (timer === null) {
        timer = setInterval(refreshCount, 60_000);
      }
    }

    function stop() {
      if (timer !== null) {
        clearInterval(timer);
        timer = null;
      }
    }

    function onVisibilityChange() {
      if (document.visibilityState === "visible") {
        // Refresh immediately on return, then resume polling. Coming back to
        // a stale badge is the most noticeable version of this being wrong.
        void refreshCount();
        start();
      } else {
        stop();
      }
    }

    if (document.visibilityState === "visible") {
      start();
    }

    document.addEventListener("visibilitychange", onVisibilityChange);

    return () => {
      stop();
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  }, [refreshCount]);

  // --- Opening and closing -----------------------------------------------

  const open = useCallback(async () => {
    setIsOpen(true);

    // Fetched on open rather than kept in sync, because the list is only
    // looked at occasionally and the badge already carries the urgent part.
    try {
      const response = await fetch("/api/notifications/list", {
        cache: "no-store",
      });

      if (response.ok) {
        const data = (await response.json()) as {
          unreadCount: number;
          notifications: NotificationItem[];
        };

        setNotifications(data.notifications);
        setUnreadCount(data.unreadCount);
      }
    } catch {
      // Keeps whatever was last loaded.
    }
  }, []);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    function onPointerDown(event: MouseEvent) {
      const target = event.target as Node;

      if (
        !panelRef.current?.contains(target) &&
        !buttonRef.current?.contains(target)
      ) {
        setIsOpen(false);
      }
    }

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setIsOpen(false);
        // Focus returns to the bell, so tab order continues from there rather
        // than from the top of the document.
        buttonRef.current?.focus();
      }
    }

    document.addEventListener("mousedown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);

    return () => {
      document.removeEventListener("mousedown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [isOpen]);

  // --- Actions ------------------------------------------------------------

  function markAllRead() {
    // Optimistic: the badge is the thing people watch, so it clears at once.
    const previousCount = unreadCount;
    const previousNotifications = notifications;

    setUnreadCount(0);
    setNotifications((current) =>
      current.map((item) => ({ ...item, readAt: item.readAt ?? new Date().toISOString() })),
    );

    startTransition(async () => {
      const result = await markNotificationsReadAction(null);

      if (!result.ok) {
        setUnreadCount(previousCount);
        setNotifications(previousNotifications);
      }
    });
  }

  function dismiss(notificationId: string) {
    const previousNotifications = notifications;
    const wasUnread = notifications.find(
      (item) => item.id === notificationId,
    )?.readAt === null;

    setNotifications((current) =>
      current.filter((item) => item.id !== notificationId),
    );

    if (wasUnread) {
      setUnreadCount((count) => Math.max(0, count - 1));
    }

    startTransition(async () => {
      const result = await dismissNotificationAction(notificationId);

      if (!result.ok) {
        setNotifications(previousNotifications);

        if (wasUnread) {
          setUnreadCount((count) => count + 1);
        }
      }
    });
  }

  function openNotification(item: NotificationItem) {
    setIsOpen(false);

    if (item.readAt === null) {
      setUnreadCount((count) => Math.max(0, count - 1));

      startTransition(async () => {
        await markNotificationsReadAction([item.id]);
      });
    }

    if (item.linkPath) {
      router.push(item.linkPath);
    }
  }

  return (
    <div className="relative">
      <button
        ref={buttonRef}
        type="button"
        onClick={() => (isOpen ? setIsOpen(false) : void open())}
        aria-expanded={isOpen}
        aria-haspopup="true"
        aria-label={
          unreadCount > 0
            ? `Notifications, ${unreadCount} unread`
            : "Notifications"
        }
        className="relative inline-flex size-9 items-center justify-center rounded-control
                   text-ink-secondary transition-colors hover:bg-surface-sunken hover:text-ink"
      >
        <Bell className="size-[18px]" aria-hidden="true" />

        {unreadCount > 0 ? (
          <span
            className="absolute -right-0.5 -top-0.5 inline-flex min-w-[17px] items-center
                       justify-center rounded-full bg-danger px-1 text-[10px] font-bold
                       leading-[17px] text-white ring-2 ring-surface"
            aria-hidden="true"
          >
            {unreadCount > 99 ? "99+" : unreadCount}
          </span>
        ) : null}
      </button>

      {isOpen ? (
        <div
          ref={panelRef}
          role="dialog"
          aria-label="Notifications"
          className="absolute right-0 top-11 z-50 w-[min(24rem,calc(100vw-2rem))]
                     overflow-hidden rounded-surface border border-line bg-surface
                     shadow-floating"
        >
          <header className="flex items-center justify-between gap-2 border-b border-line px-4 py-2.5">
            <h2 className="text-[13.5px] font-semibold">Notifications</h2>

            <div className="flex items-center gap-1">
              {unreadCount > 0 ? (
                <button
                  type="button"
                  onClick={markAllRead}
                  className="inline-flex items-center gap-1 rounded-control px-2 py-1
                             text-[12px] text-ink-secondary hover:bg-surface-sunken hover:text-ink"
                >
                  <CheckCheck className="size-3.5" aria-hidden="true" />
                  Mark all read
                </button>
              ) : null}

              <Link
                href="/notifications"
                onClick={() => setIsOpen(false)}
                className="rounded-control px-2 py-1 text-[12px] font-medium text-accent
                           hover:bg-accent-tint"
              >
                See all
              </Link>
            </div>
          </header>

          <div className="max-h-[26rem] overflow-y-auto">
            {notifications.length === 0 ? (
              <div className="px-4 py-10 text-center">
                <p className="text-[13px] font-medium">You are all caught up</p>
                <p className="mt-1 text-[12px] text-ink-secondary">
                  Rent reminders, lease dates, and task updates appear here.
                </p>
              </div>
            ) : (
              <ul className="divide-y divide-line">
                {notifications.map((item) => (
                  <li
                    key={item.id}
                    className={cx(
                      "group relative",
                      item.readAt === null && "bg-accent-tint/40",
                    )}
                  >
                    <button
                      type="button"
                      onClick={() => openNotification(item)}
                      className="block w-full px-4 py-2.5 pr-9 text-left
                                 transition-colors hover:bg-surface-subtle"
                    >
                      <span className="flex items-start gap-2">
                        {item.readAt === null ? (
                          <span
                            className="mt-1.5 size-1.5 shrink-0 rounded-full bg-accent"
                            aria-hidden="true"
                          />
                        ) : (
                          <span className="mt-1.5 size-1.5 shrink-0" aria-hidden="true" />
                        )}

                        <span className="min-w-0 flex-1">
                          <span className="block text-[13px] font-medium leading-snug">
                            {item.title}
                          </span>
                          <span className="mt-0.5 block line-clamp-2 text-[12px] leading-snug text-ink-secondary">
                            {item.body}
                          </span>
                          <span className="mt-1 flex items-center gap-1.5 text-[11px] text-ink-muted">
                            <span>{relativeTime(item.createdAt)}</span>
                            {item.recipientRole ? (
                              // Answers "why am I getting this?", which is the
                              // question that otherwise turns into a request to
                              // turn notifications off entirely.
                              <>
                                <span aria-hidden="true">·</span>
                                <span>{describeRole(item.recipientRole)}</span>
                              </>
                            ) : null}
                          </span>
                        </span>
                      </span>
                    </button>

                    <button
                      type="button"
                      onClick={() => dismiss(item.id)}
                      aria-label="Dismiss notification"
                      className="absolute right-2 top-2.5 inline-flex size-6 items-center
                                 justify-center rounded text-ink-muted opacity-0
                                 transition-opacity hover:bg-surface-sunken hover:text-ink
                                 focus-visible:opacity-100 group-hover:opacity-100"
                    >
                      <X className="size-3.5" />
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      ) : null}
    </div>
  );
}

/** Plain words for why somebody received a notification. */
function describeRole(role: string): string {
  switch (role) {
    case "assignee":
      return "you are assigned";
    case "taskCreator":
      return "you created this";
    case "reportingTenant":
      return "you reported this";
    case "propertyManager":
      return "you manage this property";
    case "propertyOwner":
      return "you own this property";
    case "vendor":
      return "you are the vendor";
    case "recipientGroup":
      return "via a recipient group";
    default:
      return "you are a recipient";
  }
}

/** Shown in the full-page notification list when it is empty. */
export function NotificationEmptyState() {
  return (
    <div className="px-6 py-14 text-center">
      <div
        className="mx-auto mb-3 grid size-10 place-items-center rounded-full
                   bg-surface-sunken text-ink-muted"
        aria-hidden="true"
      >
        <Check className="size-5" />
      </div>
      <h3>Nothing here yet</h3>
      <p className="mx-auto mt-1.5 max-w-md text-[13px] leading-relaxed text-ink-secondary">
        Rent reminders, lease expiry warnings, and maintenance task updates will
        appear here as they happen.
      </p>
    </div>
  );
}
