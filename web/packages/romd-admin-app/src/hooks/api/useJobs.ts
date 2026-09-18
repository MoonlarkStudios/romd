import type { JobDto } from '@romd/admin-api-client';
import { getJobById, getRecentJobs } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export const jobKeys = {
  all: ['jobs'] as const,
  lists: () => [...jobKeys.all, 'list'] as const,
  list: () => [...jobKeys.lists()] as const,
  details: () => [...jobKeys.all, 'detail'] as const,
  detail: (id: string) => [...jobKeys.details(), id] as const,
};

export function useJobs() {
  return useQuery({
    queryKey: jobKeys.list(),
    queryFn: async ({ signal }) => {
      const response = await getRecentJobs({ signal });
      if (response.error || !response.data) {
        throw new Error('Failed to fetch jobs');
      }
      return response.data as JobDto[];
    },
    // staleTime and refetchInterval are controlled dynamically by useJobSocket
    // via queryClient.setQueryDefaults() — defaults to no polling when SignalR is connected
  });
}

export function useJob(jobId: string | undefined, options?: { pollingInterval?: number }) {
  const { pollingInterval = 2000 } = options ?? {};

  return useQuery({
    queryKey: jobKeys.detail(jobId ?? ''),
    queryFn: async () => {
      if (!jobId) throw new Error('Job ID is required');
      const response = await getJobById({ path: { id: jobId } });
      if (response.error) {
        throw new Error('Failed to fetch job');
      }
      return response.data as JobDto;
    },
    enabled: !!jobId,
    refetchInterval: (query) => {
      const data = query.state.data as JobDto | undefined;
      if (data?.isTerminal) {
        return false;
      }
      return pollingInterval;
    },
  });
}
