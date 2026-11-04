"use client";

import { useRef, useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { CloudUpload, TriangleAlert } from "lucide-react";

import { Button } from "@/components/ui/primitives";
import { PermissionNotice } from "@/components/ui/permission-notice";
import { formatFileSize } from "@/lib/documents";

/** Matches the API's limit and the database's CHECK constraint. */
const MAXIMUM_BYTES = 50 * 1024 * 1024;

/**
 * What the file picker offers.
 *
 * <p>
 * A convenience, not a control. The browser uses it to filter the dialog; a
 * determined caller can send anything. What actually decides is the server's
 * inspection of the file's leading bytes, which is why the list here and the
 * validator there are allowed to be written in different terms.
 * </p>
 */
const ACCEPTED = ".pdf,.docx,.jpg,.jpeg,.png";

/**
 * Uploads a lease document.
 *
 * <h3>Why this posts through a route handler</h3>
 *
 * A server action would mean the file crossing the network twice: once to the
 * Next server as an action payload, then again to the API. For a 50 MB scan
 * that is slow and doubles the memory cost. This posts to a thin route handler
 * that streams the body straight through with the session's token attached.
 */
export function UploadPanel({
  organizationId,
  canUpload,
  permissionReason,
  extractionAvailable,
}: {
  organizationId: string;
  canUpload: boolean;
  /** From `permissions(role).reason`. Names the caller's own role. */
  permissionReason: string | null;
  extractionAvailable: boolean;
}) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const [error, setError] = useState<string | null>(null);
  const [isDragging, setIsDragging] = useState(false);
  const [uploading, setUploading] = useState<string | null>(null);
  const [name, setName] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);

  async function upload(file: File) {
    setError(null);

    // Checked here only so an obviously oversized file fails instantly
    // instead of after a long upload. The server checks again, and the
    // database constrains the column, because neither can trust this.
    if (file.size > MAXIMUM_BYTES) {
      setError(
        `That file is ${formatFileSize(file.size)}. The limit is 50 MB.`,
      );

      return;
    }

    setUploading(file.name);

    try {
      const body = new FormData();
      body.append("file", file);
      body.append("organizationId", organizationId);

      // Optional. Without one the document shows its filename, which is what
      // the list falls back to anyway.
      if (name.trim()) {
        body.append("name", name.trim());
      }

      const response = await fetch("/documents/api/upload", {
        method: "POST",
        body,
      });

      if (!response.ok) {
        const payload = (await response.json().catch(() => null)) as
          | { error?: string }
          | null;

        setError(payload?.error ?? "The document could not be uploaded.");

        return;
      }

      // Cleared on success only. A failed upload is likely to be retried,
      // and retyping the name would be a second penalty for one mistake.
      setName("");
      startTransition(() => router.refresh());
    } catch {
      setError("The document could not be uploaded. Check your connection.");
    } finally {
      setUploading(null);
    }
  }

  if (!canUpload) {
    return (
      <PermissionNotice
        reason={permissionReason}
        action="Uploading lease documents"
      />
    );
  }

  return (
    <div>
      <div
        onDragOver={(event) => {
          event.preventDefault();
          setIsDragging(true);
        }}
        onDragLeave={() => setIsDragging(false)}
        onDrop={(event) => {
          event.preventDefault();
          setIsDragging(false);

          const file = event.dataTransfer.files[0];

          if (file) {
            void upload(file);
          }
        }}
        className={`rounded-surface border-2 border-dashed px-6 py-8 text-center
                    transition-colors ${
                      isDragging
                        ? "border-accent bg-accent-tint"
                        : "border-line-strong bg-surface-subtle"
                    }`}
      >
        <CloudUpload
          className="mx-auto mb-2 size-6 text-ink-muted"
          aria-hidden="true"
        />

        <p className="text-[13.5px] font-medium">
          {uploading ? `Uploading ${uploading}…` : "Drop a lease here"}
        </p>

        <p className="mx-auto mt-1 max-w-sm text-[12.5px] leading-relaxed text-ink-secondary">
          A PDF, a Word document, or a photograph of the signed pages. Up to
          50 MB.
          {extractionAvailable
            ? " The terms will be read out automatically for you to check."
            : " Automatic reading is not configured, so the terms will need entering by hand."}
        </p>

        <input
          ref={inputRef}
          type="file"
          accept={ACCEPTED}
          className="sr-only"
          onChange={(event) => {
            const file = event.target.files?.[0];

            if (file) {
              void upload(file);
            }

            // Cleared so selecting the same file twice in a row still fires a
            // change event.
            event.target.value = "";
          }}
        />

        {/* Asked for before the file, because afterwards is too late: the
            upload starts the moment a file is chosen. */}
        <div className="mx-auto mt-3 max-w-sm text-left">
          <label
            htmlFor="document-name"
            className="block text-[12px] font-medium text-ink-secondary"
          >
            Name this document{" "}
            <span className="font-normal text-ink-muted">(optional)</span>
          </label>
          <input
            id="document-name"
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder="Maple Street — 2026 renewal"
            maxLength={300}
            disabled={uploading !== null}
            className="mt-1 w-full rounded-control border border-line-strong bg-surface
                       px-2.5 py-1.5 text-[13px] transition-colors focus:border-accent
                       focus:outline-none focus:ring-2 focus:ring-accent/20
                       disabled:bg-surface-subtle"
          />
          <p className="mt-1 text-[11.5px] leading-relaxed text-ink-muted">
            Left blank, it shows the filename. You can rename it later either
            way — the uploaded filename is kept as a record regardless.
          </p>
        </div>

        <Button
          className="mt-3"
          variant="primary"
          size="sm"
          disabled={uploading !== null || isPending}
          onClick={() => inputRef.current?.click()}
        >
          Choose a file
        </Button>
      </div>

      {error ? (
        <p
          role="alert"
          className="mt-2 flex items-start gap-1.5 rounded-control border
                     border-danger/30 bg-danger/10 px-3 py-2 text-[12.5px] text-danger-text"
        >
          <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          {error}
        </p>
      ) : null}

      <p className="mt-2 text-[12px] leading-relaxed text-ink-secondary">
        Nothing read from a document takes effect on its own. Every term is a
        suggestion until somebody approves it.
      </p>
    </div>
  );
}
