import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import {
  useEnrichmentAssessment,
  useEnrichmentOmission,
  useEnrichmentWorkspace,
  useEnrichmentWorkspaceCounts,
} from './useEnrichmentWorkspace';

const { loadWorkspace, loadCounts, loadAssessment, saveOmission, loadTitleMedia } = vi.hoisted(
  () => ({
    loadWorkspace: vi.fn(),
    loadCounts: vi.fn(),
    loadAssessment: vi.fn(),
    saveOmission: vi.fn(),
    loadTitleMedia: vi.fn(),
  }),
);
vi.mock('@romd/admin-api-client', () => ({
  listEnrichmentWorkbench: loadWorkspace,
  getEnrichmentWorkbenchStats: loadCounts,
  getTitleEnrichmentAssessment: loadAssessment,
  replaceTitleEnrichmentOmission: saveOmission,
  listTitleMedia: loadTitleMedia,
}));

let cache: QueryClient;
const counts = {
  total: 10000,
  needsReview: 100,
  automatic: 200,
  processing: 0,
  blocked: 0,
};
function renderWorkspace(view = 'NeedsReview', search = '') {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  return renderHook(
    () => {
      const { data, fetchNextPage } = useEnrichmentWorkspace(view, search);
      const { data: queueCounts } = useEnrichmentWorkspaceCounts(search);
      return {
        data,
        fetchNextPage,
        queueCounts,
      };
    },
    {
      wrapper,
    },
  );
}
async function settle() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1);
  });
}
beforeEach(() => {
  vi.useFakeTimers();
  cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  loadWorkspace.mockReset();
  loadCounts.mockReset();
  loadAssessment.mockReset();
  saveOmission.mockReset();
  loadTitleMedia.mockReset();
  loadCounts.mockResolvedValue({
    data: counts,
  });
  loadWorkspace.mockImplementation(async ({ query }) => ({
    data: {
      items: [],
      hasNextPage: (query.cursor ? null : 'second-page') !== null,
      nextCursor: query.cursor ? null : 'second-page',
    },
  }));
});
afterEach(() => {
  cache.clear();
  vi.useRealTimers();
});

it('loads global counts separately and never computes them on cursor pages', async () => {
  const { result } = renderWorkspace();
  await settle();
  await act(async () => {
    await result.current.fetchNextPage();
  });
  await settle();
  expect(
    loadWorkspace.mock.calls.every(([options]) => !Object.hasOwn(options.query, 'includeCounts')),
  ).toBe(true);
  expect(result.current.queueCounts).toEqual(counts);
  expect(loadCounts).toHaveBeenCalledTimes(1);
  expect(result.current.data?.pages[1]).not.toHaveProperty('counts');
});

it('waits for the slower fallback and discovers work even when the queue was idle', async () => {
  const { result } = renderWorkspace();
  await settle();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(29_000);
  });
  expect(loadWorkspace).toHaveBeenCalledTimes(1);
  loadCounts.mockResolvedValueOnce({
    data: {
      ...counts,
      processing: 1,
    },
  });
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1000);
  });
  expect(loadWorkspace).toHaveBeenCalledTimes(2);
  expect(result.current.queueCounts?.processing).toBe(1);
});

it('refreshes curator changes immediately and follows new cursors to preserve a consistent loaded queue', async () => {
  const { result } = renderWorkspace();
  await settle();
  await act(async () => {
    await result.current.fetchNextPage();
  });
  await settle();
  loadWorkspace.mockClear();
  loadWorkspace.mockImplementation(async ({ query }) => ({
    data: {
      items: [],
      hasNextPage: (query.cursor ? null : 'changed-boundary') !== null,
      nextCursor: query.cursor ? null : 'changed-boundary',
    },
  }));
  await act(async () => {
    await cache.invalidateQueries({
      queryKey: [
        'enrichment-workspace',
      ],
    });
  });
  await settle();
  expect(loadWorkspace.mock.calls.map(([options]) => options.query.cursor)).toEqual([
    undefined,
    'changed-boundary',
  ]);
  expect(result.current.data?.pages).toHaveLength(2);
  expect(loadCounts).toHaveBeenCalledTimes(2);
});

it('does not cancel a cursor-chain refresh when pagination is clicked before its loading state renders', async () => {
  const { result } = renderWorkspace();
  await settle();
  await act(async () => {
    await result.current.fetchNextPage();
  });
  await settle();
  const beforeRefresh = result.current.fetchNextPage;
  let finishRefresh!: () => void;
  let refreshSignal!: AbortSignal;
  loadWorkspace.mockClear();
  loadWorkspace.mockImplementation(async ({ query, signal }) => {
    if (!query.cursor)
      return {
        data: {
          items: [
            {
              titleId: 'remaining',
            },
          ],
          hasNextPage: true,
          nextCursor: 'changed-boundary',
        },
      };
    if (query.cursor === 'changed-boundary') {
      refreshSignal = signal;
      return new Promise((resolve) => {
        finishRefresh = () =>
          resolve({
            data: {
              items: [
                {
                  titleId: 'following',
                },
              ],
              hasNextPage: true,
              nextCursor: 'fresh-next-page',
            },
          });
      });
    }
    return {
      data: {
        items: [
          {
            titleId: 'last',
          },
        ],
        hasNextPage: false,
        nextCursor: null,
      },
    };
  });
  let refresh!: Promise<void>;
  act(() => {
    refresh = cache.invalidateQueries({
      queryKey: [
        'enrichment-workspace',
        'list',
      ],
    });
  });
  await settle();
  await act(async () => {
    expect(await beforeRefresh()).toBeUndefined();
  });
  expect(refreshSignal.aborted).toBe(false);
  expect(loadWorkspace.mock.calls.map(([options]) => options.query.cursor)).toEqual([
    undefined,
    'changed-boundary',
  ]);
  await act(async () => {
    finishRefresh();
    await refresh;
  });
  await settle();
  await act(async () => {
    await result.current.fetchNextPage();
  });
  await settle();
  expect(loadWorkspace.mock.calls.map(([options]) => options.query.cursor)).toEqual([
    undefined,
    'changed-boundary',
    'fresh-next-page',
  ]);
  expect(
    result.current.data?.pages.flatMap((page) => page.items).map((item) => item.titleId),
  ).toEqual([
    'remaining',
    'following',
    'last',
  ]);
});

it('ignores duplicate pagination clicks while the first page request is pending', async () => {
  const { result } = renderWorkspace();
  await settle();
  let finishPage!: () => void;
  let signal!: AbortSignal;
  loadWorkspace.mockImplementationOnce((options) => {
    signal = options.signal;
    return new Promise((resolve) => {
      finishPage = () =>
        resolve({
          data: {
            items: [],
            hasNextPage: false,
            nextCursor: null,
          },
        });
    });
  });
  const paginate = result.current.fetchNextPage;
  let pending!: ReturnType<typeof paginate>;
  act(() => {
    pending = paginate();
  });
  await act(async () => {
    expect(await paginate()).toBeUndefined();
  });
  expect(signal.aborted).toBe(false);
  expect(loadWorkspace).toHaveBeenCalledTimes(2);
  await act(async () => {
    finishPage();
    await pending;
  });
});

it('refreshes an idle selected assessment even when a missed event removes it from the queue', async () => {
  const selected = {
    titleId: 't1',
    revision: 'old',
    needsReview: true,
    hasActiveWork: false,
  };
  loadAssessment.mockResolvedValue({
    data: selected,
  });
  loadWorkspace.mockResolvedValue({
    data: {
      items: [
        selected,
      ],
      hasNextPage: false,
      nextCursor: null,
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(
    () => ({
      queue: useEnrichmentWorkspace('NeedsReview', ''),
      selected: useEnrichmentAssessment('t1'),
    }),
    {
      wrapper,
    },
  );
  await settle();
  loadAssessment.mockResolvedValue({
    data: {
      ...selected,
      revision: 'new',
      needsReview: false,
    },
  });
  loadWorkspace.mockResolvedValue({
    data: {
      items: [],
      hasNextPage: false,
      nextCursor: null,
    },
  });
  await act(async () => {
    await vi.advanceTimersByTimeAsync(29_000);
  });
  expect(loadAssessment).toHaveBeenCalledTimes(1);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1001);
  });
  expect(result.current.queue.data?.pages[0].items).toEqual([]);
  expect(result.current.selected.data).toMatchObject({
    titleId: 't1',
    revision: 'new',
    needsReview: false,
  });
});

it('polls active assessments quickly and returns to the idle interval when work completes', async () => {
  loadAssessment.mockResolvedValue({
    data: {
      titleId: 't1',
      hasActiveWork: true,
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useEnrichmentAssessment('t1'), {
    wrapper,
  });
  await settle();
  loadAssessment.mockResolvedValue({
    data: {
      titleId: 't1',
      hasActiveWork: false,
    },
  });
  await act(async () => {
    await vi.advanceTimersByTimeAsync(3001);
  });
  expect(result.current.data?.hasActiveWork).toBe(false);
  expect(loadAssessment).toHaveBeenCalledTimes(2);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(3000);
  });
  expect(loadAssessment).toHaveBeenCalledTimes(2);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(27_001);
  });
  expect(loadAssessment).toHaveBeenCalledTimes(3);
});

it('reuses a fresh queue on remount but refreshes after its freshness window', async () => {
  const first = renderWorkspace();
  await settle();
  first.unmount();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(10_000);
  });
  const second = renderWorkspace();
  await settle();
  expect(loadWorkspace).toHaveBeenCalledTimes(1);
  expect(second.result.current.queueCounts).toEqual(counts);
  expect(loadCounts).toHaveBeenCalledTimes(1);
  second.unmount();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(5000);
  });
  renderWorkspace();
  await settle();
  expect(loadWorkspace).toHaveBeenCalledTimes(2);
});

it('shares fresh counts between views but scopes counts to the search', async () => {
  const first = renderWorkspace();
  await settle();
  first.unmount();
  const second = renderWorkspace('Automatic');
  await settle();
  expect(loadWorkspace).toHaveBeenCalledTimes(2);
  expect(loadCounts).toHaveBeenCalledTimes(1);
  second.unmount();
  renderWorkspace('Automatic', 'Mario');
  await settle();
  expect(loadCounts).toHaveBeenCalledTimes(2);
  expect(loadCounts.mock.lastCall?.[0].query).toEqual({
    query: 'Mario',
  });
});

it('keeps an accepted omission successful when a later title media page fails', async () => {
  const previous = {
    id: 't1',
    revision: 'old',
    media: [
      {
        id: 'previous-complete-media',
      },
    ],
    releases: [],
  };
  const titleKey = [
    'titleDetail',
    't1',
  ];
  cache.setQueryData(titleKey, previous);
  const accepted = {
    id: 't1',
    revision: 'accepted',
    media: {
      items: [
        {
          id: 'partial-media',
        },
      ],
      nextCursor: 'later',
      hasNextPage: true,
    },
    releases: {
      items: [],
      nextCursor: null,
      hasNextPage: false,
    },
  };
  saveOmission.mockResolvedValue({
    data: accepted,
  });
  loadTitleMedia.mockResolvedValue({
    error: {
      detail: 'A later media page is unavailable.',
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useEnrichmentOmission(), {
    wrapper,
  });
  await act(async () => {
    expect(
      await result.current.mutateAsync({
        titleId: 't1',
        field: 'Description',
        accepted: true,
        revision: 'old',
      }),
    ).toEqual(accepted);
  });
  await settle();
  expect(result.current.isSuccess).toBe(true);
  expect(loadTitleMedia).toHaveBeenCalledWith(
    expect.objectContaining({
      path: {
        titleId: 't1',
      },
      query: {
        cursor: 'later',
        limit: 100,
      },
    }),
  );
  expect(cache.getQueryData(titleKey)).toEqual(previous);
  expect(cache.getQueryState(titleKey)?.isInvalidated).toBe(true);
});

it('finishes saving an omission after refreshing decisions without waiting for slow global counts', async () => {
  const title = {
    titleId: 't1',
    revision: 'old',
    needsReview: true,
  };
  const saved = {
    ...title,
    revision: 'new',
    needsReview: false,
  };
  loadAssessment.mockResolvedValue({
    data: title,
  });
  saveOmission.mockResolvedValue({
    data: {
      id: 't1',
      revision: 'new',
      media: {
        items: [],
        nextCursor: null,
        hasNextPage: false,
      },
      releases: {
        items: [],
        nextCursor: null,
        hasNextPage: false,
      },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(
    () => {
      const { data: pages } = useEnrichmentWorkspace('NeedsReview', '');
      const { data: assessment } = useEnrichmentAssessment('t1');
      const { isFetching: countsPending } = useEnrichmentWorkspaceCounts('');
      const { mutate, isSuccess } = useEnrichmentOmission();
      return {
        pages,
        assessment,
        countsPending,
        mutate,
        isSuccess,
      };
    },
    {
      wrapper,
    },
  );
  await settle();
  let finishCounts!: () => void;
  loadCounts.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        finishCounts = () =>
          resolve({
            data: counts,
          });
      }),
  );
  loadAssessment.mockResolvedValue({
    data: saved,
  });
  loadWorkspace.mockResolvedValue({
    data: {
      items: [],
      hasNextPage: false,
      nextCursor: null,
    },
  });
  act(() => {
    result.current.mutate({
      titleId: 't1',
      field: 'Description',
      accepted: true,
      revision: 'old',
    });
  });
  await settle();
  try {
    expect(saveOmission.mock.lastCall?.[0]).toMatchObject({
      path: {
        titleId: 't1',
        field: 'Description',
      },
      headers: {
        'If-Match': '"old"',
      },
      body: {
        accepted: true,
      },
    });
    expect(result.current.isSuccess).toBe(true);
    expect(result.current.countsPending).toBe(true);
    expect(result.current.assessment).toEqual(saved);
    expect(result.current.pages?.pages[0].nextCursor).toBeNull();
    expect(loadAssessment).toHaveBeenCalledTimes(2);
    expect(loadWorkspace).toHaveBeenCalledTimes(2);
  } finally {
    finishCounts();
    await settle();
  }
});
