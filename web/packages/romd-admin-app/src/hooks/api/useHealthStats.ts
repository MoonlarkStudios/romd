import type { SystemHealthDto } from '@romd/admin-api-client';
import { getSystemHealth } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export interface HealthStats {
  totalRoms: number;
  identifiedCount: number;
  unidentifiedCount: number;
  routedCount: number;
  unroutedCount: number;
  failedJobCount: number;
}

export const healthStatsKeys = {
  all: ['dashboard', 'health'] as const,
};

const mapHealthStats = (dto: SystemHealthDto): HealthStats => ({
  totalRoms: dto.totalRoms,
  identifiedCount: dto.identifiedCount,
  unidentifiedCount: dto.unidentifiedCount,
  routedCount: dto.routedCount,
  unroutedCount: dto.unroutedCount,
  failedJobCount: dto.failedJobCount,
});

export function useHealthStats() {
  return useQuery({
    queryKey: healthStatsKeys.all,
    queryFn: async (): Promise<HealthStats> => {
      const response = await getSystemHealth();
      if (response.error || !response.data) {
        throw new Error('Failed to fetch health stats');
      }
      return mapHealthStats(response.data as SystemHealthDto);
    },
    staleTime: Number.POSITIVE_INFINITY,
  });
}
