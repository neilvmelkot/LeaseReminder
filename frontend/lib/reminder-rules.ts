import type {
  ReminderEventType,
  ReminderOffsetUnit,
  ReminderRule,
  ReminderTriggerType,
  RuleInheritanceMode,
} from "@/lib/api";

/**
 * Turning a reminder rule into a sentence.
 *
 * <h3>Why a sentence and not a row of fields</h3>
 *
 * A rule is six values — event, direction, offset, unit, time, scope — and
 * read as six labelled fields it takes real effort to answer the only
 * question anybody has: when will this actually send? "3 days before rent is
 * due, at 09:00" answers it in one pass. The fields are still there in the
 * editor, where you are setting them rather than checking them.
 */

const eventLabels: Record<ReminderEventType, string> = {
  rentDue: "rent is due",
  gracePeriodEnd: "the grace period ends",
  lateFeeTrigger: "a late fee applies",
  leaseExpiration: "the lease ends",
  renewalNoticeDeadline: "the renewal notice is due",
  rentIncrease: "the rent increases",
  earlyTerminationDeadline: "the early termination deadline",
  custom: "a custom event",
};

/** For a dropdown, where the phrase has to stand alone. */
const eventTitles: Record<ReminderEventType, string> = {
  rentDue: "Rent due",
  gracePeriodEnd: "Grace period ends",
  lateFeeTrigger: "Late fee applies",
  leaseExpiration: "Lease ends",
  renewalNoticeDeadline: "Renewal notice deadline",
  rentIncrease: "Rent increase",
  earlyTerminationDeadline: "Early termination deadline",
  custom: "Custom event",
};

export const eventOptions = (
  Object.keys(eventTitles) as ReminderEventType[]
).map((value) => ({ value, label: eventTitles[value] }));

export function eventTitle(event: ReminderEventType): string {
  return eventTitles[event] ?? event;
}

export const triggerOptions: Array<{
  value: ReminderTriggerType;
  label: string;
}> = [
  { value: "before", label: "Before" },
  { value: "on", label: "On the day" },
  { value: "after", label: "After" },
];

export const offsetUnitOptions: Array<{
  value: ReminderOffsetUnit;
  label: string;
}> = [
  { value: "days", label: "days" },
  { value: "hours", label: "hours" },
  { value: "minutes", label: "minutes" },
];

export const inheritanceOptions: Array<{
  value: RuleInheritanceMode;
  label: string;
  hint: string;
}> = [
  {
    value: "additional",
    label: "As well as inherited rules",
    hint: "Sends in addition to anything set at a broader level.",
  },
  {
    value: "overridden",
    label: "Instead of an inherited rule",
    hint: "Replaces one rule from a broader level, for this scope only.",
  },
  {
    value: "disabled",
    label: "Switch off an inherited rule",
    hint: "Stops a broader rule applying here, without deleting it.",
  },
];

/** "09:00:00" and "09:00" both become "09:00". */
export function formatSendTime(value: string): string {
  return /^(\d{2}:\d{2})/.exec(value)?.[1] ?? value;
}

/**
 * The whole rule as one line.
 *
 * Reads the direction first because that is what people scan for: "3 days
 * before" and "3 days after" are opposite instructions that otherwise differ
 * by one word in the middle of a sentence.
 */
export function describeRule(rule: ReminderRule): string {
  const when =
    rule.triggerType === "on"
      ? `On the day ${eventLabels[rule.eventType]}`
      : `${rule.offsetValue} ${pluraliseUnit(rule.offsetUnit, rule.offsetValue)} `
        + `${rule.triggerType} ${eventLabels[rule.eventType]}`;

  return `${when}, at ${formatSendTime(rule.sendLocalTime)}`;
}

function pluraliseUnit(unit: ReminderOffsetUnit, count: number): string {
  const singular = unit.slice(0, -1);

  return count === 1 ? singular : unit;
}

/** "Everywhere", a property name, or "This lease". */
export function scopeLabel(rule: ReminderRule): string {
  switch (rule.scope) {
    case "global":
      return "Every property";
    case "property":
      return rule.propertyName ?? "One property";
    default:
      return "One lease";
  }
}

/**
 * Why a rule will not send, if it will not.
 *
 * <p>
 * Four separate reasons, and they are not interchangeable: a disabled rule is
 * somebody's decision, a paused one is the system protecting itself, and a
 * rule with no recipients is an oversight that looks exactly like a working
 * rule until the day it does not send. Each needs its own sentence.
 * </p>
 */
export function ruleWarning(rule: ReminderRule): string | null {
  if (!rule.enabled) {
    return "Switched off. Nothing is sent by this rule.";
  }

  if (rule.pausedAt) {
    return rule.pausedReason
      ? `Paused automatically: ${rule.pausedReason}`
      : "Paused automatically after repeated failures.";
  }

  if (rule.targets.length === 0) {
    return "No recipients, so this rule sends to nobody.";
  }

  if (rule.inheritanceMode === "disabled") {
    return "This switches off an inherited rule rather than sending anything.";
  }

  return null;
}
