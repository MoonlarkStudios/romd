import type { JobItemPage } from '@romd/admin-api-client';
import { getJobItems } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { jobKeys } from './useJobs';

/**
 * Per-file provenance for an upload job. `outcome` (if given) filters to a single outcome
 * (e.g. "rejected", "dat_unrouted"). Enabled only once the job is terminal.
 */
export function useJobItems(
  jobId: string | undefined,
  outcome: string | undefined,
  options?: { enabled?: boolean; cursor?: string; search?: string },
) {
  return useQuery({
    queryKey: [...jobKeys.detail(jobId ?? ''), 'items', outcome ?? 'all', options?.cursor, options?.search] as const,
    queryFn: async ({ signal }) => {
      if (!jobId) throw new Error('Job ID is required');
      const response = await getJobItems({
        path: { id: jobId },
        signal,
        query: { outcome, cursor: options?.cursor, search: options?.search, limit: 100 },
      });
      if (response.error) {
        throw new Error('Failed to fetch import details');
      }
      return response.data as JobItemPage;
    },
    enabled: !!jobId && (options?.enabled ?? true),
  });
}
