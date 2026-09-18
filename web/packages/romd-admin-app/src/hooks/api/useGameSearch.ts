import type { PageOfDatGame, SearchGamesData } from '@romd/admin-api-client';
import { searchGames } from '@romd/admin-api-client';
import { useInfiniteQuery } from '@tanstack/react-query';

export const gameSearchKeys = {
  all: ['gameSearch'] as const,
  search: (params: SearchParams) => [...gameSearchKeys.all, params] as const,
};

export interface SearchParams {
  query?: string;
  systemKey?: string;
  year?: string;
  manufacturer?: string;
  regionId?: string;
  bios?: string;
  sortBy?: string;
}

interface UseGameSearchOptions {
  /** Whether the query is enabled (default: true) */
  enabled?: boolean;
  /** Page size limit (default: 50) */
  limit?: number;
}

/**
 * Fetches paginated games using full-text search with cursor-based pagination.
 *
 * Uses useInfiniteQuery to accumulate pages for "Load More" functionality.
 *
 * @example
 * ```tsx
 * const { data, isLoading, hasNextPage, fetchNextPage, isFetchingNextPage } =
 *   useGameSearch({ query: 'mario', sortBy: 'relevance' });
 *
 * const allGames = data?.pages.flatMap(page => page.items) ?? [];
 * ```
 */
export function useGameSearch(params: SearchParams, options?: UseGameSearchOptions) {
  const { enabled = true, limit = 50 } = options ?? {};

  return useInfiniteQuery({
    queryKey: gameSearchKeys.search(params),
    queryFn: async ({ pageParam }) => {
      const query: NonNullable<SearchGamesData['query']> = {
        limit,
      };

      // Add search params
      if (params.query) query.query = params.query;
      if (params.systemKey) query.systemKey = params.systemKey;
      if (params.year) query.year = params.year;
      if (params.manufacturer) query.manufacturer = params.manufacturer;
      if (params.regionId) query.regionId = params.regionId;
      if (params.bios) query.bios = params.bios;
      if (params.sortBy) query.sortBy = params.sortBy;
      if (pageParam) query.cursor = pageParam;

      const response = await searchGames({ query });

      if (response.error) {
        throw new Error('Failed to search games');
      }

      return response.data as PageOfDatGame;
    },
    getNextPageParam: (lastPage) => (lastPage.hasNextPage ? lastPage.nextCursor : undefined),
    initialPageParam: undefined as string | undefined,
    enabled,
  });
}
