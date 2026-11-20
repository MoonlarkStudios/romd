import type { Job } from '@romd/admin-api-client';
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

/** One job's detail, polled until it is terminal; shared by `useJob` and callers that watch several jobs. */
export function jobQueryOptions(jobId: string | undefined, pollingInterval = 2000) {
  return {
    queryKey: jobKeys.detail(jobId ?? ''),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
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
    refetchInterval: (query: {
      state: {
        data?: Job;
      };
    }) => (query.state.data?.isTerminal ? false : pollingInterval),
  };
}

export function useJob(
  jobId: string | undefined,
  options?: {
    pollingInterval?: number;
  },
) {
  return useQuery(jobQueryOptions(jobId, options?.pollingInterval));
}
