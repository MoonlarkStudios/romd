import type { Job } from '@romd/admin-api-client';
import { QueryClient, QueryObserver } from '@tanstack/react-query';
import { waitFor } from '@testing-library/react';
import { expect, it } from 'vitest';
import { storeJobSnapshot } from './jobCache';
import { jobKeys } from './useJobs';

const job = (index: number): Job => ({
  id: `${index.toString().padStart(8, '0')}-0000-4000-8000-000000000000`,
  correlationId: 'batch',
  jobType: 'upload',
  sourceFilename: `${index}.zip`,
  phase: 'Completed',
  isTerminal: true,
  isArchived: false,
  createdAt: new Date(Date.UTC(2026, 0, 1, 0, index)).toISOString(),
  progressPercent: 100,
  errors: [],
});

it('keeps only the newest fifty jobs after more than one page of live snapshots arrives', () => {
  const cache = new QueryClient();
  for (let index = 0; index < 60; index++) storeJobSnapshot(cache, job(index));
  expect(cache.getQueryData(jobKeys.list())).toEqual(
    Array.from(
      {
        length: 50,
      },
      (_, index) => job(59 - index),
    ),
  );
  expect(cache.getQueryData(jobKeys.detail(job(0).id))).toEqual(job(0));
});

it('does not move an older unarchived job ahead of the bounded newest page', () => {
  const cache = new QueryClient();
  const newest = Array.from(
    {
      length: 50,
    },
    (_, index) => job(59 - index),
  );
  cache.setQueryData(jobKeys.list(), newest);
  cache.setQueryData(jobKeys.detail(job(0).id), {
    ...job(0),
    isArchived: true,
  });
  storeJobSnapshot(cache, job(0));
  expect(cache.getQueryData(jobKeys.list())).toEqual(newest);
  expect(cache.getQueryData(jobKeys.detail(job(0).id))).toEqual(job(0));
});

it('backfills the observed newest page after a visible archive while ignoring unseen archived rows', async () => {
  const cache = new QueryClient();
  const newest = Array.from(
    {
      length: 50,
    },
    (_, index) => job(59 - index),
  );
  const backfilled = Array.from(
    {
      length: 50,
    },
    (_, index) => job(58 - index),
  );
  const historyKey = [
    'jobs',
    'history',
    {
      archive: 'all',
    },
  ];
  const history = {
    items: newest,
    nextCursor: 'next-page',
    hasNextPage: true,
  };
  cache.setQueryData(jobKeys.list(), newest);
  cache.setQueryData(historyKey, history);
  let reads = 0;
  const observer = new QueryObserver(cache, {
    queryKey: jobKeys.list(),
    queryFn: async () => {
      reads++;
      return backfilled;
    },
    staleTime: Infinity,
  });
  const unsubscribe = observer.subscribe(() => {});
  try {
    storeJobSnapshot(cache, {
      ...job(59),
      isArchived: true,
    });
    await waitFor(() => expect(cache.getQueryData(jobKeys.list())).toEqual(backfilled));
    expect(reads).toBe(1);
    storeJobSnapshot(cache, {
      ...job(0),
      isArchived: true,
    });
    expect(reads).toBe(1);
    expect(cache.getQueryData(historyKey)).toEqual(history);
  } finally {
    unsubscribe();
  }
});
