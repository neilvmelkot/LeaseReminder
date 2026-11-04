/**
 * Maintenance module types and client calls.
 *
 * <h3>Why the board is fetched on the server and mutated from the client</h3>
 *
 * The board page is a server component: it renders with data already in hand,
 * so the first paint has no spinner and no waterfall. Drag-and-drop, though,
 * has to feel instant, so the board itself is a client component that holds
 * the card positions in state, moves a card optimistically, and calls the API
 * in the background.
 *
 * That means two different call paths, and they are separated deliberately:
 * `fetchBoard` runs on the server with the session cookie, while the mutations
 * below run in the browser against a route handler that forwards the request
 * with the caller's token. Letting the browser call the API directly would
 * mean shipping the access token into client JavaScript, which is exactly what
 * the server-only Supabase module exists to prevent.
 */

export type TaskPriority = "urgent" | "high" | "medium" | "low";

export type TaskStatusCategory =
  | "backlog"
  | "new"
  | "assigned"
  | "inProgress"
  | "blocked"
  | "review"
  | "completed"
  | "cancelled";

export type TaskCategory =
  | "plumbing"
  | "electrical"
  | "hvac"
  | "appliance"
  | "structural"
  | "roofing"
  | "pest"
  | "landscaping"
  | "cleaning"
  | "safety"
  | "inspection"
  | "turnover"
  | "administrative"
  | "other";

export type TaskSource =
  | "tenantReport"
  | "inspection"
  | "preventiveMaintenance"
  | "staff"
  | "vendor"
  | "system";

export interface TaskCard {
  id: string;
  referenceNumber: number;
  title: string;
  taskColumnId: string;
  statusCategory: TaskStatusCategory;
  position: number;
  priority: TaskPriority;
  category: TaskCategory;
  propertyId: string;
  propertyName: string;
  unitNumber: string | null;
  dueDate: string | null;

  /**
   * Computed server-side in the property's own time zone. The browser cannot
   * work this out: it knows only the viewer's zone, and a task in Los Angeles
   * is not overdue because it has gone midnight in London.
   */
  isOverdue: boolean;
  daysUntilDue: number | null;

  assigneeNames: string[];
  commentCount: number;
  attachmentCount: number;
  hasSubtasks: boolean;
  vendorName: string | null;
}

export interface BoardColumn {
  id: string;
  name: string;
  category: TaskStatusCategory;
  position: number;
  colour: string | null;
  collapsedByDefault: boolean;
  workInProgressLimit: number | null;
  taskCount: number;
  overdueCount: number;
  urgentCount: number;
  tasks: TaskCard[];
}

export interface Board {
  organizationId: string;
  propertyId: string | null;
  propertyName: string | null;
  openTaskCount: number;
  overdueTaskCount: number;
  columns: BoardColumn[];
  /**
   * Completed or cancelled cards too old to be shown on the board.
   *
   * The board keeps settled cards for a fortnight and then drops them, so the
   * Done column stays a column rather than becoming a filing cabinet. This is
   * what it is leaving out — reported so the board can say so, because hiding
   * rows silently is how somebody concludes a task was lost.
   */
  hiddenCompletedCount: number;
}

export interface TaskAssignee {
  userId: string | null;
  vendorId: string | null;
  displayName: string;
  isLead: boolean;
  assignedAt: string;
}

export interface TaskComment {
  id: string;
  authorDisplayName: string;
  body: string;
  isInternal: boolean;
  createdAt: string;
}

export interface TaskActivity {
  id: number;
  actorDisplayName: string;
  actorKind: string;
  activityType: string;
  summary: string;
  createdAt: string;
}

export interface TaskDetail {
  id: string;
  referenceNumber: number;
  title: string;
  description: string | null;
  taskColumnId: string;
  columnName: string;
  statusCategory: TaskStatusCategory;
  priority: TaskPriority;
  category: TaskCategory;
  source: TaskSource;
  propertyId: string;
  propertyName: string;
  unitId: string | null;
  unitNumber: string | null;
  reportedByTenantName: string | null;
  dueDate: string | null;
  isOverdue: boolean;
  daysUntilDue: number | null;
  startedAt: string | null;
  completedAt: string | null;
  estimatedCost: number | null;
  actualCost: number | null;
  currency: string;
  vendorId: string | null;
  vendorName: string | null;
  parentTaskId: string | null;
  parentTaskTitle: string | null;
  recurrenceIntervalDays: number | null;
  /**
   * Set when the task has been withdrawn from the board.
   *
   * Archiving keeps everything the task carries and is reversible, which is
   * why there is no delete.
   */
  archivedAt: string | null;
  lastActivityAt: string;
  createdAt: string;
  assignees: TaskAssignee[];
  comments: TaskComment[];
  activity: TaskActivity[];
  subtasks: Array<{
    id: string;
    referenceNumber: number;
    title: string;
    statusCategory: TaskStatusCategory;
  }>;
}

export interface Vendor {
  id: string;
  name: string;
  company: string | null;
  email: string | null;
  phone: string | null;
  trades: TaskCategory[];
  hourlyRate: number | null;
  currency: string;
  active: boolean;
  insuranceExpiresOn: string | null;
  insuranceExpiringSoon: boolean;
  openTaskCount: number;
}

export type TaskRuleTrigger = "event" | "schedule";

export interface TaskNotificationRule {
  id: string;
  propertyId: string | null;
  name: string;
  triggerKind: TaskRuleTrigger;
  eventType: string | null;
  scheduleBasis: string | null;
  scheduleOffsetDays: number | null;
  repeatDaily: boolean;
  maxRepeats: number | null;
  onlyPriorities: TaskPriority[] | null;
  channels: Array<"email" | "inApp">;
  sendLocalTime: string | null;
  enabled: boolean;
  pausedAt: string | null;
  pausedReason: string | null;

  /** A one-line restatement, composed server-side so the wording is consistent. */
  summary: string;

  recipientRoles: string[];
}

export interface NotificationItem {
  id: string;
  subjectType: string;
  subjectId: string | null;
  title: string;
  body: string;
  linkPath: string | null;
  recipientRole: string | null;
  readAt: string | null;
  createdAt: string;
}

export interface NotificationList {
  unreadCount: number;
  notifications: NotificationItem[];
}

/* ========================================================================== */
/* Presentation helpers                                                        */
/* ========================================================================== */

const categoryLabels: Record<TaskCategory, string> = {
  plumbing: "Plumbing",
  electrical: "Electrical",
  hvac: "HVAC",
  appliance: "Appliance",
  structural: "Structural",
  roofing: "Roofing",
  pest: "Pest control",
  landscaping: "Landscaping",
  cleaning: "Cleaning",
  safety: "Safety",
  inspection: "Inspection",
  turnover: "Turnover",
  administrative: "Administrative",
  other: "Other",
};

export function categoryLabel(category: TaskCategory): string {
  return categoryLabels[category] ?? category;
}

const sourceLabels: Record<TaskSource, string> = {
  tenantReport: "Reported by tenant",
  inspection: "From an inspection",
  preventiveMaintenance: "Preventive maintenance",
  staff: "Raised by staff",
  vendor: "Raised by a vendor",
  system: "Created automatically",
};

export function sourceLabel(source: TaskSource): string {
  return sourceLabels[source] ?? source;
}

const statusLabels: Record<TaskStatusCategory, string> = {
  backlog: "Backlog",
  new: "New",
  assigned: "Assigned",
  inProgress: "In progress",
  blocked: "Blocked",
  review: "In review",
  completed: "Completed",
  cancelled: "Cancelled",
};

export function statusLabel(category: TaskStatusCategory): string {
  return statusLabels[category] ?? category;
}

/**
 * How a due date should read on a card.
 *
 * <p>
 * The relative phrasing is the point. "Due 2 Nov" requires the reader to work
 * out whether that is soon; "Due tomorrow" and "4 days overdue" do not. The
 * absolute date is still shown on the detail view, where precision matters
 * more than speed of reading.
 * </p>
 *
 * <p>
 * `daysUntilDue` is computed server-side in the property's zone, so this
 * function never has to touch a clock and cannot disagree with the overdue
 * flag beside it.
 * </p>
 */
export function dueDateLabel(
  card: Pick<TaskCard, "dueDate" | "daysUntilDue" | "isOverdue">,
): { text: string; tone: "neutral" | "warning" | "danger" } | null {
  if (!card.dueDate || card.daysUntilDue === null) {
    return null;
  }

  const days = card.daysUntilDue;

  if (card.isOverdue) {
    const overdueBy = Math.abs(days);

    return {
      text:
        overdueBy === 0
          ? "Overdue"
          : `${overdueBy} ${overdueBy === 1 ? "day" : "days"} overdue`,
      tone: "danger",
    };
  }

  // Past, but not flagged overdue: the task has been completed or cancelled,
  // so the server stopped counting it against the deadline. Without this guard
  // the "days <= 7" branch below matches every negative number and renders
  // "Due in -10 days" in urgency amber on a task that is already finished.
  // The date is historical now, so it is shown plainly.
  if (days < 0) {
    return { text: formatShortDate(card.dueDate), tone: "neutral" };
  }

  if (days === 0) {
    return { text: "Due today", tone: "warning" };
  }

  if (days === 1) {
    return { text: "Due tomorrow", tone: "warning" };
  }

  if (days <= 7) {
    return { text: `Due in ${days} days`, tone: "warning" };
  }

  return { text: formatShortDate(card.dueDate), tone: "neutral" };
}

/**
 * Formats an ISO date-only string as "2 Nov".
 *
 * Parsed from its parts rather than through `new Date()`, which would treat
 * "2026-11-02" as midnight UTC and render 1 November for any viewer west of
 * Greenwich. For a maintenance due date that is a wrong day on a card.
 */
export function formatShortDate(isoDate: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(isoDate);

  if (!match) {
    return isoDate;
  }

  const months = [
    "Jan", "Feb", "Mar", "Apr", "May", "Jun",
    "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
  ] as const;

  const month = months[Number(match[2]) - 1] ?? match[2];

  return `${Number(match[3])} ${month}`;
}

/** Formats an instant as a short relative time, for timelines and the inbox. */
export function relativeTime(isoInstant: string): string {
  const then = new Date(isoInstant).getTime();

  if (Number.isNaN(then)) {
    return "";
  }

  const seconds = Math.round((Date.now() - then) / 1000);

  if (seconds < 60) {
    return "just now";
  }

  const minutes = Math.round(seconds / 60);
  if (minutes < 60) {
    return `${minutes}m ago`;
  }

  const hours = Math.round(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }

  const days = Math.round(hours / 24);
  if (days < 7) {
    return `${days}d ago`;
  }

  const weeks = Math.round(days / 7);
  if (weeks < 5) {
    return `${weeks}w ago`;
  }

  return new Date(isoInstant).toLocaleDateString("en-US", {
    day: "numeric",
    month: "short",
    year: "numeric",
  });
}
