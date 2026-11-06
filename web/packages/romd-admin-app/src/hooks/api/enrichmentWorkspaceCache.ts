import type { QueryClient } from '@tanstack/react-query';
import { announceInvalidation } from './crossTabCache';
import { catalogSearchKeys } from './useCatalogSearch';

export const enrichmentWorkspaceKeys = {
  all: [
    'enrichment-workspace',
  ] as const,
  counts: (search: string) =>
    [
      'enrichment-workspace',
      'counts',
      search,
    ] as const,
  title: (titleId: string) =>
    [
      'enrichment-workspace',
      'title',
      titleId,
    ] as const,
  /** Every enrichment run's counts and item lists. */
  runs: [
    'enrichment-workspace',
    'run',
  ] as const,
};

/** Keep decisions current before the next action, without making saves wait for global counts. */
export function invalidateEnrichmentWorkspace(cache: QueryClient): Promise<void> {
  void cache.invalidateQueries({
    queryKey: [
      ...enrichmentWorkspaceKeys.all,
      'counts',
    ],
  });
  return cache.invalidateQueries({
    queryKey: enrichmentWorkspaceKeys.all,
    predicate: (query) => query.queryKey[1] !== 'counts',
  });
}

/** After changing an input every title's assessment reads, such as provider or artwork settings. Other open tabs follow. */
export async function invalidateEnrichmentAssessments(cache: QueryClient): Promise<void> {
  announceInvalidation(cache, {
    kind: 'assessments',
  });
  await invalidateEnrichmentAssessmentsInThisTab(cache);
}

/** `invalidateEnrichmentAssessments` for this tab only, as applied when another tab announces it. */
export async function invalidateEnrichmentAssessmentsInThisTab(cache: QueryClient): Promise<void> {
  await Promise.all([
    invalidateEnrichmentWorkspace(cache),
    cache.invalidateQueries({
      queryKey: catalogSearchKeys.all,
    }),
  ]);
}
