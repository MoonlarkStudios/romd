import { type JobDto, listJobs } from '@romd/admin-api-client';
import type { QueryClient } from '@tanstack/react-query';

export const ACTIVE_JOB_LIMIT = 50;
const MAX_READ_SNAPSHOTS = ACTIVE_JOB_LIMIT * 2;
export const jobKeys = {
  all: [
    'jobs',
  ] as const,
  lists: () =>
    [
      ...jobKeys.all,
      'list',
    ] as const,
  list: () =>
    [
      ...jobKeys.lists(),
    ] as const,
  details: () =>
    [
      ...jobKeys.all,
      'detail',
    ] as const,
  detail: (id: string) =>
    [
      ...jobKeys.details(),
      id,
    ] as const,
};

interface ActiveRead {
  signal: AbortSignal;
  snapshots: Map<string, JobDto>;
}
// Entries exist only for the exact in-flight read, and cannot retain a QueryClient.
const activeReads = new WeakMap<QueryClient, ActiveRead>();

function newestActiveJobs(rows: Iterable<JobDto>, snapshots: Iterable<JobDto> = []): JobDto[] {
  const jobs = new Map<string, JobDto>();
  for (const job of rows) if (!job.isArchived) jobs.set(job.id, job);
  for (const job of snapshots) {
    if (job.isArchived) jobs.delete(job.id);
    else jobs.set(job.id, job);
  }
  return [
    ...jobs.values(),
  ]
    .sort((left, right) => {
      // Stub seeds without a timestamp stay visible until their canonical read arrives.
      const leftTime = left.createdAt ? Date.parse(left.createdAt) : Number.POSITIVE_INFINITY;
      const rightTime = right.createdAt ? Date.parse(right.createdAt) : Number.POSITIVE_INFINITY;
      if (leftTime !== rightTime) return leftTime > rightTime ? -1 : 1;
      return left.id === right.id ? 0 : left.id > right.id ? -1 : 1;
    })
    .slice(0, ACTIVE_JOB_LIMIT);
}

/** Reads the newest complete active page while reconciling snapshots observed during this read only. */
export async function readActiveJobPage(
  cache: QueryClient,
  signal: AbortSignal,
): Promise<JobDto[]> {
  const read: ActiveRead = {
    signal,
    snapshots: new Map(),
  };
  activeReads.set(cache, read);
  const forget = () => {
    if (activeReads.get(cache) === read) activeReads.delete(cache);
  };
  const markAborted = () => {
    if (activeReads.get(cache) !== read) return;
    forget();
    // Query cancellation has already restored manual snapshot state; keep that partial page stale.
    void cache.invalidateQueries({
      queryKey: jobKeys.list(),
      exact: true,
      refetchType: 'none',
    });
  };
  signal.addEventListener('abort', markAborted, {
    once: true,
  });
  let candidates: JobDto[] = [];
  let cursor: string | undefined;
  const seen = new Set<string>();
  try {
    do {
      signal.throwIfAborted();
      const response = await listJobs({
        signal,
        query: {
          archive: 'active',
          limit: ACTIVE_JOB_LIMIT - candidates.length,
          cursor,
        },
      });
      signal.throwIfAborted();
      if (response.error || !response.data) throw new Error('Failed to fetch jobs');
      candidates = newestActiveJobs(
        [
          ...candidates,
          ...response.data.items,
        ],
        read.snapshots.values(),
      );
      if (candidates.length === ACTIVE_JOB_LIMIT || !response.data.hasNextPage) return candidates;
      const next = response.data.nextCursor;
      if (!next || seen.has(next) || seen.size === MAX_READ_SNAPSHOTS)
        throw new Error('Failed to fetch jobs');
      seen.add(next);
      cursor = next;
    } while (cursor);
    return candidates;
  } finally {
    signal.removeEventListener('abort', markAborted);
    forget();
  }
}

/** Applies a newer snapshot without starving an in-flight canonical page or allowing an older read to replace it. */
export function storeJobSnapshot(cache: QueryClient, job: JobDto): void {
  const state = cache.getQueryState<JobDto[]>(jobKeys.list());
  const wasVisible = state?.data?.some((current) => current.id === job.id) ?? false;
  const read = activeReads.get(cache);
  const overflow =
    !!read && !read.snapshots.has(job.id) && read.snapshots.size === MAX_READ_SNAPSHOTS;
  if (read && !overflow) read.snapshots.set(job.id, job);
  else {
    // Unknown/custom reads and an overflowing overlay must restart; tombstones are never silently evicted.
    void cache.cancelQueries({
      queryKey: jobKeys.list(),
      exact: true,
    });
  }
  void cache.cancelQueries({
    queryKey: jobKeys.detail(job.id),
    exact: true,
  });
  cache.setQueryData(jobKeys.detail(job.id), job);
  cache.setQueryData<JobDto[]>(jobKeys.list(), (old) =>
    newestActiveJobs(old ?? [], [
      job,
    ]),
  );
  if (!read || overflow) {
    const incomplete =
      state?.data === undefined || state.isInvalidated || state.fetchStatus !== 'idle';
    if (incomplete || (job.isArchived && wasVisible))
      void cache.invalidateQueries({
        queryKey: jobKeys.list(),
        exact: true,
      });
  }
}
