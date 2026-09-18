import type { PageOfRom, Rom } from '@romd/admin-api-client';
import { listRoms } from '@romd/admin-api-client';
import type { InfiniteData } from '@tanstack/react-query';
import { useInfiniteQuery } from '@tanstack/react-query';

export type StatusFilter = 'cataloged' | 'unrouted' | 'unidentified';

interface UseRomsListOptions {
  /** Status filter (lowercase) */
  status?: StatusFilter;
  search?: string;
  enabled?: boolean;
  limit?: number;
}

export const romKeys = {
  all: ['roms'] as const,
  list: (filters: { status?: StatusFilter; search?: string; limit?: number }) => [...romKeys.all, 'list', filters] as const,
};

type RomsPage = {
  items: Rom[];
  nextCursor?: string;
  hasNextPage: boolean;
};

export function useRomsList(options?: UseRomsListOptions) {
  const { status, search, enabled = true, limit = 50 } = options ?? {};

  return useInfiniteQuery<RomsPage, Error, InfiniteData<RomsPage>, ReturnType<typeof romKeys.list>, string | undefined>({
    queryKey: romKeys.list({ status, search, limit }),
    queryFn: async ({ pageParam, signal }): Promise<RomsPage> => {
      const cursor = typeof pageParam === 'string' ? pageParam : undefined;
      const response = await listRoms({
        signal,
        query: {
          status,
          search,
          cursor,
          limit,
        },
      });

      if (response.error) {
        throw new Error('Failed to fetch ROMs');
      }

      const page = response.data as PageOfRom;
      return {
        items: (page.items ?? []) as Rom[],
        nextCursor: page.nextCursor ?? undefined,
        hasNextPage: page.hasNextPage ?? false,
      };
    },
    enabled,
    initialPageParam: undefined,
    getNextPageParam: (lastPage) => (lastPage.hasNextPage ? lastPage.nextCursor : undefined),
  });
}
