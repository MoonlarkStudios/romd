import {
  cancelJob,
  type Job,
  retryFailedJobItems,
  retryMediaReconciliation,
  updateJob,
} from '@romd/admin-api-client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import { createRequestId } from '../../utils/requestId';
import { storeJobSnapshot } from './jobCache';
import { useCreateJob } from './useCreateJob';
import { jobKeys } from './useJobs';

export function useCancelJob() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (jobId: string) => {
      const response = await cancelJob({
        path: {
          jobId,
        },
      });
      if (response.error) {
        throw new Error('Failed to cancel job');
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: jobKeys.all,
      });
    },
  });
}

export function useArchiveJob() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (
      input:
        | string
        | {
            jobId: string;
            isArchived: boolean;
          },
    ) => {
      const { jobId, isArchived } =
        typeof input === 'string'
          ? {
              jobId: input,
              isArchived: true,
            }
          : input;
      const response = await updateJob({
        path: {
          jobId,
        },
        body: {
          isArchived,
        },
      });
      if (response.error || !response.data) throw new Error('Failed to update job history.');
      storeJobSnapshot(queryClient, response.data);
      return response.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: jobKeys.all,
      });
    },
  });
}

function retryFailure(error: unknown, fallback: string) {
  const detail =
    typeof error === 'object' &&
    error !== null &&
    'detail' in error &&
    typeof error.detail === 'string'
      ? error.detail
      : undefined;
  return new Error(detail || fallback);
}

export function useRetryJob() {
  const queryClient = useQueryClient();
  const { seedJob } = useCreateJob();
  const requestIds = useRef(new Map<string, string>());
  return useMutation({
    retry: false,
    mutationFn: async (
      job: Job,
    ): Promise<
      | {
          kind: 'media';
          jobId: string;
        }
      | {
          kind: 'items';
          requeued: number;
        }
    > => {
      if (job.jobType === 'media-reconciliation') {
        const requestId = requestIds.current.get(job.id) ?? createRequestId();
        requestIds.current.set(job.id, requestId);
        const response = await retryMediaReconciliation({
          path: {
            jobId: job.id,
          },
          body: {
            requestId,
          },
        });
        if (response.error || !response.data)
          throw retryFailure(response.error, 'Could not retry media reconciliation.');
        requestIds.current.delete(job.id);
        seedJob(response.data, {
          invalidateDelayMs: 0,
        });
        return {
          kind: 'media',
          jobId: response.data.jobId,
        };
      }
      const response = await retryFailedJobItems({
        path: {
          jobId: job.id,
        },
      });
      if (response.error || !response.data)
        throw retryFailure(response.error, 'Could not retry this job.');
      return {
        kind: 'items',
        requeued: response.data.requeued,
      };
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({
        queryKey: jobKeys.all,
      });
    },
  });
}
