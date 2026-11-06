import type { JobDto } from '@romd/admin-api-client';
import { getJob } from '@romd/admin-api-client';
import { useQuery, useQueryClient } from '@tanstack/react-query';

import { jobKeys, readActiveJobPage } from './jobCache';

export { ACTIVE_JOB_LIMIT, jobKeys } from './jobCache';

export function useJobs() {
  const cache = useQueryClient();
  return useQuery({
    queryKey: jobKeys.list(),
    queryFn: ({ signal }) => readActiveJobPage(cache, signal),
    // Realtime is an accelerator, not proof that every update was delivered.
    // Reconcile accepted jobs even while the connection still reports connected.
    refetchInterval: (query) => (query.state.data?.some((job) => !job.isTerminal) ? 3000 : 30_000),
  });
}

export function useJob(
  jobId: string | undefined,
  options?: {
    pollingInterval?: number;
  },
) {
  const { pollingInterval = 2000 } = options ?? {};

  return useQuery({
    queryKey: jobKeys.detail(jobId ?? ''),
    queryFn: async ({ signal }) => {
      if (!jobId) throw new Error('Job ID is required');
      const response = await getJob({
        signal,
        path: {
          jobId,
        },
      });
      if (response.error || !response.data) {
        throw new Error('Failed to fetch job');
      }
      return response.data;
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
