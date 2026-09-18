import { notifications } from '@mantine/notifications';
import type { JobDto } from '@romd/admin-api-client';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import { useLocation } from 'react-router';
import { getJobOutcome, getJobTypeConfig } from '../../components/Jobs/jobTypeRegistry';
import { useJobs } from '../api/useJobs';

interface JobSnapshot {
  isTerminal: boolean;
  hasErrors: boolean;
  progressPercent: number;
  currentItem: string | null | undefined;
  phase: string;
}

function jobNotificationId(jobId: string): string {
  return `job-${jobId}`;
}

function buildProgressMessage(job: JobDto): string {
  const parts: string[] = [];
  if (job.phase) parts.push(job.phase);
  if (job.currentItem) parts.push(job.currentItem);
  const pct = Number(job.progressPercent ?? 0);
  if (pct > 0) parts.push(`(${pct}%)`);
  return parts.join(': ') || 'Processing...';
}

function extractTitleIdFromPath(pathname: string): string | null {
  const match = pathname.match(/^\/titles\/([^/]+)/);
  return match ? match[1] : null;
}

function buildTerminalNotification(
  job: JobDto,
  label: string,
): { title: string; message: string; color: string } {
  switch (getJobOutcome(job)) {
    case 'partial':
      return {
        title: `${label}: Completed with errors`,
        message: `${job.sourceFilename}: ${job.errors?.length ?? 0} error(s)`,
        color: 'yellow',
      };
    case 'failed':
      return { title: `${label}: Failed`, message: `${job.sourceFilename}: ${job.errors?.length ?? 0} error(s)`, color: 'red' };
    case 'cancelled':
      return { title: `${label}: Cancelled`, message: job.sourceFilename, color: 'gray' };
    case 'unknown':
      return { title: `${label}: Unknown outcome`, message: job.phase || 'No outcome reported', color: 'gray' };
    case 'deferred':
      return {
        title: `${label}: Deferred`,
        message: `${job.sourceFilename}: catalog rebuild pending — will retry automatically`,
        color: 'gray',
      };
    default:
      return {
        title: `${label}: Complete`,
        message: `${job.sourceFilename} processed successfully`,
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
        });
      }
      prevJobsRef.current = snapshot;
      return;
    }

    const prevMap = prevJobsRef.current;
    const now = Date.now();
    const currentJobIds = new Set(jobs.map((j) => j.id));

    for (const job of jobs) {
      const prev = prevMap.get(job.id);
      const notifId = jobNotificationId(job.id);
      const config = getJobTypeConfig(job.jobType);
      const isTerminal = job.isTerminal ?? false;
      const hasErrors = job.hasErrors ?? false;

      if (!prev) {
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
          const keysToInvalidate = config.invalidates(job);
          for (const key of keysToInvalidate) {
            queryClient.invalidateQueries({ queryKey: key });
          }
        }
      } else if (!prev.isTerminal && isTerminal) {
        // Terminal transition — job just completed
        // Fire cache invalidation immediately
        const keysToInvalidate = config.invalidates(job);
        for (const key of keysToInvalidate) {
          queryClient.invalidateQueries({ queryKey: key });
        }

        // Context-aware suppression: hide enrichment completion when on that title's page
        const isEnrichmentJob = job.jobType === 'enrichment';
        const jobTitleId = isEnrichmentJob ? (job as { titleId?: string }).titleId : null;
        const currentTitleId = extractTitleIdFromPath(location.pathname);
        const shouldSuppress = isEnrichmentJob && jobTitleId === currentTitleId && getJobOutcome(job) === 'completed';

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
          prev.phase !== (job.phase ?? '');

        if (progressChanged) {
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
              message: `${job.sourceFilename}: error during ${job.phase}`,
              color: 'yellow',
              loading: true,
              autoClose: false,
              withCloseButton: false,
            });
          }
        }
      }
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
      });
    }
    prevJobsRef.current = nextMap;

    // Clean up stale error cooldowns
    for (const [id] of errorCooldownRef.current) {
      if (!nextMap.has(id)) {
        errorCooldownRef.current.delete(id);
      }
    }
  }, [jobs, queryClient, location.pathname]);
}
