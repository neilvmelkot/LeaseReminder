"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useCallback, useEffect, useState, useSyncExternalStore } from "react";
import {
  Bell,
  Building2,
  CalendarDays,
  ChevronDown,
  FileScan,
  FileText,
  LayoutDashboard,
  Mail,
  Menu,
  PanelLeft,
  Settings,
  Users,
  Wrench,
  X,
} from "lucide-react";

import { NotificationBell } from "@/components/notification-bell";
import { cx } from "@/components/ui/primitives";
import { Logo } from "@/components/ui/logo";
import type { NotificationItem } from "@/lib/maintenance";

/**
 * The application shell: a sidebar on desktop, a drawer on mobile.
 *
 * <h3>Navigation structure</h3>
 *
 * Grouped rather than flat. Twelve flat items is a list people stop reading;
 * the groups match how the work divides, so somebody doing maintenance never
 * has to scan past the leasing items.
 *
 * <pre>
 *   Overview      Dashboard
 *   Portfolio     Properties, Tenants, Leases, Documents
 *   Operations    Maintenance, Calendar
 *   Comms         Rent reminders, Email, Templates, Notifications
 *                 Settings
 * </pre>
 *
 * Maintenance sits in Operations rather than under Leases on purpose: a repair
 * outlives the tenancy that reported it, and filing it under the lease would
 * orphan it at renewal.
 *
 * <h3>Collapsing</h3>
 *
 * The sidebar collapses to a 60px icon rail, and the choice is remembered.
 * The width itself is decided by a <code>data-sidebar</code> attribute on
 * &lt;html&gt; and a stylesheet, not by React — see app/globals.css and the
 * script in app/layout.tsx for why it has to be settled before the first
 * paint. React subscribes to that attribute rather than holding its own copy,
 * so there is one source of truth for whether the rail is collapsed and
 * nothing to keep in step.
 */

interface NavItem {
  href: string;
  label: string;
  icon: typeof LayoutDashboard;
}

interface NavGroup {
  label: string | null;
  items: NavItem[];
}

const navigation: NavGroup[] = [
  {
    label: null,
    items: [{ href: "/", label: "Dashboard", icon: LayoutDashboard }],
  },
  {
    label: "Portfolio",
    items: [
      { href: "/properties", label: "Properties", icon: Building2 },
      { href: "/tenants", label: "Tenants", icon: Users },
      { href: "/leases", label: "Leases", icon: FileText },
      { href: "/documents", label: "Documents", icon: FileScan },
    ],
  },
  {
    label: "Operations",
    items: [
      { href: "/maintenance", label: "Maintenance", icon: Wrench },
      { href: "/calendar", label: "Calendar", icon: CalendarDays },
    ],
  },
  {
    label: "Communications",
    items: [
      { href: "/reminders", label: "Rent reminders", icon: Bell },
      { href: "/settings/email", label: "Email", icon: Mail },
      { href: "/settings/templates", label: "Templates", icon: FileText },
      { href: "/notifications", label: "Notifications", icon: Bell },
    ],
  },
  {
    label: null,
    items: [{ href: "/settings", label: "Settings", icon: Settings }],
  },
];

/**
 * Notifies React when the sidebar attribute on &lt;html&gt; changes.
 *
 * Declared at module scope, not inline: useSyncExternalStore resubscribes
 * whenever this function's identity changes, and an inline arrow would tear
 * down and rebuild the observer on every render.
 */
function subscribeToSidebarAttribute(onChange: () => void): () => void {
  const observer = new MutationObserver(onChange);

  observer.observe(document.documentElement, {
    attributes: true,
    attributeFilter: ["data-sidebar"],
  });

  return () => observer.disconnect();
}

/**
 * Which nav item the current URL belongs to.
 *
 * <h3>Why not a prefix test per item</h3>
 *
 * Several items are nested under others: /settings/email and
 * /settings/templates both sit under /settings, and /settings itself is a real
 * page. Testing each item independently with `startsWith` lights up two at
 * once, which reads as the sidebar not knowing where you are.
 *
 * So the longest matching href wins, and every other item is inactive. The
 * match respects segment boundaries, so /settings never claims /settings-beta.
 */
function mostSpecificMatch(pathname: string): string | null {
  let best: string | null = null;

  for (const group of navigation) {
    for (const { href } of group.items) {
      const matches =
        href === "/"
          ? pathname === "/"
          : pathname === href || pathname.startsWith(`${href}/`);

      if (matches && (best === null || href.length > best.length)) {
        best = href;
      }
    }
  }

  return best;
}

export function AppShell({
  children,
  userEmail,
  organizationName,
  unreadNotificationCount = 0,
  notifications = [],
}: {
  children: React.ReactNode;
  userEmail: string | null;
  organizationName: string | null;
  unreadNotificationCount?: number;
  notifications?: NotificationItem[];
}) {
  const [drawerOpen, setDrawerOpen] = useState(false);
  const pathname = usePathname();

  // The attribute on <html> is the state, and this subscribes to it.
  //
  // Not useState-plus-an-effect, which is the obvious shape and the wrong
  // one: the width is already decided by the time React runs, so copying it
  // into state in an effect means rendering once with the wrong value and
  // then again with the right one — the cascading render the lint rule
  // objects to, and a visible flicker besides.
  //
  // useSyncExternalStore is built for exactly this. The server snapshot is
  // "expanded", which is what the server rendered; React swaps in the real
  // one after hydration without complaining that they differ.
  const isCollapsed = useSyncExternalStore(
    subscribeToSidebarAttribute,
    () => document.documentElement.getAttribute("data-sidebar") === "collapsed",
    () => false,
  );

  const toggleCollapsed = useCallback(() => {
    const next = !(
      document.documentElement.getAttribute("data-sidebar") === "collapsed"
    );

    // Writing the attribute is what re-renders this component, via the
    // observer in subscribeToSidebarAttribute. There is no second copy of
    // the state to keep in step.
    if (next) {
      document.documentElement.setAttribute("data-sidebar", "collapsed");
    } else {
      document.documentElement.removeAttribute("data-sidebar");
    }

    // Best effort. A browser blocking site data should still get a sidebar
    // that collapses for this visit; it just will not be remembered.
    try {
      localStorage.setItem("sidebar", next ? "collapsed" : "expanded");
    } catch {
      // Nothing to do, and nothing worth telling anybody about.
    }
  }, []);

  // Ctrl/Cmd+B, the same shortcut every editor with a sidebar uses. Ignored
  // while typing, because a comment box is not a place for a chord to mean
  // something else.
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.key !== "b" || !(event.metaKey || event.ctrlKey)) {
        return;
      }

      const target = event.target as HTMLElement | null;

      if (
        target?.isContentEditable
        || ["INPUT", "TEXTAREA", "SELECT"].includes(target?.tagName ?? "")
      ) {
        return;
      }

      event.preventDefault();
      toggleCollapsed();
    }

    document.addEventListener("keydown", onKeyDown);

    return () => document.removeEventListener("keydown", onKeyDown);
  }, [toggleCollapsed]);

  return (
    <div className="min-h-dvh">
      {/*
        A skip link, first in the tab order. With twelve navigation items this
        is the difference between reaching the content in one key press and in
        thirteen.
      */}
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:fixed focus:left-4 focus:top-4 focus:z-[60]
                   focus:rounded-control focus:bg-surface focus:px-4 focus:py-2
                   focus:text-sm focus:font-medium focus:shadow-floating"
      >
        Skip to content
      </a>

      <Sidebar
        pathname={pathname}
        organizationName={organizationName}
        className="hidden lg:flex"
        isCollapsed={isCollapsed}
        onToggleCollapsed={toggleCollapsed}
      />

      {drawerOpen ? (
        <>
          <div
            className="fixed inset-0 z-40 bg-ink/35 lg:hidden"
            onClick={() => setDrawerOpen(false)}
            aria-hidden="true"
          />
          <Sidebar
            pathname={pathname}
            organizationName={organizationName}
            className="z-50 flex lg:hidden"
            onNavigate={() => setDrawerOpen(false)}
          />
        </>
      ) : null}

      <div data-rail-content className="lg:pl-sidebar">
        <header
          className="sticky top-0 z-30 flex h-14 items-center gap-3 border-b border-line
                     bg-surface/90 px-4 backdrop-blur-sm sm:px-6"
        >
          <button
            type="button"
            onClick={() => setDrawerOpen((open) => !open)}
            className="-ml-1 inline-flex size-9 items-center justify-center rounded-control
                       text-ink-secondary hover:bg-surface-sunken hover:text-ink lg:hidden"
            aria-expanded={drawerOpen}
            aria-label={drawerOpen ? "Close navigation" : "Open navigation"}
          >
            {drawerOpen ? <X className="size-5" /> : <Menu className="size-5" />}
          </button>

          <span className="truncate text-[14px] font-semibold lg:hidden">
            LeaseReminder
          </span>

          <div className="ml-auto flex items-center gap-1.5">
            {/* Server-rendered initial state, so the badge is correct on first
                paint with no effect and no flash of zero. */}
            <NotificationBell
              initialUnreadCount={unreadNotificationCount}
              initialNotifications={notifications}
            />

            <AccountMenu userEmail={userEmail} />
          </div>
        </header>

        {/*
          A restrained maximum width (section 5). The board is wider than this,
          and scrolls inside its own region; everything else is read, and an
          unbounded column on a 32-inch monitor produces rows the eye cannot
          track from one end to the other.
        */}
        <main id="main" className="mx-auto w-full max-w-[90rem] px-4 py-6 sm:px-6 lg:px-8">
          {children}
        </main>
      </div>
    </div>
  );
}

function AccountMenu({ userEmail }: { userEmail: string | null }) {
  const [isOpen, setIsOpen] = useState(false);

  return (
    <div className="relative">
      <button
        type="button"
        onClick={() => setIsOpen((open) => !open)}
        aria-expanded={isOpen}
        aria-haspopup="true"
        className="inline-flex items-center gap-1.5 rounded-control px-2 py-1.5
                   text-[13px] text-ink-secondary transition-colors
                   hover:bg-surface-sunken hover:text-ink"
      >
        <span className="hidden max-w-[16ch] truncate sm:inline">
          {userEmail ?? "Account"}
        </span>
        <ChevronDown className="size-3.5 shrink-0" aria-hidden="true" />
      </button>

      {isOpen ? (
        <>
          <div
            className="fixed inset-0 z-40"
            onClick={() => setIsOpen(false)}
            aria-hidden="true"
          />
          <div
            className="absolute right-0 top-10 z-50 w-56 overflow-hidden rounded-surface
                       border border-line bg-surface shadow-floating"
            role="menu"
          >
            {userEmail ? (
              <div className="border-b border-line px-3 py-2.5">
                <p className="truncate text-[12.5px] font-medium">{userEmail}</p>
              </div>
            ) : null}

            <Link
              href="/settings"
              onClick={() => setIsOpen(false)}
              role="menuitem"
              className="block px-3 py-2 text-[13px] hover:bg-surface-subtle"
            >
              Settings
            </Link>

            <Link
              href="/settings/notifications"
              onClick={() => setIsOpen(false)}
              role="menuitem"
              className="block px-3 py-2 text-[13px] hover:bg-surface-subtle"
            >
              Notification rules
            </Link>

            {/* POST, not a link. A GET sign-out can be triggered by any page
                that embeds it, which is cross-site request forgery; the damage
                is small but the fix is free. */}
            <form action="/auth/sign-out" method="post" className="border-t border-line">
              <button
                type="submit"
                role="menuitem"
                className="w-full px-3 py-2 text-left text-[13px] hover:bg-surface-subtle"
              >
                Sign out
              </button>
            </form>
          </div>
        </>
      ) : null}
    </div>
  );
}

/**
 * The navigation panel.
 *
 * <h3>Why it is dark</h3>
 *
 * The navigation is not content: it is the frame the content sits in. Given
 * the same white ground as the page, the two read as one sheet with a
 * hairline down it, and the eye has to find the boundary every time. Its own
 * ground draws that boundary once.
 *
 * <h3>The current page</h3>
 *
 * A filled row, and nothing else. An earlier version had a tinted pill plus a
 * 3px bar down the left edge; on the collapsed rail that bar ends up detached
 * from the icon it belongs to, sitting against the panel edge like a stray
 * mark. One signal, applied the same way at both widths, is clearer than two
 * that disagree.
 */
function Sidebar({
  pathname,
  organizationName,
  className,
  onNavigate,
  isCollapsed,
  onToggleCollapsed,
}: {
  pathname: string;
  organizationName: string | null;
  className?: string;
  onNavigate?: () => void;
  /** Absent on the mobile drawer, which does not collapse. */
  isCollapsed?: boolean;
  onToggleCollapsed?: () => void;
}) {
  const activeHref = mostSpecificMatch(pathname);
  const canCollapse = onToggleCollapsed !== undefined;

  return (
    <aside
      // Only the desktop sidebar is rail-capable. A drawer that opens as a
      // narrow strip of unlabelled icons would be useless, so the mobile copy
      // deliberately carries neither the attribute nor the toggle.
      {...(canCollapse ? { "data-rail": "" } : {})}
      className={cx(
        "fixed inset-y-0 left-0 w-sidebar flex-col bg-sidebar",
        className,
      )}
    >
      {/* px-3.5, not px-4, for the same reason the rail is 60px: it puts the
          32px badge at (60 - 32) / 2 = 14px, exactly where centring it in the
          rail would, so it holds still while the panel moves. */}
      <div className="flex h-14 shrink-0 items-center gap-2.5 px-3.5">
        <span
          className="grid size-8 shrink-0 place-items-center rounded-control bg-accent
                     text-white"
          aria-hidden="true"
        >
          <Logo className="size-[19px]" />
        </span>
        <div className="rail-hide min-w-0">
          <p className="truncate text-[14px] font-semibold leading-tight text-sidebar-ink-strong">
            LeaseReminder
          </p>
          {organizationName ? (
            <p className="truncate text-[12px] leading-tight text-sidebar-ink">
              {organizationName}
            </p>
          ) : null}
        </div>
      </div>

      <nav
        className="thin-scrollbar thin-scrollbar-dark flex-1 overflow-y-auto px-2.5 pb-3"
        aria-label="Main"
      >
        {navigation.map((group, groupIndex) => (
          <div
            key={group.label ?? `group-${groupIndex}`}
            className="nav-group mb-4 last:mb-0"
          >
            {group.label ? (
              // Sentence case, not the tracked-out capitals this had before.
              // Three stacked all-caps captions is the loudest thing in a
              // panel whose whole job is to be quiet.
              //
              // 13px, though, not the 11.5px that replaced them. Capitals are
              // all cap-height, so dropping to lower case loses the ascender
              // line and the same nominal size reads visibly smaller — the
              // caption has to come back up a step to look like it did, and
              // it is a heading rather than a footnote.
              <p className="rail-hide mb-1.5 px-2.5 text-[13px] font-semibold text-sidebar-ink">
                {group.label}
              </p>
            ) : null}

            <ul className="space-y-0.5">
              {group.items.map(({ href, label, icon: Icon }) => {
                // Only the most specific match highlights. A plain prefix test
                // lights up Settings as well as Email on /settings/email,
                // which says the sidebar does not know where you are.
                const active = href === activeHref;

                return (
                  <li key={href}>
                    <Link
                      href={href}
                      // Always a function, never undefined: exactOptionalPropertyTypes
                      // treats an explicitly-undefined prop as a type error.
                      onClick={() => onNavigate?.()}
                      // The only label left once the rail collapses, so it is
                      // set whether or not it is currently needed.
                      title={isCollapsed ? label : undefined}
                      // aria-current is what tells a screen reader which page
                      // it is on. The background colour alone says nothing.
                      aria-current={active ? "page" : undefined}
                      className={cx(
                        "nav-link flex items-center gap-3 rounded-control",
                        "px-2.5 py-2 text-[14px] transition-colors",
                        active
                          ? "bg-sidebar-active font-medium text-sidebar-ink-strong"
                          : "text-sidebar-ink hover:bg-sidebar-hover hover:text-sidebar-ink-strong",
                      )}
                    >
                      <Icon className="size-5 shrink-0" aria-hidden="true" />
                      <span className="nav-label truncate">{label}</span>
                    </Link>
                  </li>
                );
              })}
            </ul>
          </div>
        ))}
      </nav>

      {canCollapse ? (
        <div className="shrink-0 border-t border-sidebar-line p-2.5">
          <button
            type="button"
            onClick={onToggleCollapsed}
            // Not aria-expanded: this does not disclose a region, it changes
            // the width of one. A label that names the resulting state is
            // what a screen reader user actually needs here.
            aria-label={isCollapsed ? "Expand sidebar" : "Collapse sidebar"}
            title={`${isCollapsed ? "Expand" : "Collapse"} sidebar  (Ctrl+B)`}
            className="nav-link flex w-full items-center gap-3 rounded-control px-2.5
                       py-2 text-[13px] text-sidebar-ink transition-colors
                       hover:bg-sidebar-hover hover:text-sidebar-ink-strong"
          >
            <PanelLeft
              className="rail-rotate size-5 shrink-0 transition-transform"
              aria-hidden="true"
            />
            <span className="nav-label">Collapse</span>
          </button>
        </div>
      ) : null}
    </aside>
  );
}
