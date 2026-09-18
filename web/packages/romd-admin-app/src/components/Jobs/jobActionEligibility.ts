import type { JobDto } from '@romd/admin-api-client';

export type JobAction = 'archive' | 'cancel' | 'retry';

// Visibility is already scoped by the API. The server rechecks authorization and
// current job state for every operation, including changes since this snapshot.
export function canPerformJobAction(job: JobDto, action: JobAction, isManager: boolean): boolean {
  switch (action) {
    case 'archive': return !!job.isTerminal && !job.isArchived;
    case 'cancel': return !job.isTerminal && !job.isArchived;
    case 'retry': return isManager && !!job.isTerminal && job.jobType === 'bulk_enrichment' && !!job.hasErrors && Number(job.failedCount ?? 0) > 0;
  }
}
