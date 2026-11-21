import { client, type Job } from '@romd/admin-api-client';
import { startBrowserDownload } from '@romd/delivery';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { type ArtworkRunJob, useEnrichmentRun } from '../../components/Enrichment/useEnrichmentRun';
import { useImportSession } from '../../pages/Import/useImportSession';
import { server } from '../../test/msw/server';
import { downloadExportFile } from './useExport';
import { useArchiveJob } from './useJobActions';
import { useJobHistory } from './useJobHistory';
import { useJobItems } from './useJobItems';
import { jobKeys, useJob, useJobs } from './useJobs';

vi.mock('@romd/delivery', () => ({
  startBrowserDownload: vi.fn(),
}));
const batchId = '11111111-2222-4333-8444-555555555555';
const first = {
  id: '11111111-2222-4333-8444-666666666666',
  jobType: 'upload',
  correlationId: batchId,
  sourceFilename: 'first.zip',
  phase: 'Completed',
  isTerminal: true,
  errors: [],
  progressPercent: 100,
} as Job;
const second = {
  ...first,
  id: '11111111-2222-4333-8444-777777777777',
  sourceFilename: 'second.zip',
} as Job;
const page = (items: Job[], nextCursor: string | null = null) => ({
  items,
  nextCursor,
  hasNextPage: nextCursor !== null,
});
function harness(batch = false) {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
      mutations: {
        retry: false,
      },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>
      <MemoryRouter
        initialEntries={[
          batch ? `/import?batch=${batchId}` : '/jobs',
        ]}
      >
        {children}
      </MemoryRouter>
    </QueryClientProvider>
  );
  return {
    cache,
    wrapper,
  };
}
beforeEach(() => {
  vi.clearAllMocks();
  client.setConfig({
    baseUrl: 'http://localhost',
  });
  const retired = () =>
    HttpResponse.json(
      {
        status: 410,
        detail: 'Retired job route.',
      },
      {
        status: 410,
      },
    );
  server.use(
    http.get('http://localhost/api/jobs/history', retired),
    http.get('http://localhost/api/upload/batches/:batchId', retired),
    http.post('http://localhost/api/jobs/:jobId/archive', retired),
    http.get('http://localhost/api/artwork/completion/:jobId', retired),
    http.get('http://localhost/api/admin/media/chd/reverification/:jobId/items', retired),
    http.post('http://localhost/api/export/:jobId/download-ticket', retired),
  );
});
afterEach(() => {
  client.setConfig({
    baseUrl: '',
  });
  vi.restoreAllMocks();
});

it('adapts the canonical active page to the existing array cache while filtered history remains separate', async () => {
  const requests: URL[] = [];
  server.use(
    http.get('http://localhost/api/jobs', ({ request }) => {
      const url = new URL(request.url);
      requests.push(url);
      return HttpResponse.json(
        page(
          url.searchParams.has('query')
            ? [
                second,
              ]
            : [
                first,
              ],
        ),
      );
    }),
  );
  const { cache, wrapper } = harness();
  const active = renderHook(() => useJobs(), {
    wrapper,
  });
  const filters = {
    query: 'second',
    archive: 'all',
    limit: 50,
  };
  const history = renderHook(() => useJobHistory(filters), {
    wrapper,
  });
  await waitFor(() =>
    expect(active.result.current.data).toEqual([
      first,
    ]),
  );
  await waitFor(() =>
    expect(history.result.current.data?.items).toEqual([
      second,
    ]),
  );
  expect(
    requests.some(
      (url) =>
        url.searchParams.get('archive') === 'active' && url.searchParams.get('limit') === '50',
    ),
  ).toBe(true);
  act(() =>
    cache.setQueryData(jobKeys.list(), [
      second,
      first,
    ]),
  );
  expect(history.result.current.data?.items).toEqual([
    second,
  ]);
});

it('archives through PATCH with the actual updated representation', async () => {
  const requests: unknown[] = [];
  server.use(
    http.patch('http://localhost/api/jobs/:jobId', async ({ request }) => {
      requests.push(await request.json());
      return HttpResponse.json({
        ...first,
        isArchived: true,
      });
    }),
  );
  const { cache, wrapper } = harness();
  cache.setQueryData(jobKeys.detail(first.id), first);
  cache.setQueryData(jobKeys.list(), [
    first,
    second,
  ]);
  const { result } = renderHook(() => useArchiveJob(), {
    wrapper,
  });
  await act(async () => {
    await result.current.mutateAsync(first.id);
  });
  expect(requests).toEqual([
    {
      isArchived: true,
    },
  ]);
  expect(cache.getQueryData(jobKeys.detail(first.id))).toEqual({
    ...first,
    isArchived: true,
  });
  expect(cache.getQueryData(jobKeys.list())).toEqual([
    second,
  ]);
});

it('loads the next import page on demand and keeps the first page visible', async () => {
  const requests: URL[] = [];
  let release: () => void = () => {};
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  server.use(
    http.get('http://localhost/api/jobs', async ({ request }) => {
      const url = new URL(request.url);
      requests.push(url);
      if (url.searchParams.has('cursor')) {
        await held;
        return HttpResponse.json(
          page([
            second,
          ]),
        );
      }
      return HttpResponse.json(
        page(
          [
            first,
          ],
          'page-two',
        ),
      );
    }),
  );
  const { wrapper } = harness(true);
  const { result } = renderHook(() => useImportSession(), {
    wrapper,
  });
  await waitFor(() => expect(result.current.hasMoreJobs).toBe(true));
  expect(requests).toHaveLength(1);
  act(() => result.current.loadMoreJobs());
  await waitFor(() => expect(requests).toHaveLength(2));
  expect(result.current.activeJobIds).toEqual([
    first.id,
  ]);
  await act(async () => {
    release();
  });
  await waitFor(() =>
    expect(result.current.activeJobIds).toEqual([
      first.id,
      second.id,
    ]),
  );
  for (const url of requests) {
    expect(url.searchParams.get('correlationId')).toBe(batchId);
    expect(url.searchParams.get('jobType')).toBe('upload');
    expect(url.searchParams.get('archive')).toBe('all');
    expect(url.searchParams.get('limit')).toBe('25');
  }
});

it('retries a later import page without losing the imports already loaded', async () => {
  let pages = 0;
  server.use(
    http.get('http://localhost/api/jobs', ({ request }) => {
      pages++;
      if (!new URL(request.url).searchParams.has('cursor')) {
        return HttpResponse.json(
          page(
            [
              first,
            ],
            'page-two',
          ),
        );
      }
      return pages === 2
        ? HttpResponse.json(
            {
              detail: 'Page failed.',
            },
            {
              status: 500,
            },
          )
        : HttpResponse.json(
            page([
              second,
            ]),
          );
    }),
  );
  const { wrapper } = harness(true);
  const { result } = renderHook(() => useImportSession(), {
    wrapper,
  });
  await waitFor(() => expect(result.current.hasMoreJobs).toBe(true));
  act(() => result.current.loadMoreJobs());
  await waitFor(() => expect(result.current.batchError).toBe('next-page'));
  expect(pages).toBe(2);
  expect(result.current.activeJobIds).toEqual([
    first.id,
  ]);
  act(() => result.current.retryBatch());
  await waitFor(() =>
    expect(result.current.activeJobIds).toEqual([
      first.id,
      second.id,
    ]),
  );
  expect(result.current.batchError).toBeNull();
});

it('polls loaded pages while a later page contains active import work', async () => {
  let pages = 0;
  server.use(
    http.get('http://localhost/api/jobs', ({ request }) => {
      pages++;
      return HttpResponse.json(
        new URL(request.url).searchParams.has('cursor')
          ? page([
              {
                ...second,
                isTerminal: false,
                phase: 'Ingesting',
              },
            ])
          : page(
              [
                first,
              ],
              'page-two',
            ),
      );
    }),
  );
  const { wrapper } = harness(true);
  const { result } = renderHook(() => useImportSession(), {
    wrapper,
  });
  await waitFor(() => expect(result.current.hasMoreJobs).toBe(true));
  act(() => result.current.loadMoreJobs());
  await waitFor(() => expect(pages).toBe(2));
  await waitFor(() => expect(pages).toBeGreaterThanOrEqual(4), {
    timeout: 3000,
  });
});

function useRunFromCanonicalJob(initial: ArtworkRunJob) {
  const { data = initial } = useJob(initial.id);
  return useEnrichmentRun(data.jobType === 'artwork-completion' ? data : initial);
}

it('loads one canonical artwork job and keeps summary facts when that job changes', async () => {
  const job: ArtworkRunJob = {
    id: first.id,
    correlationId: batchId,
    jobType: 'artwork-completion',
    phase: 'Completed',
    sourceFilename: 'Enrichment run',
    progressPercent: 100,
    errors: [],
    isTerminal: true,
    mode: null,
    total: 2,
    processed: 2,
  };
  const summary = {
    roles: [],
    review: 2,
    failed: 1,
    unfinished: 0,
    working: 0,
  };
  const jobReads = vi.fn();
  server.use(
    http.get('http://localhost/api/jobs/:jobId', () => {
      jobReads();
      return HttpResponse.json(job);
    }),
    http.get('http://localhost/api/jobs/:jobId/artwork-summary', () => HttpResponse.json(summary)),
  );
  const { cache, wrapper } = harness();
  const { result } = renderHook(() => useRunFromCanonicalJob(job), {
    wrapper,
  });
  await waitFor(() => {
    expect(jobReads).toHaveBeenCalledOnce();
    expect(result.current.data).toMatchObject({
      jobId: first.id,
      mode: null,
      review: 2,
      failed: 1,
      processed: 2,
    });
  });
  act(() =>
    cache.setQueryData(jobKeys.detail(first.id), {
      ...job,
      processed: 1,
    }),
  );
  await waitFor(() =>
    expect(result.current.data).toMatchObject({
      processed: 1,
      review: 2,
    }),
  );
  expect(jobReads).toHaveBeenCalledOnce();
});

it('uses the typed item feed query', async () => {
  const requests: URL[] = [];
  server.use(
    http.get('http://localhost/api/jobs/:jobId/items', ({ request }) => {
      requests.push(new URL(request.url));
      return HttpResponse.json({
        items: [],
        nextCursor: null,
        hasNextPage: false,
      });
    }),
  );
  const { wrapper } = harness();
  const items = renderHook(
    () =>
      useJobItems(first.id, 'failed', {
        search: 'name',
      }),
    {
      wrapper,
    },
  );

  await waitFor(() => expect(items.result.current.isSuccess).toBe(true));
  expect(requests[0]?.searchParams.get('query')).toBe('name');
  expect(requests[0]?.searchParams.has('search')).toBe(false);
});

it('requests a fresh top-level ExportJob capability per click with no format field', async () => {
  const requests: unknown[] = [];
  server.use(
    http.post('http://localhost/api/download-tickets', async ({ request }) => {
      requests.push(await request.json());
      return HttpResponse.json(
        {
          downloadUrl: `/api/download-tickets/fresh-${requests.length}`,
          expiresAt: '2030-01-01T00:00:00Z',
        },
        {
          status: 201,
        },
      );
    }),
  );
  await downloadExportFile(first.id);
  await downloadExportFile(first.id);
  expect(requests).toEqual([
    {
      target: 'ExportJob',
      id: first.id,
    },
    {
      target: 'ExportJob',
      id: first.id,
    },
  ]);
  expect(startBrowserDownload).toHaveBeenNthCalledWith(1, '/api/download-tickets/fresh-1');
  expect(startBrowserDownload).toHaveBeenNthCalledWith(2, '/api/download-tickets/fresh-2');
});

it('unarchives through the same explicit state mutation and invalidates filtered history without splicing it', async () => {
  const requests: unknown[] = [];
  server.use(
    http.patch('http://localhost/api/jobs/:jobId', async ({ request }) => {
      requests.push(await request.json());
      return HttpResponse.json({
        ...first,
        isArchived: false,
      });
    }),
  );
  const { cache, wrapper } = harness();
  const historyKey = [
    'jobs',
    'history',
    {
      archive: 'archived',
    },
  ];
  const archived = {
    ...first,
    isArchived: true,
  };
  cache.setQueryData(
    historyKey,
    page([
      archived,
    ]),
  );
  cache.setQueryData(jobKeys.detail(first.id), archived);
  cache.setQueryData(jobKeys.list(), []);
  const { result } = renderHook(() => useArchiveJob(), {
    wrapper,
  });
  await act(async () => {
    await result.current.mutateAsync({
      jobId: first.id,
      isArchived: false,
    });
  });
  expect(requests).toEqual([
    {
      isArchived: false,
    },
  ]);
  expect(cache.getQueryData(jobKeys.detail(first.id))).toEqual({
    ...first,
    isArchived: false,
  });
  expect(cache.getQueryData(jobKeys.list())).toEqual([
    {
      ...first,
      isArchived: false,
    },
  ]);
  expect(cache.getQueryData(historyKey)).toEqual(
    page([
      archived,
    ]),
  );
  expect(cache.getQueryState(historyKey)?.isInvalidated).toBe(true);
});

it.each([
  {
    jobType: 'mistyped',
  },
  {
    outcome: 'mistyped',
  },
  {
    archive: 'mistyped',
  },
])('rejects an invalid URL filter %j without broadening the request', async (filters) => {
  const transport = vi.spyOn(client, 'get');
  const { wrapper } = harness();
  const { result } = renderHook(() => useJobHistory(filters), {
    wrapper,
  });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(transport).not.toHaveBeenCalled();
});

it('aborts an in-flight import page on unmount and keeps the completed page cached', async () => {
  let release: () => void = () => {};
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  const calls = vi.spyOn(client, 'get');
  server.use(
    http.get('http://localhost/api/jobs', async ({ request }) => {
      if (new URL(request.url).searchParams.has('cursor')) {
        await held;
        return HttpResponse.json(
          page([
            second,
          ]),
        );
      }
      return HttpResponse.json(
        page(
          [
            first,
          ],
          'page-two',
        ),
      );
    }),
  );
  const { cache, wrapper } = harness(true);
  const { result, unmount } = renderHook(() => useImportSession(), {
    wrapper,
  });
  await waitFor(() => expect(result.current.hasMoreJobs).toBe(true));
  act(() => result.current.loadMoreJobs());
  const listCalls = () => calls.mock.calls.filter(([options]) => options.url === '/api/jobs');
  await waitFor(() => expect(listCalls()).toHaveLength(2));
  const signal = listCalls()[1]?.[0].signal;
  expect(signal?.aborted).toBe(false);
  unmount();
  expect(signal?.aborted).toBe(true);
  await act(async () => {
    release();
  });
  expect(
    cache.getQueryData([
      'import-batch',
      batchId,
    ]),
  ).toMatchObject({
    pages: [
      page(
        [
          first,
        ],
        'page-two',
      ),
    ],
  });
});
