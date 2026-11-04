import "server-only";

import { getAccessToken } from "@/lib/supabase/server";
import {
  invitableRoles,
  permissions,
  roleLabel,
  type OrgRole,
} from "@/lib/roles";

/**
 * Typed client for the LeaseReminder API.
 *
 * <h3>Why the frontend never talks to the database</h3>
 *
 * It would be shorter to query Supabase directly from the browser with the
 * anon key and let Row Level Security sort it out. That would work for reads,
 * and it is a perfectly reasonable pattern for simpler applications. It is
 * avoided here because the rules this system has to enforce are not all
 * expressible as policies: the approval gate records who approved and when,
 * the audit log must be written as part of the same transaction, schedule
 * generation runs server-side, and the Gmail credential must never be
 * reachable from a browser at all.
 *
 * Routing everything through the API keeps one place where those invariants
 * hold. RLS still backs it up underneath, so this is defence in depth rather
 * than a replacement.
 */

const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:8080";

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly fieldErrors?: Record<string, string[]>,
  ) {
    super(message);
    this.name = "ApiError";
  }

  /** True when the caller is not signed in, or the session has expired. */
  get isUnauthenticated(): boolean {
    return this.status === 401;
  }

  /** True when the caller is signed in but lacks permission. */
  get isForbidden(): boolean {
    return this.status === 403;
  }
}

/**
 * The generic authenticated call.
 *
 * Exported as `apiFetch` so server actions can reach endpoints that have no
 * typed wrapper below, without each one re-implementing token attachment and
 * error classification.
 */
export async function apiFetch<T>(
  path: string,
  init?: RequestInit,
): Promise<T> {
  const token = await getAccessToken();

  const headers = new Headers(init?.headers);
  headers.set("Accept", "application/json");

  // FormData is the exception: the browser has to set the Content-Type
  // itself, because the multipart boundary is generated with the body and the
  // header has to quote it. Setting "application/json" here, or even setting
  // "multipart/form-data" without the boundary, produces a request the server
  // cannot parse.
  if (init?.body && !(init.body instanceof FormData)) {
    headers.set("Content-Type", "application/json");
  }

  if (token) {
    headers.set("Authorization", `Bearer ${token}`);
  }

  const response = await fetch(`${apiBaseUrl}${path}`, {
    ...init,
    headers,
    // Lease and tenant data must not be cached by Next's data cache: it is
    // per-user, and a cached response could be served to a different session.
    cache: "no-store",
  });

  if (!response.ok) {
    throw await toApiError(response);
  }

  // Any empty body, not just 204.
  //
  // This tested the status code alone, and 202 Accepted — which the test-send
  // endpoint returns — carries no body either. response.json() on an empty
  // body throws a SyntaxError, which is not an ApiError, so every caller's
  // catch fell through to its "the server could not be reached" branch. The
  // request had in fact succeeded: the test message was queued every time,
  // and the screen reported a network failure.
  //
  // Reading the body as text first makes the check about what actually
  // arrived rather than about which codes somebody remembered to list.
  const body = await response.text();

  if (body.length === 0) {
    return undefined as T;
  }

  return JSON.parse(body) as T;
}

async function toApiError(response: Response): Promise<ApiError> {
  // The API returns ProblemDetails with no stack trace and no internal detail
  // (section 4.F), so whatever arrives is safe to surface. Parsing is still
  // defensive, because a proxy or gateway error will not be in that shape.
  let detail = `Request failed with status ${response.status}`;
  let fieldErrors: Record<string, string[]> | undefined;

  try {
    const body = (await response.json()) as {
      title?: string;
      detail?: string;
      errors?: Record<string, string[]>;
    };

    detail = body.detail ?? body.title ?? detail;
    fieldErrors = body.errors;
  } catch {
    // Not JSON. The status-derived message above is the best available.
  }

  return new ApiError(detail, response.status, fieldErrors);
}

/* -------------------------------------------------------------------------- */
/* Types, mirroring the API contracts                                          */
/* -------------------------------------------------------------------------- */

// Re-exported so every server-side import of a role helper keeps working,
// and so there is still one name to import from for API-shaped code.
export { invitableRoles, permissions, roleLabel, type OrgRole };

export interface Organization {
  id: string;
  name: string;
  timeZone: string;
  callerRole: OrgRole;
}

export interface CurrentUser {
  userId: string;
  email: string;
  displayName: string | null;
  organizations: Organization[];
}

export interface Property {
  id: string;
  organizationId: string;
  name: string;
  propertyType: string;
  timeZone: string;
  addressLine1: string | null;
  addressLine2: string | null;
  locality: string | null;
  region: string | null;
  postalCode: string | null;
  countryCode: string;
  notes: string | null;
  unitCount: number;
  activeLeaseCount: number;
  /**
   * Non-null when the property has been withdrawn from use.
   *
   * Archived properties are excluded from the portfolio list, so this is only
   * ever set on a property fetched by id — which is how somebody reaches one
   * in order to restore it.
   */
  archivedAt: string | null;
}

export interface Unit {
  id: string;
  propertyId: string;
  unitNumber: string;
  status: string;
  bedrooms: number | null;
  bathrooms: number | null;
  squareFeet: number | null;
  notes: string | null;
  /** Null unless a lease on this unit is both approved and live. */
  activeLeaseId: string | null;
  currentTenantName: string | null;
}

/**
 * One reminder this workspace is going to send.
 *
 * <p>
 * scheduledAtUtc is the instant the dispatcher orders by; eventLocalDate is
 * the date the reminder is *about*, read in the property's own zone. They
 * routinely disagree, and showing only one of them is how somebody concludes
 * a reminder is going out on the wrong day.
 * </p>
 */
export interface UpcomingReminder {
  id: string;
  leaseId: string;
  eventType: string;
  eventLocalDate: string;
  scheduledAtUtc: string;
  localTimeZone: string;
  /** True when the local send time did not exist, or existed twice, that day. */
  dstAdjusted: boolean;
  status: string;
  propertyName: string;
  unitNumber: string | null;
  amount: number | null;
  currency: string | null;
  dueDate: string | null;
  recipientCount: number;
}

export interface RecentReminder {
  id: string;
  leaseId: string;
  eventType: string;
  eventLocalDate: string;
  status: string;
  sentAt: string | null;
  attemptCount: number;
  lastErrorCategory: string;
  lastErrorMessage: string | null;
  propertyName: string;
  unitNumber: string | null;
}

export interface ReminderSummary {
  scheduledCount: number;
  failedCount: number;
  liveLeaseCount: number;
  scheduleCount: number;
  mailboxConnected: boolean;
}

export interface ReminderBoard {
  upcoming: UpcomingReminder[];
  recent: RecentReminder[];
  summary: ReminderSummary;
}

export interface Tenant {
  id: string;
  legalName: string;
  preferredName: string | null;
  primaryEmail: string | null;
  secondaryEmail: string | null;
  phone: string | null;
  status: string;
  notificationsOptedIn: boolean;
  notes: string | null;
  leaseCount: number;
}

/**
 * Three independent statuses, exactly as the API reports them (section 13.E).
 * The UI shows all three rather than deriving one summary, because a lease can
 * be approved and active with its automation paused, and collapsing that into
 * a single word would hide the thing the user most needs to know.
 */
export interface Lease {
  id: string;
  organizationId: string;
  propertyId: string;
  propertyName: string;
  unitId: string | null;
  unitNumber: string | null;
  reference: string | null;

  lifecycleStatus:
    | "draft"
    | "upcoming"
    | "active"
    | "expired"
    | "terminated"
    | "archived";
  approvalStatus: "pendingReview" | "approved" | "rejected";
  automationStatus:
    | "notConfigured"
    | "pendingActivation"
    | "active"
    | "paused"
    | "error";

  startDate: string | null;
  endDate: string | null;
  rentAmount: number | null;
  currency: string;
  securityDepositAmount: number | null;
  firstPaymentDate: string | null;
  gracePeriodDays: number | null;
  lateFeeAmount: number | null;
  lateFeeIsPercentage: boolean;
  lateFeeAfterDays: number | null;
  renewalNoticeDays: number | null;
  renewalTerms: string | null;
  earlyTerminationTerms: string | null;
  noticePeriodDays: number | null;
  paymentInstructions: string | null;
  specialClauses: string | null;
  notes: string | null;

  approvedAt: string | null;
  approvedByUserId: string | null;
  rejectedAt: string | null;
  rejectionReason: string | null;
  automationPausedAt: string | null;
  automationPausedReason: string | null;

  /** Whether reminders will actually be generated and sent. */
  canSendReminders: boolean;

  tenants: Array<{
    tenantId: string;
    legalName: string;
    preferredName: string | null;
    role: string;
    notificationsOptedIn: boolean;
  }>;
}

/* -------------------------------------------------------------------------- */
/* Endpoints                                                                   */
/* -------------------------------------------------------------------------- */

export const api = {
  currentUser: () => apiFetch<CurrentUser>("/api/v1/me"),

  createOrganization: (body: { name: string; timeZone: string }) =>
    apiFetch<Organization>("/api/v1/organizations", {
      method: "POST",
      body: JSON.stringify(body),
    }),

  /**
   * The portfolio.
   *
   * Archived properties are excluded unless asked for. They are not part of
   * the portfolio any more, but they stay reachable so one can be found and
   * restored.
   */
  properties: (organizationId: string, options?: { includeArchived?: boolean }) =>
    apiFetch<Property[]>(
      `/api/v1/organizations/${organizationId}/properties`
      + (options?.includeArchived ? "?includeArchived=true" : ""),
    ),

  property: (organizationId: string, propertyId: string) =>
    apiFetch<Property>(
      `/api/v1/organizations/${organizationId}/properties/${propertyId}`,
    ),

  /**
   * One lease.
   *
   * Returns the same shape as the list, so a detail screen and a row never
   * disagree about what a lease is.
   */
  lease: (organizationId: string, leaseId: string) =>
    apiFetch<Lease>(
      `/api/v1/organizations/${organizationId}/leases/${leaseId}`,
    ),

  reminders: (organizationId: string) =>
    apiFetch<ReminderBoard>(
      `/api/v1/organizations/${organizationId}/reminders`,
    ),

  units: (organizationId: string, propertyId: string) =>
    apiFetch<Unit[]>(
      `/api/v1/organizations/${organizationId}/properties/${propertyId}/units`,
    ),

  tenants: (organizationId: string) =>
    apiFetch<Tenant[]>(`/api/v1/organizations/${organizationId}/tenants`),

  leases: (
    organizationId: string,
    filters?: {
      approvalStatus?: string;
      lifecycleStatus?: string;
      expiringWithinDays?: number;
    },
  ) => {
    const query = new URLSearchParams();

    if (filters?.approvalStatus) {
      query.set("approvalStatus", filters.approvalStatus);
    }

    if (filters?.lifecycleStatus) {
      query.set("lifecycleStatus", filters.lifecycleStatus);
    }

    if (filters?.expiringWithinDays) {
      query.set("expiringWithinDays", String(filters.expiringWithinDays));
    }

    const suffix = query.size > 0 ? `?${query.toString()}` : "";

    return apiFetch<Lease[]>(`/api/v1/organizations/${organizationId}/leases${suffix}`);
  },
};

/* -------------------------------------------------------------------------- */
/* Maintenance module                                                          */
/*                                                                            */
/* Read calls only. Every mutation goes through a server action in             */
/* app/maintenance/actions.ts, so that a failure can be classified and shown   */
/* rather than surfacing as an opaque thrown digest in the browser.            */
/* -------------------------------------------------------------------------- */

export const maintenanceApi = {
  board: (
    organizationId: string,
    filters?: {
      propertyId?: string;
      priority?: string;
      category?: string;
      assigneeUserId?: string;
      includeCompleted?: boolean;
      /** The archive view: archived tasks, and only those. */
      archivedOnly?: boolean;
      /** Lifts the fortnight window on settled cards. */
      includeAllCompleted?: boolean;
    },
  ) => {
    const query = new URLSearchParams();

    if (filters?.propertyId) {
      query.set("propertyId", filters.propertyId);
    }

    if (filters?.priority) {
      query.set("priority", filters.priority);
    }

    if (filters?.category) {
      query.set("category", filters.category);
    }

    if (filters?.assigneeUserId) {
      query.set("assigneeUserId", filters.assigneeUserId);
    }

    if (filters?.includeCompleted === false) {
      query.set("includeCompleted", "false");
    }

    if (filters?.archivedOnly) {
      query.set("archivedOnly", "true");
    }

    if (filters?.includeAllCompleted) {
      query.set("includeAllCompleted", "true");
    }

    const suffix = query.size > 0 ? `?${query.toString()}` : "";

    return apiFetch<import("@/lib/maintenance").Board>(
      `/api/v1/organizations/${organizationId}/maintenance/board${suffix}`,
    );
  },

  task: (organizationId: string, taskId: string) =>
    apiFetch<import("@/lib/maintenance").TaskDetail>(
      `/api/v1/organizations/${organizationId}/maintenance/tasks/${taskId}`,
    ),

  vendors: (organizationId: string) =>
    apiFetch<import("@/lib/maintenance").Vendor[]>(
      `/api/v1/organizations/${organizationId}/maintenance/vendors`,
    ),

  notificationRules: (organizationId: string) =>
    apiFetch<import("@/lib/maintenance").TaskNotificationRule[]>(
      `/api/v1/organizations/${organizationId}/maintenance/notification-rules`,
    ),
};

/* -------------------------------------------------------------------------- */
/* Notification centre                                                         */
/* -------------------------------------------------------------------------- */

export const notificationApi = {
  list: (includeRead = false, limit = 50) =>
    apiFetch<import("@/lib/maintenance").NotificationList>(
      `/api/v1/notifications?includeRead=${includeRead}&limit=${limit}`,
    ),

  unreadCount: () =>
    apiFetch<{ unreadCount: number }>("/api/v1/notifications/unread-count"),
};

/* -------------------------------------------------------------------------- */
/* Lease documents                                                             */
/* -------------------------------------------------------------------------- */

export const documentApi = {
  list: (organizationId: string) =>
    apiFetch<import("@/lib/documents").LeaseDocumentSummary[]>(
      `/api/v1/organizations/${organizationId}/documents`,
    ),

  detail: (documentId: string) =>
    apiFetch<import("@/lib/documents").DocumentDetail>(
      `/api/v1/documents/${documentId}`,
    ),

  /**
   * A short-lived URL for the viewer.
   *
   * Requested separately from the document, and on demand, because the URL is
   * a bearer credential for that object with a few minutes of life. Folding it
   * into the detail response would mean every list render minted one, and a
   * stale page would hold an expired link.
   */
  viewUrl: (documentId: string) =>
    apiFetch<{ url: string; expiresAt: string }>(
      `/api/v1/documents/${documentId}/url`,
    ),

  /**
   * Uploads a file.
   *
   * Multipart, so the browser streams it rather than base64-encoding it into
   * JSON, which would inflate a 50 MB lease by a third. The Content-Type
   * header is deliberately not set: the browser has to add the multipart
   * boundary itself, and setting it by hand produces a body the server cannot
   * parse.
   */
  upload: (organizationId: string, file: File, name?: string) => {
    const body = new FormData();
    body.append("file", file);

    // Only when there is one. An empty field would be sent as "" and stored
    // as a blank label rather than left unset.
    if (name && name.trim()) {
      body.append("name", name.trim());
    }

    return apiFetch<{
      id: string;
      originalFilename: string;
      detectedMimeType: string;
      fileSizeBytes: number;
      processingStatus: string;
      automaticExtractionAvailable: boolean;
    }>(`/api/v1/organizations/${organizationId}/documents`, {
      method: "POST",
      body,
    });
  },

  reviewField: (
    fieldId: string,
    decision: "accepted" | "corrected" | "rejected",
    correctedValue?: unknown,
  ) =>
    apiFetch<void>(`/api/v1/extracted-fields/${fieldId}/review`, {
      method: "POST",
      body: JSON.stringify({
        decision,
        correctedValue:
          correctedValue === undefined ? null : JSON.stringify(correctedValue),
      }),
    }),

  /**
   * Approves a lease.
   *
   * Posted to the lease's own endpoint, not to one under /documents. There is
   * a single approval route because approval also settles the lease's
   * lifecycle and its automation state, and a second route would be a second
   * place for those rules to drift.
   */
  approveLease: (
    organizationId: string,
    leaseId: string,
    body: { termsReviewed: true; note?: string | null; extractionId?: string | null },
  ) =>
    apiFetch<unknown>(
      `/api/v1/organizations/${organizationId}/leases/${leaseId}/approve`,
      { method: "POST", body: JSON.stringify(body) },
    ),

  rejectLease: (organizationId: string, leaseId: string, reason: string) =>
    apiFetch<unknown>(
      `/api/v1/organizations/${organizationId}/leases/${leaseId}/reject`,
      { method: "POST", body: JSON.stringify({ reason }) },
    ),

  remove: (documentId: string) =>
    apiFetch<void>(`/api/v1/documents/${documentId}`, { method: "DELETE" }),
};

/* -------------------------------------------------------------------------- */
/* The connected Gmail mailbox                                                 */
/* -------------------------------------------------------------------------- */

export interface EmailAccount {
  id: string;
  emailAddress: string;
  displayName: string | null;
  connectionStatus:
    | "notConnected"
    | "connected"
    | "reauthorizationRequired"
    | "revoked"
    | "error";
  statusDetail: string | null;
  grantedScopes: string[];
  connectedAt: string | null;
  lastSuccessfulSendAt: string | null;
  lastErrorAt: string | null;
  dailySendLimit: number;
}

export interface EmailAccountState {
  account: EmailAccount | null;
  /** Whether a Google OAuth client is configured at all. */
  isOAuthConfigured: boolean;
  /** Whether a token encryption key is configured. */
  isEncryptionConfigured: boolean;
}

export interface EmailLogEntry {
  id: string;
  senderEmail: string;
  /** A JSON array, as stored. Parsed by the caller. */
  recipientsJson: string;
  subject: string;
  status: "accepted" | "failed" | "suppressed";
  errorCategory: string;
  errorMessage: string | null;
  providerMessageId: string | null;
  attemptNumber: number;
  createdAt: string;
}

export const emailApi = {
  state: (organizationId: string) =>
    apiFetch<EmailAccountState>(
      `/api/v1/organizations/${organizationId}/email-account`,
    ),

  logs: (organizationId: string, limit = 50) =>
    apiFetch<EmailLogEntry[]>(
      `/api/v1/organizations/${organizationId}/email-logs?limit=${limit}`,
    ),

  /**
   * Queues a test message to your own address.
   *
   * Queued rather than sent: the API holds no Gmail credential. The worker
   * picks it up and sends it the same way it sends a rent reminder, which is
   * the only way a test proves anything about the real path.
   */
  sendTest: (organizationId: string) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/email-account/test`,
      { method: "POST" },
    ),

  disconnect: (organizationId: string) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/email-account/disconnect`,
      { method: "POST" },
    ),

  /**
   * Where to send the browser to start the Google consent flow.
   *
   * A full-page navigation, not a fetch. Google will not be framed and will
   * not answer a cross-origin request, and rightly so: consent has to happen
   * somewhere the user can see the address bar. The API sets the PKCE cookie
   * on this request, which is why it must be a real navigation to the API's
   * own origin rather than a redirect assembled here.
   */
  /**
   * Where the Connect button points.
   *
   * This origin, not the API's. The API's authorize endpoint needs a bearer
   * token and a top-level navigation cannot carry one, so the browser goes
   * to a route handler here which attaches the token and forwards both the
   * PKCE cookie and the redirect on to Google.
   */
  authorizeUrl: (organizationId: string) =>
    `/settings/email/connect?organizationId=${organizationId}`,
};

/* -------------------------------------------------------------------------- */
/* Email templates                                                             */
/* -------------------------------------------------------------------------- */

export interface EmailTemplate {
  id: string;
  templateKey: string;
  name: string;
  eventType: string | null;
  version: number;
  status: "draft" | "published" | "archived";
  subject: string;
  htmlBody: string;
  textBody: string;
  publishedAt: string | null;
  updatedAt: string;
}

export interface EmailTemplateList {
  templates: EmailTemplate[];
  /** The placeholders a template may use. Anything else is left as written. */
  availableVariables: string[];
}

export interface TemplatePreview {
  subject: string;
  htmlBody: string;
  textBody: string;
  /** Placeholders that are not on the allowlist — almost always a typo. */
  unknownVariables: string[];
}

export const templateApi = {
  list: (organizationId: string) =>
    apiFetch<EmailTemplateList>(
      `/api/v1/organizations/${organizationId}/email-templates`,
    ),

  saveDraft: (
    organizationId: string,
    templateKey: string,
    body: { name: string; subject: string; htmlBody: string; textBody: string },
  ) =>
    apiFetch<{ templateId: string }>(
      `/api/v1/organizations/${organizationId}/email-templates/${templateKey}`,
      { method: "PUT", body: JSON.stringify(body) },
    ),

  publish: (organizationId: string, templateId: string) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/email-templates/${templateId}/publish`,
      { method: "POST" },
    ),

  discardDraft: (organizationId: string, templateId: string) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/email-templates/${templateId}`,
      { method: "DELETE" },
    ),

  preview: (
    organizationId: string,
    body: { subject: string; htmlBody: string; textBody: string },
  ) =>
    apiFetch<TemplatePreview>(
      `/api/v1/organizations/${organizationId}/email-templates/preview`,
      { method: "POST", body: JSON.stringify(body) },
    ),
};

/* -------------------------------------------------------------------------- */
/* Membership: who is in a workspace, and how they got there                   */
/* -------------------------------------------------------------------------- */

export interface Member {
  userId: string;
  email: string;
  displayName: string | null;
  role: OrgRole;
  joinedAt: string;
}

export interface Invitation {
  id: string;
  email: string;
  role: OrgRole;
  createdAt: string;
  expiresAt: string;
  acceptedAt: string | null;
  revokedAt: string | null;
}

export interface DescribedInvitation {
  isValid: boolean;
  organizationName?: string;
  email?: string;
  role?: OrgRole;
  expiresAt?: string;
}

export const membershipApi = {
  /**
   * Whether this deployment has no users at all.
   *
   * True only before the first account exists. It is what lets the very first
   * person register, since nobody is there to invite them; afterwards the only
   * way in is an invitation.
   */
  bootstrapStatus: () =>
    apiFetch<{ needsFirstUser: boolean }>("/api/v1/bootstrap"),

  members: (organizationId: string) =>
    apiFetch<Member[]>(`/api/v1/organizations/${organizationId}/members`),

  changeMemberRole: (organizationId: string, userId: string, role: OrgRole) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/members/${userId}`,
      { method: "PATCH", body: JSON.stringify({ role }) },
    ),

  removeMember: (organizationId: string, userId: string) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/members/${userId}`,
      { method: "DELETE" },
    ),

  invitations: (organizationId: string) =>
    apiFetch<Invitation[]>(`/api/v1/organizations/${organizationId}/invitations`),

  /**
   * Creates an invitation and returns its token.
   *
   * The token comes back exactly once: the server stores only its SHA-256
   * digest, so there is no endpoint that could produce it again.
   */
  invite: (
    organizationId: string,
    body: { email: string; role: OrgRole; expiresInDays?: number },
  ) =>
    apiFetch<{ invitationId: string; token: string }>(
      `/api/v1/organizations/${organizationId}/invitations`,
      { method: "POST", body: JSON.stringify(body) },
    ),

  revokeInvitation: (invitationId: string) =>
    apiFetch<void>(`/api/v1/invitations/${invitationId}`, { method: "DELETE" }),

  describeInvitation: (token: string) =>
    apiFetch<DescribedInvitation>(
      `/api/v1/invitations/describe?token=${encodeURIComponent(token)}`,
    ),

  acceptInvitation: (token: string) =>
    apiFetch<{ organizationId: string }>("/api/v1/invitations/accept", {
      method: "POST",
      body: JSON.stringify({ token }),
    }),

  updateOrganization: (
    organizationId: string,
    body: { name: string; timeZone: string },
  ) =>
    apiFetch<Organization>(`/api/v1/organizations/${organizationId}`, {
      method: "PATCH",
      body: JSON.stringify(body),
    }),
};


/* -------------------------------------------------------------------------- */
/* Rent reminder rules, and who they are addressed to                          */
/*                                                                            */
/* The tables have existed since migration 0005; until these endpoints there   */
/* was no way to write to them, which is why an approved lease with a rent     */
/* schedule and a connected mailbox still sent nothing.                        */
/* -------------------------------------------------------------------------- */

export type ReminderEventType =
  | "rentDue"
  | "gracePeriodEnd"
  | "lateFeeTrigger"
  | "leaseExpiration"
  | "renewalNoticeDeadline"
  | "rentIncrease"
  | "earlyTerminationDeadline"
  | "custom";

export type ReminderTriggerType = "before" | "on" | "after";

export type ReminderOffsetUnit = "minutes" | "hours" | "days";

export type RuleInheritanceMode =
  | "inherited"
  | "overridden"
  | "additional"
  | "disabled";

export type RecipientKind = "to" | "cc" | "bcc";

export interface Recipient {
  id: string;
  email: string;
  displayName: string | null;
  tenantId: string | null;
  tenantName: string | null;
  /**
   * Null when the address is not a tenant's.
   *
   * False is the case worth showing: the address can sit on a rule and still
   * be skipped at send time, because tenant consent is honoured by the
   * dispatcher rather than by the rule.
   */
  tenantOptedIn: boolean | null;
  active: boolean;
  groupCount: number;
  ruleCount: number;
}

export interface RecipientGroup {
  id: string;
  name: string;
  kind: string;
  description: string | null;
  recipientIds: string[];
  ruleCount: number;
}

export interface RuleTarget {
  recipientId: string | null;
  recipientGroupId: string | null;
  recipientKind: RecipientKind;
  /** The person's or group's name, so a row reads without a second lookup. */
  label: string | null;
}

export interface ReminderRule {
  id: string;
  /** global, property, or lease. Generated by the database. */
  scope: "global" | "property" | "lease";
  propertyId: string | null;
  propertyName: string | null;
  leaseId: string | null;
  inheritanceMode: RuleInheritanceMode;
  overridesRuleId: string | null;
  eventType: ReminderEventType;
  triggerType: ReminderTriggerType;
  offsetValue: number;
  offsetUnit: ReminderOffsetUnit;
  /** "09:00:00" — a wall-clock time, read in the property's zone. */
  sendLocalTime: string;
  timeZone: string | null;
  sendSeparately: boolean;
  maxSendsPerDay: number;
  description: string | null;
  enabled: boolean;
  pausedAt: string | null;
  pausedReason: string | null;
  targets: RuleTarget[];
}

export interface SaveReminderRuleInput {
  propertyId?: string | null;
  leaseId?: string | null;
  inheritanceMode: RuleInheritanceMode;
  overridesRuleId?: string | null;
  eventType: ReminderEventType;
  triggerType: ReminderTriggerType;
  offsetValue: number;
  offsetUnit: ReminderOffsetUnit;
  sendLocalTime: string;
  timeZone?: string | null;
  sendSeparately: boolean;
  maxSendsPerDay?: number | null;
  description?: string | null;
  enabled: boolean;
  targets: Array<{
    recipientId: string | null;
    recipientGroupId: string | null;
    recipientKind: RecipientKind;
  }>;
}

export const reminderRuleApi = {
  recipients: (organizationId: string) =>
    apiFetch<Recipient[]>(`/api/v1/organizations/${organizationId}/recipients`),

  createRecipient: (
    organizationId: string,
    body: { email: string; displayName?: string | null; tenantId?: string | null },
  ) =>
    apiFetch<{ id: string }>(`/api/v1/organizations/${organizationId}/recipients`, {
      method: "POST",
      body: JSON.stringify(body),
    }),

  updateRecipient: (
    organizationId: string,
    recipientId: string,
    body: {
      email: string;
      displayName?: string | null;
      tenantId?: string | null;
      active?: boolean;
    },
  ) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/recipients/${recipientId}`,
      { method: "PATCH", body: JSON.stringify(body) },
    ),

  deleteRecipient: (organizationId: string, recipientId: string) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/recipients/${recipientId}`,
      { method: "DELETE" },
    ),

  groups: (organizationId: string) =>
    apiFetch<RecipientGroup[]>(
      `/api/v1/organizations/${organizationId}/recipient-groups`,
    ),

  createGroup: (
    organizationId: string,
    body: {
      name: string;
      kind?: string;
      description?: string | null;
      recipientIds: string[];
    },
  ) =>
    apiFetch<{ id: string }>(
      `/api/v1/organizations/${organizationId}/recipient-groups`,
      { method: "POST", body: JSON.stringify(body) },
    ),

  updateGroup: (
    organizationId: string,
    groupId: string,
    body: {
      name: string;
      kind?: string;
      description?: string | null;
      recipientIds: string[];
    },
  ) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/recipient-groups/${groupId}`,
      { method: "PUT", body: JSON.stringify(body) },
    ),

  deleteGroup: (organizationId: string, groupId: string) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/recipient-groups/${groupId}`,
      { method: "DELETE" },
    ),

  /**
   * The rules in force.
   *
   * Asking about a lease returns the global and property rules it inherits
   * as well as its own, because "what will fire for this lease" is the
   * question — a list of only its own rules reads as "nothing is set up" for
   * a lease covered perfectly well from above.
   */
  rules: (
    organizationId: string,
    filters?: { propertyId?: string; leaseId?: string },
  ) => {
    const query = new URLSearchParams();

    if (filters?.propertyId) {
      query.set("propertyId", filters.propertyId);
    }

    if (filters?.leaseId) {
      query.set("leaseId", filters.leaseId);
    }

    const suffix = query.size > 0 ? `?${query.toString()}` : "";

    return apiFetch<ReminderRule[]>(
      `/api/v1/organizations/${organizationId}/reminder-rules${suffix}`,
    );
  },

  createRule: (organizationId: string, body: SaveReminderRuleInput) =>
    apiFetch<{ id: string }>(
      `/api/v1/organizations/${organizationId}/reminder-rules`,
      { method: "POST", body: JSON.stringify(body) },
    ),

  updateRule: (
    organizationId: string,
    ruleId: string,
    body: SaveReminderRuleInput,
  ) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/reminder-rules/${ruleId}`,
      { method: "PUT", body: JSON.stringify(body) },
    ),

  deleteRule: (organizationId: string, ruleId: string) =>
    apiFetch<void>(
      `/api/v1/organizations/${organizationId}/reminder-rules/${ruleId}`,
      { method: "DELETE" },
    ),
};
