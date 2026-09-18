import type { EnrichmentStatsResponse } from '@romd/admin-api-client';
import { getEnrichmentStats } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export const enrichmentStatsKeys = {
  all: ['enrichmentStats'] as const,
  stats: () => [...enrichmentStatsKeys.all, 'stats'] as const,
};

interface UseEnrichmentStatsOptions {
  pollingInterval?: number;
  enabled?: boolean;
}

/**
 * Fetches dedicated enrichment statistics including lowConfidence count.
 * Complements useSystemStats by providing richer enrichment breakdown.
 */
export function useEnrichmentStats(options?: UseEnrichmentStatsOptions) {
  const { pollingInterval, enabled = true } = options ?? {};

  return useQuery({
    queryKey: enrichmentStatsKeys.stats(),
    queryFn: async () => {
      const response = await getEnrichmentStats();
      if (response.error || !response.data) {
        throw new Error('Failed to fetch enrichment stats');
      }
      return response.data as EnrichmentStatsResponse;
    },
    enabled,
    refetchInterval: pollingInterval,
  });
}
