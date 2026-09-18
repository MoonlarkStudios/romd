import type { PageOfCatalogTitle } from '@romd/admin-api-client';
import { searchCatalog } from '@romd/admin-api-client';
import { useInfiniteQuery } from '@tanstack/react-query';

export const catalogSearchKeys = {
  all: ['catalogSearch'] as const,
  search: (params: CatalogSearchParams) => [...catalogSearchKeys.all, params] as const,
};

export interface CatalogSearchParams {
  query?: string;
  systemKey?: string;
  genre?: string;
  enrichmentStatus?: string;
  releaseCompleteness?: string;
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
    queryFn: async ({ pageParam }) => {
      const query: Record<string, string | undefined> = {
        limit: String(limit),
      };

      if (params.query) query.query = params.query;
      if (params.systemKey) query.systemKey = params.systemKey;
      if (params.genre) query.genre = params.genre;
      if (params.enrichmentStatus) query.enrichmentStatus = params.enrichmentStatus;
      if (params.releaseCompleteness) query.releaseCompleteness = params.releaseCompleteness;
      if (params.tracked) query.tracked = params.tracked;
      if (params.sortBy) query.sortBy = params.sortBy;
      if (pageParam) query.cursor = pageParam;

      const response = await searchCatalog({ query });

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
