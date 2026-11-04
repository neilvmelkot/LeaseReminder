"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Mail, Send, ShieldAlert, TriangleAlert, Unplug } from "lucide-react";

import { Button, Panel, Status } from "@/components/ui/primitives";
import { PermissionNotice } from "@/components/ui/permission-notice";
import type { EmailAccountState } from "@/lib/api";

import {
  disconnectMailboxAction,
  sendTestEmailAction,
} from "@/app/settings/email/actions";

/**
 * The connected mailbox.
 *
 * <h3>Why connecting is a link and not a button that fetches</h3>
 *
 * The browser has to navigate to Google at the top level. Google refuses to be
 * framed and will not answer a cross-origin request, which is correct: a
 * consent screen is only meaningful if the person can see the address bar it
 * is served from. The link also has to go to the API's own origin, because
 * that is where the server sets the PKCE cookie the callback needs.
 *
 * <h3>What this screen refuses to pretend</h3>
 *
 * It never shows "connected" unless the database says a credential is stored.
 * There is no optimistic state here and no local flag, because the cost of
 * being wrong is a user believing their rent reminders are going out when
 * nothing is sending.
 */
export function MailboxPanel({
  organizationId,
  state,
  authorizeUrl,
  canManage,
  permissionReason,
  connectionResult,
  connectionReason,
}: {
  organizationId: string;
  state: EmailAccountState;
  authorizeUrl: string;
  canManage: boolean;
  /** From `permissions(role).reason`. Names the caller's own role. */
  permissionReason: string | null;
  /** Set by the OAuth callback's redirect back to this page. */
  connectionResult: string | null;
  connectionReason: string | null;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const [isConfirming, setIsConfirming] = useState(false);
  const [testResult, setTestResult] = useState<string | null>(null);

  const { account, isOAuthConfigured, isEncryptionConfigured } = state;
  const isConnected = account?.connectionStatus === "connected";
  const needsReauthorization = account?.connectionStatus === "reauthorizationRequired";

  const blockedReason = !isOAuthConfigured
    ? "Google sign-in is not configured on the server. Add the OAuth client id, "
      + "secret, and redirect URI."
    : !isEncryptionConfigured
      ? "Token encryption is not configured on the server. A refresh token is never "
        + "stored unencrypted, so connecting is refused until a key is set."
      : null;

  function sendTest() {
    setError(null);
    setTestResult(null);

    startTransition(async () => {
      const result = await sendTestEmailAction(organizationId);

      if (!result.ok) {
        setError(result.error ?? "The test message could not be sent.");

        return;
      }

      // Queued, not sent. Saying "sent" here would be a claim this screen
      // cannot support: the worker has not picked it up yet, and whether
      // Google accepts it is answered in the log below.
      setTestResult(
        "Queued. It will arrive within a minute or so, and the result appears "
        + "in the log below either way.",
      );

      router.refresh();
    });
  }

  function disconnect() {
    setError(null);

    startTransition(async () => {
      const result = await disconnectMailboxAction(organizationId);

      if (!result.ok) {
        setError(result.error ?? "The mailbox could not be disconnected.");

        return;
      }

      setIsConfirming(false);
      router.refresh();
    });
  }

  return (
    <Panel
      title="Sending mailbox"
      description="Every reminder is sent from this one account. There is no fallback
                   sender, so nothing goes out while it is disconnected."
      padded
    >
      <div className="space-y-4">
        {/* The outcome of a just-completed OAuth round trip. */}
        {connectionResult === "connected" ? (
          <Banner tone="success">The mailbox is connected.</Banner>
        ) : connectionResult === "declined" ? (
          <Banner tone="warning">
            {connectionReason ?? "Access was declined on the Google consent screen."}
          </Banner>
        ) : connectionResult === "failed" ? (
          <Banner tone="danger">
            {connectionReason ?? "The mailbox could not be connected."}
          </Banner>
        ) : null}

        {blockedReason ? (
          <Banner tone="warning">{blockedReason}</Banner>
        ) : null}

        {account && isConnected ? (
          <div className="rounded-control border border-line bg-surface-subtle px-4 py-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div className="flex min-w-0 items-center gap-2">
                <Mail className="size-4 shrink-0 text-ink-muted" aria-hidden="true" />
                <span className="truncate text-[13.5px] font-medium">
                  {account.emailAddress}
                </span>
              </div>
              <Status tone="success">Connected</Status>
            </div>

            <dl className="mt-2 grid gap-x-4 gap-y-1 text-[12.5px] sm:grid-cols-2">
              <Detail label="Connected" value={formatWhen(account.connectedAt)} />
              <Detail
                label="Last successful send"
                value={formatWhen(account.lastSuccessfulSendAt) ?? "Nothing sent yet"}
              />
              <Detail
                label="Daily limit"
                value={`${account.dailySendLimit} messages`}
              />
              <Detail
                label="Permission"
                // Stated explicitly, because "connect your Gmail" sounds far
                // broader than what is actually granted.
                value={
                  account.grantedScopes.some((scope) => scope.includes("gmail.send"))
                    ? "Send only — this cannot read your mail"
                    : account.grantedScopes.join(", ") || "Unknown"
                }
              />
            </dl>
          </div>
        ) : needsReauthorization ? (
          <div className="rounded-control border border-danger/30 bg-danger/10 px-4 py-3">
            <p className="flex items-center gap-1.5 text-[13px] font-semibold text-danger-text">
              <ShieldAlert className="size-4" aria-hidden="true" />
              This mailbox needs reconnecting
            </p>
            <p className="mt-1 text-[12.5px] leading-relaxed text-ink-secondary">
              {account?.statusDetail
                ?? "Google refused the stored credential. Access may have been revoked."}{" "}
              Nothing is being sent until it is reconnected.
            </p>
          </div>
        ) : (
          <div className="rounded-control border border-line bg-surface-subtle px-4 py-3">
            <p className="text-[13px] font-medium">No mailbox is connected</p>
            <p className="mt-0.5 text-[12.5px] leading-relaxed text-ink-secondary">
              Reminders are generated and queued, but nothing is sent. Connect a
              Gmail account to start delivery.
            </p>
          </div>
        )}

        {error ? (
          <Banner tone="danger">{error}</Banner>
        ) : null}

        {testResult ? (
          <Banner tone="success">{testResult}</Banner>
        ) : null}

        {canManage ? (
          <div className="flex flex-wrap items-center gap-2">
            {/* A plain anchor, not a router link: this is a navigation to the
                API's origin, which is where the PKCE cookie is set. */}
            {!blockedReason ? (
              <a
                href={authorizeUrl}
                className="inline-flex items-center gap-1.5 rounded-control bg-accent px-3
                           py-1.5 text-[13px] font-semibold text-white hover:bg-accent/90"
              >
                <Mail className="size-4" aria-hidden="true" />
                {isConnected ? "Reconnect" : "Connect a Gmail account"}
              </a>
            ) : null}

            {isConnected ? (
              <Button variant="subtle" size="sm" disabled={isPending} onClick={sendTest}>
                <Send className="size-3.5" aria-hidden="true" />
                {isPending ? "Queueing…" : "Send me a test"}
              </Button>
            ) : null}

            {account && (isConnected || needsReauthorization) ? (
              isConfirming ? (
                <div className="reveal flex items-center gap-2">
                  <span className="text-[12.5px] text-ink-secondary">
                    Disconnect {account.emailAddress}? Queued reminders will be
                    cancelled.
                  </span>
                  <Button variant="danger" size="sm" disabled={isPending} onClick={disconnect}>
                    {isPending ? "Disconnecting…" : "Disconnect"}
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={isPending}
                    onClick={() => setIsConfirming(false)}
                  >
                    Keep it
                  </Button>
                </div>
              ) : (
                <Button variant="subtle" size="sm" onClick={() => setIsConfirming(true)}>
                  <Unplug className="size-3.5" aria-hidden="true" />
                  Disconnect
                </Button>
              )
            ) : null}
          </div>
        ) : (
          <PermissionNotice
            reason={permissionReason}
            action="Connecting or disconnecting the mailbox"
          />
        )}

        <p className="text-[12px] leading-relaxed text-ink-secondary">
          The permission requested is <span className="font-medium">send only</span>.
          This application cannot read your mail. The credential Google issues is
          encrypted before it is stored, with a key held outside the database, and
          disconnecting destroys it rather than marking it unused.
        </p>
      </div>
    </Panel>
  );
}

function Detail({ label, value }: { label: string; value: string | null }) {
  return (
    <div className="flex gap-1.5">
      <dt className="text-ink-secondary">{label}:</dt>
      <dd className="font-medium">{value ?? "—"}</dd>
    </div>
  );
}

function Banner({
  tone,
  children,
}: {
  tone: "success" | "warning" | "danger";
  children: React.ReactNode;
}) {
  const classes =
    tone === "success"
      ? "border-success/30 bg-success/10 text-success-text"
      : tone === "warning"
        ? "border-warning/40 bg-warning/10 text-warning-text"
        : "border-danger/30 bg-danger/10 text-danger-text";

  return (
    <p
      role={tone === "success" ? undefined : "alert"}
      className={`reveal flex items-start gap-1.5 rounded-control border px-3
                  py-2 text-[12.5px] leading-relaxed ${classes}`}
    >
      {tone !== "success" ? (
        <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
      ) : null}
      {children}
    </p>
  );
}

function formatWhen(iso: string | null): string | null {
  if (!iso) {
    return null;
  }

  return new Date(iso).toLocaleString();
}
