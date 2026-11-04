"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Check, TriangleAlert } from "lucide-react";

import { Button, Panel } from "@/components/ui/primitives";
import { Field, fieldClass } from "@/components/ui/form-dialog";
import { PermissionNotice } from "@/components/ui/permission-notice";

import { updateWorkspaceAction } from "@/app/settings/actions";

/**
 * Renaming the workspace, and the time zone new properties inherit.
 *
 * <h3>Why changing the time zone does not move anything</h3>
 *
 * Existing properties keep the zone they were created with. This is a default
 * for the next one. Silently re-reading every existing due date in a new zone
 * would move when tenants are told to pay, which is not a thing a settings
 * field should do quietly.
 */
export function WorkspaceSettings({
  organizationId,
  name: initialName,
  timeZone: initialTimeZone,
  canEdit,
  permissionReason,
}: {
  organizationId: string;
  name: string;
  timeZone: string;
  canEdit: boolean;
  permissionReason: string | null;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [name, setName] = useState(initialName);
  const [timeZone, setTimeZone] = useState(initialTimeZone);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const isDirty = name !== initialName || timeZone !== initialTimeZone;

  function save() {
    setError(null);
    setSaved(false);

    startTransition(async () => {
      const result = await updateWorkspaceAction(organizationId, name, timeZone);

      if (!result.ok) {
        setError(result.error ?? "These settings could not be saved.");

        return;
      }

      setSaved(true);
      router.refresh();
    });
  }

  return (
    <Panel
      title="Workspace"
      description="What this workspace is called, and the default time zone for new properties."
      padded
    >
      <form
        className="space-y-4"
        onSubmit={(event) => {
          event.preventDefault();
          save();
        }}
      >
        <Field label="Name" htmlFor="workspace-name" required>
          <input
            id="workspace-name"
            value={name}
            onChange={(event) => {
              setName(event.target.value);
              setSaved(false);
            }}
            maxLength={200}
            disabled={!canEdit || isPending}
            className={fieldClass}
          />
        </Field>

        <Field
          label="Default time zone"
          htmlFor="workspace-tz"
          hint="Applies to properties added from now on. Existing ones keep theirs,
                because changing it would move when their rent is due."
        >
          <input
            id="workspace-tz"
            value={timeZone}
            onChange={(event) => {
              setTimeZone(event.target.value);
              setSaved(false);
            }}
            placeholder="America/New_York"
            disabled={!canEdit || isPending}
            className={fieldClass}
          />
        </Field>

        {error ? (
          <p
            role="alert"
            className="reveal flex items-start gap-1.5 rounded-control border
                       border-danger/30 bg-danger/10 px-3 py-2 text-[12.5px]
                       text-danger-text"
          >
            <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            {error}
          </p>
        ) : null}

        {canEdit ? (
          <div className="flex items-center gap-2">
            <Button
              type="submit"
              variant="primary"
              size="sm"
              disabled={isPending || !isDirty}
            >
              {isPending ? "Saving…" : "Save changes"}
            </Button>

            {saved && !isDirty ? (
              <span className="reveal flex items-center gap-1 text-[12.5px] text-success-text">
                <Check className="size-3.5" aria-hidden="true" />
                Saved
              </span>
            ) : null}
          </div>
        ) : (
          <PermissionNotice
            reason={permissionReason}
            action="Changing workspace settings"
          />
        )}
      </form>
    </Panel>
  );
}
