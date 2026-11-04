"use client";

import { useMemo, useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Check, Eye, TriangleAlert } from "lucide-react";

import { Button, EmptyState, Panel, Status } from "@/components/ui/primitives";
import { Disclosure } from "@/components/ui/disclosure";
import { PermissionNotice } from "@/components/ui/permission-notice";
import { PillTabs } from "@/components/ui/pill-tabs";
import type { EmailTemplate, EmailTemplateList, TemplatePreview } from "@/lib/api";

import {
  discardDraftAction,
  previewTemplateAction,
  publishTemplateAction,
  saveTemplateAction,
} from "@/app/settings/templates/actions";

/**
 * Editing the wording of what tenants receive.
 *
 * <h3>Draft and published are different things here</h3>
 *
 * A published version cannot be edited — a database trigger refuses it — so
 * that an email log entry always points at text that still says what it said
 * when it was sent. Editing therefore produces a draft, and publishing
 * promotes that draft and archives the one it replaces.
 *
 * The screen says this plainly rather than hiding it, because the alternative
 * is somebody editing a template, seeing no change in what goes out, and
 * having no idea why.
 *
 * <h3>Why the form is a separate component</h3>
 *
 * It is mounted with the template's id as its key, so selecting a different
 * message unmounts one form and mounts another. Every field then starts from
 * the newly selected template by construction. Clearing them in an effect
 * instead would be a cascading render doing the job remounting already does —
 * and it is how one template's text ends up carried into another the first
 * time somebody adds a field and forgets to reset it.
 */
export function TemplateEditor({
  organizationId,
  data,
  canEdit,
  permissionReason,
}: {
  organizationId: string;
  data: EmailTemplateList;
  canEdit: boolean;
  /** From `permissions(role).reason`. Names the caller's own role. */
  permissionReason: string | null;
}) {
  // Grouped by key: a key has at most one published and one draft version,
  // and the editor works on the draft when there is one.
  const groups = useMemo(() => groupByKey(data.templates), [data.templates]);

  const [selectedKey, setSelectedKey] = useState<string | null>(
    () => groups[0]?.key ?? null,
  );

  const selected = groups.find((entry) => entry.key === selectedKey) ?? null;
  const editing = selected?.draft ?? selected?.published ?? null;

  if (groups.length === 0) {
    return (
      <Panel title="Email templates">
        <EmptyState
          title="No templates yet"
          description="A new workspace is seeded with a default set when it is created.
                       If this is empty, the workspace was created before templates
                       were seeded."
        />
      </Panel>
    );
  }

  return (
    <div
      className="grid items-start gap-4
                 lg:grid-cols-[minmax(0,14rem)_minmax(0,1fr)]"
    >
      <Panel title="Messages" padded>
        <ul className="-mx-2">
          {groups.map((entry) => (
            <li key={entry.key}>
              <button
                type="button"
                onClick={() => setSelectedKey(entry.key)}
                aria-current={entry.key === selectedKey ? "true" : undefined}
                className={`w-full rounded-control px-2 py-1.5 text-left text-[13px]
                            transition-colors ${
                              entry.key === selectedKey
                                ? "bg-accent-tint font-semibold text-accent"
                                : "hover:bg-surface-subtle"
                            }`}
              >
                <span className="block truncate">
                  {entry.draft?.name ?? entry.published?.name ?? entry.key}
                </span>
                {entry.draft ? (
                  <span className="mt-0.5 block text-[11.5px] font-normal text-warning-text">
                    Unpublished draft
                  </span>
                ) : null}
              </button>
            </li>
          ))}
        </ul>
      </Panel>

      {selected ? (
        <TemplateForm
          key={editing?.id ?? selected.key}
          organizationId={organizationId}
          group={selected}
          editing={editing}
          availableVariables={data.availableVariables}
          canEdit={canEdit}
          permissionReason={permissionReason}
        />
      ) : null}
    </div>
  );
}

/**
 * The editing form for one template.
 */
function TemplateForm({
  organizationId,
  group,
  editing,
  availableVariables,
  canEdit,
  permissionReason,
}: {
  organizationId: string;
  group: TemplateGroup;
  editing: EmailTemplate | null;
  availableVariables: string[];
  canEdit: boolean;
  permissionReason: string | null;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [form, setForm] = useState<TemplateFormState>(() => toForm(editing));
  const [preview, setPreview] = useState<TemplatePreview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  function update(key: keyof TemplateFormState, value: string) {
    setForm((previous) => ({ ...previous, [key]: value }));
    setSaved(false);
  }

  function runPreview() {
    setError(null);

    startTransition(async () => {
      const result = await previewTemplateAction(organizationId, {
        subject: form.subject,
        htmlBody: form.htmlBody,
        textBody: form.textBody,
      });

      if (!result.ok || !result.preview) {
        setError(result.error ?? "This template could not be previewed.");

        return;
      }

      setPreview(result.preview);
    });
  }

  function save() {
    setError(null);

    startTransition(async () => {
      const result = await saveTemplateAction(organizationId, group.key, form);

      if (!result.ok) {
        setError(result.error ?? "This draft could not be saved.");

        return;
      }

      setSaved(true);
      router.refresh();
    });
  }

  function publish() {
    if (!group.draft) {
      return;
    }

    const draftId = group.draft.id;

    setError(null);

    startTransition(async () => {
      const result = await publishTemplateAction(organizationId, draftId);

      if (!result.ok) {
        setError(result.error ?? "This draft could not be published.");

        return;
      }

      router.refresh();
    });
  }

  function discard() {
    if (!group.draft) {
      return;
    }

    const draftId = group.draft.id;

    setError(null);

    startTransition(async () => {
      const result = await discardDraftAction(organizationId, draftId);

      if (!result.ok) {
        setError(result.error ?? "This draft could not be discarded.");

        return;
      }

      router.refresh();
    });
  }

  return (
    <div className="space-y-4">
      <Panel
        title={editing?.name ?? "Template"}
        description={
          group.draft
            ? "You are editing a draft. Tenants still receive the published version "
              + "until you publish this one."
            : "Editing creates a draft. The published version keeps going out until "
              + "you publish the draft."
        }
        action={
          group.published ? (
            <Status tone="success">Published v{group.published.version}</Status>
          ) : (
            <Status tone="neutral">Never published</Status>
          )
        }
        padded
      >
        <div className="space-y-3">
          <Field label="Name" hint="Shown in this list only">
            <input
              value={form.name}
              onChange={(event) => update("name", event.target.value)}
              disabled={!canEdit}
              className={inputClass}
            />
          </Field>

          <Field label="Subject">
            <input
              value={form.subject}
              onChange={(event) => update("subject", event.target.value)}
              disabled={!canEdit}
              className={inputClass}
            />
          </Field>

          <Field label="Message, HTML" hint="What most people will see">
            <textarea
              rows={8}
              value={form.htmlBody}
              onChange={(event) => update("htmlBody", event.target.value)}
              disabled={!canEdit}
              className={`${inputClass} font-mono text-[12.5px]`}
            />
          </Field>

          <Field label="Message, plain text" hint="For clients that do not show HTML">
            <textarea
              rows={6}
              value={form.textBody}
              onChange={(event) => update("textBody", event.target.value)}
              disabled={!canEdit}
              className={`${inputClass} font-mono text-[12.5px]`}
            />
          </Field>

          <Disclosure label="Placeholders you can use">
            <p className="text-[12px] leading-relaxed text-ink-secondary">
              Write them in double braces. Anything not on this list is left in the
              message exactly as typed rather than replaced, so a misspelling shows
              up in the preview rather than in a tenant&apos;s inbox.
            </p>
            <ul className="mt-1.5 flex flex-wrap gap-1">
              {availableVariables.map((name) => (
                <li
                  key={name}
                  className="rounded-control border border-line bg-surface px-1.5 py-0.5
                             font-mono text-[11.5px]"
                >
                  {`{{${name}}}`}
                </li>
              ))}
            </ul>
          </Disclosure>

          {error ? (
            <p
              role="alert"
              className="flex items-start gap-1.5 rounded-control border border-danger/30
                         bg-danger/10 px-3 py-2 text-[12.5px] text-danger-text"
            >
              <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              {error}
            </p>
          ) : null}

          {canEdit ? (
            <div className="flex flex-wrap items-center gap-2">
              <Button variant="subtle" size="sm" disabled={isPending} onClick={runPreview}>
                <Eye className="size-3.5" aria-hidden="true" />
                Preview
              </Button>

              <Button variant="primary" size="sm" disabled={isPending} onClick={save}>
                {isPending ? "Saving…" : "Save draft"}
              </Button>

              {group.draft ? (
                <>
                  <Button variant="primary" size="sm" disabled={isPending} onClick={publish}>
                    <Check className="size-3.5" aria-hidden="true" />
                    Publish
                  </Button>
                  <Button variant="ghost" size="sm" disabled={isPending} onClick={discard}>
                    Discard draft
                  </Button>
                </>
              ) : null}

              {saved ? (
                <span className="text-[12px] text-success-text">
                  Draft saved. Publish it to start sending this wording.
                </span>
              ) : null}
            </div>
          ) : (
            <PermissionNotice
              reason={permissionReason}
              action="Editing email templates"
            />
          )}
        </div>
      </Panel>

      {preview ? (
        <Panel
          title="Preview"
          description="Rendered with sample values by the same code that sends the real
                       thing."
          padded
        >
          <div className="space-y-3">
            {preview.unknownVariables.length > 0 ? (
              <p
                className="rounded-control border border-warning/40 bg-warning/10 px-3 py-2
                           text-[12.5px] leading-relaxed text-warning-text"
              >
                These placeholders are not recognised and will appear in the message
                exactly as written:{" "}
                <span className="font-mono">{preview.unknownVariables.join(", ")}</span>
              </p>
            ) : null}

            <PreviewPart label="Subject">
              <p className="text-[13.5px] font-medium">{preview.subject}</p>
            </PreviewPart>

            <HtmlPreview html={preview.htmlBody} />

            <PreviewPart label="Plain text">
              <Pre>{preview.textBody}</Pre>
            </PreviewPart>
          </div>
        </Panel>
      ) : null}
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Shapes and small pieces                                                     */
/* -------------------------------------------------------------------------- */

interface TemplateFormState {
  name: string;
  subject: string;
  htmlBody: string;
  textBody: string;
}

interface TemplateGroup {
  key: string;
  published: EmailTemplate | null;
  draft: EmailTemplate | null;
}

function toForm(template: EmailTemplate | null): TemplateFormState {
  return {
    name: template?.name ?? "",
    subject: template?.subject ?? "",
    htmlBody: template?.htmlBody ?? "",
    textBody: template?.textBody ?? "",
  };
}

/**
 * One entry per template key, with its published and draft versions.
 *
 * The database permits at most one published and one draft per key, so this is
 * a regrouping rather than a reduction that could lose anything.
 */
function groupByKey(templates: EmailTemplate[]): TemplateGroup[] {
  const byKey = new Map<string, TemplateGroup>();

  for (const template of templates) {
    const entry = byKey.get(template.templateKey) ?? {
      key: template.templateKey,
      published: null,
      draft: null,
    };

    if (template.status === "published") {
      entry.published = template;
    } else if (template.status === "draft") {
      entry.draft = template;
    }

    byKey.set(template.templateKey, entry);
  }

  return [...byKey.values()].sort((left, right) => left.key.localeCompare(right.key));
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

function PreviewPart({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <p className="text-[11.5px] font-medium uppercase tracking-wide text-ink-secondary">
        {label}
      </p>
      <div className="mt-0.5">{children}</div>
    </div>
  );
}

function Pre({ children }: { children: React.ReactNode }) {
  return (
    <pre
      className="thin-scrollbar overflow-x-auto rounded-control border border-line
                 bg-surface-subtle px-3 py-2 font-mono text-[12px] leading-relaxed
                 whitespace-pre-wrap"
    >
      {children}
    </pre>
  );
}

/**
 * The HTML body as a tenant would actually see it.
 *
 * <h3>Why an iframe and not dangerouslySetInnerHTML</h3>
 *
 * This showed the markup as text, on the reasoning that injecting a rendered
 * template into the page would undo the server's escaping. The caution was
 * right and the conclusion was not: a column of tags is not a preview, and
 * "does this look right" is the only question this panel exists to answer.
 *
 * A sandboxed iframe answers it without the risk. With no `allow-` tokens at
 * all the frame gets an opaque origin, so the document inside cannot run
 * script, reach this page's DOM, read the session cookie or localStorage,
 * submit a form, or navigate the top window. Script is blocked by the sandbox
 * itself rather than by the page's Content-Security-Policy, which matters
 * because that policy permits inline script for the application's own needs.
 *
 * srcdoc rather than a blob URL, so nothing is written to a URL that could
 * outlive the component or be opened in a tab of its own.
 *
 * <h3>Why the source is still reachable</h3>
 *
 * A rendered preview cannot show you an unclosed tag or a placeholder that
 * resolved to nothing visible. Both views answer different questions, so the
 * panel keeps both rather than trading one for the other.
 */
function HtmlPreview({ html }: { html: string }) {
  const [view, setView] = useState<"rendered" | "source">("rendered");

  return (
    <div>
      <div className="mb-1.5 flex items-center justify-between gap-3">
        <p className="text-[11.5px] font-medium uppercase tracking-wide text-ink-secondary">
          HTML
        </p>

        <PillTabs
          ariaLabel="HTML preview view"
          activeKey={view}
          onSelect={setView}
          size="sm"
          tabs={[
            { key: "rendered", label: "Rendered" },
            { key: "source", label: "Source" },
          ]}
        />
      </div>

      {view === "rendered" ? (
        <div className="overflow-hidden rounded-control border border-line bg-white">
          <iframe
            // The whole security model of this component. Empty, not omitted:
            // an omitted sandbox attribute applies no restrictions at all.
            sandbox=""
            srcDoc={html}
            title="Rendered email preview"
            className="h-[26rem] w-full border-0"
          />
        </div>
      ) : (
        <Pre>{html}</Pre>
      )}

      {/* Always white, whatever the application's theme. A mail client
          renders on its own background, and previewing this template on a
          dark one would show contrast the recipient will never see. */}
      <p className="mt-1 text-[11.5px] text-ink-muted">
        Shown on white, as a mail client would render it.
      </p>
    </div>
  );
}
