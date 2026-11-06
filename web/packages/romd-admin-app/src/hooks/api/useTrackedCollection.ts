import type { TrackedCollectionStatsDto } from '@romd/admin-api-client';
import { getTrackedCollectionStats, listTrackedTitles } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { collectPages } from '../../utils/collectPages';

export const trackedCollectionKeys = {
  all: [
    'trackedCollection',
  ] as const,
  stats: () =>
    [
      ...trackedCollectionKeys.all,
      'stats',
    ] as const,
  lists: () =>
    [
      ...trackedCollectionKeys.all,
      'list',
    ] as const,
  satisfied: () =>
    [
      ...trackedCollectionKeys.lists(),
      'satisfied',
    ] as const,
  missing: () =>
    [
      ...trackedCollectionKeys.lists(),
      'missing',
    ] as const,
  upgrades: () =>
    [
      ...trackedCollectionKeys.lists(),
      'upgrades',
    ] as const,
};

export function useTrackedCollectionStats() {
  return useQuery({
    queryKey: trackedCollectionKeys.stats(),
    queryFn: async () => {
      const response = await getTrackedCollectionStats();
      if (response.error || !response.data)
        throw new Error('Failed to fetch tracked collection stats');
      return response.data as TrackedCollectionStatsDto;
    },
  });
}

export function useSatisfiedTrackedTitles() {
  return useQuery({
    queryKey: trackedCollectionKeys.satisfied(),
    queryFn: async () => {
      return collectPages(async (cursor) => {
        const response = await listTrackedTitles({
          query: {
            status: 'satisfied',
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data) throw new Error('Failed to fetch tracked titles');
        return response.data;
      });
    },
  });
}

export function useMissingTrackedTitles() {
  return useQuery({
    queryKey: trackedCollectionKeys.missing(),
    queryFn: async () => {
      return collectPages(async (cursor) => {
        const response = await listTrackedTitles({
          query: {
            status: 'missing',
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data) throw new Error('Failed to fetch tracked titles');
        return response.data;
      });
    },
  });
}

export function useTrackedTitleUpgrades() {
  return useQuery({
    queryKey: trackedCollectionKeys.upgrades(),
    queryFn: async () => {
      return collectPages(async (cursor) => {
        const response = await listTrackedTitles({
          query: {
            status: 'upgrades',
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data) throw new Error('Failed to fetch tracked titles');
        return response.data;
      });
    },
  });
}
