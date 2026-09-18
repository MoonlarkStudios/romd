import {
  archiveJob,
  cancelJob,
  retryFailedJobItems,
} from '@romd/admin-api-client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { jobKeys } from './useJobs';

export function useCancelJob() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (jobId: string) => {
      const response = await cancelJob({ path: { id: jobId } });
      if (response.error) {
        throw new Error('Failed to cancel job');
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: jobKeys.all });
    },
  });
}

export function useArchiveJob() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (jobId: string) => {
      const response = await archiveJob({ path: { id: jobId } });
      if (response.error) {
        throw new Error('Failed to archive job');
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: jobKeys.all });
    },
  });
}

export function useRetryFailedJob() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (jobId: string) => {
      const response = await retryFailedJobItems({ path: { id: jobId } });
      if (response.error) {
        throw new Error('Failed to retry job');
      }
      return response.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: jobKeys.all });
    },
  });
}
