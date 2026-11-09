import { notifications } from '@mantine/notifications';
import type { Job } from '@romd/admin-api-client';
import { useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import { canPerformJobAction, type JobAction } from '../components/Jobs/jobActionEligibility';
import { useArchiveJob, useCancelJob, useRetryJob } from './api/useJobActions';
import { usePermissions } from './usePermissions';

export function useJobOperations() {
  const navigate = useNavigate();
  const { hasRole } = usePermissions();
  const cancelMutation = useCancelJob();
  const archiveMutation = useArchiveJob();
  const retryMutation = useRetryJob();
  const [retryingId, setRetryingId] = useState<string | null>(null);

  const batchLock = useRef(false);
  const [batchPending, setBatchPending] = useState(false);
  const actionsPending =
    batchPending ||
    cancelMutation.isPending ||
    archiveMutation.isPending ||
    retryMutation.isPending;

  const handleCancel = async (job: Job) => {
    try {
      await cancelMutation.mutateAsync(job.id);
      const media = job.jobType === 'media-reconciliation';
      notifications.show({
        title: media ? 'Cancellation requested' : 'Job cancelled',
        message: media
          ? 'The job remains active until its media work has stopped safely.'
          : 'The job has been cancelled.',
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

  const handleArchive = async (job: Job) => {
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

  const handleRetry = async (job: Job) => {
    setRetryingId(job.id);
    try {
      const result = await retryMutation.mutateAsync(job);
      const requeued = result.kind === 'items' ? result.requeued : 0;
      if (result.kind === 'media') {
        navigate(`/jobs/${result.jobId}`);
        notifications.show({
          title: 'Media retry accepted',
          message: 'Following the accepted media job.',
          color: 'blue',
        });
        return;
      }
      notifications.show({
        title: 'Retry queued',
        message:
          requeued > 0
            ? `Re-enqueued ${requeued} failed ${requeued === 1 ? 'title' : 'titles'}.`
            : 'No retryable failures remained.',
        color: requeued > 0 ? 'green' : 'gray',
      });
    } catch (error) {
      notifications.show({
        title: 'Retry failed',
        message:
          error instanceof Error ? error.message : 'Could not queue the retry. Please try again.',
        color: 'red',
      });
    } finally {
      setRetryingId(null);
    }
  };

  const runBatch = async (jobs: Job[], action: JobAction, isManager: boolean) => {
    if (batchLock.current) return null;
    batchLock.current = true;
    setBatchPending(true);
    const result = {
      action,
      succeeded: 0,
      skipped: 0,
      failed: [] as Job[],
    };
    try {
      // A bounded page, processed sequentially, avoids flooding worker dispatch.
      for (const job of jobs) {
        if (!canPerformJobAction(job, action, isManager, hasRole('Admin'))) {
          result.skipped++;
          continue;
        }
        try {
          if (action === 'archive') await archiveMutation.mutateAsync(job.id);
          else if (action === 'cancel') await cancelMutation.mutateAsync(job.id);
          else {
            const retry = await retryMutation.mutateAsync(job);
            if (retry.kind === 'items' && retry.requeued === 0) {
              result.skipped++;
              continue;
            }
          }
          result.succeeded++;
        } catch {
          result.failed.push(job);
        }
      }
      return result;
    } finally {
      batchLock.current = false;
      setBatchPending(false);
    }
  };

  return {
    onCancel: handleCancel,
    onArchive: handleArchive,
    onRetry: handleRetry,
    retryingId,
    actionsPending,
    runBatch,
  };
}
