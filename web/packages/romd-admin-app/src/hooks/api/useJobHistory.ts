import { type GetJobHistoryData, getJobHistory } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { jobKeys } from './useJobs';

export function useJobHistory(filters: NonNullable<GetJobHistoryData['query']>) {
  return useQuery({
    queryKey: [...jobKeys.all, 'history', filters],
    queryFn: async ({ signal }) => {
      const response = await getJobHistory({ query: filters, signal });
      if (response.error || !response.data) throw new Error(response.error?.detail ?? 'Could not load job history');
      return response.data;
    },
    // Separate from the live recent feed: re-query bounded pages without splicing
    // realtime rows across a filter/cursor boundary. Mutations invalidate jobKeys.all.
    staleTime: 10_000,
    refetchInterval: 15_000,
  });
}
