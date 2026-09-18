import type { SystemStats } from '@romd/admin-api-client';
import { getSystemStats } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export const systemStatsKeys = {
  all: ['systemStats'] as const,
  stats: () => [...systemStatsKeys.all, 'stats'] as const,
};

interface UseSystemStatsOptions {
  /** Enable polling at specified interval in milliseconds. Default: no polling */
  pollingInterval?: number;
  /** Whether the query is enabled. Default: true */
  enabled?: boolean;
}

/**
 * Fetches unified system statistics including ROM counts, enrichment status, and platform breakdown.
 * Useful for dashboard display with optional polling for real-time updates.
 *
 * @example
 * ```tsx
 * // Basic usage
 * const { data: stats, isLoading } = useSystemStats();
 *
 * // With 30-second polling for dashboard
 * const { data: stats } = useSystemStats({ pollingInterval: 30000 });
 * ```
 */
export function useSystemStats(options?: UseSystemStatsOptions) {
  const { pollingInterval, enabled = true } = options ?? {};

  return useQuery({
    queryKey: systemStatsKeys.stats(),
    queryFn: async () => {
      const response = await getSystemStats();
      if (response.error) {
        throw new Error('Failed to fetch system stats');
      }
      return response.data as SystemStats;
    },
    enabled,
    refetchInterval: pollingInterval,
  });
}
