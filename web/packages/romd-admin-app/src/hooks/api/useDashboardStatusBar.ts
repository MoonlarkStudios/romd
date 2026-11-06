import { getDashboardStatusBar } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { mapCoverageStats } from './useCoverageStats';
import { mapHealthStats } from './useHealthStats';
import { mapStorageStats } from './useStorageStats';

export const dashboardStatusBarKeys = {
  all: [
    'dashboard',
    'status-bar',
  ] as const,
};

export function useDashboardStatusBar() {
  return useQuery({
    queryKey: dashboardStatusBarKeys.all,
    queryFn: async ({ signal }) => {
      const response = await getDashboardStatusBar({
        signal,
      });
      if (response.error || !response.data) throw new Error('Could not load the status bar.');
      return {
        storage: mapStorageStats(response.data.storage),
        coverage: mapCoverageStats(response.data.coverage),
        health: mapHealthStats(response.data.health),
      };
    },
    staleTime: Number.POSITIVE_INFINITY,
  });
}
