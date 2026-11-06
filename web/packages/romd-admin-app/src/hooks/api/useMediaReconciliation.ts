import {
  getMediaReconciliationPolicies,
  type StartMediaReconciliationSelectionRequest,
  startMediaReconciliationSelection,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCreateJob } from './useCreateJob';
import { jobKeys } from './useJobs';

export const mediaReconciliationKeys = {
  policies: [
    'media',
    'reconciliation',
    'policies',
  ] as const,
};

export function useMediaReconciliationPolicies() {
  return useQuery({
    queryKey: mediaReconciliationKeys.policies,
    queryFn: async ({ signal }) => {
      const response = await getMediaReconciliationPolicies({
        signal,
      });
      if (response.error || !response.data)
        throw new Error(response.error?.detail ?? 'Could not load media policies.');
      return response.data;
    },
  });
}

export function useStartMediaSelection() {
  const queryClient = useQueryClient();
  const { seedJob } = useCreateJob();
  return useMutation({
    retry: false,
    mutationFn: async (request: StartMediaReconciliationSelectionRequest) => {
      const response = await startMediaReconciliationSelection({
        body: request,
      });
      if (response.error || !response.data)
        throw new Error(response.error?.detail ?? 'Could not start media reconciliation.');
      return response.data;
    },
    onSuccess: (reference) => {
      seedJob(reference, {
        invalidateDelayMs: 0,
      });
      void queryClient.invalidateQueries({
        queryKey: jobKeys.all,
      });
    },
  });
}
