import { type ListJobsData, listJobs } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { isJobOutcome, isJobType } from '../../components/Jobs/jobTypeRegistry';
import { jobKeys } from './useJobs';

export type JobHistoryFilters = Omit<
  NonNullable<ListJobsData['query']>,
  'outcome' | 'jobType' | 'archive'
> & {
  outcome?: string;
  jobType?: string;
  archive?: string;
};

export function useJobHistory(filters: JobHistoryFilters) {
  return useQuery({
    queryKey: [
      ...jobKeys.all,
      'history',
      filters,
    ],
    queryFn: async ({ signal }) => {
      const { outcome, jobType, archive, ...rest } = filters;
      if (outcome !== undefined && !isJobOutcome(outcome))
        throw new Error('Unknown job outcome. Review the filters.');
      if (jobType !== undefined && !isJobType(jobType))
        throw new Error('Unknown job type. Review the filters.');
      if (
        archive !== undefined &&
        archive !== 'active' &&
        archive !== 'archived' &&
        archive !== 'all'
      )
        throw new Error('Unknown job history scope. Review the filters.');
      const response = await listJobs({
        query: {
          ...rest,
          outcome,
          jobType,
          archive,
        },
        signal,
      });
      if (response.error || !response.data)
        throw new Error(response.error?.detail ?? 'Could not load job history');
      return response.data;
    },
    // Separate from the live recent feed: re-query bounded pages without splicing
    // realtime rows across a filter/cursor boundary. Mutations invalidate jobKeys.all.
    staleTime: 10_000,
    refetchInterval: 15_000,
  });
}
