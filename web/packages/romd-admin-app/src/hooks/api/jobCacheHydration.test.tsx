import { client, type JobDto } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { server } from '../../test/msw/server';
import { storeJobSnapshot } from './jobCache';
import { jobKeys, useJobs } from './useJobs';

const job = (index: number, overrides: Partial<JobDto> = {}): JobDto =>
  ({
    id: `${index.toString().padStart(8, '0')}-0000-4000-8000-000000000000`,
    correlationId: 'correlation',
    jobType: 'upload',
    sourceFilename: `${index}.zip`,
    phase: 'IngestingRoms',
    isTerminal: false,
    isArchived: false,
    createdAt: new Date(Date.UTC(2026, 0, 1, 0, index)).toISOString(),
    progressPercent: 0,
    errors: [],
    ...overrides,
  }) as JobDto;
const rows = (last: number, count = 50) =>
  Array.from(
    {
      length: count,
    },
    (_, index) => job(last - index),
  );
const page = (items: JobDto[], nextCursor: string | null = null) => ({
  items,
  nextCursor,
  hasNextPage: nextCursor !== null,
});
function deferred() {
  let release: () => void = () => {};
  const promise = new Promise<void>((resolve) => {
    release = resolve;
  });
  return {
    promise,
    release,
  };
}
function setup(
  cache = new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: Infinity,
        retry: false,
      },
    },
  }),
) {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  return {
    cache,
    wrapper,
  };
}
beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
});
afterEach(() => {
  client.setConfig({
    baseUrl: '',
  });
  vi.restoreAllMocks();
});

it('finishes first hydration under repeated same-job snapshots without restarting or overwriting the latest progress', async () => {
  const held = deferred();
  const calls = vi.spyOn(client, 'get');
  server.use(
    http.get('http://localhost/api/jobs', async () => {
      await held.promise;
      return HttpResponse.json(page(rows(59)));
    }),
  );
  const { cache, wrapper } = setup();
  const { result } = renderHook(() => useJobs(), {
    wrapper,
  });
  await waitFor(() => expect(calls).toHaveBeenCalledTimes(1));
  const signal = calls.mock.calls[0]?.[0].signal;
  const latest = job(59, {
    progressPercent: 75,
    currentItem: 'latest.rom',
  });
  act(() => {
    for (let progress = 1; progress < 75; progress++)
      storeJobSnapshot(
        cache,
        job(59, {
          progressPercent: progress,
        }),
      );
    storeJobSnapshot(cache, latest);
  });
  expect(signal?.aborted).toBe(false);
  expect(calls).toHaveBeenCalledTimes(1);
  await act(async () => {
    held.release();
  });
  await waitFor(() =>
    expect(result.current.data).toEqual([
      latest,
      ...rows(58, 49),
    ]),
  );
  expect(calls).toHaveBeenCalledTimes(1);
});

it('uses the real next cursor to backfill an archived row from an older first-page response', async () => {
  const held = deferred();
  const urls: URL[] = [];
  const calls = vi.spyOn(client, 'get');
  server.use(
    http.get('http://localhost/api/jobs', async ({ request }) => {
      const url = new URL(request.url);
      urls.push(url);
      if (url.searchParams.has('cursor'))
        return HttpResponse.json(
          page(
            [
              job(9),
            ],
            'after-nine',
          ),
        );
      await held.promise;
      return HttpResponse.json(page(rows(59), 'after-ten'));
    }),
  );
  const { cache, wrapper } = setup();
  const { result } = renderHook(() => useJobs(), {
    wrapper,
  });
  await waitFor(() => expect(urls).toHaveLength(1));
  act(() =>
    storeJobSnapshot(
      cache,
      job(59, {
        isArchived: true,
      }),
    ),
  );
  await act(async () => {
    held.release();
  });
  await waitFor(() => expect(result.current.data).toEqual(rows(58)));
  expect(urls).toHaveLength(2);
  expect(urls[1]?.searchParams.get('cursor')).toBe('after-ten');
  expect(urls[1]?.searchParams.get('limit')).toBe('1');
  expect(calls.mock.calls[1]?.[0].signal).toBe(calls.mock.calls[0]?.[0].signal);
  expect(cache.getQueryData(jobKeys.detail(job(59).id))).toMatchObject({
    isArchived: true,
  });
});

it('lets archive backfill finish while repeated progress updates arrive', async () => {
  const held = deferred();
  const calls = vi.spyOn(client, 'get');
  server.use(
    http.get('http://localhost/api/jobs', async () => {
      await held.promise;
      return HttpResponse.json(page(rows(58)));
    }),
  );
  const { cache, wrapper } = setup();
  cache.setQueryData(jobKeys.list(), rows(59));
  const { result } = renderHook(() => useJobs(), {
    wrapper,
  });
  act(() =>
    storeJobSnapshot(
      cache,
      job(59, {
        isArchived: true,
      }),
    ),
  );
  await waitFor(() => expect(calls).toHaveBeenCalledTimes(1));
  const latest = job(58, {
    progressPercent: 80,
  });
  act(() => {
    for (let progress = 1; progress < 80; progress++)
      storeJobSnapshot(
        cache,
        job(58, {
          progressPercent: progress,
        }),
      );
    storeJobSnapshot(cache, latest);
  });
  expect(calls.mock.calls[0]?.[0].signal?.aborted).toBe(false);
  await act(async () => {
    held.release();
  });
  await waitFor(() =>
    expect(result.current.data).toEqual([
      latest,
      ...rows(57, 49),
    ]),
  );
  expect(calls).toHaveBeenCalledTimes(1);
});

it('keeps inactive seed caches invalidated until the next Infinity observer hydrates', async () => {
  const latest = job(59, {
    progressPercent: 80,
  });
  const calls = vi.spyOn(client, 'get');
  server.use(
    http.get('http://localhost/api/jobs', () =>
      HttpResponse.json(
        page([
          latest,
          ...rows(58, 49),
        ]),
      ),
    ),
  );
  const { cache, wrapper } = setup();
  for (let progress = 1; progress < 80; progress++)
    storeJobSnapshot(
      cache,
      job(59, {
        progressPercent: progress,
      }),
    );
  storeJobSnapshot(cache, latest);
  expect(calls).not.toHaveBeenCalled();
  expect(cache.getQueryState(jobKeys.list())?.isInvalidated).toBe(true);
  const { result } = renderHook(() => useJobs(), {
    wrapper,
  });
  await waitFor(() =>
    expect(result.current.data).toEqual([
      latest,
      ...rows(58, 49),
    ]),
  );
  expect(calls).toHaveBeenCalledTimes(1);
});

it('safely restarts when distinct in-flight snapshots exceed the bounded overlay and ignores the abandoned response', async () => {
  const held = deferred();
  const canonical = rows(159);
  const calls = vi.spyOn(client, 'get');
  let reads = 0;
  server.use(
    http.get('http://localhost/api/jobs', async () => {
      if (++reads === 1) {
        await held.promise;
        return HttpResponse.json(page(rows(59)));
      }
      return HttpResponse.json(page(canonical));
    }),
  );
  const { cache, wrapper } = setup();
  const { result } = renderHook(() => useJobs(), {
    wrapper,
  });
  await waitFor(() => expect(reads).toBe(1));
  act(() => {
    storeJobSnapshot(
      cache,
      job(59, {
        isArchived: true,
      }),
    );
    for (let index = 60; index <= 159; index++) storeJobSnapshot(cache, job(index));
  });
  await waitFor(() => expect(reads).toBe(2));
  await waitFor(() => expect(result.current.data).toEqual(canonical));
  expect(calls.mock.calls[0]?.[0].signal?.aborted).toBe(true);
  await act(async () => {
    held.release();
  });
  expect(result.current.data).toEqual(canonical);
  expect(cache.getQueryData(jobKeys.detail(job(59).id))).toMatchObject({
    isArchived: true,
  });
});

it('does not publish a partial page when cursor backfill fails and clears per-read state for retry', async () => {
  const held = deferred();
  let fail = true;
  server.use(
    http.get('http://localhost/api/jobs', async ({ request }) => {
      if (new URL(request.url).searchParams.has('cursor'))
        return HttpResponse.json(
          {
            detail: 'Backfill failed.',
          },
          {
            status: 500,
          },
        );
      await held.promise;
      return HttpResponse.json(fail ? page(rows(59), 'after-ten') : page(rows(58)));
    }),
  );
  const { cache, wrapper } = setup();
  const { result } = renderHook(() => useJobs(), {
    wrapper,
  });
  await waitFor(() => expect(cache.getQueryState(jobKeys.list())?.fetchStatus).toBe('fetching'));
  act(() =>
    storeJobSnapshot(
      cache,
      job(59, {
        isArchived: true,
      }),
    ),
  );
  await act(async () => {
    held.release();
  });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.data).toEqual([]);
  fail = false;
  await act(async () => {
    await result.current.refetch();
  });
  expect(cache.getQueryData(jobKeys.list())).toEqual(rows(58));
  await waitFor(() => expect(result.current.data).toEqual(rows(58)));
});

it('lets a later canonical refresh replace cached progress when no snapshot arrived during that read', async () => {
  const { cache, wrapper } = setup();
  cache.setQueryData(jobKeys.list(), [
    job(59, {
      progressPercent: 10,
    }),
    ...rows(58, 49),
  ]);
  const canonical = [
    job(59, {
      progressPercent: 90,
    }),
    ...rows(58, 49),
  ];
  server.use(http.get('http://localhost/api/jobs', () => HttpResponse.json(page(canonical))));
  const { result } = renderHook(() => useJobs(), {
    wrapper,
  });
  expect(result.current.data?.[0]?.progressPercent).toBe(10);
  await act(async () => {
    await result.current.refetch();
  });
  expect(cache.getQueryData(jobKeys.list())).toEqual(canonical);
  await waitFor(() => expect(result.current.data).toEqual(canonical));
});

it('marks interrupted first hydration stale on unmount and hydrates on the next Infinity observer', async () => {
  const held = deferred();
  const latest = job(59, {
    progressPercent: 75,
  });
  const canonical = [
    latest,
    ...rows(58, 49),
  ];
  let reads = 0;
  server.use(
    http.get('http://localhost/api/jobs', async () => {
      if (++reads === 1) {
        await held.promise;
        return HttpResponse.json(page(rows(59)));
      }
      return HttpResponse.json(page(canonical));
    }),
  );
  const { cache, wrapper } = setup();
  const first = renderHook(() => useJobs(), {
    wrapper,
  });
  try {
    await waitFor(() => expect(reads).toBe(1));
    act(() => storeJobSnapshot(cache, latest));
    await waitFor(() =>
      expect(first.result.current.data).toEqual([
        latest,
      ]),
    );
    first.unmount();
    expect(cache.getQueryState(jobKeys.list())?.fetchStatus).toBe('idle');
    expect(cache.getQueryState(jobKeys.list())?.isInvalidated).toBe(true);
    expect(reads).toBe(1);
    const second = renderHook(() => useJobs(), {
      wrapper,
    });
    await waitFor(() => expect(second.result.current.data).toEqual(canonical));
    expect(reads).toBe(2);
    await act(async () => {
      held.release();
    });
    expect(cache.getQueryData(jobKeys.list())).toEqual(canonical);
    expect(cache.getQueryState(jobKeys.list())?.isInvalidated).toBe(false);
  } finally {
    held.release();
  }
});

it('marks directly cancelled archive backfill stale without automatically refetching and recovers on remount', async () => {
  const held = deferred();
  const latest = job(58, {
    progressPercent: 80,
  });
  const canonical = [
    latest,
    ...rows(57, 49),
  ];
  let reads = 0;
  server.use(
    http.get('http://localhost/api/jobs', async () => {
      if (++reads === 1) {
        await held.promise;
        return HttpResponse.json(page(rows(58)));
      }
      return HttpResponse.json(page(canonical));
    }),
  );
  const { cache, wrapper } = setup();
  cache.setQueryData(jobKeys.list(), rows(59));
  const first = renderHook(() => useJobs(), {
    wrapper,
  });
  try {
    act(() =>
      storeJobSnapshot(
        cache,
        job(59, {
          isArchived: true,
        }),
      ),
    );
    await waitFor(() => expect(reads).toBe(1));
    act(() => storeJobSnapshot(cache, latest));
    await act(async () => {
      await cache.cancelQueries({
        queryKey: jobKeys.list(),
        exact: true,
      });
    });
    expect(cache.getQueryState(jobKeys.list())?.fetchStatus).toBe('idle');
    expect(cache.getQueryState(jobKeys.list())?.isInvalidated).toBe(true);
    expect(reads).toBe(1);
    first.unmount();
    const second = renderHook(() => useJobs(), {
      wrapper,
    });
    await waitFor(() => expect(second.result.current.data).toEqual(canonical));
    expect(reads).toBe(2);
    await act(async () => {
      held.release();
    });
    expect(cache.getQueryData(jobKeys.list())).toEqual(canonical);
    expect(cache.getQueryState(jobKeys.list())?.isInvalidated).toBe(false);
    expect(cache.getQueryData(jobKeys.detail(job(59).id))).toMatchObject({
      isArchived: true,
    });
  } finally {
    held.release();
  }
});

it('does not let an abandoned read finalizer invalidate or overwrite a newer successful read', async () => {
  const held = deferred();
  let reads = 0;
  server.use(
    http.get('http://localhost/api/jobs', async () => {
      if (++reads === 1) {
        await held.promise;
        return HttpResponse.json(page(rows(59)));
      }
      return HttpResponse.json(page(rows(58)));
    }),
  );
  const { cache, wrapper } = setup();
  const first = renderHook(() => useJobs(), {
    wrapper,
  });
  try {
    await waitFor(() => expect(reads).toBe(1));
    act(() =>
      storeJobSnapshot(
        cache,
        job(59, {
          isArchived: true,
        }),
      ),
    );
    first.unmount();
    const second = renderHook(() => useJobs(), {
      wrapper,
    });
    await waitFor(() => expect(second.result.current.data).toEqual(rows(58)));
    await act(async () => {
      held.release();
    });
    expect(cache.getQueryData(jobKeys.list())).toEqual(rows(58));
    expect(cache.getQueryState(jobKeys.list())?.isInvalidated).toBe(false);
    expect(reads).toBe(2);
  } finally {
    held.release();
  }
});
