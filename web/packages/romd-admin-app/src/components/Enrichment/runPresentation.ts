import type {
  ArtworkCompletionItemDto,
  ArtworkCompletionMetadataOutcome,
} from '@romd/admin-api-client';
import { artworkRoleLabel } from '../artworkPresentation';
import type { EnrichmentRun } from './useEnrichmentRun';

// Run copy reports recorded execution and current review needs separately and never claims more than either shows.

const terminalPhases = [
  'Completed',
  'CompletedWithErrors',
  'Failed',
  'Cancelled',
];

/** Execution can still move until the run reaches one of the four terminal phases; after that it is history. */
export function isRunActive(phase: string): boolean {
  return !terminalPhases.includes(phase);
}

const plural = (count: number, one: string, many: string) =>
  `${count.toLocaleString()} ${count === 1 ? one : many}`;

export function runHeading({ mode, total }: Pick<EnrichmentRun, 'mode' | 'total'>): string {
  const titles = plural(total, 'title', 'titles');
  if (mode === 'Enrich') return `This run · Enrich ${titles}`;
  if (mode === 'Artwork') return `This run · Fill artwork for ${titles}`;
  return `This run · ${titles}`;
}

const nextAttemptTime = (value: string) =>
  new Date(value).toLocaleTimeString(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  });

const terminalLines: Partial<Record<string, string>> = {
  Completed: 'Finished',
  CompletedWithErrors: 'Finished with errors',
  Failed: 'Stopped with an error',
  Cancelled: 'Cancelled',
};

/** What execution did, from the job row alone. */
export function executionLine(
  run: Pick<EnrichmentRun, 'phase' | 'processed' | 'total' | 'nextAttemptAt'>,
  now = Date.now(),
): string {
  const progress = `${run.processed.toLocaleString()} of ${run.total.toLocaleString()} processed`;
  if (isRunActive(run.phase) && run.nextAttemptAt && Date.parse(run.nextAttemptAt) > now)
    return `Paused · next attempt at ${nextAttemptTime(run.nextAttemptAt)}`;
  if (run.phase === 'Pending') return 'Queued';
  if (run.phase === 'Enriching') return `Running · ${progress}`;
  return `${terminalLines[run.phase] ?? run.phase} · ${progress}`;
}

/** Review is current, failed and unfinished are recorded; they overlap, so they are listed and never summed. */
export function runCountsLine({
  review,
  failed,
  unfinished,
}: Pick<EnrichmentRun, 'review' | 'failed' | 'unfinished'>): string {
  return [
    `${plural(review, 'needs', 'need')} review now`,
    `${failed.toLocaleString()} failed`,
    `${unfinished.toLocaleString()} unfinished`,
  ].join(' · ');
}

export function globalReviewLabel(needsReview: number | undefined): string {
  return needsReview === undefined
    ? 'Global queue →'
    : `Global queue: ${plural(needsReview, 'needs', 'need')} review →`;
}

const metadataOutcomes: Record<ArtworkCompletionMetadataOutcome, string> = {
  Succeeded: 'Metadata step completed',
  Failed: 'Metadata failed',
  NotAttempted: 'Metadata not attempted',
};

/** Null is history recorded before outcomes existed: unknown, never a success or a failure. */
export function metadataOutcomeLabel(outcome: ArtworkCompletionMetadataOutcome | null): string {
  return outcome ? metadataOutcomes[outcome] : 'Metadata: not recorded';
}

const roleOutcomes: Partial<Record<string, string>> = {
  Updated: 'filled',
  AlreadyPresent: 'already present',
  NeedsMatch: 'waiting for a provider match',
  NeedsReview: 'candidates ready',
  Failed: 'failed',
  Unavailable: 'nothing usable found',
  Disabled: 'not requested in settings',
  Unlinked: 'provider unlinked',
  ProviderUnconfigured: 'provider not configured',
  ProviderDisabled: 'no provider enabled',
  LeftEmpty: 'left empty by you',
  Superseded: 'skipped because your curation changed',
  Untracked: 'skipped because the title is untracked',
  Removed: 'skipped because the title was removed',
};

export function roleOutcomeLabel(role: string, outcome: string): string {
  return `${artworkRoleLabel(role)}: ${roleOutcomes[outcome] ?? `recorded as ${outcome}`}`;
}

/** The leaf execution's own record for one title: its metadata step, then each artwork role. */
export function recordedLine({
  metadataOutcome,
  roles,
}: Pick<ArtworkCompletionItemDto, 'metadataOutcome' | 'roles'>): string {
  return `Recorded: ${[
    metadataOutcomeLabel(metadataOutcome),
    ...roles.map((row) => roleOutcomeLabel(row.role, row.status)),
  ].join(' · ')}`;
}
