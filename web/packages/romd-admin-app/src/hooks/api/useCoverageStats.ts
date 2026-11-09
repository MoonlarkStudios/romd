import type { CoverageStats as CoverageStatsContract } from '@romd/admin-api-client';
import { getCatalogCoverageStats } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export interface CoverageStats {
  expectedTitleCount: number;
  localPayloadTitleCount: number;
  completeTitleCount: number;
  partialTitleCount: number;
  coverageHealthPercent: number;
  lastDatRefreshAt: string | null;
}

export const coverageStatsKeys = {
  all: [
    'dashboard',
    'coverage',
  ] as const,
};

export const mapCoverageStats = (dto: CoverageStatsContract): CoverageStats => ({
  expectedTitleCount: dto.expectedTitleCount,
  localPayloadTitleCount: dto.localPayloadTitleCount,
  completeTitleCount: dto.completeTitleCount,
  partialTitleCount: dto.partialTitleCount,
  coverageHealthPercent: dto.coverageHealthPercent,
  lastDatRefreshAt: dto.lastDatRefreshAt ?? null,
});

export function useCoverageStats() {
  return useQuery({
    queryKey: coverageStatsKeys.all,
    queryFn: async ({ signal }): Promise<CoverageStats> => {
      const response = await getCatalogCoverageStats({
        signal,
      });
      if (response.error || !response.data) {
        throw new Error('Failed to fetch coverage stats');
      }
      return mapCoverageStats(response.data as CoverageStatsContract);
    },
    staleTime: Number.POSITIVE_INFINITY,
  });
}
