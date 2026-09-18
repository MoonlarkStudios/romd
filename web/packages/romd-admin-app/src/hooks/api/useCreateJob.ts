import type { JobDto, JobReference } from '@romd/admin-api-client';
import { useQueryClient } from '@tanstack/react-query';
import { useCallback } from 'react';
import { jobKeys } from './useJobs';

/**
 * Seeds the React Query cache with a newly created job.
 * Call this after any mutation that returns a job reference.
 *
 * If the response contains a full JobDto, it's seeded directly.
 * Otherwise, we prefetch the job detail to pick it up from the API.
 */
export function useCreateJob() {
  const queryClient = useQueryClient();

  const seedJob = useCallback(
    (
      result: Pick<JobReference, 'jobId'> & { job?: JobDto },
      options?: { invalidateDelayMs?: number },
    ) => {
      const { jobId, job } = result;

      if (job) {
        // Backend returned full JobDto — seed directly
        queryClient.setQueryData(jobKeys.detail(jobId), job);
        queryClient.setQueryData<JobDto[]>(jobKeys.list(), (old) =>
          old ? [job, ...old] : [job],
        );
      } else {
        const invalidate = () => {
          queryClient.invalidateQueries({ queryKey: jobKeys.detail(jobId) });
          queryClient.invalidateQueries({ queryKey: jobKeys.list() });
        };
        const delay = options?.invalidateDelayMs ?? 500;
        if (delay > 0) {
          // Upload acceptance may race the job row becoming queryable.
          setTimeout(invalidate, delay);
        } else {
          invalidate();
        }
      }
    },
    [queryClient],
  );

  return { seedJob };
}
