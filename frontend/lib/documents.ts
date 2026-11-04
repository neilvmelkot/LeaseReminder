/**
 * Types and helpers for lease documents and their extraction review.
 *
 * The field catalogue is NOT duplicated here. It arrives with each document
 * from the API, because it is the same list the model was asked for: a second
 * copy in TypeScript would be a second thing to keep in step, and the symptom
 * of the two drifting apart would be a field rendered with no label.
 */

export type DocumentProcessingStatus =
  | "uploaded"
  | "validating"
  | "quarantined"
  | "queued"
  | "extracting"
  | "reviewReady"
  | "failed";

export type FieldReviewStatus =
  | "pending"
  | "accepted"
  | "corrected"
  | "rejected"
  | "flagged";

export type ExtractionReviewStatus = "pending" | "accepted" | "corrected" | "rejected";

export type ValueProvenance =
  | "statedInDocument"
  | "inferredByCalculation"
  | "userEntered"
  | "defaultApplied";

export type FieldImportance = "Required" | "Important" | "Supplementary";

export interface LeaseDocumentSummary {
  id: string;
  originalFilename: string;
  /**
   * The editable label, or null.
   *
   * originalFilename is what was uploaded and never changes — it is the
   * record of what arrived. Use {@link documentLabel} to show whichever one
   * a person should read.
   */
  displayName: string | null;
  detectedMimeType: string | null;
  fileSizeBytes: number;
  pageCount: number | null;
  processingStatus: DocumentProcessingStatus;
  processingError: string | null;
  leaseId: string | null;
  uploadedAt: string;
}

export interface CatalogueField {
  name: string;
  label: string;
  group: string;
  description: string;
  type: string;
  importance: FieldImportance;
  allowedValues: string[] | null;
}

export interface ExtractedField {
  id: string;
  fieldName: string;
  valueType: string;
  extractedValue: string | null;
  /** The typed value, or null when it could not be parsed. */
  normalizedValue: unknown;
  confidence: number | null;
  provenance: ValueProvenance;
  sourcePage: number | null;
  sourceExcerpt: string | null;
  reviewStatus: FieldReviewStatus;
  correctedValue: unknown;
  issueCodes: string[];
}

export interface ExtractionIssue {
  id: string;
  issueCode: string;
  severity: "info" | "warning" | "blocking";
  message: string;
  sourcePages: number[];
  isResolved: boolean;
}

export interface ExtractionSummary {
  id: string;
  provider: string;
  model: string | null;
  extractionVersion: string;
  reviewStatus: ExtractionReviewStatus;
  completedAt: string | null;
  errorMessage: string | null;
  /** What still stands between this document and approval. */
  pendingFieldCount: number;
  blockingIssueCount: number;
}

export interface DocumentDetail {
  document: LeaseDocumentSummary & { organizationId: string };
  extraction: ExtractionSummary | null;
  fields: ExtractedField[];
  issues: ExtractionIssue[];
  catalogue: CatalogueField[];
}

/* -------------------------------------------------------------------------- */
/* Presentation                                                                */
/* -------------------------------------------------------------------------- */

const statusLabels: Record<DocumentProcessingStatus, string> = {
  uploaded: "Uploaded",
  validating: "Checking",
  quarantined: "Quarantined",
  queued: "Waiting to be read",
  extracting: "Being read",
  reviewReady: "Ready for review",
  failed: "Could not be read",
};

export function documentStatusLabel(status: DocumentProcessingStatus): string {
  return statusLabels[status] ?? status;
}

export function documentStatusTone(
  status: DocumentProcessingStatus,
): "neutral" | "info" | "success" | "warning" | "danger" {
  switch (status) {
    case "reviewReady":
      return "info";
    case "failed":
    case "quarantined":
      return "danger";
    case "queued":
    case "extracting":
    case "validating":
      return "warning";
    default:
      return "neutral";
  }
}

const issueLabels: Record<string, string> = {
  embedded_instructions: "The document contains text addressed to an automated reader",
  conflicting_clause: "Two clauses disagree",
  illegible_text: "Part of the document could not be read",
  missing_signature: "No signature was found",
  incomplete_document: "The document looks incomplete",
  ambiguous_term: "A term is ambiguous",
  handwritten_amendment: "There is a handwritten amendment",
  unsupported_language: "The document is not in a supported language",
  // Raised by the parser rather than the model.
  unparseable_value: "The value could not be read as the expected type",
  out_of_range: "The value was outside the possible range",
  duplicate_answer: "The document was read as giving two different answers",
  missing_citation: "No supporting passage was given",
};

export function issueLabel(code: string): string {
  return issueLabels[code] ?? code.replaceAll("_", " ");
}

/**
 * How a confidence should read.
 *
 * <p>
 * Deliberately coarse. A model's self-reported confidence is not a
 * probability, and rendering "0.87" invites a reviewer to treat it as one.
 * Three bands say the only thing it can honestly support: whether this is
 * worth a second look.
 * </p>
 */
export function confidenceBand(
  confidence: number | null,
): { label: string; tone: "success" | "warning" | "danger" } {
  if (confidence === null) {
    return { label: "Not rated", tone: "danger" };
  }

  if (confidence >= 0.9) {
    return { label: "Clear", tone: "success" };
  }

  if (confidence >= 0.75) {
    return { label: "Fairly clear", tone: "warning" };
  }

  return { label: "Unclear", tone: "danger" };
}

/**
 * Whether a field needs a decision before the lease can be approved.
 *
 * Mirrors the rule the database enforces in
 * `app.approve_lease_from_extraction`. Duplicated on purpose so the screen can
 * say what is outstanding before the user clicks; the database remains the
 * authority, and a disagreement shows up as a refused approval rather than as
 * a wrong one.
 */
export function needsDecision(field: ExtractedField): boolean {
  return field.reviewStatus === "pending" || field.reviewStatus === "flagged";
}

/** Renders a normalised value for display. */
export function displayValue(field: ExtractedField): string | null {
  const value = field.correctedValue ?? field.normalizedValue;

  if (value === null || value === undefined) {
    return null;
  }

  if (typeof value === "boolean") {
    return value ? "Yes" : "No";
  }

  if (typeof value === "number") {
    // Money and rates both arrive as numbers; the type decides the shape.
    if (field.valueType === "money") {
      return value.toLocaleString(undefined, {
        minimumFractionDigits: 2,
        maximumFractionDigits: 2,
      });
    }

    return String(value);
  }

  return String(value);
}

/** Groups fields under their catalogue headings, in catalogue order. */
export function groupFields(
  fields: ExtractedField[],
  catalogue: CatalogueField[],
): Array<{ group: string; fields: Array<{ field: ExtractedField; meta: CatalogueField }> }> {
  const metaByName = new Map(catalogue.map((entry) => [entry.name, entry]));
  const groups: string[] = [];

  for (const entry of catalogue) {
    if (!groups.includes(entry.group)) {
      groups.push(entry.group);
    }
  }

  return groups
    .map((group) => ({
      group,
      fields: fields
        .map((field) => ({ field, meta: metaByName.get(field.fieldName) }))
        .filter(
          (pair): pair is { field: ExtractedField; meta: CatalogueField } =>
            pair.meta !== undefined && pair.meta.group === group,
        ),
    }))
    .filter((entry) => entry.fields.length > 0);
}

/**
 * Catalogue fields marked Required that the extraction has no usable value
 * for.
 *
 * These are shown separately and first: they are not a model error to review
 * but a gap a person has to fill, and mixing them into the list would bury
 * them among thirty answered fields.
 */
export function missingRequired(
  fields: ExtractedField[],
  catalogue: CatalogueField[],
): CatalogueField[] {
  return catalogue.filter((entry) => {
    if (entry.importance !== "Required") {
      return false;
    }

    const found = fields.find((field) => field.fieldName === entry.name);

    return !found || found.normalizedValue === null || found.normalizedValue === undefined;
  });
}

export function formatFileSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  if (bytes < 1024 * 1024) {
    return `${Math.round(bytes / 1024)} KB`;
  }

  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/**
 * What to show as a document's name.
 *
 * <p>
 * The label if there is one, the uploaded filename otherwise. Having one
 * function decide this is what stops half the screens showing
 * "Scan_20260104_0001.pdf" while the other half show "Maple Street renewal".
 * </p>
 */
export function documentLabel(document: {
  displayName?: string | null;
  originalFilename: string;
}): string {
  const named = document.displayName?.trim();

  return named ? named : document.originalFilename;
}
