import { MantineProvider } from '@mantine/core';
import {
  type ArtworkCompletionItem,
  type ArtworkCompletionRequest,
  client,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { useState } from 'react';
import {
  createMemoryRouter,
  MemoryRouter,
  Outlet,
  RouterProvider,
  useSearchParams,
} from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { artworkJobFixture } from '../../test/fixtures/artworkRun';
import { server } from '../../test/msw/server';
import { DraftGuardProvider } from '../Administration/DraftGuard';
import { UnsavedChanges } from '../Administration/UnsavedChanges';
import { RunPanel } from './RunPanel';
import { type EnrichmentRun, RUN_POLL_INTERVAL } from './useEnrichmentRun';

const settledNow = {
  needsReview: false,
  canEnrichAutomatically: false,
  hasActiveWork: false,
  hasOperationalBlocker: false,
};
const runDto = (over: Partial<EnrichmentRun>): EnrichmentRun => ({
  jobId: 'run',
  phase: 'Completed',
  total: 240,
  processed: 240,
  roles: [],
  review: 0,
  failed: 0,
  unfinished: 0,
  working: 0,
  nextAttemptAt: null,
  mode: 'Enrich',
  createdAt: null,
  ...over,
});

interface ServerState {
  runs: Map<string, EnrichmentRun>;
  items: Map<string, ArtworkCompletionItem[]>;
  runReads: string[];
  itemReads: string[];
  queued: ArtworkCompletionRequest[];
  cancelled: string[];
  pageSize: number;
  /** Status answered when `retry` creates no run. */
  retryFailure: number;
  /** Holds a run read open until the returned promise settles. */
  holdRunRead: () => Promise<void> | undefined;
  runSignals: AbortSignal[];
  countReads: number;
  /** The run a queued retry creates; none answers 500. */
  retry: (request: ArtworkCompletionRequest) => EnrichmentRun | undefined;
  cancel: (jobId: string) => void;
}
const state: ServerState = {
  runs: new Map(),
  items: new Map(),
  runReads: [],
  itemReads: [],
  queued: [],
  cancelled: [],
  pageSize: 50,
  retryFailure: 500,
  holdRunRead: () => undefined,
  runSignals: [],
  countReads: 0,
  retry: () => undefined,
  cancel: () => {},
};

beforeEach(() => {
  Object.assign(state, {
    runs: new Map(),
    items: new Map(),
    runReads: [],
    itemReads: [],
    queued: [],
    cancelled: [],
    pageSize: 50,
    retryFailure: 500,
    holdRunRead: () => undefined,
    runSignals: [],
    countReads: 0,
    retry: () => undefined,
    cancel: () => {},
  });
  client.setConfig({
    baseUrl: 'http://localhost',
  });
  server.use(
    http.get('http://localhost/api/jobs/:jobId', async ({ params, request }) => {
      const jobId = String(params.jobId);
      state.runReads.push(jobId);
      state.runSignals.push(request.signal);
      await state.holdRunRead();
      const run = state.runs.get(jobId);
      return run
        ? HttpResponse.json(artworkJobFixture(run))
        : new HttpResponse(null, {
            status: 404,
          });
    }),
    http.get('http://localhost/api/jobs/:jobId/artwork-summary', ({ params }) => {
      const run = state.runs.get(String(params.jobId));
      return run
        ? HttpResponse.json({
            roles: run.roles,
            review: run.review,
            failed: run.failed,
            unfinished: run.unfinished,
            working: run.working,
          })
        : new HttpResponse(null, {
            status: 404,
          });
    }),
    http.get('http://localhost/api/jobs/:jobId/artwork-items', ({ params, request }) => {
      const query = new URL(request.url).searchParams;
      const status = query.get('status');
      const cursor = query.get('cursor');
      state.itemReads.push(`${String(params.jobId)}:${status}${cursor ? `:${cursor}` : ''}`);
      const items = state.items.get(`${String(params.jobId)}:${status}`) ?? [];
      const start = cursor ? Number(cursor) : 0;
      return HttpResponse.json({
        items: items.slice(start, start + state.pageSize),
        hasNextPage: start + state.pageSize < items.length,
        nextCursor: start + state.pageSize < items.length ? String(start + state.pageSize) : null,
      });
    }),
    http.post('http://localhost/api/artwork/completion', async ({ request }) => {
      const body = (await request.json()) as ArtworkCompletionRequest;
      state.queued.push(body);
      const run = state.retry(body);
      if (!run)
        return new HttpResponse(null, {
          status: state.retryFailure,
        });
      state.runs.set(run.jobId, run);
      return HttpResponse.json(
        {
          jobId: run.jobId,
        },
        {
          status: 202,
        },
      );
    }),
    http.post('http://localhost/api/jobs/:id/cancel', ({ params }) => {
      state.cancelled.push(String(params.id));
      state.cancel(String(params.id));
      return new HttpResponse(null, {
        status: 204,
      });
    }),
    http.get('http://localhost/api/enrichment/workbench/stats', () => {
      state.countReads += 1;
      return HttpResponse.json({
        total: 500,
        needsReview: 61,
        automatic: 4,
        processing: 1,
        blocked: 2,
      });
    }),
  );
});
afterEach(() => {
  vi.useRealTimers();
});

function Host({
  initialJobId,
  onDismiss = () => {},
  onOpenTitle = () => {},
}: {
  initialJobId: string;
  onDismiss?: () => void;
  onOpenTitle?: (titleId: string) => void;
}) {
  const [jobId, setJobId] = useState(initialJobId);
  return (
    <>
      <output data-testid="activity">{jobId}</output>
      <RunPanel
        jobId={jobId}
        onDismiss={onDismiss}
        onOpenTitle={onOpenTitle}
        onRunChange={setJobId}
      />
    </>
  );
}

function renderPanel(props: Parameters<typeof Host>[0]) {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <MemoryRouter>
          <Host {...props} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
  return cache;
}

const reviewItem = (titleId: string, titleName: string): ArtworkCompletionItem => ({
  titleId,
  titleName,
  completion: 'Done',
  metadataOutcome: 'Succeeded',
  followUpJobId: null,
  roles: [],
  now: {
    ...settledNow,
    needsReview: true,
  },
});

const poll = () => act(() => vi.advanceTimersByTimeAsync(RUN_POLL_INTERVAL));
/** Under fake timers, user-event's own waits advance Mantine transition timers too, so they run inside act. */
const fakeTimerUser = () =>
  userEvent.setup({
    advanceTimers: async (ms) => {
      await act(async () => {
        await vi.advanceTimersByTimeAsync(ms);
      });
    },
  });

describe('execution line', () => {
  it.each([
    [
      'Pending',
      'Queued',
      true,
    ],
    [
      'Enriching',
      'Running · 120 of 240 processed',
      true,
    ],
    [
      'Completed',
      'Finished · 120 of 240 processed',
      false,
    ],
    [
      'CompletedWithErrors',
      'Finished with errors · 120 of 240 processed',
      false,
    ],
    [
      'Failed',
      'Stopped with an error · 120 of 240 processed',
      false,
    ],
    [
      'Cancelled',
      'Cancelled · 120 of 240 processed',
      false,
    ],
  ])('shows %s as "%s" with the review and retry line', async (phase, line, active) => {
    state.runs.set(
      'run',
      runDto({
        phase,
        processed: 120,
        review: 2,
        failed: 1,
        unfinished: 3,
      }),
    );
    renderPanel({
      initialJobId: 'run',
    });
    expect(await screen.findByText(line)).toBeInTheDocument();
    expect(screen.getByText('2 need review now · 1 failed · 3 unfinished')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Review 2 in this run',
      }),
    ).toBeInTheDocument();
    expect(
      !!screen.queryByRole('button', {
        name: 'Cancel run',
      }),
    ).toBe(active);
    expect(
      !!screen.queryByRole('button', {
        name: 'Retry 1 failed',
      }),
    ).toBe(!active);
    expect(
      !!screen.queryByRole('button', {
        name: 'Retry 3 unfinished',
      }),
    ).toBe(!active);
  });

  it('shows a deferred run as paused until its next attempt, in local time', async () => {
    vi.useFakeTimers({
      shouldAdvanceTime: true,
    });
    vi.setSystemTime(new Date(2026, 8, 22, 14, 0));
    state.runs.set(
      'run',
      runDto({
        phase: 'Enriching',
        processed: 40,
        nextAttemptAt: new Date(2026, 8, 22, 14, 32).toISOString(),
      }),
    );
    renderPanel({
      initialJobId: 'run',
    });
    expect(await screen.findByText('Paused · next attempt at 14:32')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Cancel run',
      }),
    ).toBeInTheDocument();
  });
});

describe('polling', () => {
  it('refreshes while execution is active and stops once the run is history', async () => {
    vi.useFakeTimers({
      shouldAdvanceTime: true,
    });
    state.runs.set(
      'run',
      runDto({
        phase: 'Enriching',
        total: 12,
        processed: 3,
      }),
    );
    renderPanel({
      initialJobId: 'run',
    });
    expect(await screen.findByText('Running · 3 of 12 processed')).toBeInTheDocument();
    state.runs.set(
      'run',
      runDto({
        phase: 'Completed',
        total: 12,
        processed: 12,
      }),
    );
    await act(() => vi.advanceTimersByTimeAsync(3000));
    expect(state.runReads).toEqual([
      'run',
    ]);
    await act(() => vi.advanceTimersByTimeAsync(2000));
    expect(await screen.findByText('Finished · 12 of 12 processed')).toBeInTheDocument();
    const reads = state.runReads.length;
    await act(() => vi.advanceTimersByTimeAsync(RUN_POLL_INTERVAL * 10));
    expect(state.runReads).toHaveLength(reads);
  });

  it('reloads the counts and the open review list together on each refresh', async () => {
    vi.useFakeTimers({
      shouldAdvanceTime: true,
    });
    const item = (titleId: string, titleName: string): ArtworkCompletionItem => ({
      titleId,
      titleName,
      completion: 'Working',
      metadataOutcome: 'Succeeded',
      followUpJobId: null,
      roles: [],
      now: {
        ...settledNow,
        needsReview: true,
      },
    });
    state.runs.set(
      'run',
      runDto({
        phase: 'Enriching',
        total: 12,
        processed: 3,
        review: 2,
      }),
    );
    state.items.set('run:Review', [
      item('t1', 'Chrono Trigger'),
      item('t2', 'Secret of Mana'),
    ]);
    const user = fakeTimerUser();
    renderPanel({
      initialJobId: 'run',
    });
    await user.click(
      await screen.findByRole('button', {
        name: 'Review 2 in this run',
      }),
    );
    expect(await screen.findByText('Chrono Trigger')).toBeInTheDocument();
    state.runs.set(
      'run',
      runDto({
        phase: 'Enriching',
        total: 12,
        processed: 4,
        review: 1,
      }),
    );
    state.items.set('run:Review', [
      item('t2', 'Secret of Mana'),
    ]);
    await poll();
    await waitFor(() => expect(screen.queryByText('Chrono Trigger')).not.toBeInTheDocument());
    expect(screen.getByText('1 needs review now · 0 failed · 0 unfinished')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Review 1 in this run',
      }),
    ).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('Secret of Mana')).toBeInTheDocument();
    expect(state.runReads).toEqual([
      'run',
      'run',
    ]);
    expect(state.itemReads).toEqual([
      'run:Review',
      'run:Review',
    ]);
  });

  it('lets a slow refresh finish instead of cancelling it on the next tick', async () => {
    vi.useFakeTimers({
      shouldAdvanceTime: true,
    });
    state.runs.set(
      'run',
      runDto({
        phase: 'Enriching',
        total: 12,
        processed: 3,
      }),
    );
    renderPanel({
      initialJobId: 'run',
    });
    expect(await screen.findByText('Running · 3 of 12 processed')).toBeInTheDocument();
    let release = () => {};
    state.holdRunRead = () =>
      new Promise<void>((resolve) => {
        release = resolve;
      });
    state.runs.set(
      'run',
      runDto({
        phase: 'Enriching',
        total: 12,
        processed: 9,
      }),
    );
    await poll();
    await waitFor(() => expect(state.runReads).toHaveLength(2));
    await poll();
    await poll();
    expect(state.runReads).toHaveLength(2);
    expect(state.runSignals[1].aborted).toBe(false);
    state.holdRunRead = () => undefined;
    await act(async () => {
      release();
    });
    expect(await screen.findByText('Running · 9 of 12 processed')).toBeInTheDocument();
  });

  it('refreshes only page one of an open list on each tick', async () => {
    vi.useFakeTimers({
      shouldAdvanceTime: true,
    });
    state.pageSize = 1;
    state.runs.set(
      'run',
      runDto({
        phase: 'Enriching',
        total: 12,
        processed: 3,
        review: 2,
      }),
    );
    state.items.set('run:Review', [
      reviewItem('t1', 'Chrono Trigger'),
      reviewItem('t2', 'Secret of Mana'),
    ]);
    const user = fakeTimerUser();
    renderPanel({
      initialJobId: 'run',
    });
    await user.click(
      await screen.findByRole('button', {
        name: 'Review 2 in this run',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Show more',
      }),
    );
    expect(await screen.findByText('Secret of Mana')).toBeInTheDocument();
    await poll();
    await waitFor(() =>
      expect(state.itemReads).toEqual([
        'run:Review',
        'run:Review:1',
        'run:Review',
      ]),
    );
    await waitFor(() => expect(screen.queryByText('Secret of Mana')).not.toBeInTheDocument());
    expect(screen.getByText('Chrono Trigger')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Show more',
      }),
    ).toBeInTheDocument();
  });

  it('reads the global counts once while the run keeps polling', async () => {
    vi.useFakeTimers({
      shouldAdvanceTime: true,
    });
    state.runs.set(
      'run',
      runDto({
        phase: 'Enriching',
        total: 12,
        processed: 3,
      }),
    );
    renderPanel({
      initialJobId: 'run',
    });
    expect(
      await screen.findByRole('link', {
        name: 'Global queue: 61 need review →',
      }),
    ).toBeInTheDocument();
    await act(() => vi.advanceTimersByTimeAsync(65_000));
    expect(state.runReads.length).toBeGreaterThan(10);
    expect(state.countReads).toBe(1);
  });

  it('never polls a run that is already history', async () => {
    vi.useFakeTimers({
      shouldAdvanceTime: true,
    });
    state.runs.set(
      'run',
      runDto({
        phase: 'CompletedWithErrors',
        failed: 2,
      }),
    );
    renderPanel({
      initialJobId: 'run',
    });
    expect(
      await screen.findByText('Finished with errors · 240 of 240 processed'),
    ).toBeInTheDocument();
    await act(() => vi.advanceTimersByTimeAsync(RUN_POLL_INTERVAL * 10));
    expect(state.runReads).toEqual([
      'run',
    ]);
  });
});

describe('review list', () => {
  it('lists current review needs with recorded history and opens a title without leaving the run', async () => {
    const onOpenTitle = vi.fn();
    state.runs.set(
      'run',
      runDto({
        phase: 'CompletedWithErrors',
        review: 2,
      }),
    );
    state.items.set('run:Review', [
      {
        titleId: 't1',
        titleName: 'Chrono Trigger',
        completion: 'Done',
        metadataOutcome: 'Succeeded',
        followUpJobId: null,
        roles: [
          {
            role: 'Backdrop',
            status: 'NeedsReview',
            count: 1,
          },
          {
            role: 'Logo',
            status: 'Unavailable',
            count: 1,
          },
        ],
        now: {
          ...settledNow,
          needsReview: true,
        },
      },
      {
        titleId: 't2',
        titleName: 'Secret of Mana',
        completion: 'Unfinished',
        metadataOutcome: null,
        followUpJobId: null,
        roles: [],
        now: {
          ...settledNow,
          needsReview: true,
          hasOperationalBlocker: true,
        },
      },
    ]);
    const user = userEvent.setup();
    renderPanel({
      initialJobId: 'run',
      onOpenTitle,
    });
    await user.click(
      await screen.findByRole('button', {
        name: 'Review 2 in this run',
      }),
    );
    expect(
      await screen.findByText(
        'Recorded: Metadata step completed · Backdrop: candidates ready · Logo: nothing usable found',
      ),
    ).toBeInTheDocument();
    expect(
      within(
        screen.getByRole('group', {
          name: 'Chrono Trigger status now',
        }),
      ).getByText('Needs review'),
    ).toBeInTheDocument();
    expect(screen.getByText('Recorded: Metadata: not recorded')).toBeInTheDocument();
    expect(
      within(
        screen.getByRole('group', {
          name: 'Secret of Mana status now',
        }),
      )
        .getAllByText(/Needs review|Blocked/)
        .map((chip) => chip.textContent),
    ).toEqual([
      'Needs review',
      'Blocked',
    ]);
    expect(screen.getByText('Unfinished')).toBeInTheDocument();
    expect(state.itemReads).toEqual([
      'run:Review',
    ]);
    await user.click(
      screen.getByRole('button', {
        name: 'Chrono Trigger',
      }),
    );
    expect(onOpenTitle).toHaveBeenCalledWith('t1');
    expect(screen.getByTestId('activity')).toHaveTextContent('run');
  });
});

describe('refresh sources', () => {
  it('pages the review list with the server cursor', async () => {
    state.pageSize = 1;
    state.runs.set(
      'run',
      runDto({
        review: 2,
      }),
    );
    state.items.set('run:Review', [
      reviewItem('t1', 'Chrono Trigger'),
      reviewItem('t2', 'Secret of Mana'),
    ]);
    const user = userEvent.setup();
    renderPanel({
      initialJobId: 'run',
    });
    await user.click(
      await screen.findByRole('button', {
        name: 'Review 2 in this run',
      }),
    );
    expect(await screen.findByText('Chrono Trigger')).toBeInTheDocument();
    expect(screen.queryByText('Secret of Mana')).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Show more',
      }),
    );
    expect(await screen.findByText('Secret of Mana')).toBeInTheDocument();
    expect(screen.getByText('Chrono Trigger')).toBeInTheDocument();
    expect(state.itemReads).toEqual([
      'run:Review',
      'run:Review:1',
    ]);
    expect(
      screen.queryByRole('button', {
        name: 'Show more',
      }),
    ).not.toBeInTheDocument();
  });

  it('reloads a finished run and its open list when job events refresh the enrichment workspace', async () => {
    state.runs.set(
      'run',
      runDto({
        review: 1,
      }),
    );
    state.items.set('run:Review', [
      reviewItem('t1', 'Chrono Trigger'),
    ]);
    const user = userEvent.setup();
    const cache = renderPanel({
      initialJobId: 'run',
    });
    await user.click(
      await screen.findByRole('button', {
        name: 'Review 1 in this run',
      }),
    );
    expect(await screen.findByText('Chrono Trigger')).toBeInTheDocument();
    await act(() =>
      cache.invalidateQueries({
        queryKey: [
          'enrichment-workspace',
        ],
      }),
    );
    expect(state.runReads).toEqual([
      'run',
      'run',
    ]);
    expect(state.itemReads).toEqual([
      'run:Review',
      'run:Review',
    ]);
  });
});

describe('links and dismissal', () => {
  it('labels the review queue as global and dismisses the panel', async () => {
    const onDismiss = vi.fn();
    state.runs.set(
      'run',
      runDto({
        review: 3,
      }),
    );
    renderPanel({
      initialJobId: 'run',
      onDismiss,
    });
    const global = await screen.findByRole('link', {
      name: 'Global queue: 61 need review →',
    });
    expect(global).toHaveAttribute('href', '/enrichment?view=NeedsReview');
    expect(screen.getByText('3 need review now · 0 failed · 0 unfinished')).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Dismiss run',
      }),
    );
    expect(onDismiss).toHaveBeenCalledOnce();
  });

  it('keeps the panel on the run and refreshes its counts when the API rejects a retry', async () => {
    state.runs.set(
      'run',
      runDto({
        phase: 'CompletedWithErrors',
        failed: 2,
      }),
    );
    state.retryFailure = 400;
    state.retry = () => {
      state.runs.set(
        'run',
        runDto({
          phase: 'CompletedWithErrors',
          failed: 0,
        }),
      );
      return undefined;
    };
    const user = userEvent.setup();
    renderPanel({
      initialJobId: 'run',
    });
    await user.click(
      await screen.findByRole('button', {
        name: 'Retry 2 failed',
      }),
    );
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Nothing in this run can be retried now.',
    );
    await waitFor(() =>
      expect(
        screen.queryByRole('button', {
          name: /Retry .* failed/,
        }),
      ).not.toBeInTheDocument(),
    );
    expect(screen.getByText('0 need review now · 0 failed · 0 unfinished')).toBeInTheDocument();
    expect(screen.getByTestId('activity')).toHaveTextContent('run');
    expect(state.runReads).toEqual([
      'run',
      'run',
    ]);
  });

  it('keeps a failed retry request so trying again cannot queue it twice', async () => {
    state.runs.set(
      'run',
      runDto({
        phase: 'CompletedWithErrors',
        failed: 2,
      }),
    );
    state.retry = () =>
      state.queued.length === 1
        ? undefined
        : runDto({
            jobId: 'retry',
            phase: 'Pending',
            total: 2,
            processed: 0,
          });
    const user = userEvent.setup();
    renderPanel({
      initialJobId: 'run',
    });
    await user.click(
      await screen.findByRole('button', {
        name: 'Retry 2 failed',
      }),
    );
    expect(await screen.findByText(/Could not start the retry/)).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Retry 2 failed',
      }),
    );
    await waitFor(() => expect(screen.getByTestId('activity')).toHaveTextContent('retry'));
    expect(state.queued[1].requestId).toBe(state.queued[0].requestId);
  });
});

/** A host that keeps the run in the URL, as the workspace does, beside an unsaved draft. */
function DraftHost() {
  const [params, setParams] = useSearchParams();
  return (
    <>
      <UnsavedChanges
        dirty
        description="Your description edit is not saved."
      />
      <output data-testid="activity">{params.get('activity')}</output>
      <RunPanel
        jobId={params.get('activity') ?? ''}
        onDismiss={() => {}}
        onOpenTitle={() => {}}
        onRunChange={(jobId) =>
          setParams({
            activity: jobId,
          })
        }
      />
    </>
  );
}

function renderWithDraft() {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const router = createMemoryRouter(
    [
      {
        element: (
          <DraftGuardProvider>
            <Outlet />
          </DraftGuardProvider>
        ),
        children: [
          {
            path: '/',
            element: <DraftHost />,
          },
        ],
      },
    ],
    {
      initialEntries: [
        '/?activity=run',
      ],
    },
  );
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('open drafts', () => {
  beforeEach(() => {
    state.runs.set(
      'run',
      runDto({
        phase: 'CompletedWithErrors',
        failed: 2,
      }),
    );
    state.retry = () =>
      runDto({
        jobId: 'retry',
        phase: 'Pending',
        total: 2,
        processed: 0,
      });
  });

  it('asks before queueing a retry, and Keep editing queues nothing', async () => {
    renderWithDraft();
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Retry 2 failed',
      }),
    );
    expect(await screen.findByText('Discard unsaved changes?')).toBeInTheDocument();
    expect(state.queued).toHaveLength(0);
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    await waitFor(() =>
      expect(screen.queryByText('Discard unsaved changes?')).not.toBeInTheDocument(),
    );
    expect(state.queued).toHaveLength(0);
    expect(screen.getByTestId('activity')).toHaveTextContent('run');
  });

  it('after Discard queues the retry once and switches the run without asking again', async () => {
    renderWithDraft();
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Retry 2 failed',
      }),
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Discard',
      }),
    );
    await waitFor(() => expect(screen.getByTestId('activity')).toHaveTextContent('retry'));
    expect(await screen.findByText('This run · Enrich 2 titles')).toBeInTheDocument();
    expect(state.queued).toHaveLength(1);
    expect(screen.queryByText('Discard unsaved changes?')).not.toBeInTheDocument();
  });
});

it('follows plan scenario 8.3 from a finished run through a cancelled retry to retrying the unfinished items', async () => {
  // Only the run poller's interval is fake, so transitions and waits run on real timers inside Testing Library's act.
  vi.useFakeTimers({
    toFake: [
      'setInterval',
      'clearInterval',
    ],
  });
  state.runs.set(
    'run-240',
    runDto({
      jobId: 'run-240',
      phase: 'CompletedWithErrors',
      total: 240,
      processed: 240,
      review: 28,
      failed: 12,
      unfinished: 0,
    }),
  );
  state.retry = (request) =>
    request.retryStatus === 'Failed'
      ? runDto({
          jobId: 'retry-failed',
          phase: 'Pending',
          total: 12,
          processed: 0,
        })
      : runDto({
          jobId: 'retry-unfinished',
          phase: 'Pending',
          total: 7,
          processed: 0,
        });
  state.cancel = (jobId) =>
    state.runs.set(
      jobId,
      runDto({
        jobId,
        phase: 'Cancelled',
        total: 12,
        processed: 5,
        review: 1,
        unfinished: 7,
      }),
    );
  const user = userEvent.setup();
  renderPanel({
    initialJobId: 'run-240',
  });

  expect(await screen.findByText('This run · Enrich 240 titles')).toBeInTheDocument();
  expect(screen.getByText('Finished with errors · 240 of 240 processed')).toBeInTheDocument();
  expect(screen.getByText('28 need review now · 12 failed · 0 unfinished')).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Review 28 in this run',
    }),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: /unfinished/,
    }),
  ).not.toBeInTheDocument();
  expect(
    await screen.findByRole('link', {
      name: 'Global queue: 61 need review →',
    }),
  ).toBeInTheDocument();

  await user.click(
    screen.getByRole('button', {
      name: 'Retry 12 failed',
    }),
  );
  await waitFor(() => expect(screen.getByTestId('activity')).toHaveTextContent('retry-failed'));
  expect(state.queued[0]).toEqual({
    requestId: expect.any(String),
    scope: 'Retry',
    retryJobId: 'run-240',
    retryStatus: 'Failed',
  });
  expect(await screen.findByText('This run · Enrich 12 titles')).toBeInTheDocument();
  expect(screen.getByText('Queued')).toBeInTheDocument();

  state.runs.set(
    'retry-failed',
    runDto({
      jobId: 'retry-failed',
      phase: 'Enriching',
      total: 12,
      processed: 3,
    }),
  );
  await poll();
  expect(await screen.findByText('Running · 3 of 12 processed')).toBeInTheDocument();

  await user.click(
    screen.getByRole('button', {
      name: 'Cancel run',
    }),
  );
  expect(await screen.findByText('Cancelled · 5 of 12 processed')).toBeInTheDocument();
  expect(state.cancelled).toEqual([
    'retry-failed',
  ]);
  expect(screen.getByText('1 needs review now · 0 failed · 7 unfinished')).toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: 'Cancel run',
    }),
  ).not.toBeInTheDocument();

  await user.click(
    screen.getByRole('button', {
      name: 'Retry 7 unfinished',
    }),
  );
  await waitFor(() => expect(screen.getByTestId('activity')).toHaveTextContent('retry-unfinished'));
  expect(state.queued[1]).toEqual({
    requestId: expect.any(String),
    scope: 'Retry',
    retryJobId: 'retry-failed',
    retryStatus: 'Unfinished',
  });
  expect(await screen.findByText('This run · Enrich 7 titles')).toBeInTheDocument();
  expect(screen.getByText('Queued')).toBeInTheDocument();
});
