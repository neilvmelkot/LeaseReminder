"use server";

import { revalidatePath } from "next/cache";

import { ApiError, templateApi, type TemplatePreview } from "@/lib/api";

export interface ActionResult {
  ok: boolean;
  error?: string;
}

export interface PreviewResult extends ActionResult {
  preview?: TemplatePreview;
}

/**
 * Saves a draft.
 *
 * <p>
 * A published version is immutable, so saving never touches one: it creates
 * or updates the draft beside it. That is not a limitation of this action but
 * a database trigger, and it exists so an email log entry always refers to
 * text that still says what it said when it was sent.
 * </p>
 */
export async function saveTemplateAction(
  organizationId: string,
  templateKey: string,
  body: { name: string; subject: string; htmlBody: string; textBody: string },
): Promise<ActionResult> {
  try {
    await templateApi.saveDraft(organizationId, templateKey, body);

    revalidatePath("/settings/templates");

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describe(error, "save this draft") };
  }
}

/**
 * Publishes a draft, archiving the version it replaces.
 */
export async function publishTemplateAction(
  organizationId: string,
  templateId: string,
): Promise<ActionResult> {
  try {
    await templateApi.publish(organizationId, templateId);

    revalidatePath("/settings/templates");

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describe(error, "publish this template") };
  }
}

export async function discardDraftAction(
  organizationId: string,
  templateId: string,
): Promise<ActionResult> {
  try {
    await templateApi.discardDraft(organizationId, templateId);

    revalidatePath("/settings/templates");

    return { ok: true };
  } catch (error) {
    return { ok: false, error: describe(error, "discard this draft") };
  }
}

/**
 * Renders a template with sample values.
 *
 * <p>
 * Runs the renderer the worker uses, on the server, rather than a lookalike
 * in the browser. A preview produced by different code is a preview that can
 * be wrong about the one thing it exists to show.
 * </p>
 */
export async function previewTemplateAction(
  organizationId: string,
  body: { subject: string; htmlBody: string; textBody: string },
): Promise<PreviewResult> {
  try {
    const preview = await templateApi.preview(organizationId, body);

    return { ok: true, preview };
  } catch (error) {
    return { ok: false, error: describe(error, "preview this template") };
  }
}

function describe(error: unknown, attempted: string): string {
  if (error instanceof ApiError) {
    return error.isUnauthenticated
      ? "Your session has expired. Sign in again to continue."
      : error.message;
  }

  return `The server could not be reached, so it was not possible to ${attempted}.`;
}
