import type { LibraryStats, PurgeUnidentifiedResponse } from '@romd/admin-api-client';
import { deleteRom, getRomStats, listRoms, purgeUnidentifiedRoms } from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { collectPages } from '../../utils/collectPages';

export const romKeys = {
  all: [
    'roms',
  ] as const,
  lists: () =>
    [
      ...romKeys.all,
      'list',
    ] as const,
  list: () =>
    [
      ...romKeys.lists(),
    ] as const,
  stats: () =>
    [
      ...romKeys.all,
      'stats',
    ] as const,
  details: () =>
    [
      ...romKeys.all,
      'detail',
    ] as const,
  detail: (id: string) =>
    [
      ...romKeys.details(),
      id,
    ] as const,
};

/** Loads the complete ROM collection for callers that require a full list. */
export function useRoms() {
  return useQuery({
    queryKey: romKeys.list(),
    queryFn: ({ signal }) =>
      collectPages(async (cursor) => {
        const response = await listRoms({
          signal,
          query: {
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data) throw new Error('Failed to fetch ROMs');
        return response.data;
      }),
  });
}

export function useLibraryStats() {
  return useQuery({
    queryKey: romKeys.stats(),
    queryFn: async () => {
      const response = await getRomStats();
      if (response.error) {
        throw new Error('Failed to fetch library stats');
      }
      return response.data as LibraryStats;
    },
  });
}

export function useDeleteRom() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (romId: string) => {
      const response = await deleteRom({
        path: {
          romId,
        },
      });
      if (response.error) {
        throw new Error('Failed to delete ROM');
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: romKeys.all,
      });
    },
  });
}

/** Deletes every unidentified ROM (the inbox) in one operation. */
export function usePurgeUnidentifiedRoms() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async () => {
      const response = await purgeUnidentifiedRoms();
      if (response.error) {
        throw new Error('Failed to clear the inbox');
      }
      return response.data as PurgeUnidentifiedResponse;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: romKeys.all,
      });
    },
  });
}
