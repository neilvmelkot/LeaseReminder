"use client";

import { useMemo, useState, useTransition } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Pencil, Search, Trash2, TriangleAlert, X } from "lucide-react";

import {
  Button,
  EmptyState,
  Status,
  TableFrame,
  Td,
  Th,
} from "@/components/ui/primitives";
import { Field, FormDialog, fieldClass } from "@/components/ui/form-dialog";
import {
  documentLabel,
  documentStatusLabel,
  documentStatusTone,
  formatFileSize,
  type LeaseDocumentSummary,
} from "@/lib/documents";

import {
  deleteDocumentAction,
  renameDocumentAction,
} from "@/app/documents/actions";

/**
 * The uploaded documents, searchable, with renaming and removal.
 *
 * <h3>Why the search filters in the browser</h3>
 *
 * The whole list is already here — the page fetches it to render the table —
 * so filtering it is a string comparison over data in hand. A server round
 * trip per keystroke would be slower, would need debouncing to be bearable,
 * and would make an empty result ambiguous between "no match" and "request in
 * flight".
 *
 * This stops being the right call at a few thousand documents, when the list
 * itself needs paging. The honest marker for that is the page's own fetch, not
 * this component.
 *
 * <h3>What it searches</h3>
 *
 * Both names. A document named "Maple Street renewal" is still findable by the
 * filename it arrived as, which matters because that is what somebody has in
 * their email when they come looking for it.
 */
export function DocumentList({
  documents,
  canWrite,
  canAdminister,
}: {
  documents: LeaseDocumentSummary[];
  /** Renaming is a label change, so write level is enough. */
  canWrite: boolean;
  /** Removal is not. */
  canAdminister: boolean;
}) {
  const router = useRouter();
  const [query, setQuery] = useState("");
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);

  const [renaming, setRenaming] = useState<LeaseDocumentSummary | null>(null);
  const [draftName, setDraftName] = useState("");
  const [deleting, setDeleting] = useState<LeaseDocumentSummary | null>(null);

  const visible = useMemo(() => {
    const needle = query.trim().toLowerCase();

    if (!needle) {
      return documents;
    }

    return documents.filter((document) =>
      `${document.displayName ?? ""} ${document.originalFilename}`
        .toLowerCase()
        .includes(needle),
    );
  }, [documents, query]);

  function openRename(document: LeaseDocumentSummary) {
    setError(null);
    // Seeded with the current label, not the filename: opening the dialog on a
    // document that has never been named should offer an empty box rather than
    // a filename to delete by hand.
    setDraftName(document.displayName ?? "");
    setRenaming(document);
  }

  function rename() {
    if (!renaming) {
      return;
    }

    setError(null);

    startTransition(async () => {
      const result = await renameDocumentAction(renaming.id, draftName);

      if (!result.ok) {
        setError(result.error ?? "That document could not be renamed.");

        return;
      }

      setRenaming(null);
      router.refresh();
    });
  }

  function remove() {
    if (!deleting) {
      return;
    }

    setError(null);

    startTransition(async () => {
      const result = await deleteDocumentAction(deleting.id);

      if (!result.ok) {
        setError(result.error ?? "That document could not be removed.");

        return;
      }

      setDeleting(null);
      router.refresh();
    });
  }

  return (
    <div>
      {/* The search sits above the table and outside it, so an empty result
          still leaves somewhere to clear the query from. */}
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2 px-5 pt-4">
        <label className="relative flex-1 sm:max-w-xs">
          <span className="sr-only">Search documents</span>
          <Search
            className="pointer-events-none absolute top-1/2 left-2.5 size-3.5
                       -translate-y-1/2 text-ink-muted"
            aria-hidden="true"
          />
          <input
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Search by name or filename"
            className="w-full rounded-control border border-line-strong bg-surface
                       py-1.5 pr-8 pl-8 text-[13px] transition-colors
                       focus:border-accent focus:outline-none focus:ring-2
                       focus:ring-accent/20"
          />
          {query ? (
            <button
              type="button"
              onClick={() => setQuery("")}
              aria-label="Clear search"
              className="absolute top-1/2 right-2 -translate-y-1/2 rounded-control p-0.5
                         text-ink-muted transition-colors hover:text-ink"
            >
              <X className="size-3.5" aria-hidden="true" />
            </button>
          ) : null}
        </label>

        <p className="text-[12.5px] text-ink-secondary">
          {query
            ? `${visible.length} of ${documents.length}`
            : `${documents.length} document${documents.length === 1 ? "" : "s"}`}
        </p>
      </div>

      {error ? (
        <div className="px-5 pb-3">
          <p
            role="alert"
            className="reveal flex items-start gap-1.5 rounded-control border
                       border-danger/30 bg-danger/10 px-3 py-2 text-[12.5px]
                       text-danger-text"
          >
            <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            {error}
          </p>
        </div>
      ) : null}

      {visible.length === 0 ? (
        query ? (
          <EmptyState
            compact
            title="Nothing matches that"
            description="Search looks at both the name you gave a document and the filename it was uploaded with."
          />
        ) : (
          <EmptyState
            title="No lease documents yet"
            description="Upload a signed lease, a renewal, or an amendment. A photograph
                         of the pages works too."
          />
        )
      ) : (
        <TableFrame minWidth="52rem">
          <thead>
            <tr>
              <Th>Document</Th>
              <Th>Status</Th>
              <Th>Pages</Th>
              <Th>Size</Th>
              <Th>Uploaded</Th>
              <Th />
            </tr>
          </thead>
          <tbody>
            {visible.map((document) => (
              <tr key={document.id} className="hover:bg-surface-subtle">
                <Td className="font-medium">
                  <Link
                    href={`/documents/${document.id}`}
                    className="transition-colors hover:text-accent"
                  >
                    {documentLabel(document)}
                  </Link>

                  {/* Shown underneath only when it differs, so a renamed
                      document still says what arrived without repeating
                      itself on every other row. */}
                  {document.displayName
                  && document.displayName !== document.originalFilename ? (
                    <p className="mt-0.5 truncate text-[11.5px] font-normal text-ink-muted">
                      {document.originalFilename}
                    </p>
                  ) : null}

                  {document.processingError ? (
                    <p className="mt-0.5 text-[12px] font-normal text-danger-text">
                      {document.processingError}
                    </p>
                  ) : null}
                </Td>

                <Td>
                  <Status tone={documentStatusTone(document.processingStatus)}>
                    {documentStatusLabel(document.processingStatus)}
                  </Status>
                </Td>

                <Td className="tabular text-[13px] text-ink-secondary">
                  {document.pageCount ?? "—"}
                </Td>

                <Td className="tabular text-[13px] text-ink-secondary">
                  {formatFileSize(document.fileSizeBytes)}
                </Td>

                <Td className="tabular text-[13px] text-ink-secondary">
                  {new Date(document.uploadedAt).toLocaleDateString()}
                </Td>

                <Td className="text-right">
                  <div className="flex items-center justify-end gap-1">
                    {document.processingStatus === "reviewReady" ? (
                      <Link
                        href={`/documents/${document.id}`}
                        className="inline-flex items-center rounded-control border
                                   border-accent bg-accent-tint px-2.5 py-1
                                   text-[12.5px] font-semibold text-accent
                                   transition-colors hover:bg-accent/15"
                      >
                        {document.leaseId ? "View" : "Review"}
                      </Link>
                    ) : null}

                    {canWrite ? (
                      <Button
                        size="sm"
                        variant="ghost"
                        onClick={() => openRename(document)}
                      >
                        <Pencil className="size-3.5" aria-hidden="true" />
                        <span className="sr-only">
                          Rename {documentLabel(document)}
                        </span>
                      </Button>
                    ) : null}

                    {canAdminister ? (
                      <Button
                        size="sm"
                        variant="ghost"
                        onClick={() => {
                          setError(null);
                          setDeleting(document);
                        }}
                      >
                        <Trash2 className="size-3.5" aria-hidden="true" />
                        <span className="sr-only">
                          Remove {documentLabel(document)}
                        </span>
                      </Button>
                    ) : null}
                  </div>
                </Td>
              </tr>
            ))}
          </tbody>
        </TableFrame>
      )}

      {renaming ? (
        <FormDialog
          title="Rename document"
          description="This is the name shown in lists. The uploaded filename is kept as a record and does not change."
          submitLabel="Save name"
          isSubmitting={isPending}
          onSubmit={rename}
          onCancel={() => setRenaming(null)}
        >
          <Field
            label="Name"
            htmlFor="rename-document"
            hint="Leave it empty to go back to showing the filename."
          >
            <input
              id="rename-document"
              value={draftName}
              onChange={(event) => setDraftName(event.target.value)}
              placeholder={renaming.originalFilename}
              maxLength={300}
              className={fieldClass}
            />
          </Field>

          <p className="rounded-control border border-line bg-surface-subtle px-3 py-2
                        text-[12px] leading-relaxed text-ink-secondary">
            Uploaded as{" "}
            <span className="font-mono text-[11.5px]">
              {renaming.originalFilename}
            </span>
          </p>
        </FormDialog>
      ) : null}

      {deleting ? (
        <FormDialog
          title="Remove this document?"
          description="It disappears from this list. The stored file is kept until the retention period removes it."
          submitLabel="Remove"
          isSubmitting={isPending}
          onSubmit={remove}
          onCancel={() => setDeleting(null)}
        >
          <div className="rounded-control border border-line bg-surface-subtle px-3 py-2.5">
            <p className="text-[13px] font-medium">{documentLabel(deleting)}</p>
            <p className="mt-0.5 text-[12px] text-ink-secondary">
              {formatFileSize(deleting.fileSizeBytes)}
              {deleting.pageCount ? ` · ${deleting.pageCount} pages` : ""}
            </p>
          </div>

          {deleting.leaseId ? (
            <p
              className="rounded-control border border-warning/40 bg-warning/10 px-3
                         py-2 text-[12.5px] leading-relaxed text-warning-text"
            >
              A lease was approved from this document. Removing it does not
              undo that approval or stop any reminder — the lease keeps the
              terms that were approved, and this only removes the file from
              the list.
            </p>
          ) : null}
        </FormDialog>
      ) : null}
    </div>
  );
}
