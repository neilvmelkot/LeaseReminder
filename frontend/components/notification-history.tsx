"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Bell, CalendarClock, FileText, Wrench } from "lucide-react";

import { markNotificationsReadAction } from "@/app/maintenance/actions";
import { cx } from "@/components/ui/primitives";
import { relativeTime, type NotificationItem } from "@/lib/maintenance";

/**
 * The notification history list.
 *
 * Each row is a button rather than a link, because opening one also marks it
 * read. A plain link would navigate and leave the badge wrong until the next
 * poll, which is exactly the kind of small inconsistency that makes people
 * stop trusting a badge.
 */
export function NotificationHistory({
  notifications,
}: {
  notifications: NotificationItem[];
}) {
  const router = useRouter();
  const [readIds, setReadIds] = useState<Set<string>>(new Set());
  const [, startTransition] = useTransition();

  function open(item: NotificationItem) {
    if (item.readAt === null && !readIds.has(item.id)) {
      setReadIds((current) => new Set(current).add(item.id));

      startTransition(async () => {
        await markNotificationsReadAction([item.id]);
      });
    }

    if (item.linkPath) {
      router.push(item.linkPath);
    }
  }

  return (
    <ul className="divide-y divide-line">
      {notifications.map((item) => {
        const isUnread = item.readAt === null && !readIds.has(item.id);

        return (
          <li key={item.id}>
            <button
              type="button"
              onClick={() => open(item)}
              className={cx(
                "flex w-full items-start gap-3 px-5 py-3 text-left transition-colors",
                "hover:bg-surface-subtle",
                isUnread && "bg-accent-tint/30",
              )}
            >
              <span
                className={cx(
                  "mt-0.5 grid size-7 shrink-0 place-items-center rounded-full",
                  isUnread
                    ? "bg-accent-tint text-accent"
                    : "bg-surface-sunken text-ink-muted",
                )}
                aria-hidden="true"
              >
                <SubjectIcon subjectType={item.subjectType} />
              </span>

              <span className="min-w-0 flex-1">
                <span className="flex items-baseline gap-2">
                  <span
                    className={cx(
                      "text-[13.5px] leading-snug",
                      isUnread ? "font-semibold" : "font-medium",
                    )}
                  >
                    {item.title}
                  </span>
                  <span className="ml-auto shrink-0 text-[11.5px] text-ink-muted">
                    {relativeTime(item.createdAt)}
                  </span>
                </span>
                <span className="mt-0.5 block text-[12.5px] leading-snug text-ink-secondary">
                  {item.body}
                </span>
              </span>
            </button>
          </li>
        );
      })}
    </ul>
  );
}

/** The icon says which part of the product a notification came from. */
function SubjectIcon({ subjectType }: { subjectType: string }) {
  switch (subjectType) {
    case "task":
      return <Wrench className="size-3.5" />;
    case "lease":
      return <FileText className="size-3.5" />;
    case "rent_occurrence":
      return <CalendarClock className="size-3.5" />;
    default:
      return <Bell className="size-3.5" />;
  }
}
