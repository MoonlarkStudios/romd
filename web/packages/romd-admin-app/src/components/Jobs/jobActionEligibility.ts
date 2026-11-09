import type { Job } from '@romd/admin-api-client';

export type JobAction = 'archive' | 'cancel' | 'retry';

// Visibility is already scoped by the API. The server rechecks authorization and
// current job state for every operation, including changes since this snapshot.
export function canPerformJobAction(
  job: Job,
  action: JobAction,
  isManager: boolean,
  isAdmin = false,
): boolean {
  const media = job.jobType === 'media-reconciliation';
  if (media && !isAdmin) return false;
  switch (action) {
    case 'archive':
      return !!job.isTerminal && !job.isArchived;
    case 'cancel':
      return job.jobType !== 'metadata-rematerialization' && !job.isTerminal && !job.isArchived;
    case 'retry':
      if (job.jobType === 'media-reconciliation')
        return (
          !!job.isTerminal &&
          [
            'CompletedWithErrors',
            'Failed',
            'Cancelled',
            'Superseded',
          ].includes(job.phase)
        );
      return (
        isManager &&
        !!job.isTerminal &&
        job.jobType === 'bulk_enrichment' &&
        !!job.hasErrors &&
        Number(job.failedCount ?? 0) > 0
      );
  }
}
