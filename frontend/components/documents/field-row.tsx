"use client";

import { useState } from "react";
import { Check, Pencil, Quote, X } from "lucide-react";

import { Button, Status } from "@/components/ui/primitives";
import {
  confidenceBand,
  displayValue,
  issueLabel,
  needsDecision,
  type CatalogueField,
  type ExtractedField,
} from "@/lib/documents";

/**
 * One extracted lease term, with its evidence and its three decisions.
 *
 * <h3>What this row is arguing</h3>
 *
 * The value is a claim, and the excerpt and page are the evidence for it. They
 * are shown together, at equal weight, because the reviewer's job is to decide
 * whether the second supports the first. A design that showed the value
 * prominently and hid the evidence behind a click would be optimising for
 * speed in exactly the place where speed is the failure mode.
 *
 * <h3>Confidence is a band, not a number</h3>
 *
 * The model's self-reported confidence is not a probability. Rendering "0.87"
 * invites a reviewer to treat it as one and to skip the 0.9s. Three words say
 * the only thing it honestly supports: whether this is worth a second look.
 */
export function FieldRow({
  field,
  meta,
  isSelected,
  canEdit,
  onSelect,
  onDecide,
}: {
  field: ExtractedField;
  meta: CatalogueField;
  isSelected: boolean;
  canEdit: boolean;
  onSelect: (field: ExtractedField) => void;
  onDecide: (
    field: ExtractedField,
    decision: "accepted" | "corrected" | "rejected",
    correctedValue?: unknown,
  ) => void;
}) {
  const [isEditing, setIsEditing] = useState(false);
  const [draft, setDraft] = useState(() => displayValue(field) ?? "");

  const value = displayValue(field);
  const band = confidenceBand(field.confidence);
  const outstanding = needsDecision(field);

  return (
    <li
      className={`border-b border-line last:border-b-0 ${
        isSelected ? "bg-accent-tint/40" : ""
      }`}
    >
      <div className="px-4 py-3">
        <button
          type="button"
          onClick={() => onSelect(field)}
          className="w-full text-left"
          aria-label={`Show the passage supporting ${meta.label}`}
        >
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-1.5">
                <span className="text-[13px] font-medium">{meta.label}</span>

                {meta.importance === "Required" ? (
                  <span className="text-[11px] font-medium text-ink-muted">Required</span>
                ) : null}

                {field.provenance === "inferredByCalculation" ? (
                  // Worth separating from a stated value. "60 days" derived
                  // from "two months" is a different kind of fact from "60
                  // days" written in the lease, and only one of them is what
                  // the parties agreed to.
                  <Status tone="warning">Calculated</Status>
                ) : null}
              </div>

              <p
                className={`mt-1 text-[14px] ${
                  value === null
                    ? "text-ink-muted italic"
                    : "font-medium text-ink"
                }`}
              >
                {value ?? "Not found in the document"}
              </p>

              {/* The model's own words, when they could not be used. Showing
                  this is what lets a reviewer tell a misreading from a
                  genuinely odd clause. */}
              {value === null && field.extractedValue ? (
                <p className="mt-0.5 text-[12px] text-ink-secondary">
                  Read as: <span className="font-mono">{field.extractedValue}</span>
                </p>
              ) : null}

              {field.correctedValue !== null &&
              field.correctedValue !== undefined &&
              field.extractedValue ? (
                <p className="mt-0.5 text-[12px] text-ink-secondary">
                  Originally read as:{" "}
                  <span className="font-mono line-through">{field.extractedValue}</span>
                </p>
              ) : null}
            </div>

            <div className="flex shrink-0 flex-col items-end gap-1">
              {field.reviewStatus === "accepted" ? (
                <Status tone="success">Accepted</Status>
              ) : field.reviewStatus === "corrected" ? (
                <Status tone="success">Corrected</Status>
              ) : field.reviewStatus === "rejected" ? (
                <Status tone="neutral">Rejected</Status>
              ) : (
                <Status tone={band.tone === "success" ? "success" : band.tone === "warning" ? "warning" : "danger"}>
                  {band.label}
                </Status>
              )}

              {field.sourcePage ? (
                <span className="tabular text-[11.5px] text-ink-secondary">
                  Page {field.sourcePage}
                </span>
              ) : null}
            </div>
          </div>

          {field.sourceExcerpt ? (
            <blockquote
              className="mt-2 flex gap-1.5 rounded-control border border-line
                         bg-surface-subtle px-2.5 py-1.5"
            >
              <Quote
                className="mt-0.5 size-3 shrink-0 text-ink-muted"
                aria-hidden="true"
              />
              <span className="text-[12.5px] leading-relaxed text-ink-secondary">
                {field.sourceExcerpt}
              </span>
            </blockquote>
          ) : null}

          {field.issueCodes.length > 0 ? (
            <ul className="mt-1.5 flex flex-wrap gap-1">
              {field.issueCodes.map((code) => (
                <li
                  key={code}
                  className="rounded-control border border-warning/40 bg-warning/10
                             px-1.5 py-0.5 text-[11.5px] text-warning-text"
                >
                  {issueLabel(code)}
                </li>
              ))}
            </ul>
          ) : null}
        </button>

        {/* Decisions. Shown on every field, not only the flagged ones: the
            database requires each to be decided before approval, so hiding
            the controls on "clear" fields would hide the work. */}
        {canEdit ? (
          isEditing ? (
            <form
              className="mt-2 flex items-center gap-1.5"
              onSubmit={(event) => {
                event.preventDefault();
                onDecide(field, "corrected", coerce(draft, field.valueType));
                setIsEditing(false);
              }}
            >
              <input
                autoFocus
                value={draft}
                onChange={(event) => setDraft(event.target.value)}
                placeholder={placeholderFor(field.valueType)}
                className="min-w-0 flex-1 rounded-control border border-line-strong
                           bg-surface px-2 py-1 text-[13px]
                           focus:border-accent focus:outline-none focus:ring-2
                           focus:ring-accent/20"
                aria-label={`Corrected value for ${meta.label}`}
              />
              <Button type="submit" size="sm" variant="primary">
                Save
              </Button>
              <Button
                type="button"
                size="sm"
                variant="ghost"
                onClick={() => setIsEditing(false)}
              >
                Cancel
              </Button>
            </form>
          ) : (
            <div className="mt-2 flex flex-wrap items-center gap-1.5">
              <Button
                size="sm"
                variant={outstanding ? "primary" : "subtle"}
                onClick={() => onDecide(field, "accepted")}
                disabled={field.reviewStatus === "accepted"}
              >
                <Check className="size-3.5" aria-hidden="true" />
                {field.reviewStatus === "accepted" ? "Accepted" : "Correct"}
              </Button>

              <Button
                size="sm"
                variant="subtle"
                onClick={() => {
                  setDraft(displayValue(field) ?? "");
                  setIsEditing(true);
                }}
              >
                <Pencil className="size-3.5" aria-hidden="true" />
                Change
              </Button>

              <Button
                size="sm"
                variant="ghost"
                onClick={() => onDecide(field, "rejected")}
                disabled={field.reviewStatus === "rejected"}
              >
                <X className="size-3.5" aria-hidden="true" />
                Not in this lease
              </Button>
            </div>
          )
        ) : null}
      </div>
    </li>
  );
}

/**
 * Turns typed text into the JSON shape the column expects.
 *
 * <p>
 * A date has to stay a date and a number has to stay a number: storing "2400"
 * as a string where the schedule generator expects a number would fail later
 * and further away. Anything that will not convert is sent as text, and the
 * database's own validation refuses it with a message, which is a better
 * outcome than guessing here.
 * </p>
 */
function coerce(input: string, valueType: string): unknown {
  const trimmed = input.trim();

  if (trimmed.length === 0) {
    return null;
  }

  switch (valueType) {
    case "money":
    case "decimal": {
      const cleaned = trimmed.replace(/[^0-9.\-]/g, "");
      const number = Number.parseFloat(cleaned);

      return Number.isFinite(number) ? number : trimmed;
    }

    case "integer":
    case "duration_days": {
      const number = Number.parseInt(trimmed.replace(/[^0-9\-]/g, ""), 10);

      return Number.isFinite(number) ? number : trimmed;
    }

    case "boolean": {
      const lowered = trimmed.toLowerCase();

      if (["yes", "true", "y"].includes(lowered)) {
        return true;
      }

      if (["no", "false", "n"].includes(lowered)) {
        return false;
      }

      return trimmed;
    }

    default:
      return trimmed;
  }
}

function placeholderFor(valueType: string): string {
  switch (valueType) {
    case "date":
      return "2026-06-01";
    case "money":
      return "2400.00";
    case "duration_days":
      return "30";
    case "boolean":
      return "Yes or No";
    default:
      return "";
  }
}
