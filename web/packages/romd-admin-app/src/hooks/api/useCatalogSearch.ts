import type { ListCatalogTitlesData, PageOfCatalogTitle } from '@romd/admin-api-client';
import { getCatalogStats, listCatalogTitles } from '@romd/admin-api-client';
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query';

export const catalogSearchKeys = {
  all: [
    'catalogSearch',
  ] as const,
  search: (params: CatalogSearchParams) =>
    [
      ...catalogSearchKeys.all,
      params,
    ] as const,
};

export interface CatalogSearchParams {
  query?: string;
  systemKey?: string;
  genre?: string;
  /** Repeatable title status flags; omitted when none are selected. */
  enrichment?: string[];
  enrichmentStatus?: string;
  artworkStatus?: string;
  releaseCompleteness?: string;
  releaseReview?: string;
  tracked?: string;
  sortBy?: string;
}

interface UseCatalogSearchOptions {
  enabled?: boolean;
  limit?: number;
}

/**
 * Fetches paginated catalog titles using full-text search with cursor-based pagination.
 *
 * @example
 * ```tsx
 * const { data, isLoading, hasNextPage, fetchNextPage, isFetchingNextPage } =
 *   useCatalogSearch({ query: 'mario', releaseCompleteness: 'complete' });
 *
 * const allTitles = data?.pages.flatMap(page => page.items) ?? [];
 * ```
 */
export function useCatalogSearch(params: CatalogSearchParams, options?: UseCatalogSearchOptions) {
  const { enabled = true, limit = 48 } = options ?? {};

  return useInfiniteQuery({
    queryKey: catalogSearchKeys.search(params),
    placeholderData: keepPreviousData,
    queryFn: async ({ pageParam, signal }) => {
      const query = {
        ...catalogFilterQuery(params),
        limit,
        sortBy: closed(params.sortBy, [
          'name',
          'rating',
          'relevance',
        ] as const),
        cursor: pageParam,
      };
      if (pageParam) query.cursor = pageParam;

      const response = await listCatalogTitles({
        query,
        signal,
      });

      if (response.error) {
        throw new Error('Failed to search catalog');
      }

      return response.data as PageOfCatalogTitle;
    },
    getNextPageParam: (lastPage) => (lastPage.hasNextPage ? lastPage.nextCursor : undefined),
    initialPageParam: undefined as string | undefined,
    enabled,
  });
}

function catalogFilterQuery(params: CatalogSearchParams) {
  const query: NonNullable<ListCatalogTitlesData['query']> = {};
  if (params.query) query.query = params.query;
  if (params.systemKey) query.systemKey = params.systemKey;
  if (params.genre) query.genre = params.genre;
  if (params.artworkStatus)
    query.artworkStatus = closed(params.artworkStatus, [
      'Poster',
      'Backdrop',
      'Logo',
      'Hero',
      'NeedsMatch',
    ] as const);
  if (params.enrichment?.length) query.enrichment = params.enrichment;
  if (params.enrichmentStatus)
    query.enrichmentStatus = closed(params.enrichmentStatus, [
      'None',
      'Pending',
      'Completed',
      'Failed',
      'NotFound',
      'LowConfidence',
    ] as const);
  if (params.releaseReview)
    query.releaseReview = closed(params.releaseReview, [
      'all',
      'missing',
      'upgrades',
    ] as const);
  if (params.releaseCompleteness)
    query.releaseCompleteness = closed(params.releaseCompleteness, [
      'all',
      'complete',
      'partial',
      'none',
    ] as const);
  if (params.tracked)
    query.tracked = closed(params.tracked, [
      'all',
      'tracked',
      'untracked',
    ] as const);
  return query;
}

/** Exact totals run independently so expensive filters do not delay the first page. */
export function useCatalogCount(params: CatalogSearchParams) {
  const query = catalogFilterQuery(params);
  return useQuery({
    queryKey: [
      ...catalogSearchKeys.all,
      'count',
      query,
    ],
    queryFn: async ({ signal }) => {
      const response = await getCatalogStats({
        query,
        signal,
      });
      if (response.error || !response.data) throw new Error('Failed to count catalog');
      return response.data.totalCount;
    },
    staleTime: 30_000,
    retry: false,
  });
}

function closed<T extends string>(value: string | undefined, values: readonly T[]): T | undefined {
  if (value === undefined) return undefined;
  const match = values.find((candidate) => candidate === value);
  if (match === undefined) throw new Error(`Invalid catalog filter: ${value}`);
  return match;
}
