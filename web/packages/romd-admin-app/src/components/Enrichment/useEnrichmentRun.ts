import {
  type ArtworkCompletionSummary,
  getJobArtworkSummary,
  type Job,
  listJobArtworkItems,
  type PageOfArtworkCompletionItem,
  queueArtworkCompletion,
} from '@romd/admin-api-client';
import {
  type InfiniteData,
  type QueryClient,
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
} from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import {
  enrichmentWorkspaceKeys,
  invalidateEnrichmentWorkspace,
} from '../../hooks/api/enrichmentWorkspaceCache';
import { useCreateJob } from '../../hooks/api/useCreateJob';
import { jobKeys } from '../../hooks/api/useJobs';
import { createRequestId } from '../../utils/requestId';
import { isRunActive } from './runPresentation';

export type ArtworkRunJob = Extract<
  Job,
  {
    jobType?: 'artwork-completion';
  }
>;

/** The UI combines cheap execution state with the separately loaded review summary. */
export type EnrichmentRun = ArtworkCompletionSummary &
  Pick<ArtworkRunJob, 'phase' | 'mode'> & {
    jobId: string;
    total: number;
    processed: number;
    nextAttemptAt: string | null;
    createdAt?: string | null;
  };

export type RunItemStatus = 'Review' | 'Failed' | 'Unfinished' | 'Working';
export type RunRetryStatus = 'Failed' | 'Unfinished';

// Each poll evaluates the facts function over the run's titles for the Review count (about 400 ms of DB time at 10k titles).
export const RUN_POLL_INTERVAL = 5000;

/**
 * A run and its item lists share one prefix, so a refresh reloads counts and lists together. It sits inside the
 * workspace family, so workspace invalidations and job events refresh it as well.
 */
export const enrichmentRunKeys = {
  all: enrichmentWorkspaceKeys.runs,
  run: (jobId: string) =>
    [
      ...enrichmentRunKeys.all,
      jobId,
    ] as const,
  items: (jobId: string, status: RunItemStatus) =>
    [
      ...enrichmentRunKeys.run(jobId),
      'items',
      status,
    ] as const,
};

/**
 * One polling tick: the run and page one of each open list. A slow refresh is never cancelled by the next tick, and
 * later pages reload when the curator pages to them again, so each tick does bounded work.
 */
async function refreshActiveRun(cache: QueryClient, jobId: string): Promise<void> {
  cache.setQueriesData<InfiniteData<PageOfArtworkCompletionItem>>(
    {
      queryKey: [
        ...enrichmentRunKeys.run(jobId),
        'items',
      ],
      type: 'active',
      fetchStatus: 'idle',
    },
    (data) =>
      data && data.pages.length > 1
        ? {
            pages: data.pages.slice(0, 1),
            pageParams: data.pageParams.slice(0, 1),
          }
        : data,
  );
  await cache.invalidateQueries(
    {
      queryKey: enrichmentRunKeys.run(jobId),
    },
    {
      cancelRefetch: false,
    },
  );
}

/** Execution comes from the host's canonical job; summary facts and open lists refresh on their own cadence. */
export function useEnrichmentRun(job: ArtworkRunJob) {
  const cache = useQueryClient();
  const jobId = job.id;
  const active = isRunActive(job.phase);
  const previouslyActive = useRef(active);
  const query = useQuery({
    queryKey: enrichmentRunKeys.run(jobId),
    queryFn: async ({ signal }) => {
      const summary = await getJobArtworkSummary({
        path: {
          jobId,
        },
        signal,
      });
      if (summary.error || !summary.data)
        throw new Error(
          summary.response?.status === 404
            ? 'This run is no longer available.'
            : 'Could not load the results for this run.',
        );
      return summary.data;
    },
    select: (summary): EnrichmentRun => ({
      ...summary,
      jobId,
      phase: job.phase,
      mode: job.mode,
      total: Number(job.total ?? 0),
      processed: Number(job.processed ?? 0),
      nextAttemptAt: job.nextAttemptAt ?? null,
      createdAt: job.createdAt ?? null,
    }),
  });

  useEffect(() => {
    const finished = previouslyActive.current && !active;
    previouslyActive.current = active;
    if (finished) void refreshActiveRun(cache, jobId);
    if (!active) return;
    const timer = setInterval(() => {
      void refreshActiveRun(cache, jobId);
    }, RUN_POLL_INTERVAL);
    return () => clearInterval(timer);
  }, [
    active,
    cache,
    jobId,
  ]);

  return query;
}

export function useEnrichmentRunItems(jobId: string, status: RunItemStatus) {
  return useInfiniteQuery({
    queryKey: enrichmentRunKeys.items(jobId, status),
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam, signal }) => {
      const result = await listJobArtworkItems({
        path: {
          jobId,
        },
        query: {
          status,
          cursor: pageParam,
          limit: 100,
        },
        signal,
      });
      if (result.error || !result.data) throw new Error('Could not load titles in this run.');
      return result.data;
    },
    getNextPageParam: (page) => (page.hasNextPage ? (page.nextCursor ?? undefined) : undefined),
  });
}

class RetryRejectedError extends Error {
  constructor() {
    super('Nothing in this run can be retried now.');
  }
}

/** Queues a continuation of the run's recorded failures or unfinished items and returns the new run id. */
export function useRetryEnrichmentRun(jobId: string) {
  const { seedJob } = useCreateJob();
  const cache = useQueryClient();
  // A failed attempt keeps its id so trying again cannot queue the same retry twice.
  const requestIds = useRef(new Map<string, string>());
  return useMutation({
    mutationFn: async (retryStatus: RunRetryStatus) => {
      const key = `${jobId}:${retryStatus}`;
      const requestId = requestIds.current.get(key) ?? createRequestId();
      requestIds.current.set(key, requestId);
      const result = await queueArtworkCompletion({
        body: {
          requestId,
          scope: 'Retry',
          retryJobId: jobId,
          retryStatus,
        },
      });
      // The API rejects a retry that no longer covers any title, e.g. once the failed titles were removed.
      if (result.response?.status === 400) throw new RetryRejectedError();
      if (result.error || !result.data)
        throw new Error('Could not start the retry. Try again; it will not be queued twice.');
      seedJob(result.data);
      requestIds.current.delete(key);
      return result.data.jobId;
    },
    onSuccess: () => {
      void cache.invalidateQueries({
        queryKey: jobKeys.all,
      });
      void invalidateEnrichmentWorkspace(cache);
    },
    // A rejected retry means the run's counts are out of date; refresh them so the offered retries match.
    onError: (error) =>
      error instanceof RetryRejectedError
        ? cache.invalidateQueries({
            queryKey: enrichmentRunKeys.run(jobId),
          })
        : undefined,
  });
}
