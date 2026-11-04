"use client";

import { useCallback, useEffect, useMemo, useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { CalendarClock, CircleAlert, Info, TriangleAlert } from "lucide-react";

import { Button, Panel, Status } from "@/components/ui/primitives";
import {
  adjustmentLabel,
  businessDayOptions,
  dueDayOptions,
  formatDate,
  formatMonth,
  formatMoney,
  frequencyOptions,
  shortMonthOptions,
  todayIso,
  type BusinessDayShift,
  type DueDayRule,
  type RentFrequency,
  type RentScheduleInput,
  type SavedSchedule,
  type SchedulePreview,
  type ShortMonthFallback,
} from "@/lib/rent";

import { previewScheduleAction, saveScheduleAction } from "@/app/leases/[leaseId]/schedule/actions";

/**
 * The rent schedule editor.
 *
 * <h3>Why the preview is computed on the server</h3>
 *
 * It would be faster to work the dates out in the browser. It would also be a
 * second implementation of the rules that decide when somebody is told to pay
 * rent — short months, business-day shifts, the difference between monthly and
 * every thirty days — and the two would agree right up until they did not.
 *
 * So the preview calls the same <code>RentScheduleCalculator</code> the worker
 * will call, on the definition as typed. What you see is produced by the code
 * that will produce the real thing, which is the only way a preview can be
 * trusted.
 *
 * <h3>Why nothing generates on save</h3>
 *
 * Saving records the definition; the worker expands it, within seconds.
 * Generating inline would mean a slow request, a second code path doing the
 * same job, and no claim to stop two requests doing it at once.
 */
export function ScheduleEditor({
  organizationId,
  leaseId,
  existing,
  propertyTimeZone,
  leaseStartDate,
  leaseEndDate,
  leaseRentAmount,
  leaseCurrency,
  gracePeriodDays,
  lateFeeAfterDays,
  canEdit,
}: {
  organizationId: string;
  leaseId: string;
  existing: SavedSchedule | null;
  propertyTimeZone: string;
  leaseStartDate: string | null;
  leaseEndDate: string | null;
  leaseRentAmount: number | null;
  leaseCurrency: string;
  gracePeriodDays: number | null;
  lateFeeAfterDays: number | null;
  canEdit: boolean;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();

  // Seeded from the saved schedule when there is one, and otherwise from the
  // lease itself. A lease already carries its start date, rent, and currency,
  // so asking for them again would be asking the user to retype what the
  // system knows.
  const [input, setInput] = useState<RentScheduleInput>(() => ({
    frequency: (existing?.frequency ?? "monthly") as RentFrequency,
    dueDayRule: (existing?.dueDayRule ?? "first_of_month") as DueDayRule,
    dueDayOfMonth: existing?.dueDayOfMonth ?? null,
    startDate: existing?.startDate ?? leaseStartDate ?? todayIso(),
    endDate: existing?.endDate ?? leaseEndDate ?? null,
    amount: existing?.amount ?? leaseRentAmount ?? 0,
    currency: existing?.currency ?? leaseCurrency,
    timeZone: existing?.timeZone ?? propertyTimeZone,
    shortMonthFallback: (existing?.shortMonthFallback ??
      "last_day_of_month") as ShortMonthFallback,
    businessDayShift: (existing?.businessDayShift ?? "none") as BusinessDayShift,
    holidayCalendarId: existing?.holidayCalendarId ?? null,
    gracePeriodDays,
    lateFeeAfterDays,
  }));

  const [preview, setPreview] = useState<SchedulePreview | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<number | null>(null);

  // Serialised so the effect below compares by value. Comparing the object
  // itself would re-run on every render, since a new one is created each time
  // a field changes.
  const fingerprint = useMemo(() => JSON.stringify(input), [input]);

  // Derived during render rather than stored. Clearing the preview in the
  // effect would be a setState in an effect body, and the render below has to
  // check this anyway -- so storing it would mean two sources for one fact.
  const isIncomplete = !input.amount || !input.startDate;

  useEffect(() => {
    if (isIncomplete) {
      return;
    }

    let cancelled = false;

    // Debounced, because this runs on every keystroke in the amount field and
    // each run is a round trip.
    const timer = setTimeout(async () => {
      const result = await previewScheduleAction(organizationId, leaseId, input);

      if (cancelled) {
        return;
      }

      if (result.ok && result.preview) {
        setPreview(result.preview);
        setPreviewError(null);
      } else {
        setPreview(null);
        setPreviewError(result.error ?? "This schedule could not be previewed.");
      }
    }, 350);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
    // fingerprint stands in for input; the others are stable for this page.
  }, [fingerprint, input, isIncomplete, organizationId, leaseId]);

  const update = useCallback(
    <K extends keyof RentScheduleInput>(key: K, value: RentScheduleInput[K]) => {
      setInput((previous) => ({ ...previous, [key]: value }));
      setSavedAt(null);
    },
    [],
  );

  function save() {
    setSaveError(null);

    startTransition(async () => {
      const result = await saveScheduleAction(organizationId, leaseId, input);

      if (!result.ok) {
        setSaveError(result.error ?? "This schedule could not be saved.");

        return;
      }

      setSavedAt(Date.now());
      router.refresh();
    });
  }

  const shifted = preview?.occurrences.filter((o) => o.wasShifted).length ?? 0;

  return (
    <div className="grid gap-4 lg:grid-cols-[minmax(0,22rem)_minmax(0,1fr)]">
      {/* ------------------------------------------------------------------ */}
      {/* The definition                                                      */}
      {/* ------------------------------------------------------------------ */}
      <Panel title="How rent falls due" padded>
        <div className="space-y-4">
          <Field label="Amount" hint={`In ${input.currency}`}>
            <input
              type="number"
              step="0.01"
              min="0"
              value={input.amount || ""}
              onChange={(event) => update("amount", Number(event.target.value))}
              disabled={!canEdit}
              className={inputClass}
            />
          </Field>

          <Field label="How often">
            <select
              value={input.frequency}
              onChange={(event) => update("frequency", event.target.value as RentFrequency)}
              disabled={!canEdit}
              className={inputClass}
            >
              {frequencyOptions.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
            <Hint>
              {frequencyOptions.find((o) => o.value === input.frequency)?.hint}
            </Hint>
          </Field>

          {input.frequency === "monthly" ? (
            <>
              <Field label="Due on">
                <select
                  value={input.dueDayRule}
                  onChange={(event) =>
                    update("dueDayRule", event.target.value as DueDayRule)
                  }
                  disabled={!canEdit}
                  className={inputClass}
                >
                  {dueDayOptions.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </select>
                <Hint>
                  {dueDayOptions.find((o) => o.value === input.dueDayRule)?.hint}
                </Hint>
              </Field>

              {input.dueDayRule === "specific_day" ? (
                <Field label="Day of the month">
                  <input
                    type="number"
                    min="1"
                    max="31"
                    value={input.dueDayOfMonth ?? ""}
                    onChange={(event) =>
                      update("dueDayOfMonth", Number(event.target.value) || null)
                    }
                    disabled={!canEdit}
                    className={inputClass}
                  />
                </Field>
              ) : null}

              {/* Only asked when it can actually happen. A due day of 15 never
                  meets a short month, and offering the choice anyway would be
                  a question with no consequence. */}
              {(input.dueDayOfMonth ?? 0) > 28 ? (
                <Field label="When that day does not exist">
                  <select
                    value={input.shortMonthFallback}
                    onChange={(event) =>
                      update(
                        "shortMonthFallback",
                        event.target.value as ShortMonthFallback,
                      )
                    }
                    disabled={!canEdit}
                    className={inputClass}
                  >
                    {shortMonthOptions.map((option) => (
                      <option key={option.value} value={option.value}>
                        {option.label}
                      </option>
                    ))}
                  </select>
                  <Hint>
                    {shortMonthOptions.find(
                      (o) => o.value === input.shortMonthFallback,
                    )?.hint}
                  </Hint>
                </Field>
              ) : null}
            </>
          ) : null}

          <Field label="If it lands on a weekend or holiday">
            <select
              value={input.businessDayShift}
              onChange={(event) =>
                update("businessDayShift", event.target.value as BusinessDayShift)
              }
              disabled={!canEdit}
              className={inputClass}
            >
              {businessDayOptions.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
            <Hint>
              {businessDayOptions.find((o) => o.value === input.businessDayShift)?.hint}
            </Hint>
          </Field>

          <div className="grid grid-cols-2 gap-3">
            <Field label="First due">
              <input
                type="date"
                value={input.startDate}
                onChange={(event) => update("startDate", event.target.value)}
                disabled={!canEdit}
                className={inputClass}
              />
            </Field>

            <Field label="Last due" hint="Blank for open ended">
              <input
                type="date"
                value={input.endDate ?? ""}
                onChange={(event) => update("endDate", event.target.value || null)}
                disabled={!canEdit}
                className={inputClass}
              />
            </Field>
          </div>

          <p className="flex items-start gap-1.5 rounded-control border border-line
                        bg-surface-subtle px-2.5 py-2 text-[12px] leading-relaxed
                        text-ink-secondary">
            <Info className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            Dates are in <span className="font-medium">{input.timeZone}</span>, the
            property&apos;s own time zone. A reminder is sent at the right local hour
            there, whatever the server&apos;s clock says.
          </p>

          {canEdit ? (
            <div>
              <Button
                variant="primary"
                className="w-full"
                disabled={
                  isPending || isIncomplete || !preview || preview.occurrences.length === 0
                }
                onClick={save}
              >
                {isPending ? "Saving…" : "Save this schedule"}
              </Button>

              {savedAt ? (
                <p className="mt-1.5 text-center text-[12px] text-success-text">
                  Saved. The dates below are being generated now.
                </p>
              ) : null}

              {saveError ? (
                <p
                  role="alert"
                  className="mt-1.5 flex items-start gap-1.5 rounded-control border
                             border-danger/30 bg-danger/10 px-2.5 py-2 text-[12.5px]
                             text-danger-text"
                >
                  <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
                  {saveError}
                </p>
              ) : null}
            </div>
          ) : (
            <p className="text-[12.5px] text-ink-secondary">
              Your role does not permit editing rent schedules.
            </p>
          )}
        </div>
      </Panel>

      {/* ------------------------------------------------------------------ */}
      {/* What it produces                                                    */}
      {/* ------------------------------------------------------------------ */}
      <Panel
        title="What this produces"
        description="Computed by the same code that will generate the real dates."
        padded
      >
        {previewError ? (
          <p
            role="alert"
            className="flex items-start gap-1.5 rounded-control border border-danger/30
                       bg-danger/10 px-3 py-2 text-[12.5px] text-danger-text"
          >
            <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            {previewError}
          </p>
        ) : isIncomplete || !preview ? (
          <p className="py-8 text-center text-[13px] text-ink-secondary">
            Enter an amount and a first due date to see the schedule.
          </p>
        ) : (
          <div className="space-y-3">
            <div className="flex flex-wrap items-center gap-2">
              <Status tone="info">
                {preview.totalGenerated} payment
                {preview.totalGenerated === 1 ? "" : "s"} in the first year
              </Status>

              {shifted > 0 ? (
                <Status tone="warning">
                  {shifted} moved off a weekend or holiday
                </Status>
              ) : null}
            </div>

            {/* Months where the requested day does not exist. Shown above the
                list, because it explains dates that would otherwise look
                wrong. */}
            {preview.unresolvedMonths.length > 0 ? (
              <div className="rounded-control border border-warning/40 bg-warning/10 px-3 py-2">
                <p className="flex items-center gap-1.5 text-[12.5px] font-medium text-warning-text">
                  <CircleAlert className="size-3.5" aria-hidden="true" />
                  The {preview.unresolvedMonths[0]?.requestedDayOfMonth}th does not exist
                  in every month
                </p>
                <p className="mt-0.5 text-[12px] leading-relaxed text-ink-secondary">
                  {preview.unresolvedMonths
                    .map((month) => formatMonth(month.monthStart))
                    .join(", ")}{" "}
                  — handled as you chose above.
                </p>
              </div>
            ) : null}

            <ol className="divide-y divide-line">
              {preview.occurrences.map((occurrence) => (
                <li
                  key={occurrence.sequenceNumber}
                  className="flex items-start justify-between gap-3 py-2"
                >
                  <div className="min-w-0">
                    <p className="text-[13.5px] font-medium">
                      {formatDate(occurrence.dueDate)}
                    </p>

                    {occurrence.wasShifted ? (
                      <p className="mt-0.5 text-[12px] text-warning-text">
                        {adjustmentLabel(occurrence.adjustmentReason) ?? "Moved"} — the
                        lease says {formatDate(occurrence.nominalDueDate)}
                      </p>
                    ) : null}

                    {occurrence.lateFeeDate ? (
                      <p className="mt-0.5 text-[12px] text-ink-secondary">
                        Late from {formatDate(occurrence.lateFeeDate)}
                      </p>
                    ) : occurrence.gracePeriodEndDate ? (
                      <p className="mt-0.5 text-[12px] text-ink-secondary">
                        Grace period ends {formatDate(occurrence.gracePeriodEndDate)}
                      </p>
                    ) : null}
                  </div>

                  <span className="tabular shrink-0 text-[13.5px] font-medium">
                    {formatMoney(occurrence.amount, occurrence.currency)}
                  </span>
                </li>
              ))}
            </ol>

            {preview.totalGenerated > preview.occurrences.length ? (
              <p className="flex items-center gap-1.5 text-[12px] text-ink-secondary">
                <CalendarClock className="size-3.5" aria-hidden="true" />
                Showing the first {preview.occurrences.length} of{" "}
                {preview.totalGenerated}.
              </p>
            ) : null}
          </div>
        )}
      </Panel>
    </div>
  );
}

const inputClass =
  "w-full rounded-control border border-line-strong bg-surface px-2.5 py-1.5 " +
  "text-[13px] focus:border-accent focus:outline-none focus:ring-2 " +
  "focus:ring-accent/20 disabled:bg-surface-subtle disabled:text-ink-secondary";

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <label className="block">
      <span className="mb-1 flex items-baseline justify-between gap-2">
        <span className="text-[12.5px] font-medium">{label}</span>
        {hint ? <span className="text-[11.5px] text-ink-muted">{hint}</span> : null}
      </span>
      {children}
    </label>
  );
}

function Hint({ children }: { children: React.ReactNode }) {
  if (!children) {
    return null;
  }

  return (
    <span className="mt-0.5 block text-[11.5px] leading-relaxed text-ink-secondary">
      {children}
    </span>
  );
}
