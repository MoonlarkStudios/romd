import type { JobDto } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  selectionReconciliationJob,
  subjectReconciliationJob,
} from '../../test/fixtures/mediaReconciliationJobs';
import { useJobNotifications } from './useJobNotifications';

const { showSpy, updateSpy, hideSpy, jobsState } = vi.hoisted(() => ({
  showSpy: vi.fn(),
  updateSpy: vi.fn(),
  hideSpy: vi.fn(),
  jobsState: {
    jobs: [] as unknown[],
  },
}));

vi.mock('@mantine/notifications', () => ({
  notifications: {
    show: showSpy,
    update: updateSpy,
    hide: hideSpy,
  },
}));

vi.mock('../api/useJobs', () => ({
  useJobs: () => ({
    data: jobsState.jobs,
  }),
}));

interface NotificationProps {
  title?: string;
  message?: string;
  color?: string;
}

function makeJob(overrides: Record<string, unknown>): JobDto {
  return {
    id: 'm1',
    correlationId: 'c1',
    jobType: 'materialization',
    sourceFilename: 'Family Room',
    phase: 'Materializing',
    progressPercent: '50',
    errors: [],
    isTerminal: false,
    hasErrors: false,
    ...overrides,
  } as JobDto;
}

function renderNotificationsHook(
  queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  }),
) {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>{children}</MemoryRouter>
    </QueryClientProvider>
  );
  return renderHook(() => useJobNotifications(), {
    wrapper,
  });
}

function allTerminalCalls(): NotificationProps[] {
  return [
    ...showSpy.mock.calls,
    ...updateSpy.mock.calls,
  ].map((call) => call[0] as NotificationProps);
}

describe('media convergence notifications', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    jobsState.jobs = [];
  });
  it.each([
    [
      'Completed',
      'Completed',
      'Complete',
      'green',
    ],
    [
      'Failed',
      'Failed',
      'Failed',
      'red',
    ],
    [
      'Superseded',
      'Superseded',
      'Superseded',
      'gray',
    ],
  ])('keeps %s notifications distinct and refreshes retained media metadata', (phase, outcome, label, color) => {
    const cache = new QueryClient();
    for (const key of [
      [
        'roms',
      ],
      [
        'dashboard',
        'storage',
      ],
      [
        'titleDetail',
      ],
    ])
      cache.setQueryData(key, {});
    jobsState.jobs = [
      subjectReconciliationJob(),
    ];
    const { rerender } = renderNotificationsHook(cache);
    jobsState.jobs = [
      subjectReconciliationJob({
        phase,
        outcome,
        isTerminal: true,
      }),
    ];
    rerender();
    expect(updateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: `Media reconciliation: ${label}`,
        color,
        loading: false,
      }),
    );
    for (const key of [
      [
        'roms',
      ],
      [
        'dashboard',
        'storage',
      ],
      [
        'titleDetail',
      ],
    ])
      expect(cache.getQueryState(key)?.isInvalidated).toBe(true);
  });
  it('uses failed disc counters rather than the count of bulk job error records', () => {
    const { rerender } = renderNotificationsHook();
    jobsState.jobs = [
      selectionReconciliationJob({
        isTerminal: true,
        phase: 'CompletedWithErrors',
        selection: {
          ...selectionReconciliationJob().selection,
          failed: 5,
          completed: 6,
        },
        hasErrors: true,
        errors: [
          {
            item: 'bulk',
            message: 'Child failures',
            occurredAt: '2026-10-02T15:00:00Z',
          },
        ],
      }),
    ];
    rerender();
    expect(showSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Media reconciliation: Completed with errors',
        message: '6 / 6 finished · 5 failed · 2 skipped',
        color: 'yellow',
      }),
    );
  });
  it('updates progress for changed bulk counters even when the percentage is unchanged', () => {
    jobsState.jobs = [
      selectionReconciliationJob(),
    ];
    const { rerender } = renderNotificationsHook();
    jobsState.jobs = [
      selectionReconciliationJob({
        selection: {
          ...selectionReconciliationJob().selection,
          completed: 4,
        },
      }),
    ];
    rerender();
    expect(updateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        message: expect.stringContaining('4 / 6 finished'),
        loading: true,
      }),
    );
  });
  it('shows active deferral and a changed next attempt without a success toast', () => {
    jobsState.jobs = [
      subjectReconciliationJob({
        phase: 'Pending',
      }),
    ];
    const { rerender } = renderNotificationsHook();
    jobsState.jobs = [
      subjectReconciliationJob({
        phase: 'Pending',
        nextAttemptAt: '2026-10-02T15:00:00Z',
      }),
    ];
    rerender();
    expect(updateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        message: expect.stringContaining('Deferred: Next attempt'),
        loading: true,
        autoClose: false,
      }),
    );
    expect(allTerminalCalls().some((call) => call.color === 'green')).toBe(false);
  });
});

describe('useJobNotifications', () => {
  beforeEach(() => {
    showSpy.mockClear();
    updateSpy.mockClear();
    hideSpy.mockClear();
    jobsState.jobs = [];
  });

  it('shows a neutral deferred notification, not the green success toast, on a deferred transition', () => {
    jobsState.jobs = [
      makeJob({}),
    ];
    const { rerender } = renderNotificationsHook();

    jobsState.jobs = [
      makeJob({
        isTerminal: true,
        phase: 'Deferred',
        progressPercent: '100',
      }),
    ];
    rerender();

    expect(updateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Materialization: Deferred',
        message:
          'Family Room: changes are still pending — refresh the library again when they settle',
        color: 'gray',
      }),
    );
    const calls = allTerminalCalls();
    expect(calls.some((props) => props.color === 'green')).toBe(false);
    expect(calls.some((props) => props.message?.includes('processed successfully'))).toBe(false);
  });

  it('shows the deferred notification for a job that appears already terminal', () => {
    const { rerender } = renderNotificationsHook();

    jobsState.jobs = [
      makeJob({
        id: 'm2',
        isTerminal: true,
        phase: 'Deferred',
      }),
    ];
    rerender();

    expect(showSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Materialization: Deferred',
        color: 'gray',
      }),
    );
    expect(allTerminalCalls().some((props) => props.color === 'green')).toBe(false);
  });

  it('still shows the green success toast for a completed materialization', () => {
    jobsState.jobs = [
      makeJob({}),
    ];
    const { rerender } = renderNotificationsHook();

    jobsState.jobs = [
      makeJob({
        isTerminal: true,
        phase: 'Completed',
        progressPercent: '100',
      }),
    ];
    rerender();

    expect(updateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Materialization: Complete',
        message: 'Family Room processed successfully',
        color: 'green',
      }),
    );
  });
});

it.each([
  [
    'Cancelled',
    'Cancelled',
    'gray',
  ],
  [
    'Failed',
    'Failed',
    'red',
  ],
  [
    'CompletedWithErrors',
    'Completed with errors',
    'yellow',
  ],
])('reports %s without a success notification', (phase, label, color) => {
  updateSpy.mockClear();
  jobsState.jobs = [
    makeJob({}),
  ];
  const { rerender } = renderNotificationsHook();
  jobsState.jobs = [
    makeJob({
      isTerminal: true,
      phase,
    }),
  ];
  rerender();
  expect(updateSpy).toHaveBeenCalledWith(
    expect.objectContaining({
      title: `Materialization: ${label}`,
      color,
    }),
  );
});

describe('enrichment run notifications', () => {
  const run = (overrides: Record<string, unknown>) =>
    makeJob({
      id: 'run',
      jobType: 'artwork-completion',
      sourceFilename: 'Fill missing artwork',
      total: 12,
      phase: 'Enriching',
      ...overrides,
    });

  it.each([
    [
      'Completed',
      false,
      'Enrichment run finished',
      'green',
    ],
    [
      'Completed',
      true,
      'Enrichment run finished with errors',
      'yellow',
    ],
    [
      'CompletedWithErrors',
      false,
      'Enrichment run finished with errors',
      'yellow',
    ],
    [
      'Failed',
      false,
      'Enrichment run stopped with an error',
      'red',
    ],
    [
      'Cancelled',
      false,
      'Enrichment run cancelled',
      'gray',
    ],
  ])('reports a run ending %s (errors: %s) as "%s" without claiming success', (phase, hasErrors, title, color) => {
    updateSpy.mockClear();
    jobsState.jobs = [
      run({}),
    ];
    const { rerender } = renderNotificationsHook();
    jobsState.jobs = [
      run({
        isTerminal: true,
        phase,
        hasErrors,
        processed: 12,
      }),
    ];
    rerender();
    expect(updateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title,
        message: 'Enrichment run · 12 titles',
        color,
      }),
    );
    const copy = JSON.stringify(updateSpy.mock.calls);
    expect(copy).not.toMatch(/Fill missing artwork|Complete\b|successfully/);
  });

  it('names a run by its job title in a mid-run warning', () => {
    updateSpy.mockClear();
    jobsState.jobs = [
      run({}),
    ];
    const { rerender } = renderNotificationsHook();
    jobsState.jobs = [
      run({
        hasErrors: true,
        progressPercent: '40',
      }),
    ];
    rerender();
    expect(updateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Enrichment run: Warning',
        message: 'Enrichment run · 12 titles: error during Enriching',
      }),
    );
  });
});

it('refreshes an idle workbench when a new enrichment job appears', () => {
  jobsState.jobs = [];
  const cache = new QueryClient();
  cache.setQueryData(
    [
      'enrichment-workspace',
      'list',
    ],
    {
      processing: 0,
    },
  );
  const { rerender } = renderNotificationsHook(cache);
  jobsState.jobs = [
    makeJob({
      jobType: 'enrichment',
      titleId: 't1',
      phase: 'Pending',
      isTerminal: false,
    }),
  ];
  rerender();
  expect(
    cache.getQueryState([
      'enrichment-workspace',
      'list',
    ])?.isInvalidated,
  ).toBe(true);
  cache.clear();
});

describe('workspace refresh coalescing', () => {
  let cache: QueryClient;
  beforeEach(() => {
    vi.useFakeTimers();
    cache = new QueryClient();
    jobsState.jobs = [
      makeJob({
        jobType: 'enrichment',
        progressPercent: 0,
      }),
    ];
  });
  afterEach(() => {
    cache.clear();
    vi.useRealTimers();
  });
  const workspaceCalls = (spy: ReturnType<typeof vi.spyOn>) =>
    spy.mock.calls.filter(
      ([filters]) =>
        (
          filters as
            | {
                queryKey?: string[];
              }
            | undefined
        )?.queryKey?.[0] === 'enrichment-workspace',
    );

  it('coalesces a continuous burst into one bounded progress refresh', () => {
    const invalidations = vi.spyOn(cache, 'invalidateQueries');
    const { rerender } = renderNotificationsHook(cache);
    for (let progress = 1; progress <= 10; progress++) {
      jobsState.jobs = [
        makeJob({
          jobType: 'enrichment',
          progressPercent: progress,
        }),
      ];
      rerender();
      act(() => {
        vi.advanceTimersByTime(1000);
      });
    }
    expect(workspaceCalls(invalidations)).toHaveLength(0);
    act(() => {
      vi.advanceTimersByTime(5000);
    });
    expect(workspaceCalls(invalidations)).toHaveLength(1);
  });

  it('refreshes completion immediately and cancels pending progress work', () => {
    const invalidations = vi.spyOn(cache, 'invalidateQueries');
    const { rerender } = renderNotificationsHook(cache);
    jobsState.jobs = [
      makeJob({
        jobType: 'enrichment',
        progressPercent: 10,
      }),
    ];
    rerender();
    jobsState.jobs = [
      makeJob({
        jobType: 'enrichment',
        isTerminal: true,
        phase: 'Completed',
      }),
    ];
    rerender();
    expect(workspaceCalls(invalidations)).toHaveLength(1);
    act(() => {
      vi.advanceTimersByTime(30_000);
    });
    expect(workspaceCalls(invalidations)).toHaveLength(1);
  });

  it('coalesces new and terminal jobs in the same update into one immediate refresh', () => {
    const invalidations = vi.spyOn(cache, 'invalidateQueries');
    const { rerender } = renderNotificationsHook(cache);
    jobsState.jobs = [
      makeJob({
        jobType: 'enrichment',
        isTerminal: true,
        phase: 'Completed',
      }),
      makeJob({
        id: 'new-job',
        jobType: 'artwork-completion',
      }),
    ];
    rerender();
    expect(workspaceCalls(invalidations)).toHaveLength(1);
  });

  it('refreshes workspace lists and runs on progress but never the global counts', () => {
    const counts = [
      'enrichment-workspace',
      'counts',
      '',
    ];
    const list = [
      'enrichment-workspace',
      'list',
      'NeedsReview',
      '',
    ];
    const runKey = [
      'enrichment-workspace',
      'run',
      'run',
    ];
    for (const key of [
      counts,
      list,
      runKey,
    ])
      cache.setQueryData(key, {});
    const { rerender } = renderNotificationsHook(cache);
    jobsState.jobs = [
      makeJob({
        jobType: 'enrichment',
        progressPercent: 10,
      }),
    ];
    rerender();
    act(() => {
      vi.advanceTimersByTime(15_000);
    });
    expect(cache.getQueryState(list)?.isInvalidated).toBe(true);
    expect(cache.getQueryState(runKey)?.isInvalidated).toBe(true);
    expect(cache.getQueryState(counts)?.isInvalidated).toBe(false);
  });

  it('refreshes the global counts when a job finishes', () => {
    const counts = [
      'enrichment-workspace',
      'counts',
      '',
    ];
    cache.setQueryData(counts, {});
    const { rerender } = renderNotificationsHook(cache);
    jobsState.jobs = [
      makeJob({
        jobType: 'enrichment',
        isTerminal: true,
        phase: 'Completed',
      }),
    ];
    rerender();
    expect(cache.getQueryState(counts)?.isInvalidated).toBe(true);
  });

  it('cancels pending refresh when the notification reactor unmounts', () => {
    const invalidations = vi.spyOn(cache, 'invalidateQueries');
    const { rerender, unmount } = renderNotificationsHook(cache);
    jobsState.jobs = [
      makeJob({
        jobType: 'enrichment',
        progressPercent: 10,
      }),
    ];
    rerender();
    unmount();
    act(() => {
      vi.advanceTimersByTime(30_000);
    });
    expect(workspaceCalls(invalidations)).toHaveLength(0);
  });
});

describe('metadata update notifications', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    jobsState.jobs = [];
  });
  it('reports deferred titles as automatic retries and refreshes policy and metadata', () => {
    const cache = new QueryClient();
    const keys = [
      [
        'metadata-policy',
        'snes',
      ],
      [
        'titleDetail',
      ],
      [
        'titleEnrichment',
      ],
      [
        'enrichment-workspace',
      ],
    ];
    for (const key of keys) cache.setQueryData(key, {});
    const { rerender } = renderNotificationsHook(cache);
    jobsState.jobs = [
      makeJob({
        id: 'metadata-job',
        jobType: 'metadata-rematerialization',
        sourceFilename: 'SNES',
        systemKey: 'snes',
        phase: 'CompletedWithErrors',
        isTerminal: true,
        deferredCount: 2,
      }),
    ];
    rerender();
    expect(showSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Metadata update: Completed with errors',
        message: 'SNES · 2 titles will retry automatically.',
        color: 'yellow',
      }),
    );
    for (const key of keys) expect(cache.getQueryState(key)?.isInvalidated).toBe(true);
  });
});
