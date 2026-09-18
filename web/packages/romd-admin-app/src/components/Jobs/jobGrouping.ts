import type { JobDto } from '@romd/admin-api-client';
import { getJobOutcome, getJobTitle } from './jobTypeRegistry';

export interface JobGroup {
  key: string;
  jobs: JobDto[];
}

/**
 * Collapse consecutive terminal jobs that share a title and outcome into one
 * group (e.g. five identical materializations). Running jobs are never merged,
 * so their live progress always stays visible.
 */
export function groupConsecutiveJobs(jobs: JobDto[]): JobGroup[] {
  const groups: JobGroup[] = [];
  for (const job of jobs) {
    const outcome = getJobOutcome(job);
    const key =
      !job.isTerminal ? `running ${job.id}` : `${getJobTitle(job)} ${outcome}`;
    const last = groups.at(-1);
    if (last && last.key === key) {
      last.jobs.push(job);
    } else {
      groups.push({ key, jobs: [job] });
    }
  }
  return groups;
}

function formatClock(ms: number): string {
  return new Date(ms).toLocaleTimeString(undefined, {
    hour: 'numeric',
    minute: '2-digit',
  });
}

/**
 * Earliest-to-latest clock range across every timestamp in the group, computed
 * by min/max so it never depends on the order jobs arrive in.
 */
export function formatGroupRange(jobs: JobDto[]): string {
  const times = jobs
    .flatMap((job) => [job.startedAt, job.completedAt, job.createdAt])
    .filter((value): value is string => Boolean(value))
    .map((value) => new Date(value).getTime())
    .filter((ms) => !Number.isNaN(ms));

  if (times.length === 0) return '';

  const start = formatClock(Math.min(...times));
  const end = formatClock(Math.max(...times));
  return start === end ? start : `${start}–${end}`;
}
