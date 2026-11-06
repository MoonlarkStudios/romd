import { notifications } from '@mantine/notifications';
import type { JobDto } from '@romd/admin-api-client';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import { useLocation } from 'react-router';
import {
  getJobOutcome,
  getJobTitle,
  getJobTypeConfig,
  type JobOutcome,
  mediaReconciliationSummary,
} from '../../components/Jobs/jobTypeRegistry';
import { useJobs } from '../api/useJobs';

interface JobSnapshot {
  isTerminal: boolean;
  hasErrors: boolean;
  progressPercent: number;
  currentItem: string | null | undefined;
  phase: string;
  nextAttemptAt: string | null | undefined;
  mediaSummary: string | null;
}

function jobNotificationId(jobId: string): string {
  return `job-${jobId}`;
}

function buildProgressMessage(job: JobDto): string {
  const parts: string[] = [];
  if (getJobOutcome(job) === 'deferred') parts.push('Deferred');
  else if (job.phase) parts.push(job.phase);
  if (!job.isTerminal && job.nextAttemptAt)
    parts.push(`Next attempt ${new Date(job.nextAttemptAt).toLocaleString()}`);
  if (job.currentItem) parts.push(job.currentItem);
  const mediaSummary = mediaReconciliationSummary(job);
  if (mediaSummary) parts.push(mediaSummary);
  const pct = Number(job.progressPercent ?? 0);
  if (pct > 0) parts.push(`(${pct}%)`);
  return parts.join(': ') || 'Processing...';
}

function extractTitleIdFromPath(pathname: string): string | null {
  const match = pathname.match(/^\/titles\/([^/]+)/);
  return match ? match[1] : null;
}

/** Enrichment runs report execution only: finishing never claims every title succeeded or is resolved. */
const runOutcomes: Partial<
  Record<
    JobOutcome,
    {
      title: string;
      color: string;
    }
  >
> = {
  completed: {
    title: 'Enrichment run finished',
    color: 'green',
  },
  partial: {
    title: 'Enrichment run finished with errors',
    color: 'yellow',
  },
  failed: {
    title: 'Enrichment run stopped with an error',
    color: 'red',
  },
  cancelled: {
    title: 'Enrichment run cancelled',
    color: 'gray',
  },
};

/** Runs store a fixed placeholder in sourceFilename, so they are named by their job title instead. */
function notificationSubject(job: JobDto): string {
  return job.jobType === 'artwork-completion' ? getJobTitle(job) : job.sourceFilename;
}

function buildTerminalNotification(
  job: JobDto,
  label: string,
): {
  title: string;
  message: string;
  color: string;
} {
  const outcome = getJobOutcome(job);
  const mediaSummary = mediaReconciliationSummary(job);
  if (
    job.jobType === 'metadata-rematerialization' &&
    [
      'completed',
      'partial',
    ].includes(outcome)
  ) {
    const deferred = Number(job.deferredCount ?? 0);
    return {
      title: `${label}: ${outcome === 'partial' ? 'Completed with errors' : 'Complete'}`,
      message: deferred
        ? `${job.sourceFilename} · ${deferred.toLocaleString()} ${deferred === 1 ? 'title' : 'titles'} will retry automatically.`
        : `${job.sourceFilename} · Titles updated.`,
      color: outcome === 'partial' ? 'yellow' : 'green',
    };
  }
  const run = job.jobType === 'artwork-completion' ? runOutcomes[outcome] : undefined;
  if (run)
    return {
      ...run,
      message: getJobTitle(job),
    };
  switch (outcome) {
    case 'partial':
      return {
        title: `${label}: Completed with errors`,
        message: mediaSummary ?? `${job.sourceFilename}: ${job.errors?.length ?? 0} error(s)`,
        color: 'yellow',
      };
    case 'failed':
      return {
        title: `${label}: Failed`,
        message: mediaSummary ?? `${job.sourceFilename}: ${job.errors?.length ?? 0} error(s)`,
        color: 'red',
      };
    case 'cancelled':
      return {
        title: `${label}: Cancelled`,
        message: job.sourceFilename,
        color: 'gray',
      };
    case 'unknown':
      return {
        title: `${label}: Unknown outcome`,
        message: job.phase || 'No outcome reported',
        color: 'gray',
      };
    case 'superseded':
      return {
        title: `${label}: Superseded`,
        message: 'The source changed before this attempt published a replacement.',
        color: 'gray',
      };
    case 'deferred':
      return {
        title: `${label}: Deferred`,
        message: `${job.sourceFilename}: changes are still pending — refresh the library again when they settle`,
        color: 'gray',
      };
    default:
      return {
        title: `${label}: Complete`,
        message: mediaSummary ?? `${job.sourceFilename} processed successfully`,
        color: 'green',
      };
  }
}

/**
 * Reactor hook that watches the jobs list and manages morphing notifications.
 *
 * Each job gets a single notification that morphs from loading → progress → success/error.
 * Notifications are suppressed for enrichment completions when the user is already
 * viewing that title's detail page.
 */
export function useJobNotifications() {
  const { data: jobs } = useJobs();
  const queryClient = useQueryClient();
  const location = useLocation();

  const initializedRef = useRef(false);
  const prevJobsRef = useRef<Map<string, JobSnapshot>>(new Map());
  const errorCooldownRef = useRef<Map<string, number>>(new Map());
  const workspaceRefreshRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  useEffect(() => () => clearTimeout(workspaceRefreshRef.current), []);

  useEffect(() => {
    if (!jobs) return;

    // First load: snapshot current state without firing notifications
    if (!initializedRef.current) {
      initializedRef.current = true;
      const snapshot = new Map<string, JobSnapshot>();
      for (const job of jobs) {
        snapshot.set(job.id, {
          isTerminal: job.isTerminal ?? false,
          hasErrors: job.hasErrors ?? false,
          progressPercent: job.progressPercent ?? 0,
          currentItem: job.currentItem,
          phase: job.phase ?? '',
          nextAttemptAt: job.nextAttemptAt,
          mediaSummary: mediaReconciliationSummary(job),
        });
      }
      prevJobsRef.current = snapshot;
      return;
    }

    let refreshWorkspaceImmediately = false;
    let workspaceProgressChanged = false;
    const invalidateJobQueries = (job: JobDto) => {
      for (const key of getJobTypeConfig(job.jobType).invalidates(job)) {
        if (key[0] === 'enrichment-workspace') refreshWorkspaceImmediately = true;
        else
          void queryClient.invalidateQueries({
            queryKey: key,
          });
      }
    };
    const prevMap = prevJobsRef.current;
    const now = Date.now();
    const currentJobIds = new Set(jobs.map((j) => j.id));

    for (const job of jobs) {
      const prev = prevMap.get(job.id);
      const notifId = jobNotificationId(job.id);
      const config = getJobTypeConfig(job.jobType);
      const isTerminal = job.isTerminal ?? false;
      const hasErrors = job.hasErrors ?? false;
      const affectsWorkspace = [
        'enrichment',
        'bulk_enrichment',
        'artwork-completion',
        'artwork-import',
      ].includes(job.jobType ?? '');

      if (!prev) {
        if (affectsWorkspace) refreshWorkspaceImmediately = true;
        // New job — never seen before
        if (isTerminal) {
          // Job appeared already terminal — show its terminal notification directly
          notifications.show({
            id: notifId,
            ...buildTerminalNotification(job, config.label),
            autoClose: 5000,
          });
        } else {
          // New non-terminal job — show loading notification
          notifications.show({
            id: notifId,
            title: config.label,
            message: buildProgressMessage(job),
            loading: true,
            autoClose: false,
            withCloseButton: false,
          });
        }

        // Fire cache invalidation for terminal jobs
        if (isTerminal) {
          invalidateJobQueries(job);
        }
      } else if (!prev.isTerminal && isTerminal) {
        // Terminal transition — job just completed
        // Fire cache invalidation immediately
        invalidateJobQueries(job);

        // Context-aware suppression: hide enrichment completion when on that title's page
        const isEnrichmentJob = job.jobType === 'enrichment';
        const jobTitleId = isEnrichmentJob
          ? (
              job as {
                titleId?: string;
              }
            ).titleId
          : null;
        const currentTitleId = extractTitleIdFromPath(location.pathname);
        const shouldSuppress =
          isEnrichmentJob && jobTitleId === currentTitleId && getJobOutcome(job) === 'completed';

        if (shouldSuppress) {
          notifications.hide(notifId);
        } else {
          notifications.update({
            id: notifId,
            ...buildTerminalNotification(job, config.label),
            loading: false,
            autoClose: 5000,
            withCloseButton: true,
          });
        }
      } else if (!isTerminal) {
        // Still running — check if progress/phase changed
        const progressChanged =
          prev.progressPercent !== (job.progressPercent ?? 0) ||
          prev.currentItem !== job.currentItem ||
          prev.phase !== (job.phase ?? '') ||
          prev.nextAttemptAt !== job.nextAttemptAt ||
          prev.mediaSummary !== mediaReconciliationSummary(job);

        if (progressChanged) {
          if (affectsWorkspace) workspaceProgressChanged = true;
          notifications.update({
            id: notifId,
            title: config.label,
            message: buildProgressMessage(job),
            loading: true,
            autoClose: false,
            withCloseButton: false,
          });
        }

        // Mid-job error toast (rate-limited per job)
        if (hasErrors && !prev.hasErrors) {
          const lastError = errorCooldownRef.current.get(job.id) ?? 0;
          if (now - lastError > 10_000) {
            errorCooldownRef.current.set(job.id, now);
            notifications.update({
              id: notifId,
              title: `${config.label}: Warning`,
              message: `${notificationSubject(job)}: error during ${job.phase}`,
              color: 'yellow',
              loading: true,
              autoClose: false,
              withCloseButton: false,
            });
          }
        }
      }
    }

    if (refreshWorkspaceImmediately) {
      clearTimeout(workspaceRefreshRef.current);
      workspaceRefreshRef.current = undefined;
      void queryClient.invalidateQueries({
        queryKey: [
          'enrichment-workspace',
        ],
      });
    } else if (workspaceProgressChanged && workspaceRefreshRef.current === undefined) {
      // Coalesce bursts across jobs without postponing refresh forever during a
      // continuous stream. Completion and curator mutations still refresh immediately.
      // Progress never refetches the global counts (about 1.8 s at 10k titles); new and finished jobs do.
      workspaceRefreshRef.current = setTimeout(() => {
        workspaceRefreshRef.current = undefined;
        void queryClient.invalidateQueries({
          queryKey: [
            'enrichment-workspace',
          ],
          predicate: (query) => query.queryKey[1] !== 'counts',
        });
      }, 15_000);
    }

    // Cleanup orphans: jobs that disappeared while non-terminal
    for (const [id, snapshot] of prevMap) {
      if (!currentJobIds.has(id) && !snapshot.isTerminal) {
        notifications.hide(jobNotificationId(id));
      }
    }

    // Update snapshot
    const nextMap = new Map<string, JobSnapshot>();
    for (const job of jobs) {
      nextMap.set(job.id, {
        isTerminal: job.isTerminal ?? false,
        hasErrors: job.hasErrors ?? false,
        progressPercent: job.progressPercent ?? 0,
        currentItem: job.currentItem,
        phase: job.phase ?? '',
        nextAttemptAt: job.nextAttemptAt,
        mediaSummary: mediaReconciliationSummary(job),
      });
    }
    prevJobsRef.current = nextMap;

    // Clean up stale error cooldowns
    for (const [id] of errorCooldownRef.current) {
      if (!nextMap.has(id)) {
        errorCooldownRef.current.delete(id);
      }
    }
  }, [
    jobs,
    queryClient,
    location.pathname,
  ]);
}
