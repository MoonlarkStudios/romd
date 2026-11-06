import type { TitleDetail } from '@romd/admin-api-client';
import type { QueryClient, QueryKey } from '@tanstack/react-query';
import { announceInvalidation, joinCrossTabInvalidation } from './crossTabCache';
import {
  enrichmentWorkspaceKeys,
  invalidateEnrichmentAssessmentsInThisTab,
} from './enrichmentWorkspaceCache';
import { artworkKeys } from './useArtwork';
import { catalogSearchKeys } from './useCatalogSearch';
import { normalizeTitleDetail, titleDetailKeys } from './useTitleDetail';

/** A successful write stays accepted even when its subsequent relationship reads fail. */
export async function cacheAcceptedTitle(cache: QueryClient, title: TitleDetail): Promise<void> {
  const queryKey = titleDetailKeys.detail(title.id);
  await cache.cancelQueries({
    queryKey,
  });
  try {
    const complete = await normalizeTitleDetail(title);
    cache.setQueryData(queryKey, complete);
  } catch {
    // Keep the previous complete view stale; the query owns refresh errors and retry UI.
    await cache.invalidateQueries({
      queryKey,
    });
  }
}

export const titleEnrichmentKeys = {
  all: [
    'titleEnrichment',
  ] as const,
  detail: (titleId: string) =>
    [
      ...titleEnrichmentKeys.all,
      titleId,
    ] as const,
};

export const acquisitionKeys = {
  settings: [
    'artwork-enrichment',
    'settings',
  ] as const,
  titles: [
    'artwork-enrichment',
    'title',
  ] as const,
  title: (titleId: string) =>
    [
      ...acquisitionKeys.titles,
      titleId,
    ] as const,
};

export const providerMatchKeys = {
  all: [
    'provider-matches',
  ] as const,
  title: (titleId: string) =>
    [
      ...providerMatchKeys.all,
      titleId,
    ] as const,
};

/**
 * Every cache outside the enrichment workspace whose server state follows one title's enrichment inputs.
 * Candidate browsers are keyed by provider game and refresh only where their candidate set changes.
 */
export function titleSurfaceKeys(titleId: string): QueryKey[] {
  return [
    catalogSearchKeys.all,
    titleDetailKeys.detail(titleId),
    titleEnrichmentKeys.detail(titleId),
    artworkKeys.selections(titleId),
    artworkKeys.saved(titleId),
    artworkKeys.titleProviders(titleId),
    acquisitionKeys.title(titleId),
    providerMatchKeys.title(titleId),
  ];
}

/** Follows an observed assessment change without re-reading the workspace that reported it. */
export async function invalidateTitleSurfaces(cache: QueryClient, titleId: string): Promise<void> {
  await Promise.all(
    titleSurfaceKeys(titleId).map((queryKey) =>
      cache.invalidateQueries({
        queryKey,
      }),
    ),
  );
}

/**
 * After a title's enrichment inputs change, every surface showing the title agrees before the
 * next action, and other open tabs follow. Global queue counts and run review counts and lists
 * refresh in the background so saves never wait for them.
 */
export async function invalidateTitle(cache: QueryClient, titleId: string): Promise<void> {
  announceInvalidation(cache, {
    kind: 'title',
    titleId,
  });
  await invalidateTitleInThisTab(cache, titleId);
}

async function invalidateTitleInThisTab(cache: QueryClient, titleId: string): Promise<void> {
  void cache.invalidateQueries({
    queryKey: [
      ...enrichmentWorkspaceKeys.all,
      'counts',
    ],
  });
  void cache.invalidateQueries({
    queryKey: enrichmentWorkspaceKeys.runs,
  });
  await Promise.all([
    cache.invalidateQueries({
      queryKey: [
        ...enrichmentWorkspaceKeys.all,
        'list',
      ],
    }),
    cache.invalidateQueries({
      queryKey: enrichmentWorkspaceKeys.title(titleId),
    }),
    invalidateTitleSurfaces(cache, titleId),
  ]);
}

/**
 * Applies the title and assessment invalidations other open admin tabs announce to this tab's cache, without
 * announcing them again. Returns the cleanup that stops listening.
 */
export function shareInvalidationsAcrossTabs(cache: QueryClient): () => void {
  return joinCrossTabInvalidation(cache, (message) => {
    void (message.kind === 'title'
      ? invalidateTitleInThisTab(cache, message.titleId)
      : invalidateEnrichmentAssessmentsInThisTab(cache));
  });
}
