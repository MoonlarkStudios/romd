import { notifications } from '@mantine/notifications';
import type { JobDto } from '@romd/admin-api-client';
import { useRef, useState } from 'react';
import { canPerformJobAction, type JobAction } from '../components/Jobs/jobActionEligibility';
import { useArchiveJob, useCancelJob, useRetryFailedJob } from './api/useJobActions';

export function useJobOperations() {
  const cancelMutation = useCancelJob();
  const archiveMutation = useArchiveJob();
  const retryMutation = useRetryFailedJob();
  const [retryingId, setRetryingId] = useState<string | null>(null);

  const batchLock = useRef(false);
  const [batchPending, setBatchPending] = useState(false);
  const actionsPending = batchPending || cancelMutation.isPending || archiveMutation.isPending || retryMutation.isPending;

  const handleCancel = async (job: JobDto) => {
    try {
      await cancelMutation.mutateAsync(job.id);
      notifications.show({
        title: 'Job cancelled',
        message: 'The job has been cancelled.',
        color: 'blue',
      });
    } catch {
      notifications.show({
        title: 'Failed to cancel',
        message: 'Could not cancel the job. Please try again.',
        color: 'red',
      });
    }
  };

  const handleArchive = async (job: JobDto) => {
    try {
      await archiveMutation.mutateAsync(job.id);
      notifications.show({
        title: 'Job archived',
        message: 'The job has been archived.',
        color: 'teal',
      });
    } catch {
      notifications.show({
        title: 'Failed to archive',
        message: 'Could not archive the job. Please try again.',
        color: 'red',
      });
    }
  };

  const handleRetry = async (job: JobDto) => {
    setRetryingId(job.id);
    try {
      const result = await retryMutation.mutateAsync(job.id);
      const requeued = Number(result?.requeued ?? 0);
      notifications.show({
        title: 'Retry queued',
        message:
          requeued > 0
            ? `Re-enqueued ${requeued} failed ${requeued === 1 ? 'title' : 'titles'}.`
            : 'No retryable failures remained.',
        color: requeued > 0 ? 'green' : 'gray',
      });
    } catch {
      notifications.show({
        title: 'Retry failed',
        message: 'Could not queue the retry. Please try again.',
        color: 'red',
      });
    } finally {
      setRetryingId(null);
    }
  };

  const runBatch = async (jobs: JobDto[], action: JobAction, isManager: boolean) => {
    if (batchLock.current) return null;
    batchLock.current = true;
    setBatchPending(true);
    const result = { action, succeeded: 0, skipped: 0, failed: [] as JobDto[] };
    try {
      // A bounded page, processed sequentially, avoids flooding worker dispatch.
      for (const job of jobs) {
        if (!canPerformJobAction(job, action, isManager)) { result.skipped++; continue; }
        try {
          if (action === 'archive') await archiveMutation.mutateAsync(job.id);
          else if (action === 'cancel') await cancelMutation.mutateAsync(job.id);
          else {
            const retry = await retryMutation.mutateAsync(job.id);
            if (Number(retry?.requeued ?? 0) === 0) { result.skipped++; continue; }
          }
          result.succeeded++;
        } catch { result.failed.push(job); }
      }
      return result;
    } finally { batchLock.current = false; setBatchPending(false); }
  };

  return { onCancel: handleCancel, onArchive: handleArchive, onRetry: handleRetry, retryingId, actionsPending, runBatch };
}
