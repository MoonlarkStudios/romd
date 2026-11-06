import type { JobDto } from '@romd/admin-api-client';
import { describe, expect, it } from 'vitest';
import {
  getJobOutcome,
  getJobTitle,
  getJobTypeConfig,
  jobTypeOptions,
  summarizeFailureReasons,
} from './jobTypeRegistry';

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
    expect(
      getJobTitle(
        makeJob({
          jobType: 'upload',
          sourceFilename: 'snes-pack.zip',
        }),
      ),
    ).toBe('Import snes-pack.zip');
  });

  it('names a single enrichment by the title', () => {
    expect(
      getJobTitle(
        makeJob({
          jobType: 'enrichment',
          titleId: 't1',
          sourceFilename: 'Chrono Trigger',
        }),
      ),
    ).toBe('Enrich Chrono Trigger');
  });

  it('names a bulk enrichment by count and platform short name', () => {
    expect(
      getJobTitle(
        makeJob({
          jobType: 'bulk_enrichment',
          totalTitles: '247',
          sourceFilename: 'SNES',
        }),
      ),
    ).toBe('Enrich 247 titles · SNES');
  });

  it('falls back to a count-only bulk title for legacy rows', () => {
    // Pre-name jobs stored the literal "Bulk Enrichment" label.
    expect(
      getJobTitle(
        makeJob({
          jobType: 'bulk_enrichment',
          totalTitles: '247',
          sourceFilename: 'Bulk Enrichment',
        }),
      ),
    ).toBe('Enrich 247 titles');
  });

  it('names an enrichment run by its size, never by the stored artwork label', () => {
    expect(
      getJobTitle(
        makeJob({
          jobType: 'artwork-completion',
          total: 12,
          sourceFilename: 'Fill missing artwork',
        }),
      ),
    ).toBe('Enrichment run · 12 titles');
    expect(
      getJobTitle(
        makeJob({
          jobType: 'artwork-completion',
          total: 1,
          sourceFilename: 'Fill missing artwork',
        }),
      ),
    ).toBe('Enrichment run · 1 title');
    expect(
      getJobTitle(
        makeJob({
          jobType: 'artwork-completion',
          sourceFilename: 'Fill missing artwork',
        }),
      ),
    ).toBe('Enrichment run');
  });

  it('names a materialization by its library', () => {
    expect(
      getJobTitle(
        makeJob({
          jobType: 'materialization',
          sourceFilename: 'Family Room',
        }),
      ),
    ).toBe('Materialize Family Room');
  });

  it('falls back to a generic materialization title for legacy rows', () => {
    expect(
      getJobTitle(
        makeJob({
          jobType: 'materialization',
          sourceFilename: 'Library Materialization',
        }),
      ),
    ).toBe('Materialize library');
  });

  it('names an export by file count, ignoring the literal label', () => {
    expect(
      getJobTitle(
        makeJob({
          jobType: 'export',
          totalFiles: '1200',
          sourceFilename: 'Library Export',
        }),
      ),
    ).toBe('Export 1,200 files');
  });

  it('falls back gracefully when the subject is empty', () => {
    expect(
      getJobTitle(
        makeJob({
          jobType: 'upload',
          sourceFilename: '',
        }),
      ),
    ).toBe('Import files');
  });
});

describe('summarizeFailureReasons', () => {
  it('maps reason codes to human labels and de-duplicates', () => {
    expect(
      summarizeFailureReasons([
        {
          reason: 'ProviderError',
        },
        {
          reason: 'ProviderError',
        },
        {
          reason: 'ProcessingError',
        },
      ]),
    ).toBe('provider error, processing error');
  });

  it('falls back to a generic label for unknown or missing reasons', () => {
    expect(
      summarizeFailureReasons([
        {
          reason: undefined,
        },
        {
          reason: 'Weird',
        },
      ]),
    ).toBe('error');
  });
});

describe('getJobOutcome', () => {
  it('is running while not terminal', () => {
    expect(
      getJobOutcome(
        makeJob({
          jobType: 'upload',
          isTerminal: false,
        }),
      ),
    ).toBe('running');
  });

  it('is failed when terminal with errors', () => {
    expect(
      getJobOutcome(
        makeJob({
          jobType: 'upload',
          isTerminal: true,
          phase: 'Failed',
          hasErrors: true,
        }),
      ),
    ).toBe('failed');
  });

  it('is completed when terminal without errors', () => {
    expect(
      getJobOutcome(
        makeJob({
          jobType: 'upload',
          isTerminal: true,
          hasErrors: false,
        }),
      ),
    ).toBe('completed');
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
    [
      'Pending',
      false,
      false,
      'queued',
    ],
    [
      'Cancelled',
      true,
      false,
      'cancelled',
    ],
    [
      'Cancelled',
      true,
      true,
      'cancelled',
    ],
    [
      'CompletedWithErrors',
      true,
      true,
      'partial',
    ],
    [
      'Completed',
      true,
      true,
      'partial',
    ],
    [
      'Failed',
      true,
      false,
      'failed',
    ],
    [
      'FuturePhase',
      true,
      false,
      'unknown',
    ],
  ])('classifies %s without guessing success', (phase, isTerminal, hasErrors, expected) => {
    expect(
      getJobOutcome(
        makeJob({
          phase,
          isTerminal,
          hasErrors,
        }),
      ),
    ).toBe(expected);
  });
});

it('names artwork import work explicitly', () => {
  expect(
    getJobTitle(
      makeJob({
        jobType: 'artwork-import',
        sourceFilename: 'cover.png',
      }),
    ),
  ).toBe('Import artwork · cover.png');
});

it.each([
  'enrichment',
  'bulk_enrichment',
  'artwork-completion',
  'artwork-import',
])('refreshes canonical assessments after %s completes', (jobType) => {
  expect(
    getJobTypeConfig(jobType).invalidates(
      makeJob({
        jobType,
        titleId: 't1',
      }),
    ),
  ).toContainEqual([
    'enrichment-workspace',
  ]);
});

it.each([
  'enrichment',
  'artwork-import',
])('refreshes every surface of the title a finished %s job changed', (jobType) => {
  const keys = getJobTypeConfig(jobType).invalidates(
    makeJob({
      jobType,
      titleId: 't1',
    }),
  );
  expect(keys).toEqual(
    expect.arrayContaining([
      [
        'catalogSearch',
      ],
      [
        'titleDetail',
        't1',
      ],
      [
        'titleEnrichment',
        't1',
      ],
      [
        'artwork',
        'selections',
        't1',
      ],
      [
        'artwork',
        'saved',
        't1',
      ],
      [
        'artwork',
        'providers',
        't1',
      ],
      [
        'artwork-enrichment',
        'title',
        't1',
      ],
      [
        'provider-matches',
        't1',
      ],
    ]),
  );
});

it('refreshes every title surface family after an enrichment run finishes', () => {
  expect(
    getJobTypeConfig('artwork-completion').invalidates(
      makeJob({
        jobType: 'artwork-completion',
      }),
    ),
  ).toEqual(
    expect.arrayContaining([
      [
        'catalogSearch',
      ],
      [
        'titleDetail',
      ],
      [
        'titleEnrichment',
      ],
      [
        'provider-matches',
      ],
      [
        'artwork',
      ],
      [
        'artwork-enrichment',
        'title',
      ],
      [
        'enrichment-workspace',
      ],
    ]),
  );
});

it('stops invalidating retired artwork completion caches', () => {
  const families = getJobTypeConfig('artwork-completion')
    .invalidates(
      makeJob({
        jobType: 'artwork-completion',
      }),
    )
    .map((key) => key[0]);
  expect(families).not.toEqual(
    expect.arrayContaining([
      expect.stringMatching(/^artwork-completion/),
    ]),
  );
});

describe('job type labels', () => {
  it('uses one enrichment vocabulary for rows and filters', () => {
    const labels = {
      'artwork-completion': 'Enrichment run',
      enrichment: 'Title enrichment',
      bulk_enrichment: 'Bulk enrichment (legacy)',
      'artwork-import': 'Artwork import',
    };
    for (const [jobType, label] of Object.entries(labels)) {
      expect(getJobTypeConfig(jobType).label).toBe(label);
      expect(jobTypeOptions).toContainEqual({
        value: jobType,
        label,
      });
    }
  });

  it('offers every registered job type exactly once', () => {
    const values = jobTypeOptions.map((option) => option.value);
    expect(new Set(values).size).toBe(values.length);
    expect(values).toEqual(
      expect.arrayContaining([
        'upload',
        'replace_dat',
        'enrichment',
        'bulk_enrichment',
        'artwork-completion',
        'artwork-import',
        'export',
        'materialization',
      ]),
    );
  });
});

it('names system metadata updates and refreshes policy plus title metadata when they finish', () => {
  const job = makeJob({
    jobType: 'metadata-rematerialization',
    systemKey: 'snes',
    sourceFilename: 'SNES',
  });
  expect(getJobTypeConfig(job.jobType).label).toBe('Metadata update');
  expect(getJobTitle(job)).toBe('Update metadata · SNES');
  expect(jobTypeOptions).toContainEqual({
    value: 'metadata-rematerialization',
    label: 'Metadata update',
  });
  expect(getJobTypeConfig(job.jobType).invalidates(job)).toEqual(
    expect.arrayContaining([
      [
        'metadata-policy',
        'snes',
      ],
      [
        'catalogSearch',
      ],
      [
        'titleDetail',
      ],
      [
        'titleEnrichment',
      ],
      [
        'enrichment-workspace',
      ],
    ]),
  );
});

it.each([
  [
    'Enrich',
    'Enrich 12 titles',
  ],
  [
    'Artwork',
    'Fill artwork · 12 titles',
  ],
  [
    null,
    'Enrichment run · 12 titles',
  ],
  [
    undefined,
    'Enrichment run · 12 titles',
  ],
  [
    'FutureMode',
    'Enrichment run · 12 titles',
  ],
])('uses recorded artwork mode %s without guessing unknown history', (mode, title) => {
  expect(
    getJobTitle(
      makeJob({
        jobType: 'artwork-completion',
        total: 12,
        mode,
      }),
    ),
  ).toBe(title);
});
