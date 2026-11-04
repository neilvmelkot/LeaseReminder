"use client";

import { useState, useTransition } from "react";
import { AlertTriangle, Mail, Monitor } from "lucide-react";

import { toggleNotificationRuleAction } from "@/app/maintenance/actions";
import { cx, Status } from "@/components/ui/primitives";
import type { TaskNotificationRule } from "@/lib/maintenance";

/**
 * A list of notification rules with independent switches.
 *
 * <h3>Why the recipients are shown on every row</h3>
 *
 * "Overdue" and "Overdue escalation" look like duplicates until you see that
 * the first goes to the assignee and the second goes to the manager and the
 * owner. Escalating to the same person who has already ignored two notices is
 * not an escalation, and the recipient list is the only thing on the row that
 * says so.
 *
 * <h3>Why an auto-paused rule is called out separately</h3>
 *
 * The engine pauses a rule after repeated send failures, to stop a broken
 * configuration generating hundreds of identical errors. A paused rule with an
 * "on" switch would be the most confusing possible state, so the pause and its
 * reason are shown, and re-enabling clears it.
 */
export function NotificationRuleList({
  organizationId,
  rules,
  canEdit,
}: {
  organizationId: string;
  rules: TaskNotificationRule[];
  canEdit: boolean;
}) {
  if (rules.length === 0) {
    return (
      <p className="px-5 py-6 text-center text-[13px] text-ink-secondary">
        No rules of this kind.
      </p>
    );
  }

  return (
    <ul className="divide-y divide-line">
      {rules.map((rule) => (
        <RuleRow
          key={rule.id}
          organizationId={organizationId}
          rule={rule}
          canEdit={canEdit}
        />
      ))}
    </ul>
  );
}

function RuleRow({
  organizationId,
  rule,
  canEdit,
}: {
  organizationId: string;
  rule: TaskNotificationRule;
  canEdit: boolean;
}) {
  const [enabled, setEnabled] = useState(rule.enabled);
  const [error, setError] = useState<string | null>(null);
  const [isPending, startTransition] = useTransition();

  function toggle() {
    const next = !enabled;
    const previous = enabled;

    // Optimistic: a switch that waits for a round trip feels broken.
    setEnabled(next);
    setError(null);

    startTransition(async () => {
      const result = await toggleNotificationRuleAction(organizationId, rule.id, next);

      if (!result.ok) {
        setEnabled(previous);
        setError(result.error ?? "The rule could not be changed.");
      }
    });
  }

  const isPaused = rule.pausedAt !== null && enabled;

  return (
    <li className="px-5 py-3.5">
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <h3 className="text-[13.5px] font-semibold">{rule.name}</h3>

            {rule.propertyId ? (
              <Status tone="info">This property only</Status>
            ) : null}

            {isPaused ? (
              <Status tone="warning">Auto-paused</Status>
            ) : null}
          </div>

          {/* Composed server-side, so the wording is identical wherever a rule
              is shown and the client never has to reassemble trigger, offset,
              and repeat into prose. */}
          <p className="mt-0.5 text-[12.5px] leading-relaxed text-ink-secondary">
            {rule.summary}
          </p>

          <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1">
            <span className="flex items-center gap-1 text-[11.5px] text-ink-muted">
              {rule.channels.includes("email") ? (
                <Mail className="size-3" aria-hidden="true" />
              ) : null}
              {rule.channels.includes("inApp") ? (
                <Monitor className="size-3" aria-hidden="true" />
              ) : null}
              {rule.channels
                .map((channel) => (channel === "email" ? "Email" : "In-app"))
                .join(" + ")}
            </span>

            {rule.recipientRoles.length > 0 ? (
              <span className="text-[11.5px] text-ink-muted">
                Notifies{" "}
                {rule.recipientRoles.map(describeRole).join(", ")}
              </span>
            ) : (
              // A rule with no recipients fires and reaches nobody. Worth
              // saying out loud, because the switch looks fine.
              <span className="text-[11.5px] font-medium text-warning-text">
                No recipients configured, so this reaches nobody
              </span>
            )}
          </div>

          {isPaused && rule.pausedReason ? (
            <p
              className="mt-2 flex items-start gap-1.5 rounded-control bg-warning-tint
                         px-2 py-1.5 text-[11.5px] text-warning-text"
            >
              <AlertTriangle className="mt-0.5 size-3 shrink-0" aria-hidden="true" />
              <span>
                {rule.pausedReason} Switching this rule off and on again clears the
                pause.
              </span>
            </p>
          ) : null}

          {error ? (
            <p role="alert" className="mt-1.5 text-[11.5px] text-danger-text">
              {error}
            </p>
          ) : null}
        </div>

        {/* A real checkbox under a styled switch, so it is keyboard operable
            and announced correctly without reimplementing the semantics. */}
        <label
          className={cx(
            "relative mt-0.5 inline-flex shrink-0 items-center",
            canEdit ? "cursor-pointer" : "cursor-not-allowed opacity-60",
          )}
        >
          <input
            type="checkbox"
            checked={enabled}
            onChange={toggle}
            disabled={!canEdit || isPending}
            className="peer sr-only"
          />
          <span className="sr-only">
            {enabled ? `Disable ${rule.name}` : `Enable ${rule.name}`}
          </span>
          <span
            className={cx(
              "h-5 w-9 rounded-full transition-colors",
              "peer-focus-visible:outline peer-focus-visible:outline-2",
              "peer-focus-visible:outline-offset-2 peer-focus-visible:outline-accent",
              enabled ? "bg-accent" : "bg-line-strong",
            )}
            aria-hidden="true"
          />
          <span
            className={cx(
              "absolute left-0.5 size-4 rounded-full bg-white shadow-raised transition-transform",
              enabled && "translate-x-4",
            )}
            aria-hidden="true"
          />
        </label>
      </div>
    </li>
  );
}

/** Plain words for a recipient role. */
function describeRole(role: string): string {
  switch (role) {
    case "assignee":
      return "the assignee";
    case "taskCreator":
      return "whoever created it";
    case "reportingTenant":
      return "the reporting tenant";
    case "propertyManager":
      return "the property manager";
    case "propertyOwner":
      return "the owner";
    case "vendor":
      return "the vendor";
    case "customRecipient":
      return "a named address";
    case "recipientGroup":
      return "a recipient group";
    default:
      return role;
  }
}
