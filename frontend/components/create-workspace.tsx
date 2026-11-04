"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { TriangleAlert } from "lucide-react";

import { Button } from "@/components/ui/primitives";

import { createWorkspaceAction } from "@/app/onboarding/actions";

/**
 * Creating the first workspace.
 *
 * <h3>Why the time zone is asked for here</h3>
 *
 * Because it cannot be guessed later without being wrong for somebody. A rent
 * due date is a calendar date in the property's own zone, and the reminder
 * for it goes out at a local hour there. This value is the default each new
 * property inherits; a property somewhere else overrides it.
 *
 * The browser's own zone is offered as the default, because for a personal
 * deployment it is almost always right and retyping "America/New_York" is a
 * poor first impression.
 */
export function CreateWorkspace({ suggestedName }: { suggestedName?: string }) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();

  const [name, setName] = useState(suggestedName ?? "");
  const [timeZone, setTimeZone] = useState(detectTimeZone);
  const [error, setError] = useState<string | null>(null);

  function submit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);

    if (name.trim().length === 0) {
      setError("Give the workspace a name.");

      return;
    }

    startTransition(async () => {
      const result = await createWorkspaceAction(name, timeZone);

      if (!result.ok) {
        // Field errors first: they name the offending value, which a general
        // message cannot.
        const detail = result.fieldErrors
          ? Object.values(result.fieldErrors).flat().join(" ")
          : null;

        setError(detail || result.error || "The workspace could not be created.");

        return;
      }

      router.refresh();
      router.push("/");
    });
  }

  return (
    <form onSubmit={submit} className="mx-auto max-w-sm text-left">
      <div className="space-y-4">
        <label className="block">
          <span className="block text-[13px] font-medium">Workspace name</span>
          <input
            value={name}
            onChange={(event) => setName(event.target.value)}
            maxLength={200}
            autoFocus
            placeholder="Melkote Properties"
            disabled={isPending}
            className="mt-1.5 h-9 w-full rounded-control border border-line-strong
                       bg-surface px-3 text-sm placeholder:text-ink-muted
                       focus:border-accent focus:outline-none focus:ring-2
                       focus:ring-accent/20"
          />
        </label>

        <label className="block">
          <span className="block text-[13px] font-medium">Time zone</span>
          <select
            value={timeZone}
            onChange={(event) => setTimeZone(event.target.value)}
            disabled={isPending}
            className="mt-1.5 h-9 w-full rounded-control border border-line-strong
                       bg-surface px-2.5 text-sm focus:border-accent
                       focus:outline-none focus:ring-2 focus:ring-accent/20"
          >
            {zoneOptions(timeZone).map((zone) => (
              <option key={zone} value={zone}>
                {zone.replace(/_/g, " ")}
              </option>
            ))}
          </select>
          <span className="mt-1 block text-[12px] leading-relaxed text-ink-secondary">
            The default for new properties. A property elsewhere can override it,
            and reminders are sent by the property&apos;s own zone.
          </span>
        </label>
      </div>

      {error ? (
        <p
          role="alert"
          className="mt-4 flex items-start gap-1.5 rounded-control border border-danger/30
                     bg-danger/10 px-3 py-2 text-[12.5px] text-danger-text"
        >
          <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          {error}
        </p>
      ) : null}

      <Button
        type="submit"
        variant="primary"
        disabled={isPending}
        className="mt-5 w-full"
      >
        {isPending ? "Creating…" : "Create workspace"}
      </Button>

      <p className="mt-3 text-center text-[12px] text-ink-secondary">
        You become its owner.
      </p>
    </form>
  );
}

/**
 * The browser's own zone, when it reports a usable one.
 *
 * Falls back to UTC rather than to a guess. A wrong zone is worse than an
 * obviously neutral one: it would silently shift every due date.
 */
function detectTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  } catch {
    return "UTC";
  }
}

/**
 * A short list, with the detected zone guaranteed to be in it.
 *
 * Not the full IANA database: several hundred entries in a select is a worse
 * experience than a dozen plus whatever the browser actually reports. The API
 * validates against the real database either way, so an unusual zone is a
 * matter of configuration rather than of this list.
 */
function zoneOptions(current: string): string[] {
  const common = [
    "America/New_York",
    "America/Chicago",
    "America/Denver",
    "America/Phoenix",
    "America/Los_Angeles",
    "America/Anchorage",
    "Pacific/Honolulu",
    "America/Toronto",
    "Europe/London",
    "Europe/Dublin",
    "Europe/Paris",
    "Europe/Berlin",
    "Asia/Kolkata",
    "Asia/Singapore",
    "Asia/Tokyo",
    "Australia/Sydney",
    "UTC",
  ];

  return common.includes(current) ? common : [current, ...common];
}
