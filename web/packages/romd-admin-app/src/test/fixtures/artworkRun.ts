import type { EnrichmentRun } from '../../components/Enrichment/useEnrichmentRun';

/** Canonical execution state deliberately excludes the separately fetched summary. */
export function artworkJobFixture(run: EnrichmentRun) {
  return {
    jobType: 'artwork-completion',
    id: run.jobId,
    correlationId: 'run-correlation',
    sourceFilename: 'Enrichment run',
    phase: run.phase,
    total: run.total,
    processed: run.processed,
    mode: run.mode,
    nextAttemptAt: run.nextAttemptAt,
    createdAt: run.createdAt,
    isTerminal: [
      'Completed',
      'CompletedWithErrors',
      'Failed',
      'Cancelled',
    ].includes(run.phase),
    progressPercent: run.total ? (run.processed / run.total) * 100 : 0,
    errors: [],
  };
}
