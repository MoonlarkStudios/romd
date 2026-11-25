import { getServerImport, getServerImportRetry, startServerImport } from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiError } from '../../components/Administration/apiError';
import { useCreateJob } from './useCreateJob';
import { ApiClientError } from './useTaxonomy';

const statusKey = [
  'server-import',
] as const;

export function useServerImportStatus(enabled: boolean, observing: boolean) {
  return useQuery({
    queryKey: statusKey,
    enabled,
    queryFn: async ({ signal }) => {
      const { data, error } = await getServerImport({
        signal,
      });
      if (error || !data)
        throw new Error(apiError(error, 'Could not check the server import folder.'));
      return data;
    },
    refetchInterval: ({ state }) => {
      if (!observing) return false;
      if (state.data?.isRunning) return 2000;
      return state.data?.intervalMinutes ? 15_000 : false;
    },
  });
}

export function useServerImportRetryReview(jobId: string) {
  return useQuery({
    queryKey: [
      ...statusKey,
      'retry-review',
      jobId,
    ],
    refetchOnMount: 'always',
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
    queryFn: async ({ signal }) => {
      const { data, error } = await getServerImportRetry({
        path: {
          jobId,
        },
        signal,
      });
      if (error || !data) throw new Error(apiError(error, 'Could not review the remaining files.'));
      return data;
    },
  });
}

export function useStartServerImport() {
  const client = useQueryClient();
  const { seedJob } = useCreateJob();
  return useMutation({
    mutationFn: async (retry?: { retryJobId: string; reviewedRevision: string }) => {
      const { data, error, response } = await startServerImport({
        body: retry ?? {},
      });
      if (error || !data)
        throw new ApiClientError(
          apiError(
            error,
            retry ? 'Could not queue the retry.' : 'Could not check the server import folder.',
          ),
          response?.status,
        );
      return data;
    },
    onSuccess: (result) => {
      if ('jobId' in result) {
        seedJob(result, {
          invalidateDelayMs: 0,
        });
        void client.invalidateQueries({
          queryKey: statusKey,
          exact: true,
        });
      } else {
        client.setQueryData(statusKey, result);
      }
    },
    onError: () => {
      void client.invalidateQueries({
        queryKey: statusKey,
        exact: true,
      });
    },
  });
}
