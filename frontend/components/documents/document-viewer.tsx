"use client";

import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, ExternalLink, FileText, RefreshCw } from "lucide-react";

import { Button } from "@/components/ui/primitives";

/**
 * The left column of the review screen: the document itself.
 *
 * <h3>Why the URL is fetched here and refreshed</h3>
 *
 * A signed URL is a bearer credential for one object with a few minutes of
 * life, which is what section 4.C asks for. That has a consequence for this
 * component: the link it is holding goes stale while somebody is still
 * reading. So it fetches on mount, tracks when the URL expires, and renews it
 * shortly before. A reviewer working through forty fields should never meet a
 * blank frame.
 *
 * <h3>Why an iframe and not a PDF library</h3>
 *
 * Every browser that matters renders PDFs natively, with text selection,
 * search, and page navigation already built. A JavaScript PDF renderer would
 * add a megabyte of script and a second parser for untrusted input, to produce
 * something worse. Images are rendered directly. A Word document cannot be
 * displayed at all, so it offers a download instead of pretending.
 */
export function DocumentViewer({
  documentId,
  filename,
  mimeType,
  pageCount,
  highlightedPage,
}: {
  documentId: string;
  filename: string;
  mimeType: string | null;
  pageCount: number | null;
  /** The page a selected field cites, so the viewer can jump to it. */
  highlightedPage: number | null;
}) {
  const [url, setUrl] = useState<string | null>(null);
  const [expiresAt, setExpiresAt] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  // A counter rather than a function reference. Renewal has to re-run the
  // fetch, and the obvious way to write that -- a callback whose timer calls
  // the callback -- reads its own binding before it is declared, so the timer
  // captures a stale one. Bumping a number that the fetching effect depends on
  // expresses "do it again" without any self-reference.
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      setIsLoading(true);
      setError(null);

      try {
        const response = await fetch(`/documents/api/url?documentId=${documentId}`, {
          cache: "no-store",
        });

        if (!response.ok) {
          throw new Error("The document could not be opened.");
        }

        const body = (await response.json()) as { url: string; expiresAt: string };

        // The request may finish after the component has gone, or after a
        // newer attempt superseded this one. Writing then would resurrect a
        // stale URL over a fresh one.
        if (cancelled) {
          return;
        }

        setUrl(body.url);
        setExpiresAt(body.expiresAt);
      } catch {
        if (!cancelled) {
          setError("The document could not be opened. It may still be uploading.");
        }
      } finally {
        if (!cancelled) {
          setIsLoading(false);
        }
      }
    }

    void load();

    return () => {
      cancelled = true;
    };
  }, [documentId, attempt]);

  // Renewal. A signed URL lives for minutes, and a reviewer works through a
  // lease for longer than that, so the link is replaced shortly before it
  // stops working rather than after somebody meets a blank frame.
  useEffect(() => {
    if (!expiresAt) {
      return;
    }

    // A minute of margin, with a floor so a short configured lifetime cannot
    // turn renewal into a tight polling loop.
    const msUntilExpiry = new Date(expiresAt).getTime() - Date.now();
    const renewIn = Math.max(30_000, msUntilExpiry - 60_000);

    const timer = setTimeout(() => setAttempt((previous) => previous + 1), renewIn);

    return () => clearTimeout(timer);
  }, [expiresAt]);

  const reload = useCallback(() => setAttempt((previous) => previous + 1), []);

  const isPdf = mimeType === "application/pdf";
  const isImage = mimeType === "image/jpeg" || mimeType === "image/png";

  return (
    <div className="flex h-full flex-col">
      <header className="flex items-center justify-between gap-3 border-b border-line px-4 py-2.5">
        <div className="flex min-w-0 items-center gap-2">
          <FileText className="size-4 shrink-0 text-ink-muted" aria-hidden="true" />
          <span className="truncate text-[13px] font-medium" title={filename}>
            {filename}
          </span>
          {pageCount ? (
            <span className="tabular shrink-0 text-[12px] text-ink-secondary">
              {pageCount} page{pageCount === 1 ? "" : "s"}
            </span>
          ) : null}
        </div>

        <div className="flex shrink-0 items-center gap-1.5">
          {highlightedPage ? (
            <span className="rounded-control bg-accent-tint px-2 py-0.5 text-[12px] font-medium text-accent">
              Cited on page {highlightedPage}
            </span>
          ) : null}

          <Button size="sm" variant="ghost" onClick={reload}>
            <RefreshCw className="size-3.5" aria-hidden="true" />
            <span className="sr-only">Reload the document</span>
          </Button>
        </div>
      </header>

      <div className="relative flex-1 overflow-hidden bg-surface-sunken">
        {isLoading && !url ? (
          <Centred>
            <p className="text-[13px] text-ink-secondary">Opening the document…</p>
          </Centred>
        ) : error ? (
          <Centred>
            <AlertTriangle
              className="mb-2 size-5 text-warning-text"
              aria-hidden="true"
            />
            <p className="max-w-xs text-center text-[13px] text-ink-secondary">{error}</p>
            <Button size="sm" className="mt-3" onClick={reload}>
              Try again
            </Button>
          </Centred>
        ) : url && isPdf ? (
          <iframe
            // Keyed on the page so changing the citation re-navigates the
            // embedded viewer; the fragment alone would not, since the src
            // string changes but the frame is not reloaded.
            key={`${url}#${highlightedPage ?? 1}`}
            src={`${url}#page=${highlightedPage ?? 1}&view=FitH`}
            title={`${filename}, page ${highlightedPage ?? 1}`}
            className="size-full border-0"
            // The document is untrusted input. A PDF can carry script, and
            // this frame is served from a storage origin rather than ours, so
            // it is given nothing: no scripts, no forms, no top-level
            // navigation, no same-origin privileges.
            sandbox=""
          />
        ) : url && isImage ? (
          <div className="thin-scrollbar size-full overflow-auto p-4">
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img
              src={url}
              alt={`Photograph of ${filename}`}
              className="mx-auto max-w-full rounded-control border border-line bg-surface shadow-sm"
            />
          </div>
        ) : (
          <Centred>
            <FileText className="mb-2 size-5 text-ink-muted" aria-hidden="true" />
            <p className="max-w-xs text-center text-[13px] text-ink-secondary">
              A Word document cannot be shown here. Open it to check the
              extracted terms against it.
            </p>
            {url ? (
              <a
                href={url}
                target="_blank"
                rel="noreferrer noopener"
                className="mt-3 inline-flex items-center gap-1.5 rounded-control border
                           border-line-strong bg-surface px-2.5 py-1.5 text-[12.5px]
                           font-medium hover:bg-surface-subtle"
              >
                <ExternalLink className="size-3.5" aria-hidden="true" />
                Open the document
              </a>
            ) : null}
          </Centred>
        )}
      </div>
    </div>
  );
}

function Centred({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex size-full flex-col items-center justify-center p-6">
      {children}
    </div>
  );
}
