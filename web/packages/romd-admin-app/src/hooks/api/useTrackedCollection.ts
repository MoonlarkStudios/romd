import type {
  TrackedCollectionStatsDto,
  TrackedCollectionTitleDto,
} from '@romd/admin-api-client';
import {
  getTrackedCollectionStats,
  listMissingTrackedTitles,
  listSatisfiedTrackedTitles,
  listTrackedTitleUpgrades,
} from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export const trackedCollectionKeys = {
  all: ['trackedCollection'] as const,
  stats: () => [...trackedCollectionKeys.all, 'stats'] as const,
  lists: () => [...trackedCollectionKeys.all, 'list'] as const,
  satisfied: () => [...trackedCollectionKeys.lists(), 'satisfied'] as const,
  missing: () => [...trackedCollectionKeys.lists(), 'missing'] as const,
  upgrades: () => [...trackedCollectionKeys.lists(), 'upgrades'] as const,
};

export function useTrackedCollectionStats() {
  return useQuery({
    queryKey: trackedCollectionKeys.stats(),
    queryFn: async () => {
      const response = await getTrackedCollectionStats();
      if (response.error || !response.data) throw new Error('Failed to fetch tracked collection stats');
      return response.data as TrackedCollectionStatsDto;
    },
  });
}

export function useSatisfiedTrackedTitles() {
  return useQuery({
    queryKey: trackedCollectionKeys.satisfied(),
    queryFn: async () => {
      const response = await listSatisfiedTrackedTitles();
      if (response.error) throw new Error('Failed to fetch owned tracked titles');
      return response.data as TrackedCollectionTitleDto[];
    },
  });
}

export function useMissingTrackedTitles() {
  return useQuery({
    queryKey: trackedCollectionKeys.missing(),
    queryFn: async () => {
      const response = await listMissingTrackedTitles();
      if (response.error) throw new Error('Failed to fetch missing tracked titles');
      return response.data as TrackedCollectionTitleDto[];
    },
  });
}

export function useTrackedTitleUpgrades() {
  return useQuery({
    queryKey: trackedCollectionKeys.upgrades(),
    queryFn: async () => {
      const response = await listTrackedTitleUpgrades();
      if (response.error) throw new Error('Failed to fetch tracked title upgrades');
      return response.data as TrackedCollectionTitleDto[];
    },
  });
}
