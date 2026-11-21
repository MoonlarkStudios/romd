import { getServerImport, startServerImport } from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiError } from '../../components/Administration/apiError';
import { useCreateJob } from './useCreateJob';

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

export function useStartServerImport() {
  const client = useQueryClient();
  const { seedJob } = useCreateJob();
  return useMutation({
    mutationFn: async (retryJobId?: string) => {
      const { data, error } = await startServerImport({
        body: {
          retryJobId,
        },
      });
      if (error || !data)
        throw new Error(apiError(error, 'Could not check the server import folder.'));
      return data;
    },
    onSuccess: (result) => {
      if ('jobId' in result) {
        seedJob(result, {
          invalidateDelayMs: 0,
        });
        void client.invalidateQueries({
          queryKey: statusKey,
        });
      } else {
        client.setQueryData(statusKey, result);
      }
    },
    onError: () => {
      void client.invalidateQueries({
        queryKey: statusKey,
      });
    },
  });
}
