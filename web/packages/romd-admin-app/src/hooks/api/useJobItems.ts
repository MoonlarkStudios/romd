import { listJobItems } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { jobKeys } from './useJobs';

/**
 * Per-file provenance for an upload job. `outcome` (if given) filters to a single outcome
 * (e.g. "rejected", "dat_unrouted"). Enabled only once the job is terminal.
 */
export function useJobItems(
  jobId: string | undefined,
  outcome: string | undefined,
  options?: {
    enabled?: boolean;
    cursor?: string;
    search?: string;
  },
) {
  return useQuery({
    queryKey: [
      ...jobKeys.detail(jobId ?? ''),
      'items',
      outcome ?? 'all',
      options?.cursor,
      options?.search,
    ] as const,
    queryFn: async ({ signal }) => {
      if (!jobId) throw new Error('Job ID is required');
      const outcomes = [
        'ingested',
        'deduplicated',
        'rejected',
        'failed',
        'dat_routed',
        'dat_unrouted',
      ] as const;
      const requested =
        outcome === undefined ? undefined : outcomes.find((value) => value === outcome);
      if (outcome !== undefined && requested === undefined)
        throw new Error('Unknown import outcome.');
      const response = await listJobItems({
        path: {
          jobId,
        },
        signal,
        query: {
          outcome: requested,
          cursor: options?.cursor,
          query: options?.search,
          limit: 100,
        },
      });
      if (response.error || !response.data) {
        throw new Error('Failed to fetch import details');
      }
      return response.data;
    },
    enabled: !!jobId && (options?.enabled ?? true),
  });
}
