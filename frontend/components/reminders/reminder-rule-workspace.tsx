"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { AlertTriangle, Pencil, Plus, Trash2, Users } from "lucide-react";

import { Button, EmptyState, Panel, Status, cx } from "@/components/ui/primitives";
import { Field, FormDialog, fieldClass } from "@/components/ui/form-dialog";
import { PermissionNotice } from "@/components/ui/permission-notice";
import { PillTabs } from "@/components/ui/pill-tabs";
import type {
  Property,
  Recipient,
  RecipientGroup,
  ReminderEventType,
  ReminderOffsetUnit,
  ReminderRule,
  ReminderTriggerType,
  RuleInheritanceMode,
  SaveReminderRuleInput,
  Tenant,
} from "@/lib/api";
import {
  describeRule,
  eventOptions,
  eventTitle,
  formatSendTime,
  inheritanceOptions,
  offsetUnitOptions,
  ruleWarning,
  scopeLabel,
  triggerOptions,
} from "@/lib/reminder-rules";

import {
  deleteGroupAction,
  deleteRecipientAction,
  deleteRuleAction,
  saveGroupAction,
  saveRecipientAction,
  saveRuleAction,
} from "@/app/settings/reminders/actions";

/**
 * Who rent reminders go to, and when.
 *
 * <h3>Why rules and recipients live on one screen</h3>
 *
 * They are useless apart. A rule with no recipients sends to nobody, and a
 * recipient nothing points at receives nothing — and both states look exactly
 * like a working setup from a distance. Keeping them on one screen with the
 * counts visible is what makes "I set this up and nothing happened" hard to
 * reach by accident.
 *
 * <h3>Three levels, one list</h3>
 *
 * A rule sits at the workspace, a property, or a lease. The list shows all of
 * them together with the level on the row, broadest first, because the
 * question is "what will send" rather than "what did I write down here" —
 * and a lease is covered by everything above it.
 */
export function ReminderRuleWorkspace({
  organizationId,
  rules,
  recipients,
  groups,
  properties,
  tenants,
  canAdminister,
  permissionReason,
}: {
  organizationId: string;
  rules: ReminderRule[];
  recipients: Recipient[];
  groups: RecipientGroup[];
  properties: Property[];
  tenants: Tenant[];
  // One role for the whole screen. Recipients used to sit at the write tier
  // and rules at administrator, which let a property manager empty a rule by
  // deleting its recipient.
  canAdminister: boolean;
  permissionReason: string | null;
}) {
  const [tab, setTab] = useState<"rules" | "people">("rules");

  return (
    <div>
      <div className="mb-4">
        <PillTabs
          ariaLabel="Reminder settings"
          activeKey={tab}
          onSelect={setTab}
          size="sm"
          tabs={[
            { key: "rules", label: `Rules (${rules.length})` },
            {
              key: "people",
              label: `Recipients (${recipients.length})`,
              icon: <Users className="size-3.5" />,
            },
          ]}
        />
      </div>

      {tab === "rules" ? (
        <RulesTab
          organizationId={organizationId}
          rules={rules}
          recipients={recipients}
          groups={groups}
          properties={properties}
          canAdminister={canAdminister}
          permissionReason={permissionReason}
        />
      ) : (
        <PeopleTab
          organizationId={organizationId}
          recipients={recipients}
          groups={groups}
          tenants={tenants}
          canAdminister={canAdminister}
          permissionReason={permissionReason}
        />
      )}
    </div>
  );
}

/* ========================================================================== */
/* Rules                                                                       */
/* ========================================================================== */

function RulesTab({
  organizationId,
  rules,
  recipients,
  groups,
  properties,
  canAdminister,
  permissionReason,
}: {
  organizationId: string;
  rules: ReminderRule[];
  recipients: Recipient[];
  groups: RecipientGroup[];
  properties: Property[];
  canAdminister: boolean;
  permissionReason: string | null;
}) {
  const router = useRouter();
  const [editing, setEditing] = useState<ReminderRule | null | "new">(null);
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);

  function remove(rule: ReminderRule) {
    setError(null);

    startTransition(async () => {
      const result = await deleteRuleAction(organizationId, rule.id);

      if (!result.ok) {
        setError(result.error ?? "That rule could not be removed.");

        return;
      }

      router.refresh();
    });
  }

  return (
    <Panel
      title="Reminder rules"
      description="When a reminder goes out, and who receives it. A rule set on the
                   workspace covers every property unless a property or lease
                   overrides it."
      action={
        canAdminister ? (
          <Button variant="primary" size="sm" onClick={() => setEditing("new")}>
            <Plus className="size-3.5" aria-hidden="true" />
            New rule
          </Button>
        ) : null
      }
    >
      {!canAdminister ? (
        <div className="px-5 pt-4">
          <PermissionNotice reason={permissionReason} />
        </div>
      ) : null}

      {error ? (
        <p
          role="alert"
          className="mx-5 mt-4 flex items-start gap-1.5 rounded-control border
                     border-danger/30 bg-danger/10 px-3 py-2 text-[12.5px] text-danger-text"
        >
          <AlertTriangle className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          {error}
        </p>
      ) : null}

      {rules.length === 0 ? (
        <EmptyState
          title="No reminder rules yet"
          description="Until there is at least one rule, no rent reminder is sent — an
                       approved lease with a schedule and a connected mailbox still
                       needs something to say when to send and to whom."
          action={
            canAdminister ? (
              <Button variant="primary" size="sm" onClick={() => setEditing("new")}>
                <Plus className="size-3.5" aria-hidden="true" />
                Create the first rule
              </Button>
            ) : null
          }
        />
      ) : (
        <ul className="divide-y divide-line">
          {rules.map((rule) => {
            const warning = ruleWarning(rule);

            return (
              <li key={rule.id} className="flex items-start gap-3 px-5 py-3.5">
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-[13.5px] font-medium">
                      {describeRule(rule)}
                    </span>

                    <Status tone={rule.scope === "global" ? "neutral" : "info"}>
                      {scopeLabel(rule)}
                    </Status>

                    {rule.sendSeparately ? (
                      <span className="text-[11.5px] text-ink-muted">
                        separate messages
                      </span>
                    ) : null}
                  </div>

                  <p className="mt-1 text-[12.5px] text-ink-secondary">
                    {rule.targets.length === 0 ? (
                      <span className="text-warning-text">No recipients</span>
                    ) : (
                      rule.targets
                        .map(
                          (target) =>
                            `${target.label ?? "Unknown"}`
                            + (target.recipientKind === "to"
                              ? ""
                              : ` (${target.recipientKind})`),
                        )
                        .join(", ")
                    )}
                  </p>

                  {rule.description ? (
                    <p className="mt-0.5 text-[12px] text-ink-muted">
                      {rule.description}
                    </p>
                  ) : null}

                  {warning ? (
                    <p className="mt-1 text-[12px] text-warning-text">{warning}</p>
                  ) : null}
                </div>

                {canAdminister ? (
                  <div className="flex shrink-0 items-center gap-1">
                    <Button
                      variant="ghost"
                      size="sm"
                      disabled={isPending}
                      onClick={() => setEditing(rule)}
                    >
                      <Pencil className="size-3.5" aria-hidden="true" />
                      Edit
                    </Button>

                    <button
                      type="button"
                      disabled={isPending}
                      onClick={() => remove(rule)}
                      title="Remove this rule"
                      className="inline-flex size-7 items-center justify-center rounded-control
                                 text-ink-muted transition-colors
                                 hover:bg-danger-tint hover:text-danger-text"
                    >
                      <Trash2 className="size-3.5" aria-hidden="true" />
                      <span className="sr-only">Remove rule</span>
                    </button>
                  </div>
                ) : null}
              </li>
            );
          })}
        </ul>
      )}

      {editing ? (
        <RuleDialog
          organizationId={organizationId}
          rule={editing === "new" ? null : editing}
          recipients={recipients}
          groups={groups}
          properties={properties}
          existingRules={rules}
          onClose={() => setEditing(null)}
        />
      ) : null}
    </Panel>
  );
}

function RuleDialog({
  organizationId,
  rule,
  recipients,
  groups,
  properties,
  existingRules,
  onClose,
}: {
  organizationId: string;
  rule: ReminderRule | null;
  recipients: Recipient[];
  groups: RecipientGroup[];
  properties: Property[];
  existingRules: ReminderRule[];
  onClose: () => void;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);

  // "global" | a property id. A lease-level rule is created from the lease's
  // own page, where the lease is already in hand; offering a lease picker of
  // every lease in the workspace here would be a long list answering a
  // question nobody asked from this screen.
  const [scope, setScope] = useState<string>(
    rule?.leaseId ? "lease" : (rule?.propertyId ?? "global"),
  );
  const [eventType, setEventType] = useState<ReminderEventType>(
    rule?.eventType ?? "rentDue",
  );
  const [triggerType, setTriggerType] = useState<ReminderTriggerType>(
    rule?.triggerType ?? "before",
  );
  const [offsetValue, setOffsetValue] = useState(String(rule?.offsetValue ?? 3));
  const [offsetUnit, setOffsetUnit] = useState<ReminderOffsetUnit>(
    rule?.offsetUnit ?? "days",
  );
  const [sendTime, setSendTime] = useState(
    formatSendTime(rule?.sendLocalTime ?? "09:00"),
  );
  const [inheritance, setInheritance] = useState<RuleInheritanceMode>(
    rule?.inheritanceMode ?? "additional",
  );
  const [overrides, setOverrides] = useState(rule?.overridesRuleId ?? "");
  const [sendSeparately, setSendSeparately] = useState(rule?.sendSeparately ?? true);
  const [enabled, setEnabled] = useState(rule?.enabled ?? true);
  const [description, setDescription] = useState(rule?.description ?? "");
  const [targets, setTargets] = useState<
    Array<{ recipientId: string | null; recipientGroupId: string | null; recipientKind: "to" | "cc" | "bcc" }>
  >(
    rule?.targets.map((target) => ({
      recipientId: target.recipientId,
      recipientGroupId: target.recipientGroupId,
      recipientKind: target.recipientKind,
    })) ?? [],
  );

  function toggleTarget(id: string, isGroup: boolean) {
    setTargets((current) => {
      const existing = current.find((target) =>
        isGroup ? target.recipientGroupId === id : target.recipientId === id,
      );

      if (existing) {
        return current.filter((target) => target !== existing);
      }

      return [
        ...current,
        {
          recipientId: isGroup ? null : id,
          recipientGroupId: isGroup ? id : null,
          recipientKind: "to" as const,
        },
      ];
    });
  }

  function submit() {
    setError(null);

    const offset = triggerType === "on" ? 0 : Number(offsetValue);

    if (triggerType !== "on" && (!Number.isFinite(offset) || offset <= 0)) {
      setError("Say how far before or after the event this should send.");

      return;
    }

    const input: SaveReminderRuleInput = {
      propertyId: scope === "global" || scope === "lease" ? null : scope,
      leaseId: rule?.leaseId ?? null,
      inheritanceMode: inheritance,
      overridesRuleId: inheritance === "additional" ? null : overrides || null,
      eventType,
      triggerType,
      offsetValue: offset,
      offsetUnit,
      // The API wants a time of day; the input gives "09:00".
      sendLocalTime: `${sendTime}:00`,
      timeZone: null,
      sendSeparately,
      maxSendsPerDay: null,
      description: description.trim() || null,
      enabled,
      targets,
    };

    startTransition(async () => {
      const result = await saveRuleAction(organizationId, rule?.id ?? null, input);

      if (!result.ok) {
        setError(result.error ?? "That rule could not be saved.");

        return;
      }

      onClose();
      router.refresh();
    });
  }

  // Only broader rules can be overridden, and only ones for the same event.
  // Offering a lease rule as the ancestor of a global one would be backwards.
  const overridable = existingRules.filter(
    (candidate) =>
      candidate.id !== rule?.id
      && candidate.eventType === eventType
      && (scope === "global" ? false : candidate.scope === "global"),
  );

  return (
    <FormDialog
      title={rule ? "Edit reminder rule" : "New reminder rule"}
      description="Nothing is sent until a rule exists, has recipients, and is switched on."
      submitLabel={rule ? "Save rule" : "Create rule"}
      isSubmitting={isPending}
      onSubmit={submit}
      onCancel={onClose}
    >
      {error ? (
        <p
          role="alert"
          className="rounded-control border border-danger/30 bg-danger/10 px-3 py-2
                     text-[12.5px] text-danger-text"
        >
          {error}
        </p>
      ) : null}

      <Field label="Applies to" htmlFor="rule-scope">
        <select
          id="rule-scope"
          value={scope}
          disabled={Boolean(rule?.leaseId)}
          onChange={(event) => setScope(event.target.value)}
          className={fieldClass}
        >
          <option value="global">Every property</option>
          {properties.map((property) => (
            <option key={property.id} value={property.id}>
              {property.name}
            </option>
          ))}
          {rule?.leaseId ? <option value="lease">This lease</option> : null}
        </select>
      </Field>

      <Field label="Send when" htmlFor="rule-event">
        <select
          id="rule-event"
          value={eventType}
          onChange={(event) => setEventType(event.target.value as ReminderEventType)}
          className={fieldClass}
        >
          {eventOptions.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </Field>

      <div className="grid grid-cols-3 gap-3">
        <Field label="Timing" htmlFor="rule-trigger">
          <select
            id="rule-trigger"
            value={triggerType}
            onChange={(event) =>
              setTriggerType(event.target.value as ReminderTriggerType)
            }
            className={fieldClass}
          >
            {triggerOptions.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </Field>

        <Field label="How far" htmlFor="rule-offset">
          <input
            id="rule-offset"
            type="number"
            min={1}
            max={365}
            value={triggerType === "on" ? "" : offsetValue}
            disabled={triggerType === "on"}
            onChange={(event) => setOffsetValue(event.target.value)}
            className={fieldClass}
          />
        </Field>

        <Field label="Unit" htmlFor="rule-unit">
          <select
            id="rule-unit"
            value={offsetUnit}
            disabled={triggerType === "on"}
            onChange={(event) =>
              setOffsetUnit(event.target.value as ReminderOffsetUnit)
            }
            className={fieldClass}
          >
            {offsetUnitOptions.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </Field>
      </div>

      <Field
        label="At"
        htmlFor="rule-time"
        hint="Read in the property's own time zone, not yours."
      >
        <input
          id="rule-time"
          type="time"
          value={sendTime}
          onChange={(event) => setSendTime(event.target.value)}
          className={fieldClass}
        />
      </Field>

      {scope !== "global" ? (
        <Field
          label="Relationship to broader rules"
          htmlFor="rule-inheritance"
          {...(() => {
            // Spread rather than passed as undefined: exactOptionalPropertyTypes
            // treats an explicitly-undefined prop as a type error.
            const hint = inheritanceOptions.find(
              (option) => option.value === inheritance,
            )?.hint;

            return hint ? { hint } : {};
          })()}
        >
          <select
            id="rule-inheritance"
            value={inheritance}
            onChange={(event) =>
              setInheritance(event.target.value as RuleInheritanceMode)
            }
            className={fieldClass}
          >
            {inheritanceOptions.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </Field>
      ) : null}

      {scope !== "global" && inheritance !== "additional" ? (
        <Field
          label="Which inherited rule"
          htmlFor="rule-overrides"
          {...(overridable.length === 0
            ? { hint: "There is no broader rule for this event to replace yet." }
            : {})}
        >
          <select
            id="rule-overrides"
            value={overrides}
            onChange={(event) => setOverrides(event.target.value)}
            className={fieldClass}
          >
            <option value="">Choose a rule…</option>
            {overridable.map((candidate) => (
              <option key={candidate.id} value={candidate.id}>
                {describeRule(candidate)}
              </option>
            ))}
          </select>
        </Field>
      ) : null}

      <fieldset>
        <legend className="block text-[13px] font-medium">Recipients</legend>
        <p className="mt-0.5 text-[12px] text-ink-secondary">
          A rule with nobody selected sends nothing.
        </p>

        {/* Add an address without leaving the dialog.

            Sending it to one specific person is the commonest thing somebody
            wants from a rule, and making them abandon a half-built rule to go
            and create a recipient first is the kind of detour that gets a
            feature called unusable. The address becomes an ordinary recipient,
            so it is reusable and appears on the Recipients tab afterwards. */}
        <QuickAddRecipient
          organizationId={organizationId}
          onAdded={(recipientId) =>
            setTargets((current) => [
              ...current,
              { recipientId, recipientGroupId: null, recipientKind: "to" as const },
            ])
          }
        />

        {recipients.length === 0 && groups.length === 0 ? (
          <p className="mt-2 rounded-control border border-line bg-surface-subtle px-3 py-2 text-[12.5px] text-ink-secondary">
            Nobody yet. Add an address above, or create recipients and groups on
            the Recipients tab.
          </p>
        ) : (
          <div className="mt-2 space-y-1.5">
            {groups.map((group) => (
              <TargetRow
                key={group.id}
                label={group.name}
                detail={`Group · ${group.recipientIds.length} ${
                  group.recipientIds.length === 1 ? "person" : "people"
                }`}
                checked={targets.some(
                  (target) => target.recipientGroupId === group.id,
                )}
                onToggle={() => toggleTarget(group.id, true)}
              />
            ))}

            {recipients.map((recipient) => (
              <TargetRow
                key={recipient.id}
                label={recipient.displayName ?? recipient.email}
                detail={
                  recipient.tenantOptedIn === false
                    ? "Tenant has opted out — will be skipped"
                    : recipient.email
                }
                warn={recipient.tenantOptedIn === false || !recipient.active}
                checked={targets.some(
                  (target) => target.recipientId === recipient.id,
                )}
                onToggle={() => toggleTarget(recipient.id, false)}
              />
            ))}
          </div>
        )}
      </fieldset>

      <Field label="Note" htmlFor="rule-description" hint="Only you see this.">
        <input
          id="rule-description"
          value={description}
          onChange={(event) => setDescription(event.target.value)}
          placeholder="Why this rule exists"
          className={fieldClass}
        />
      </Field>

      <label className="flex items-start gap-2 text-[13px]">
        <input
          type="checkbox"
          checked={sendSeparately}
          onChange={(event) => setSendSeparately(event.target.checked)}
          className="mt-0.5"
        />
        <span>
          Send a separate message to each recipient
          <span className="block text-[12px] text-ink-secondary">
            Recommended. Otherwise every recipient sees the others&apos; addresses.
          </span>
        </span>
      </label>

      <label className="flex items-center gap-2 text-[13px]">
        <input
          type="checkbox"
          checked={enabled}
          onChange={(event) => setEnabled(event.target.checked)}
        />
        Active
      </label>
    </FormDialog>
  );
}

function TargetRow({
  label,
  detail,
  checked,
  warn = false,
  onToggle,
}: {
  label: string;
  detail: string;
  checked: boolean;
  warn?: boolean;
  onToggle: () => void;
}) {
  return (
    <label
      className={cx(
        "flex cursor-pointer items-start gap-2.5 rounded-control border px-3 py-2",
        "transition-colors",
        checked ? "border-accent bg-accent-tint" : "border-line hover:bg-surface-subtle",
      )}
    >
      <input
        type="checkbox"
        checked={checked}
        onChange={onToggle}
        className="mt-0.5"
      />
      <span className="min-w-0">
        <span className="block truncate text-[13px] font-medium">{label}</span>
        <span
          className={cx(
            "block truncate text-[11.5px]",
            warn ? "text-warning-text" : "text-ink-secondary",
          )}
        >
          {detail}
        </span>
      </span>
    </label>
  );
}

/* ========================================================================== */
/* Recipients and groups                                                       */
/* ========================================================================== */

/**
 * Recipients and groups.
 *
 * Administrator-only, like the rules themselves. Deleting a recipient
 * cascades to notification_rule_recipients, so somebody who cannot edit a
 * rule must not be able to empty one -- which is the same thing with extra
 * steps. The endpoints enforce it; this only decides what to render.
 */
function PeopleTab({
  organizationId,
  recipients,
  groups,
  tenants,
  canAdminister,
  permissionReason,
}: {
  organizationId: string;
  recipients: Recipient[];
  groups: RecipientGroup[];
  tenants: Tenant[];
  canAdminister: boolean;
  permissionReason: string | null;
}) {
  const router = useRouter();
  const [editingRecipient, setEditingRecipient] =
    useState<Recipient | null | "new">(null);
  const [editingGroup, setEditingGroup] = useState<RecipientGroup | null | "new">(null);
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);

  function removeRecipient(recipient: Recipient) {
    setError(null);

    startTransition(async () => {
      const result = await deleteRecipientAction(organizationId, recipient.id);

      if (!result.ok) {
        setError(result.error ?? "That recipient could not be removed.");

        return;
      }

      router.refresh();
    });
  }

  function removeGroup(group: RecipientGroup) {
    setError(null);

    startTransition(async () => {
      const result = await deleteGroupAction(organizationId, group.id);

      if (!result.ok) {
        setError(result.error ?? "That group could not be removed.");

        return;
      }

      router.refresh();
    });
  }

  return (
    <div className="space-y-4">
      {error ? (
        <p
          role="alert"
          className="flex items-start gap-1.5 rounded-control border border-danger/30
                     bg-danger/10 px-3 py-2 text-[12.5px] text-danger-text"
        >
          <AlertTriangle className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          {error}
        </p>
      ) : null}

      <Panel
        title="Recipients"
        description="An address a reminder can be sent to. Linking one to a tenant is
                     what makes their opt-out apply."
        action={
          canAdminister ? (
            <Button
              variant="primary"
              size="sm"
              onClick={() => setEditingRecipient("new")}
            >
              <Plus className="size-3.5" aria-hidden="true" />
              Add recipient
            </Button>
          ) : null
        }
      >
        {!canAdminister ? (
          <div className="px-5 pt-4">
            <PermissionNotice reason={permissionReason} />
          </div>
        ) : null}

        {recipients.length === 0 ? (
          <EmptyState
            compact
            title="No recipients yet"
            description="Add the people a rent reminder should reach — a tenant, a
                         co-owner, your accountant."
          />
        ) : (
          <ul className="divide-y divide-line">
            {recipients.map((recipient) => (
              <li
                key={recipient.id}
                className="flex items-center gap-3 px-5 py-2.5"
              >
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[13.5px] font-medium">
                    {recipient.displayName ?? recipient.email}
                  </p>
                  <p className="truncate text-[12px] text-ink-secondary">
                    {recipient.displayName ? `${recipient.email} · ` : ""}
                    {recipient.ruleCount} {recipient.ruleCount === 1 ? "rule" : "rules"}
                    {recipient.groupCount > 0
                      ? ` · ${recipient.groupCount} ${
                          recipient.groupCount === 1 ? "group" : "groups"
                        }`
                      : ""}
                  </p>
                  {recipient.tenantOptedIn === false ? (
                    <p className="text-[12px] text-warning-text">
                      {recipient.tenantName ?? "This tenant"} has opted out of
                      notifications, so nothing is sent to this address.
                    </p>
                  ) : null}
                </div>

                {!recipient.active ? <Status tone="neutral">Inactive</Status> : null}

                {canAdminister ? (
                  <div className="flex shrink-0 items-center gap-1">
                    <Button
                      variant="ghost"
                      size="sm"
                      disabled={isPending}
                      onClick={() => setEditingRecipient(recipient)}
                    >
                      <Pencil className="size-3.5" aria-hidden="true" />
                      Edit
                    </Button>
                    <button
                      type="button"
                      disabled={isPending}
                      onClick={() => removeRecipient(recipient)}
                      title={
                        recipient.ruleCount > 0
                          ? `Removing this also removes it from ${recipient.ruleCount} rule(s)`
                          : "Remove this recipient"
                      }
                      className="inline-flex size-7 items-center justify-center rounded-control
                                 text-ink-muted transition-colors
                                 hover:bg-danger-tint hover:text-danger-text"
                    >
                      <Trash2 className="size-3.5" aria-hidden="true" />
                      <span className="sr-only">Remove recipient</span>
                    </button>
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Panel
        title="Groups"
        description="A named set of recipients, so a rule can say “the owners” once."
        action={
          canAdminister ? (
            <Button variant="secondary" size="sm" onClick={() => setEditingGroup("new")}>
              <Plus className="size-3.5" aria-hidden="true" />
              New group
            </Button>
          ) : null
        }
      >
        {groups.length === 0 ? (
          <EmptyState
            compact
            title="No groups"
            description="Groups are optional. They help when the same three people are on
                         every rule."
          />
        ) : (
          <ul className="divide-y divide-line">
            {groups.map((group) => (
              <li key={group.id} className="flex items-center gap-3 px-5 py-2.5">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[13.5px] font-medium">{group.name}</p>
                  <p className="truncate text-[12px] text-ink-secondary">
                    {group.recipientIds.length}{" "}
                    {group.recipientIds.length === 1 ? "person" : "people"}
                    {group.ruleCount > 0
                      ? ` · used by ${group.ruleCount} ${
                          group.ruleCount === 1 ? "rule" : "rules"
                        }`
                      : ""}
                  </p>
                </div>

                {canAdminister ? (
                  <div className="flex shrink-0 items-center gap-1">
                    <Button
                      variant="ghost"
                      size="sm"
                      disabled={isPending}
                      onClick={() => setEditingGroup(group)}
                    >
                      <Pencil className="size-3.5" aria-hidden="true" />
                      Edit
                    </Button>
                    <button
                      type="button"
                      disabled={isPending}
                      onClick={() => removeGroup(group)}
                      className="inline-flex size-7 items-center justify-center rounded-control
                                 text-ink-muted transition-colors
                                 hover:bg-danger-tint hover:text-danger-text"
                    >
                      <Trash2 className="size-3.5" aria-hidden="true" />
                      <span className="sr-only">Remove group</span>
                    </button>
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </Panel>

      {editingRecipient ? (
        <RecipientDialog
          organizationId={organizationId}
          recipient={editingRecipient === "new" ? null : editingRecipient}
          tenants={tenants}
          onClose={() => setEditingRecipient(null)}
        />
      ) : null}

      {editingGroup ? (
        <GroupDialog
          organizationId={organizationId}
          group={editingGroup === "new" ? null : editingGroup}
          recipients={recipients}
          onClose={() => setEditingGroup(null)}
        />
      ) : null}
    </div>
  );
}

function RecipientDialog({
  organizationId,
  recipient,
  tenants,
  onClose,
}: {
  organizationId: string;
  recipient: Recipient | null;
  tenants: Tenant[];
  onClose: () => void;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const [email, setEmail] = useState(recipient?.email ?? "");
  const [displayName, setDisplayName] = useState(recipient?.displayName ?? "");
  const [tenantId, setTenantId] = useState(recipient?.tenantId ?? "");
  const [active, setActive] = useState(recipient?.active ?? true);

  function submit() {
    setError(null);

    startTransition(async () => {
      const result = await saveRecipientAction(
        organizationId,
        recipient?.id ?? null,
        {
          email: email.trim(),
          displayName: displayName.trim() || null,
          tenantId: tenantId || null,
          active,
        },
      );

      if (!result.ok) {
        setError(result.error ?? "That recipient could not be saved.");

        return;
      }

      onClose();
      router.refresh();
    });
  }

  return (
    <FormDialog
      title={recipient ? "Edit recipient" : "Add recipient"}
      submitLabel={recipient ? "Save" : "Add"}
      isSubmitting={isPending}
      onSubmit={submit}
      onCancel={onClose}
    >
      {error ? (
        <p
          role="alert"
          className="rounded-control border border-danger/30 bg-danger/10 px-3 py-2
                     text-[12.5px] text-danger-text"
        >
          {error}
        </p>
      ) : null}

      <Field label="Email" htmlFor="recipient-email" required>
        <input
          id="recipient-email"
          type="email"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          className={fieldClass}
        />
      </Field>

      <Field label="Name" htmlFor="recipient-name" hint="Shown on rules and in the log.">
        <input
          id="recipient-name"
          value={displayName}
          onChange={(event) => setDisplayName(event.target.value)}
          className={fieldClass}
        />
      </Field>

      <Field
        label="This is a tenant"
        htmlFor="recipient-tenant"
        hint="Linking the two is what makes that tenant's opt-out apply to this
              address. Leave unset for a business contact."
      >
        <select
          id="recipient-tenant"
          value={tenantId}
          onChange={(event) => setTenantId(event.target.value)}
          className={fieldClass}
        >
          <option value="">Not a tenant</option>
          {tenants.map((tenant) => (
            <option key={tenant.id} value={tenant.id}>
              {tenant.preferredName ?? tenant.legalName}
              {tenant.notificationsOptedIn ? "" : " (opted out)"}
            </option>
          ))}
        </select>
      </Field>

      {recipient ? (
        <label className="flex items-start gap-2 text-[13px]">
          <input
            type="checkbox"
            checked={active}
            onChange={(event) => setActive(event.target.checked)}
            className="mt-0.5"
          />
          <span>
            Active
            <span className="block text-[12px] text-ink-secondary">
              Switching this off keeps the address on its rules but stops it
              receiving anything.
            </span>
          </span>
        </label>
      ) : null}
    </FormDialog>
  );
}

function GroupDialog({
  organizationId,
  group,
  recipients,
  onClose,
}: {
  organizationId: string;
  group: RecipientGroup | null;
  recipients: Recipient[];
  onClose: () => void;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState(group?.name ?? "");
  const [description, setDescription] = useState(group?.description ?? "");
  const [members, setMembers] = useState<string[]>(group?.recipientIds ?? []);

  function submit() {
    setError(null);

    startTransition(async () => {
      const result = await saveGroupAction(organizationId, group?.id ?? null, {
        name: name.trim(),
        description: description.trim() || null,
        recipientIds: members,
      });

      if (!result.ok) {
        setError(result.error ?? "That group could not be saved.");

        return;
      }

      onClose();
      router.refresh();
    });
  }

  return (
    <FormDialog
      title={group ? "Edit group" : "New group"}
      submitLabel={group ? "Save" : "Create"}
      isSubmitting={isPending}
      onSubmit={submit}
      onCancel={onClose}
    >
      {error ? (
        <p
          role="alert"
          className="rounded-control border border-danger/30 bg-danger/10 px-3 py-2
                     text-[12.5px] text-danger-text"
        >
          {error}
        </p>
      ) : null}

      <Field label="Name" htmlFor="group-name" required>
        <input
          id="group-name"
          value={name}
          onChange={(event) => setName(event.target.value)}
          placeholder="Owners"
          className={fieldClass}
        />
      </Field>

      <Field label="Note" htmlFor="group-description">
        <input
          id="group-description"
          value={description}
          onChange={(event) => setDescription(event.target.value)}
          className={fieldClass}
        />
      </Field>

      <fieldset>
        <legend className="block text-[13px] font-medium">Members</legend>

        {recipients.length === 0 ? (
          <p className="mt-2 text-[12.5px] text-ink-secondary">
            Add some recipients first.
          </p>
        ) : (
          <div className="mt-2 space-y-1.5">
            {recipients.map((recipient) => (
              <TargetRow
                key={recipient.id}
                label={recipient.displayName ?? recipient.email}
                detail={recipient.email}
                checked={members.includes(recipient.id)}
                onToggle={() =>
                  setMembers((current) =>
                    current.includes(recipient.id)
                      ? current.filter((id) => id !== recipient.id)
                      : [...current, recipient.id],
                  )
                }
              />
            ))}
          </div>
        )}
      </fieldset>
    </FormDialog>
  );
}

export { eventTitle };

/**
 * Adding one address inline, while building a rule.
 *
 * <h3>Why it creates a real recipient rather than a one-off</h3>
 *
 * A rule could hold a bare email string, and that would be simpler here and
 * worse everywhere else: the address would not appear on the Recipients tab,
 * could not be linked to a tenant so their opt-out applies, could not be put
 * in a group, and would have to be retyped for the next rule. One address
 * typed here becomes an ordinary recipient and behaves like one.
 *
 * The newly created recipient is ticked immediately, because somebody who
 * just typed an address into a rule editor has already said they want it on
 * the rule.
 */
function QuickAddRecipient({
  organizationId,
  onAdded,
}: {
  organizationId: string;
  onAdded: (recipientId: string) => void;
}) {
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);

  function add() {
    const trimmed = email.trim();

    if (!trimmed) {
      return;
    }

    setError(null);

    startTransition(async () => {
      const result = await saveRecipientAction(organizationId, null, {
        email: trimmed,
      });

      if (!result.ok || !result.id) {
        setError(result.error ?? "That address could not be added.");

        return;
      }

      onAdded(result.id);
      setEmail("");
      router.refresh();
    });
  }

  return (
    <div className="mt-2">
      <div className="flex items-center gap-2">
        <input
          type="email"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
          // Enter adds the address rather than submitting the rule, which is
          // what a bare input inside a form would otherwise do — and saving a
          // half-built rule is not what somebody typing an address meant.
          onKeyDown={(event) => {
            if (event.key === "Enter") {
              event.preventDefault();
              add();
            }
          }}
          placeholder="Add an email address"
          aria-label="Add a recipient by email"
          className="h-8 min-w-0 flex-1 rounded-control border border-line-strong
                     bg-surface px-2.5 text-[13px] transition-colors
                     focus:border-accent focus:outline-none focus:ring-2
                     focus:ring-accent/20"
        />
        <Button
          type="button"
          variant="secondary"
          size="sm"
          disabled={isPending || email.trim().length === 0}
          onClick={add}
        >
          <Plus className="size-3.5" aria-hidden="true" />
          Add
        </Button>
      </div>

      {error ? (
        <p role="alert" className="mt-1 text-[11.5px] text-danger-text">
          {error}
        </p>
      ) : null}
    </div>
  );
}
