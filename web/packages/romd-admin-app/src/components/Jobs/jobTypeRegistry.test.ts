import type { JobDto } from '@romd/admin-api-client';
import { describe, expect, it } from 'vitest';
import { getJobOutcome, getJobTitle, summarizeFailureReasons } from './jobTypeRegistry';

function makeJob(overrides: Record<string, unknown>): JobDto {
  return {
    id: '1',
    correlationId: 'c1',
    sourceFilename: '',
    phase: 'Completed',
    progressPercent: '0',
    errors: [],
    isTerminal: true,
    ...overrides,
  } as JobDto;
}

describe('getJobTitle', () => {
  it('prefixes an upload with its verb and filename', () => {
    expect(getJobTitle(makeJob({ jobType: 'upload', sourceFilename: 'snes-pack.zip' }))).toBe(
      'Import snes-pack.zip',
    );
  });

  it('names a single enrichment by the title', () => {
    expect(
      getJobTitle(makeJob({ jobType: 'enrichment', titleId: 't1', sourceFilename: 'Chrono Trigger' })),
    ).toBe('Enrich Chrono Trigger');
  });

  it('names a bulk enrichment by count and platform short name', () => {
    expect(
      getJobTitle(makeJob({ jobType: 'bulk_enrichment', totalTitles: '247', sourceFilename: 'SNES' })),
    ).toBe('Enrich 247 titles · SNES');
  });

  it('falls back to a count-only bulk title for legacy rows', () => {
    // Pre-name jobs stored the literal "Bulk Enrichment" label.
    expect(
      getJobTitle(
        makeJob({ jobType: 'bulk_enrichment', totalTitles: '247', sourceFilename: 'Bulk Enrichment' }),
      ),
    ).toBe('Enrich 247 titles');
  });

  it('names a materialization by its library', () => {
    expect(getJobTitle(makeJob({ jobType: 'materialization', sourceFilename: 'Family Room' }))).toBe(
      'Materialize Family Room',
    );
  });

  it('falls back to a generic materialization title for legacy rows', () => {
    expect(
      getJobTitle(makeJob({ jobType: 'materialization', sourceFilename: 'Library Materialization' })),
    ).toBe('Materialize library');
  });

  it('names an export by file count, ignoring the literal label', () => {
    expect(
      getJobTitle(makeJob({ jobType: 'export', totalFiles: '1200', sourceFilename: 'Library Export' })),
    ).toBe('Export 1,200 files');
  });

  it('falls back gracefully when the subject is empty', () => {
    expect(getJobTitle(makeJob({ jobType: 'upload', sourceFilename: '' }))).toBe('Import files');
  });
});

describe('summarizeFailureReasons', () => {
  it('maps reason codes to human labels and de-duplicates', () => {
    expect(
      summarizeFailureReasons([
        { reason: 'ProviderError' },
        { reason: 'ProviderError' },
        { reason: 'ProcessingError' },
      ]),
    ).toBe('provider error, processing error');
  });

  it('falls back to a generic label for unknown or missing reasons', () => {
    expect(summarizeFailureReasons([{ reason: undefined }, { reason: 'Weird' }])).toBe('error');
  });
});

describe('getJobOutcome', () => {
  it('is running while not terminal', () => {
    expect(getJobOutcome(makeJob({ jobType: 'upload', isTerminal: false }))).toBe('running');
  });

  it('is failed when terminal with errors', () => {
    expect(getJobOutcome(makeJob({ jobType: 'upload', isTerminal: true, phase: 'Failed', hasErrors: true }))).toBe(
      'failed',
    );
  });

  it('is completed when terminal without errors', () => {
    expect(getJobOutcome(makeJob({ jobType: 'upload', isTerminal: true, hasErrors: false }))).toBe(
      'completed',
    );
  });

  it('is deferred when a terminal error-free materialization landed in the Deferred phase', () => {
    expect(
      getJobOutcome(
        makeJob({
          jobType: 'materialization',
          isTerminal: true,
          hasErrors: false,
          phase: 'Deferred',
        }),
      ),
    ).toBe('deferred');
  });
});


describe('operational outcome distinctions', () => {
  it.each([
    ['Pending', false, false, 'queued'],
    ['Cancelled', true, false, 'cancelled'],
    ['Cancelled', true, true, 'cancelled'],
    ['CompletedWithErrors', true, true, 'partial'],
    ['Completed', true, true, 'partial'],
    ['Failed', true, false, 'failed'],
    ['FuturePhase', true, false, 'unknown'],
  ])('classifies %s without guessing success', (phase, isTerminal, hasErrors, expected) => {
    expect(getJobOutcome(makeJob({ phase, isTerminal, hasErrors }))).toBe(expected);
  });
});


it('names artwork import work explicitly', () => {
  expect(getJobTitle(makeJob({ jobType: 'artwork-import', sourceFilename: 'cover.png' }))).toBe('Import artwork · cover.png');
});
