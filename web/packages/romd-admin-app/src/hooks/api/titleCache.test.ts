import { QueryClient, type QueryKey, QueryObserver } from '@tanstack/react-query';
import { afterEach, describe, expect, it } from 'vitest';
import { invalidateEnrichmentAssessments } from './enrichmentWorkspaceCache';
import { invalidateTitle, invalidateTitleSurfaces } from './titleCache';

// Written out by hand in the exact shapes the hooks build, so a family missing from the
// helpers fails here instead of silently matching the helpers' own list.
const titleScoped = (titleId: string): QueryKey[] => [
  [
    'enrichment-workspace',
    'title',
    titleId,
  ],
  [
    'titleDetail',
    titleId,
  ],
  [
    'titleDetail',
    titleId,
    'sourceReferences',
  ],
  [
    'titleEnrichment',
    titleId,
  ],
  [
    'artwork',
    'selections',
    titleId,
  ],
  [
    'artwork',
    'saved',
    titleId,
  ],
  [
    'artwork',
    'providers',
    titleId,
    'Poster',
  ],
  [
    'artwork-enrichment',
    'title',
    titleId,
  ],
  [
    'provider-matches',
    titleId,
  ],
];
const workspaceLists: QueryKey[] = [
  [
    'enrichment-workspace',
    'list',
    'NeedsReview',
    '',
  ],
  [
    'enrichment-workspace',
    'list',
    'All',
    'mario',
  ],
];
const workspaceCounts: QueryKey[] = [
  [
    'enrichment-workspace',
    'counts',
    '',
  ],
  [
    'enrichment-workspace',
    'counts',
    'mario',
  ],
];
// A finished run's Review count and list drop a title once the curator resolves it.
const runs: QueryKey[] = [
  [
    'enrichment-workspace',
    'run',
    'job-1',
  ],
  [
    'enrichment-workspace',
    'run',
    'job-1',
    'items',
    'Review',
  ],
];
const catalog: QueryKey[] = [
  [
    'catalogSearch',
    {
      query: 'mario',
      tracked: 'tracked',
    },
  ],
];
const unrelated: QueryKey[] = [
  // Candidate browsers are keyed by provider game; the actions that change their candidate set refresh them.
  [
    'artwork',
    'candidates',
    'target',
    'steamgriddb',
    'game-1',
    'Backdrop',
    null,
    null,
  ],
  [
    'artwork',
    'collect',
    'target',
    'steamgriddb',
    'game-1',
    'Cover',
    '',
    '',
  ],
  [
    'artwork',
    'providers',
    undefined,
    'Poster',
  ],
  [
    'artwork-enrichment',
    'settings',
  ],
  [
    'jobs',
    'list',
  ],
];

let cache: QueryClient;
function seed() {
  cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  for (const queryKey of [
    ...titleScoped('target'),
    ...titleScoped('other'),
    ...workspaceLists,
    ...workspaceCounts,
    ...runs,
    ...catalog,
    ...unrelated,
  ]) {
    cache.setQueryData(queryKey, 'cached');
  }
}
const invalidated = (queryKey: QueryKey) => cache.getQueryState(queryKey)?.isInvalidated;
afterEach(() => cache.clear());

describe('invalidateTitle', () => {
  it('refreshes every surface that shows the target title and no other title', async () => {
    seed();
    await invalidateTitle(cache, 'target');
    for (const queryKey of [
      ...titleScoped('target'),
      ...workspaceLists,
      ...workspaceCounts,
      ...runs,
      ...catalog,
    ]) {
      expect(invalidated(queryKey), JSON.stringify(queryKey)).toBe(true);
    }
    for (const queryKey of [
      ...titleScoped('other'),
      ...unrelated,
    ]) {
      expect(invalidated(queryKey), JSON.stringify(queryKey)).toBe(false);
    }
  });

  it('waits for the queue before resolving but never for global counts or runs', async () => {
    cache = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    cache.setQueryData(workspaceLists[0], 'initial');
    let finishList!: () => void;
    const list = new QueryObserver(cache, {
      queryKey: workspaceLists[0],
      staleTime: Number.POSITIVE_INFINITY,
      queryFn: () =>
        new Promise<string>((resolve) => {
          finishList = () => resolve('refreshed');
        }),
    });
    const counts = new QueryObserver(cache, {
      queryKey: workspaceCounts[0],
      queryFn: () => new Promise<string>(() => {}),
    });
    const run = new QueryObserver(cache, {
      queryKey: runs[0],
      queryFn: () => new Promise<string>(() => {}),
    });
    const unsubscribe = [
      list.subscribe(() => {}),
      counts.subscribe(() => {}),
      run.subscribe(() => {}),
    ];
    let resolved = false;
    const refresh = invalidateTitle(cache, 'target').then(() => {
      resolved = true;
    });
    await Promise.resolve();
    expect(resolved).toBe(false);
    finishList();
    await refresh;
    expect(cache.getQueryData(workspaceLists[0])).toBe('refreshed');
    expect(cache.getQueryState(workspaceCounts[0])?.fetchStatus).toBe('fetching');
    expect(cache.getQueryState(runs[0])?.fetchStatus).toBe('fetching');
    for (const stop of unsubscribe) stop();
  });
});

describe('invalidateTitleSurfaces', () => {
  it('follows an observed assessment change without re-reading the workspace', async () => {
    seed();
    await invalidateTitleSurfaces(cache, 'target');
    const [assessment, ...surfaces] = titleScoped('target');
    for (const queryKey of [
      ...surfaces,
      ...catalog,
    ])
      expect(invalidated(queryKey), JSON.stringify(queryKey)).toBe(true);
    for (const queryKey of [
      assessment,
      ...workspaceLists,
      ...workspaceCounts,
      ...runs,
      ...titleScoped('other'),
      ...unrelated,
    ]) {
      expect(invalidated(queryKey), JSON.stringify(queryKey)).toBe(false);
    }
  });
});

describe('invalidateEnrichmentAssessments', () => {
  it('refreshes every assessment and the catalog without reloading title editors', async () => {
    seed();
    await invalidateEnrichmentAssessments(cache);
    const assessments = [
      titleScoped('target')[0],
      titleScoped('other')[0],
    ];
    for (const queryKey of [
      ...assessments,
      ...workspaceLists,
      ...workspaceCounts,
      ...runs,
      ...catalog,
    ]) {
      expect(invalidated(queryKey), JSON.stringify(queryKey)).toBe(true);
    }
    const editors = [
      ...titleScoped('target').slice(1),
      ...titleScoped('other').slice(1),
    ];
    for (const queryKey of [
      ...editors,
      ...unrelated,
    ])
      expect(invalidated(queryKey), JSON.stringify(queryKey)).toBe(false);
  });
});
