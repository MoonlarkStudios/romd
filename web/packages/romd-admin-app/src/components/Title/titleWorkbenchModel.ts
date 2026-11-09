import type { EnrichmentAssessment, EnrichmentRequirement } from '@romd/admin-api-client';
import { type ArtworkRole, artworkRoles } from '../artworkPresentation';
import { isOutstanding, outstandingRequirements } from './enrichmentPresentation';
import type { TitleEditor } from './useTitleEditorParams';

export const workbenchViews = [
  'NeedsReview',
  'Automatic',
  'Processing',
  'Blocked',
  'All',
] as const;
export type WorkbenchView = (typeof workbenchViews)[number];

type StatusFlags = Pick<
  EnrichmentAssessment,
  'needsReview' | 'hasOperationalBlocker' | 'hasActiveWork' | 'canEnrichAutomatically'
>;
const viewFlags: Record<Exclude<WorkbenchView, 'All'>, keyof StatusFlags> = {
  NeedsReview: 'needsReview',
  Automatic: 'canEnrichAutomatically',
  Processing: 'hasActiveWork',
  Blocked: 'hasOperationalBlocker',
};

/** Whether the title still carries the view's flag; every tracked title belongs to All. */
export function isInView(flags: StatusFlags, view: WorkbenchView): boolean {
  return view === 'All' || flags[viewFlags[view]];
}

/**
 * The requirement the workbench leads with: the linked one while it is still outstanding, else the first
 * outstanding row in the view's state, else the first outstanding row. Undefined when nothing is outstanding.
 */
export function currentRequirement(
  requirements: EnrichmentRequirement[],
  view: WorkbenchView,
  linkedKey?: string,
): EnrichmentRequirement | undefined {
  const outstanding = outstandingRequirements(requirements);
  return (
    outstanding.find((row) => row.key === linkedKey) ??
    outstanding.find((row) => view === 'All' || row.state === view) ??
    outstanding[0]
  );
}

export const checklistKinds = [
  'Identity',
  'Metadata',
  'Artwork',
] as const;
export type ChecklistKind = (typeof checklistKinds)[number];
export interface ChecklistGroup {
  kind: ChecklistKind;
  rows: EnrichmentRequirement[];
  outstanding: number;
}

/** The complete list grouped by kind in the backend's canonical order. Operation rows are a banner, not rows. */
export function checklistGroups(requirements: EnrichmentRequirement[]): ChecklistGroup[] {
  return checklistKinds
    .map((kind) => {
      const rows = requirements.filter((row) => row.kind === kind);
      return {
        kind,
        rows,
        outstanding: rows.filter(isOutstanding).length,
      };
    })
    .filter((group) => group.rows.length > 0);
}

export function processingOperation(
  requirements: EnrichmentRequirement[],
): EnrichmentRequirement | undefined {
  return requirements.find((row) => row.kind === 'Operation' && row.state === 'Processing');
}

/** Rows present now that were absent from the previous list for the same title. */
export function addedRequirementCount(
  previousKeys: readonly string[],
  requirements: EnrichmentRequirement[],
): number {
  return requirements.filter((row) => row.kind !== 'Operation' && !previousKeys.includes(row.key))
    .length;
}

const reviewEditors: Partial<Record<string, TitleEditor>> = {
  ReviewProvider: 'match',
  ReviewMetadata: 'metadata',
  ReviewArtwork: 'artwork',
};
const kindEditors: Partial<Record<string, TitleEditor>> = {
  Identity: 'match',
  Artwork: 'artwork',
};

export function isReviewAction(action: string): boolean {
  return reviewEditors[action] !== undefined;
}

/** The editor a review action opens; without one, the editor that owns the row's kind. */
export function reviewEditor(
  requirement: EnrichmentRequirement | undefined,
  action?: string,
): TitleEditor {
  return (
    (action ? reviewEditors[action] : undefined) ??
    requirement?.actions
      .map((item) => reviewEditors[item])
      .find((editor) => editor !== undefined) ??
    kindEditors[requirement?.kind ?? ''] ??
    'metadata'
  );
}

export function artworkRole(
  requirement: EnrichmentRequirement | undefined,
): ArtworkRole | undefined {
  return requirement?.kind === 'Artwork'
    ? artworkRoles.find((role) => role === requirement.subject)
    : undefined;
}

export type WorkbenchDensity = 'compact' | 'comfortable';

/** What every title editor accepts from the workbench: layout density and the outcome of a curator's editing. */
export interface TitleEditorOutcome {
  density?: WorkbenchDensity;
  /** A save or decision in the editor succeeded. */
  onResolved?: () => void;
  /** The curator closed the editor's draft without saving. */
  onDismissed?: () => void;
}
