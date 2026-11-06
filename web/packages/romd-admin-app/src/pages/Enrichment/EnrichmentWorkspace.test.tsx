import { readFileSync } from 'node:fs';
import { MantineProvider } from '@mantine/core';
import {
  client,
  type EnrichmentAssessmentDto,
  type EnrichmentRequirementDto,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { useState } from 'react';
import {
  createMemoryRouter,
  MemoryRouter,
  Outlet,
  Route,
  RouterProvider,
  Routes,
  useLocation,
} from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DraftGuardProvider } from '../../components/Administration/DraftGuard';
import { UnsavedChanges } from '../../components/Administration/UnsavedChanges';
import type { EnrichmentRun } from '../../components/Enrichment/useEnrichmentRun';
import { artworkJobFixture } from '../../test/fixtures/artworkRun';
import {
  assessmentFixture,
  chronoTriggerRequirements,
  satisfied,
  unsatisfied,
  withRequirements,
  writtenTitleFixture,
} from '../../test/msw/fixtures/enrichment';
import { server } from '../../test/msw/server';
import { EnrichmentWorkspace } from './EnrichmentWorkspace';

const viewport = vi.hoisted(() => ({
  mobile: false,
}));
vi.mock('@mantine/hooks', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@mantine/hooks')>()),
  useMediaQuery: (query: string) => (query === '(max-width: 53.125em)' ? viewport.mobile : false),
}));
const delayedRoute = vi.hoisted(() => ({
  replace: false,
  finish: undefined as (() => void) | undefined,
}));
vi.mock('react-router', async (importOriginal) => {
  const router = await importOriginal<typeof import('react-router')>();
  return {
    ...router,
    useSearchParams: (...args: Parameters<typeof router.useSearchParams>) => {
      const result = router.useSearchParams(...args);
      if (!delayedRoute.replace) return result;
      const [params, setParams] = result;
      const deferredSetParams: typeof setParams = (...next) => {
        if (next[1]?.replace) delayedRoute.finish = () => setParams(...next);
        else setParams(...next);
      };
      return [
        params,
        deferredSetParams,
      ] as typeof result;
    },
  };
});
const permissions = vi.hoisted(() => ({
  canManageTitles: true,
  canConfigureIntegrations: true,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => permissions,
}));
vi.mock('../../hooks/api/useTitleDetail', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../hooks/api/useTitleDetail')>()),
  useTitleDetail: (id: string) => ({
    data: {
      id,
      name: id === 'turok' ? 'Turok' : 'Tron',
    },
    isPending: false,
  }),
}));
vi.mock('../../components/Title/ArtworkPanel', () => ({
  ArtworkPanel: ({ initialRole }: { initialRole?: string }) => (
    <>
      <div>Composition review: {initialRole}</div>
      <input
        aria-label="Artwork draft"
        defaultValue=""
      />
      <section
        tabIndex={-1}
        aria-label="Compose artwork"
      >
        <div
          role="slider"
          tabIndex={0}
          aria-label="Focal point"
          aria-valuenow={50}
        />
      </section>
    </>
  ),
}));
vi.mock('../../components/Title/preview/CatalogPresentationPreview', () => ({
  CatalogPresentationPreview: () => <div>Presentation preview</div>,
}));
vi.mock('../../components/Title/MetadataPanel', () => ({
  MetadataPanel: () => {
    const [draft, setDraft] = useState('');
    return (
      <>
        <UnsavedChanges
          dirty={!!draft}
          description="Your metadata draft has not been saved."
        />
        <div>Metadata editor</div>
        <input
          aria-label="Description draft"
          value={draft}
          onChange={(event) => setDraft(event.currentTarget.value)}
        />
      </>
    );
  },
}));
vi.mock('../../components/Title/ProviderMatches', () => ({
  ProviderMatches: ({ initialProviderId }: { initialProviderId?: string }) => (
    <>
      <div>Inline provider decisions</div>
      <div>Target provider: {initialProviderId}</div>
    </>
  ),
}));
function Location() {
  const location = useLocation();
  return (
    <div>
      Location: {location.pathname}
      {location.search}
    </div>
  );
}
const caches: QueryClient[] = [];
const runEndpoint = 'http://localhost/api/artwork/completion';
const jobEndpoint = 'http://localhost/api/jobs';
const finishedRun = (jobId: string, over: Partial<EnrichmentRun> = {}): EnrichmentRun => ({
  jobId,
  phase: 'Completed',
  total: 2,
  processed: 2,
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
beforeEach(() => {
  viewport.mobile = false;
  permissions.canConfigureIntegrations = true;
  client.setConfig({
    baseUrl: 'http://localhost',
  });
  delayedRoute.replace = false;
  delayedRoute.finish = undefined;
  server.use(
    http.get('http://localhost/api/titles/turok/artwork', () =>
      HttpResponse.json({
        revision: 'turok-artwork-family',
        roles: [
          'Poster',
          'Backdrop',
          'Hero',
          'Logo',
        ].map((role) => ({
          role,
          mode: 'Automatic',
          revision: '0',
          pinnedAssetId: null,
          pendingJobId: null,
        })),
      }),
    ),
    http.get(`${jobEndpoint}/:jobId`, ({ params }) =>
      HttpResponse.json(artworkJobFixture(finishedRun(String(params.jobId)))),
    ),
    http.get(`${jobEndpoint}/:jobId/artwork-summary`, ({ params }) => {
      const { roles, review, failed, unfinished, working } = finishedRun(String(params.jobId));
      return HttpResponse.json({
        roles,
        review,
        failed,
        unfinished,
        working,
      });
    }),
    http.get(`${jobEndpoint}/:jobId/artwork-items`, () =>
      HttpResponse.json({
        items: [],
        nextCursor: null,
        hasNextPage: false,
      }),
    ),
  );
});
afterEach(() => {
  for (const cache of caches) cache.clear();
  caches.length = 0;
  vi.useRealTimers();
});
const identity = unsatisfied('Identity:igdb', 'NeedsReview', 'MatchDecisionRequired', [
  'ReviewProvider',
  'ContinueWithoutProvider',
]);
const metadata = unsatisfied('Metadata:Description', 'NeedsReview', 'NotProvided', [
  'ReviewMetadata',
  'AcceptOmission',
]);
const steamGridDbReview = unsatisfied('Identity:steamgriddb', 'NeedsReview', 'NoMatchFound', [
  'ReviewProvider',
  'ContinueWithoutProvider',
]);
const metadataCopy = 'Description is missing after enrichment.';
/** Turok with every row the facts function emits: the given rows plus satisfied ones. */
function titleWith(
  rows: EnrichmentRequirementDto[] = [],
  overrides: Partial<EnrichmentAssessmentDto> = {},
) {
  return assessmentFixture(withRequirements(...rows), {
    titleId: 'turok',
    titleName: 'Turok',
    platformName: 'Nintendo 64',
    ...overrides,
  });
}
const title = titleWith([
  identity,
]);
const counts = {
  total: 2,
  needsReview: 1,
  automatic: 1,
  processing: 0,
  blocked: 0,
};
const endpoint = 'http://localhost/api/enrichment/workbench';
const titlesEndpoint = 'http://localhost/api/titles';
const written = writtenTitleFixture({
  id: 'turok',
  name: 'Turok',
});
function renderWorkspace(entry = '/enrichment', defaultCounts = true) {
  if (defaultCounts) server.use(http.get(`${endpoint}/stats`, () => HttpResponse.json(counts)));
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  caches.push(cache);
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <MemoryRouter
          initialEntries={[
            entry,
          ]}
        >
          <Routes>
            <Route
              path="/enrichment"
              element={
                <>
                  <EnrichmentWorkspace />
                  <Location />
                </>
              }
            />
            <Route
              path="/enrichment/:taskId"
              element={
                <>
                  <EnrichmentWorkspace />
                  <Location />
                </>
              }
            />
            <Route
              path="/enrichment/titles/:titleId"
              element={
                <>
                  <EnrichmentWorkspace />
                  <Location />
                </>
              }
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
  return cache;
}
/** The app's data router with the single draft coordinator, so drafts really block navigation. */
function renderGuardedWorkspace(initialEntries: string[]) {
  server.use(http.get(`${endpoint}/stats`, () => HttpResponse.json(counts)));
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  caches.push(cache);
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
            path: '/enrichment',
            element: (
              <>
                <EnrichmentWorkspace />
                <Location />
              </>
            ),
          },
        ],
      },
    ],
    {
      initialEntries,
      initialIndex: initialEntries.length - 1,
    },
  );
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return router;
}
const currentRow = () => document.querySelector('[data-current]');
const queueNav = () =>
  screen.getByRole('navigation', {
    name: 'Enrichment title queue',
  });
function fixtures(item = title) {
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: [
          item,
        ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, () => HttpResponse.json(item)),
  );
}
it('opens a canonical decision without requiring any prior job', async () => {
  fixtures();
  renderWorkspace();
  expect(await screen.findByText('Inline provider decisions')).toBeInTheDocument();
  expect(
    screen.getByRole('tab', {
      name: /Needs review/,
    }),
  ).toHaveAttribute('aria-selected', 'true');
  expect(within(queueNav()).getByText('Nintendo 64')).toBeInTheDocument();
  expect(
    screen.getByRole('heading', {
      level: 2,
      name: 'Turok',
    }),
  ).toBeInTheDocument();
  expect(screen.queryByText('Presentation preview')).not.toBeInTheDocument();
  expect(screen.queryByText('Recent enrichment tasks')).not.toBeInTheDocument();
});
it('preserves a selected title after it is resolved and leaves the queue', async () => {
  let resolved = false;
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: resolved
          ? []
          : [
              title,
            ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/turok/enrichment-assessment`, () =>
      HttpResponse.json(
        resolved
          ? titleWith([], {
              revision: 'rev-2',
            })
          : title,
      ),
    ),
  );
  const cache = renderWorkspace();
  await screen.findByText('Inline provider decisions');
  resolved = true;
  await act(async () => {
    await cache.invalidateQueries({
      queryKey: [
        'enrichment-workspace',
      ],
    });
  });
  expect(await screen.findByText('Nothing outstanding')).toBeInTheDocument();
  expect(screen.getByText('Inline provider decisions')).toBeInTheDocument();
  expect(screen.getByText('Location: /enrichment?title=turok')).toBeInTheDocument();
});

it('catches up after a missed event without discarding the selected artwork draft', async () => {
  vi.useFakeTimers({
    toFake: [
      'setInterval',
      'clearInterval',
    ],
  });
  let resolved = false;
  const artwork = unsatisfied('Artwork:Backdrop', 'NeedsReview', 'ArtworkDecisionRequired', [
    'ReviewArtwork',
  ]);
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: resolved
          ? []
          : [
              titleWith([
                artwork,
              ]),
            ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/turok/enrichment-assessment`, () =>
      HttpResponse.json(
        resolved
          ? titleWith([], {
              revision: 'rev-2',
            })
          : titleWith([
              artwork,
            ]),
      ),
    ),
  );
  renderWorkspace();
  const draft = await screen.findByRole('textbox', {
    name: 'Artwork draft',
  });
  await userEvent.type(draft, 'Keep my composition');
  resolved = true;
  await act(async () => {
    await vi.advanceTimersByTimeAsync(30_000);
  });
  expect(await screen.findByText('Nothing outstanding')).toBeInTheDocument();
  expect(
    screen.getByRole('textbox', {
      name: 'Artwork draft',
    }),
  ).toBe(draft);
  expect(draft).toHaveValue('Keep my composition');
  const queue = screen.getByRole('navigation', {
    name: 'Enrichment title queue',
  });
  expect(
    within(queue).getByRole('button', {
      name: /Turok/,
    }),
  ).toHaveAttribute('aria-current', 'true');
  expect(within(queue).getByText('No longer in this view')).toBeInTheDocument();
  expect(screen.getByText('Location: /enrichment?title=turok')).toBeInTheDocument();
});

it.each([
  'Load more titles',
  'Next title',
])('keeps %s from interrupting a background cursor refresh', async (button) => {
  let refreshing = false;
  let finishRefresh!: () => void;
  let signal!: AbortSignal;
  const cursors: (string | null)[] = [];
  server.use(
    http.get(endpoint, async ({ request }) => {
      const cursor = new URL(request.url).searchParams.get('cursor');
      cursors.push(cursor);
      if (refreshing && !cursor) {
        signal = request.signal;
        await new Promise<void>((resolve) => {
          finishRefresh = resolve;
        });
      }
      return HttpResponse.json({
        items: cursor
          ? [
              {
                ...title,
                titleId: 'tron',
                titleName: 'Tron',
              },
            ]
          : [
              title,
            ],
        hasNextPage: (cursor ? null : refreshing ? 'fresh-page' : 'old-page') !== null,
        nextCursor: cursor ? null : refreshing ? 'fresh-page' : 'old-page',
      });
    }),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
      HttpResponse.json({
        ...title,
        titleId: params.titleId,
      }),
    ),
  );
  const cache = renderWorkspace('/enrichment?title=turok');
  await screen.findByText('Inline provider decisions');
  refreshing = true;
  let refresh!: Promise<void>;
  act(() => {
    refresh = cache.invalidateQueries({
      queryKey: [
        'enrichment-workspace',
        'list',
      ],
    });
  });
  const control = screen.getByRole('button', {
    name: button,
  });
  await waitFor(() => expect(control).toBeDisabled());
  await waitFor(() => expect(finishRefresh).toBeDefined());
  await userEvent.click(control);
  expect(signal.aborted).toBe(false);
  expect(cursors).toEqual([
    null,
    null,
  ]);
  await act(async () => {
    finishRefresh();
    await refresh;
  });
  await waitFor(() => expect(control).toBeEnabled());
  await userEvent.click(control);
  await waitFor(() =>
    expect(cursors).toEqual([
      null,
      null,
      'fresh-page',
    ]),
  );
  if (button === 'Next title')
    expect(await screen.findByText('Location: /enrichment?title=tron')).toBeInTheDocument();
  else
    expect(
      await within(
        screen.getByRole('navigation', {
          name: 'Enrichment title queue',
        }),
      ).findByText('Tron'),
    ).toBeInTheDocument();
});

it.each([
  'title',
  'view',
  'search',
])('does not overwrite a newer %s selection when a pending Next request completes', async (change) => {
  let finishPage!: () => void;
  server.use(
    http.get(endpoint, async ({ request }) => {
      const url = new URL(request.url);
      const cursor = url.searchParams.get('cursor');
      if (cursor)
        await new Promise<void>((resolve) => {
          finishPage = resolve;
        });
      return HttpResponse.json({
        items: cursor
          ? [
              {
                ...title,
                titleId: 'later',
                titleName: 'Later title',
              },
            ]
          : [
              {
                ...title,
                titleId: 'earlier',
                titleName: 'Earlier title',
              },
              title,
            ],
        hasNextPage: (cursor ? null : 'next-page') !== null,
        nextCursor: cursor ? null : 'next-page',
      });
    }),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
      HttpResponse.json({
        ...title,
        titleId: params.titleId,
      }),
    ),
  );
  renderWorkspace('/enrichment?title=turok');
  await screen.findByText('Inline provider decisions');
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Next title',
    }),
  );
  await waitFor(() => expect(finishPage).toBeDefined());
  if (change === 'title')
    await userEvent.click(
      within(
        screen.getByRole('navigation', {
          name: 'Enrichment title queue',
        }),
      ).getByRole('button', {
        name: /Earlier title/,
      }),
    );
  else if (change === 'view')
    await userEvent.click(
      screen.getByRole('tab', {
        name: /Can enrich automatically/,
      }),
    );
  else {
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Find a title in enrichment',
      }),
      'Tron',
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Search',
        exact: true,
      }),
    );
  }
  const location =
    change === 'view'
      ? 'Location: /enrichment?view=Automatic&title=earlier'
      : change === 'search'
        ? 'Location: /enrichment?q=Tron&title=earlier'
        : 'Location: /enrichment?title=earlier';
  await screen.findByText(location);
  await act(async () => {
    finishPage();
  });
  await waitFor(() =>
    expect(
      screen.getByRole('button', {
        name: 'Next title',
      }),
    ).not.toHaveAttribute('data-loading', 'true'),
  );
  expect(screen.getByText(/^Location:/).textContent).toBe(location);
  expect(location).not.toContain('title=later');
});
it('keeps a manual decision visible while unrelated work is processing', async () => {
  fixtures(
    titleWith([
      identity,
      unsatisfied('Operation:Processing', 'Processing', 'Working'),
    ]),
  );
  renderWorkspace();
  expect(await screen.findByText('Inline provider decisions')).toBeInTheDocument();
  expect(
    screen.getByText('Enrichment is running for this title. Results appear here when it finishes.'),
  ).toBeInTheDocument();
  expect(
    within(currentRow() as HTMLElement).getByRole('button', {
      name: 'Review IGDB match',
    }),
  ).toBeInTheDocument();
});
it('loads the next cursor page when advancing beyond the loaded queue', async () => {
  server.use(
    http.get(endpoint, ({ request }) =>
      HttpResponse.json({
        items: new URL(request.url).searchParams.has('cursor')
          ? [
              {
                ...title,
                titleId: 'tron',
                titleName: 'Tron',
              },
            ]
          : [
              title,
            ],
        hasNextPage: (new URL(request.url).searchParams.has('cursor') ? null : 'page-two') !== null,
        nextCursor: new URL(request.url).searchParams.has('cursor') ? null : 'page-two',
      }),
    ),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
      HttpResponse.json({
        ...title,
        titleId: params.titleId,
        titleName: params.titleId === 'tron' ? 'Tron' : 'Turok',
      }),
    ),
  );
  renderWorkspace();
  await screen.findByText('Inline provider decisions');
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Next title',
    }),
  );
  expect(await screen.findByText('Location: /enrichment?title=tron')).toBeInTheDocument();
});
it('keeps Next navigation when the URL catches up with the already-selected title', async () => {
  delayedRoute.replace = true;
  let finishPage!: () => void;
  server.use(
    http.get(endpoint, async ({ request }) => {
      const cursor = new URL(request.url).searchParams.get('cursor');
      if (cursor)
        await new Promise<void>((resolve) => {
          finishPage = resolve;
        });
      return HttpResponse.json({
        items: cursor
          ? [
              {
                ...title,
                titleId: 'tron',
                titleName: 'Tron',
              },
            ]
          : [
              title,
            ],
        hasNextPage: (cursor ? null : 'page-two') !== null,
        nextCursor: cursor ? null : 'page-two',
      });
    }),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
      HttpResponse.json({
        ...title,
        titleId: params.titleId,
      }),
    ),
  );
  renderWorkspace();
  await screen.findByText('Inline provider decisions');
  expect(screen.getByText('Location: /enrichment')).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Next title',
    }),
  );
  await waitFor(() => expect(finishPage).toBeDefined());
  act(() => {
    delayedRoute.finish?.();
  });
  await screen.findByText('Location: /enrichment?title=turok');
  await act(async () => {
    finishPage();
  });
  expect(await screen.findByText('Location: /enrichment?title=tron')).toBeInTheDocument();
});
it('retains old task links as workspace context with the run panel', async () => {
  fixtures();
  renderWorkspace('/enrichment/old-task?title=turok');
  await screen.findByText('Inline provider decisions');
  const panel = screen.getByRole('region', {
    name: 'Enrichment run',
  });
  expect(await within(panel).findByText('This run · Enrich 2 titles')).toBeInTheDocument();
  expect(
    screen.getByRole('link', {
      name: 'Activity history',
    }),
  ).toHaveAttribute('href', '/jobs');
  await waitFor(() =>
    expect(
      screen.getByText('Location: /enrichment?title=turok&activity=old-task'),
    ).toBeInTheDocument(),
  );
});
it('accepts an intentional metadata omission only after confirmation, with concurrency protection', async () => {
  const omitted = titleWith(
    [
      satisfied('Metadata:Description', 'AcceptedOmission', [
        'ReviewMetadata',
      ]),
    ],
    {
      revision: 'rev-2',
      acceptedOmissions: [
        'Description',
      ],
    },
  );
  let accepted = false;
  const accept = vi.fn(async ({ request }: { request: Request }) => {
    expect(await request.json()).toEqual({
      accepted: true,
    });
    expect(request.headers.get('If-Match')).toBe('"rev-1"');
    accepted = true;
    return HttpResponse.json(written);
  });
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: [
          titleWith([
            metadata,
          ]),
        ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/turok/enrichment-assessment`, () =>
      HttpResponse.json(
        accepted
          ? omitted
          : titleWith([
              metadata,
            ]),
      ),
    ),
    http.put(`${titlesEndpoint}/turok/enrichment-omissions/Description`, accept),
  );
  renderWorkspace();
  await screen.findByText('Metadata editor');
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Keep description empty',
    }),
  );
  const dialog = await screen.findByRole('dialog', {
    name: 'Keep description empty?',
  });
  expect(
    within(dialog).getByText(
      'The consumer app shows no description for this title. You can revisit this later.',
    ),
  ).toBeInTheDocument();
  await userEvent.click(
    within(dialog).getByRole('button', {
      name: 'Cancel',
    }),
  );
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(accept).not.toHaveBeenCalled();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Keep description empty',
    }),
  );
  await userEvent.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Keep description empty?',
      }),
    ).getByRole('button', {
      name: 'Keep description empty',
    }),
  );
  const summary = await screen.findByText('Nothing outstanding · Description intentionally empty');
  expect(accept).toHaveBeenCalledOnce();
  // Nothing else is queued, so Next title is disabled and focus lands on the gap summary.
  expect(
    screen.getByRole('button', {
      name: 'Next title',
    }),
  ).toBeDisabled();
  await waitFor(() => expect(summary).toHaveFocus());
  expect(
    screen.getByText('Nothing outstanding. Some values may be intentionally empty or unavailable.'),
  ).toBeInTheDocument();
});
it('shows fetch errors with retry instead of an empty success state', async () => {
  server.use(
    http.get(
      endpoint,
      () =>
        new HttpResponse(null, {
          status: 503,
        }),
    ),
  );
  renderWorkspace();
  const alert = await screen.findByRole('alert');
  expect(
    within(alert).getByRole('button', {
      name: 'Retry',
    }),
  ).toBeInTheDocument();
  expect(screen.queryByText('Nothing needs your review')).not.toBeInTheDocument();
});

it('advances forward after a middle title resolves and disappears from the queue', async () => {
  let resolved = false;
  const before = {
    ...title,
    titleId: 'before',
    titleName: 'Earlier title',
  };
  const after = {
    ...title,
    titleId: 'after',
    titleName: 'Following title',
  };
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: resolved
          ? [
              before,
              after,
            ]
          : [
              before,
              title,
              after,
            ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
      HttpResponse.json(
        titleWith(
          resolved
            ? []
            : [
                identity,
              ],
          {
            titleId: String(params.titleId),
          },
        ),
      ),
    ),
  );
  const cache = renderWorkspace('/enrichment?title=turok');
  await screen.findByText('Inline provider decisions');
  resolved = true;
  await act(async () => {
    await cache.invalidateQueries({
      queryKey: [
        'enrichment-workspace',
      ],
    });
  });
  await screen.findByText('Nothing outstanding');
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Next title',
    }),
  );
  expect(await screen.findByText('Location: /enrichment?title=after')).toBeInTheDocument();
});

it('offers durable accepted omissions for explicit reconsideration', async () => {
  fixtures(
    titleWith(
      [
        satisfied('Metadata:Description', 'AcceptedOmission', [
          'ReviewMetadata',
        ]),
      ],
      {
        acceptedOmissions: [
          'Description',
        ],
      },
    ),
  );
  const reverse = vi.fn(async ({ request }: { request: Request }) => {
    expect(await request.json()).toEqual({
      accepted: false,
    });
    expect(request.headers.get('If-Match')).toBe('"rev-1"');
    return HttpResponse.json(written);
  });
  server.use(http.put(`${titlesEndpoint}/turok/enrichment-omissions/Description`, reverse));
  renderWorkspace('/enrichment?title=turok');
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Revisit description',
    }),
  );
  await waitFor(() => expect(reverse).toHaveBeenCalledOnce());
});

it('defaults automation to the server-owned eligible scope', async () => {
  fixtures();
  const preview = vi.fn(async ({ request }: { request: Request }) => {
    expect(await request.json()).toMatchObject({
      scope: 'Automatic',
      mode: 'Enrich',
    });
    return HttpResponse.json({
      titles: 37,
      roles: [],
    });
  });
  server.use(http.post('http://localhost/api/artwork/completion/preview', preview));
  renderWorkspace();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Enrich titles',
    }),
  );
  expect(await screen.findByText('37 titles in this scope')).toBeInTheDocument();
  expect(preview).toHaveBeenCalledOnce();
});

it.each([
  [
    '',
    undefined,
  ],
  [
    '&q=Turok',
    {
      query: 'Turok',
      tracked: 'tracked',
    },
  ],
])('offers Enrich all N in the Automatic view for its eligible scope (search: %s)', async (search, filters) => {
  const automaticTitle = titleWith([
    unsatisfied('Identity:igdb', 'Automatic', 'Discover', [
      'Enrich',
    ]),
  ]);
  const bodies: unknown[] = [];
  fixtures(automaticTitle);
  server.use(
    http.get(`${endpoint}/stats`, () =>
      HttpResponse.json({
        ...counts,
        automatic: 37,
      }),
    ),
    http.post('http://localhost/api/artwork/completion/preview', async ({ request }) => {
      bodies.push(await request.json());
      return HttpResponse.json({
        titles: 37,
        roles: [],
      });
    }),
  );
  renderWorkspace(`/enrichment?view=Automatic${search}&title=turok`, false);
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Enrich all 37',
    }),
  );
  expect(await screen.findByText('37 titles in this scope')).toBeInTheDocument();
  expect(bodies[0]).toMatchObject({
    scope: 'Automatic',
    mode: 'Enrich',
  });
  expect(
    (
      bodies[0] as {
        filters?: unknown;
      }
    ).filters,
  ).toEqual(filters);
});

it('offers Enrich all only in the Automatic view', async () => {
  fixtures();
  renderWorkspace();
  await screen.findByText('Inline provider decisions');
  expect(
    screen.queryByRole('button', {
      name: /^Enrich all/,
    }),
  ).not.toBeInTheDocument();
});

const mixed = [
  unsatisfied('Artwork:Backdrop', 'NeedsReview', 'ArtworkDecisionRequired', [
    'ReviewArtwork',
  ]),
  unsatisfied('Identity:igdb', 'Automatic', 'Discover', [
    'Enrich',
  ]),
  unsatisfied('Metadata:Genre', 'Processing', 'Working'),
  unsatisfied('Operation:Processing', 'Processing', 'Working'),
  unsatisfied('Identity:steamgriddb', 'Blocked', 'ProviderUnconfigured', [
    'ConfigureProvider',
  ]),
];

it.each([
  [
    'NeedsReview',
    'Choose backdrop artwork or leave it empty.',
    'Composition review: Backdrop',
    'Backdrop',
  ],
  [
    'Automatic',
    'Will search IGDB on the next enrichment.',
    'Inline provider decisions',
    'IGDB',
  ],
  [
    'Processing',
    'Enrichment is running.',
    'Metadata editor',
    'Genre',
  ],
  [
    'Blocked',
    'SteamGridDB is enabled but has no credentials.',
    'Inline provider decisions',
    'SteamGridDB',
  ],
])('prioritizes the %s requirement in both queue and selected decision', async (view, reason, editor, subject) => {
  fixtures(titleWith(mixed));
  renderWorkspace(`/enrichment?view=${view}&title=turok`);
  expect(await screen.findByText(editor)).toBeInTheDocument();
  const queue = screen.getByRole('navigation', {
    name: 'Enrichment title queue',
  });
  expect(within(queue).getByText(reason)).toBeInTheDocument();
  expect(within(queue).getByText('4 outstanding')).toBeInTheDocument();
  expect(currentRow()).toHaveAttribute('aria-label', `${subject}: ${reason}`);
});

const stateCauses = {
  NeedsReview: [
    'NotProvided',
    'Description is missing after enrichment.',
  ],
  Automatic: [
    'NotAttempted',
    'Not fetched yet.',
  ],
  Processing: [
    'Working',
    'Enrichment is running.',
  ],
  Blocked: [
    'ProviderUnavailable',
    'IGDB could not be queried successfully. You can retry.',
  ],
} as const;
it.each([
  'Automatic',
  'Processing',
  'Blocked',
  'NeedsReview',
] as const)('uses explicit %s state rather than available actions to classify requirements', async (view) => {
  const [cause, copy] = stateCauses[view];
  const selected = unsatisfied('Metadata:Description', view, cause, [
    'ReviewMetadata',
    'AcceptOmission',
  ]);
  const distraction =
    view === 'NeedsReview'
      ? unsatisfied('Identity:igdb', 'Automatic', 'Discover', [])
      : unsatisfied(
          'Identity:igdb',
          'NeedsReview',
          'MatchDecisionRequired',
          view === 'Automatic'
            ? [
                'Enrich',
              ]
            : view === 'Blocked'
              ? [
                  'Retry',
                ]
              : [],
        );
  fixtures(
    titleWith([
      distraction,
      selected,
    ]),
  );
  renderWorkspace(`/enrichment?view=${view}&title=turok`);
  await screen.findByText('Metadata editor');
  expect(
    within(
      screen.getByRole('navigation', {
        name: 'Enrichment title queue',
      }),
    ).getByText(copy),
  ).toBeInTheDocument();
  const current = currentRow() as HTMLElement;
  expect(current).toHaveAttribute('aria-label', `Description: ${copy}`);
  expect(
    within(current).getByRole('button', {
      name: 'Keep description empty',
    }),
  ).toBeInTheDocument();
  expect(
    within(current).queryByRole('button', {
      name: 'Enrich this title',
    }),
  ).not.toBeInTheDocument();
  if (view === 'Blocked')
    expect(
      within(screen.getByRole('alert')).getByText(`Description: ${copy} · 1 title`),
    ).toBeInTheDocument();
});

it('changes the initial editor when explicitly switching views for the same title', async () => {
  fixtures(titleWith(mixed.slice(0, 2)));
  renderWorkspace('/enrichment?title=turok');
  expect(await screen.findByText('Composition review: Backdrop')).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('tab', {
      name: /Can enrich automatically/,
    }),
  );
  expect(await screen.findByText('Inline provider decisions')).toBeInTheDocument();
  expect(currentRow()).toHaveAttribute(
    'aria-label',
    'IGDB: Will search IGDB on the next enrichment.',
  );
});

it('honors an explicitly linked decision while the queue stays aligned to its view', async () => {
  fixtures(titleWith(mixed.slice(0, 2)));
  renderWorkspace('/enrichment?view=Automatic&title=turok&issue=Artwork%3ABackdrop');
  expect(await screen.findByText('Composition review: Backdrop')).toBeInTheDocument();
  expect(
    within(
      screen.getByRole('navigation', {
        name: 'Enrichment title queue',
      }),
    ).getByText('Will search IGDB on the next enrichment.'),
  ).toBeInTheDocument();
});

it('renders the queue and selected decision while independent counts are still loading', async () => {
  let finishCounts!: () => void;
  const pendingCounts = new Promise<void>((resolve) => {
    finishCounts = resolve;
  });
  fixtures();
  server.use(
    http.get(`${endpoint}/stats`, async () => {
      await pendingCounts;
      return HttpResponse.json(counts);
    }),
  );
  renderWorkspace('/enrichment', false);
  try {
    expect(await screen.findByText('Inline provider decisions')).toBeInTheDocument();
    expect(screen.getByText(/Checking tracked titles/)).toBeInTheDocument();
    expect(
      within(
        screen.getByRole('navigation', {
          name: 'Enrichment title queue',
        }),
      ).getByText('Turok'),
    ).toBeInTheDocument();
  } finally {
    finishCounts();
  }
  expect(await screen.findByText(/2 tracked titles/)).toBeInTheDocument();
});

it('keeps decisions usable when counts fail and offers an independent retry', async () => {
  fixtures();
  let failed = true;
  server.use(
    http.get(`${endpoint}/stats`, () =>
      failed
        ? new HttpResponse(null, {
            status: 503,
          })
        : HttpResponse.json(counts),
    ),
  );
  renderWorkspace('/enrichment', false);
  expect(await screen.findByText('Inline provider decisions')).toBeInTheDocument();
  expect(await screen.findByText(/Queue counts unavailable/)).toBeInTheDocument();
  expect(screen.queryByText(/0 tracked titles/)).not.toBeInTheDocument();
  failed = false;
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Retry counts',
    }),
  );
  expect(await screen.findByText(/2 tracked titles/)).toBeInTheDocument();
});

it('labels retained counts as stale when their background refresh fails', async () => {
  fixtures();
  const cache = renderWorkspace();
  await screen.findByText(/2 tracked titles/);
  server.use(
    http.get(
      `${endpoint}/stats`,
      () =>
        new HttpResponse(null, {
          status: 503,
        }),
    ),
  );
  await act(async () => {
    await cache.invalidateQueries({
      queryKey: [
        'enrichment-workspace',
        'counts',
      ],
    });
  });
  expect(await screen.findByText('Counts may be out of date.')).toBeInTheDocument();
  expect(screen.getByText(/2 tracked titles/)).toBeInTheDocument();
  expect(screen.getByText('Inline provider decisions')).toBeInTheDocument();
});

it('uses a mobile title chooser, restores focus on dismissal, and closes after selection', async () => {
  viewport.mobile = true;
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: [
          title,
          {
            ...title,
            titleId: 'tron',
            titleName: 'Tron',
          },
        ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
      HttpResponse.json({
        ...title,
        titleId: params.titleId,
        titleName: params.titleId === 'tron' ? 'Tron' : 'Turok',
      }),
    ),
  );
  renderWorkspace('/enrichment?title=turok');
  await screen.findByText('Inline provider decisions');
  expect(
    screen.queryByRole('navigation', {
      name: 'Enrichment title queue',
    }),
  ).not.toBeInTheDocument();
  const chooser = screen.getByRole('button', {
    name: 'Choose title, current title: Turok',
  });
  expect(
    screen.getByRole('button', {
      name: 'Next title',
    }),
  ).toBeEnabled();
  await userEvent.click(chooser);
  const dialog = await screen.findByRole('dialog', {
    name: 'Choose a title',
  });
  expect(
    within(dialog).getByRole('button', {
      name: /Turok/,
    }),
  ).toHaveAttribute('aria-current', 'true');
  await userEvent.keyboard('{Escape}');
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  await waitFor(() => expect(chooser).toHaveFocus());
  await userEvent.click(chooser);
  await userEvent.click(
    within(await screen.findByRole('dialog')).getByRole('button', {
      name: /Tron/,
    }),
  );
  expect(await screen.findByText('Location: /enrichment?title=tron')).toBeInTheDocument();
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  const selectedChooser = screen.getByRole('button', {
    name: 'Choose title, current title: Tron',
  });
  await waitFor(() => expect(selectedChooser).toHaveFocus());
  expect(Element.prototype.scrollIntoView).toHaveBeenCalledWith({
    block: 'start',
  });
});

it('opens the provider belonging to an explicitly chosen requirement', async () => {
  fixtures(
    titleWith([
      identity,
      steamGridDbReview,
    ]),
  );
  renderWorkspace('/enrichment?title=turok');
  expect(await screen.findByText('Target provider: igdb')).toBeInTheDocument();
  await userEvent.click(
    within(
      screen.getByRole('listitem', {
        name: 'SteamGridDB: SteamGridDB search found no game for this title.',
      }),
    ).getByRole('button', {
      name: 'Review SteamGridDB match',
    }),
  );
  expect(await screen.findByText('Target provider: steamgriddb')).toBeInTheDocument();
  expect(screen.getByText(/^Location:/).textContent).toContain(
    'editor=match&requirement=Identity%3Asteamgriddb',
  );
  expect(
    within(
      screen.getByRole('navigation', {
        name: 'Enrichment title queue',
      }),
    ).getByText('2 outstanding'),
  ).toBeInTheDocument();
});

it('normalizes a legacy editor deep link, preserving the rest of the review context', async () => {
  fixtures(titleWith(mixed.slice(0, 2)));
  renderWorkspace(
    '/enrichment?view=Automatic&title=turok&tab=artwork&issue=Artwork%3ABackdrop&role=Backdrop&activity=a1',
  );
  expect(await screen.findByText('Composition review: Backdrop')).toBeInTheDocument();
  expect(screen.getByText(/^Location:/).textContent).toBe(
    'Location: /enrichment?view=Automatic&title=turok&role=Backdrop&activity=a1&editor=artwork&requirement=Artwork%3ABackdrop',
  );
});

it('folds legacy editor params into an old task redirect', async () => {
  fixtures(
    titleWith([
      identity,
      steamGridDbReview,
    ]),
  );
  renderWorkspace('/enrichment/old-task?title=turok&tab=providers&issue=Identity%3Asteamgriddb');
  expect(await screen.findByText('Target provider: steamgriddb')).toBeInTheDocument();
  expect(screen.getByText(/^Location:/).textContent).toBe(
    'Location: /enrichment?title=turok&editor=match&requirement=Identity%3Asteamgriddb&activity=old-task',
  );
});

it('folds legacy editor params into an old title redirect', async () => {
  fixtures(
    titleWith([
      identity,
      metadata,
    ]),
  );
  renderWorkspace('/enrichment/titles/turok?tab=metadata&issue=Metadata%3ADescription');
  expect(await screen.findByText('Metadata editor')).toBeInTheDocument();
  expect(screen.getByText(/^Location:/).textContent).toBe(
    'Location: /enrichment?editor=metadata&requirement=Metadata%3ADescription&title=turok',
  );
  expect(currentRow()).toHaveAttribute('aria-label', `Description: ${metadataCopy}`);
});

it('keeps the linked requirement when switching editors for the same title', async () => {
  fixtures(
    titleWith([
      identity,
      metadata,
    ]),
  );
  renderWorkspace('/enrichment?title=turok');
  await screen.findByText('Inline provider decisions');
  await userEvent.click(
    within(
      screen.getByRole('listitem', {
        name: `Description: ${metadataCopy}`,
      }),
    ).getByRole('button', {
      name: 'Review description',
    }),
  );
  expect(await screen.findByText('Metadata editor')).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('tab', {
      name: 'Artwork',
    }),
  );
  expect(await screen.findByText(/^Composition review/)).toBeInTheDocument();
  expect(screen.getByText(/^Location:/).textContent).toBe(
    'Location: /enrichment?title=turok&editor=artwork&requirement=Metadata%3ADescription',
  );
  expect(currentRow()).toHaveAttribute('aria-label', `Description: ${metadataCopy}`);
});

it.each([
  [
    'view',
    'Location: /enrichment?view=Automatic&title=turok',
  ],
  [
    'search',
    'Location: /enrichment?q=Turok&title=turok',
  ],
  [
    'title',
    'Location: /enrichment?title=tron',
  ],
])('clears editor state when the %s changes', async (change, location) => {
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: [
          title,
          {
            ...title,
            titleId: 'tron',
            titleName: 'Tron',
          },
        ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
      HttpResponse.json({
        ...title,
        titleId: params.titleId,
        titleName: params.titleId === 'tron' ? 'Tron' : 'Turok',
      }),
    ),
  );
  renderWorkspace(
    '/enrichment?title=turok&editor=artwork&requirement=Artwork%3ABackdrop&role=Backdrop',
  );
  await screen.findByText('Composition review: Backdrop');
  if (change === 'view')
    await userEvent.click(
      screen.getByRole('tab', {
        name: /Can enrich automatically/,
      }),
    );
  else if (change === 'search') {
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Find a title in enrichment',
      }),
      'Turok',
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Search',
      }),
    );
  } else
    await userEvent.click(
      within(
        screen.getByRole('navigation', {
          name: 'Enrichment title queue',
        }),
      ).getByRole('button', {
        name: /Tron/,
      }),
    );
  expect(await screen.findByText(location)).toBeInTheDocument();
});

it('presents scenario 8.1 with outstanding counts, structured copy and only outstanding rows to review', async () => {
  fixtures(titleWith(chronoTriggerRequirements));
  renderWorkspace('/enrichment?title=turok');
  expect(await screen.findByText('Metadata editor')).toBeInTheDocument();
  const queue = screen.getByRole('navigation', {
    name: 'Enrichment title queue',
  });
  expect(within(queue).getByText('4 outstanding')).toBeInTheDocument();
  expect(within(queue).getByText(metadataCopy)).toBeInTheDocument();
  const current = currentRow() as HTMLElement;
  expect(current).toHaveAttribute('aria-label', `Description: ${metadataCopy}`);
  expect(
    within(current).getByRole('button', {
      name: 'Review description',
    }),
  ).toHaveAttribute('data-variant', 'filled');
  expect(
    within(current).getByRole('button', {
      name: 'Keep description empty',
    }),
  ).toBeInTheDocument();
  expect(
    screen.getByText('Enrichment is running for this title. Results appear here when it finishes.'),
  ).toBeInTheDocument();
  expect(
    screen.getByText('4 outstanding', {
      selector: '[tabindex="-1"]',
    }),
  ).toBeInTheDocument();
  const outstandingRows = screen
    .getAllByRole('listitem')
    .filter((row) => within(row).queryByText(/^(Blocked|Needs review|Processing)$/));
  expect(outstandingRows.map((row) => row.getAttribute('aria-label'))).toEqual([
    'SteamGridDB: SteamGridDB is enabled but has no credentials.',
    `Description: ${metadataCopy}`,
    'Backdrop: Saving your selected image.',
    'Logo: The artwork provider has no credentials.',
  ]);
  expect(
    screen.queryByRole('listitem', {
      name: /^Processing:/,
    }),
  ).not.toBeInTheDocument();
});

it('never lets satisfied or operation rows drive review, counts or reasons', async () => {
  fixtures(
    titleWith([
      unsatisfied('Operation:Processing', 'Processing', 'Working'),
    ]),
  );
  renderWorkspace('/enrichment?view=Processing&title=turok&requirement=Identity%3Aigdb');
  expect(await screen.findByText('No checklist actions · Processing')).toBeInTheDocument();
  expect(screen.getByText('Metadata editor')).toBeInTheDocument();
  const queue = screen.getByRole('navigation', {
    name: 'Enrichment title queue',
  });
  expect(within(queue).queryByText(/outstanding/)).not.toBeInTheDocument();
  expect(within(queue).queryByText(/Enrichment is running/)).not.toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: /^Review /,
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: /other outstanding/,
    }),
  ).not.toBeInTheDocument();
  expect(screen.queryByText(/intentionally empty or unavailable/)).not.toBeInTheDocument();
});

it.each([
  [
    true,
    'ProviderUnconfigured' as const,
    null,
  ],
  [
    false,
    'ProviderUnconfigured' as const,
    'An administrator can add SteamGridDB credentials',
  ],
  [
    false,
    'ProviderDisabled' as const,
    'An administrator can enable SteamGridDB',
  ],
])('offers provider settings only to Admins (Admin: %s, %s)', async (admin, cause, hint) => {
  permissions.canConfigureIntegrations = admin;
  fixtures(
    titleWith([
      unsatisfied('Identity:steamgriddb', 'Blocked', cause, [
        'ConfigureProvider',
      ]),
    ]),
  );
  renderWorkspace('/enrichment?view=Blocked&title=turok');
  await screen.findByText('Inline provider decisions');
  if (hint) {
    expect(screen.getByText(hint)).toBeInTheDocument();
    expect(
      screen.queryByRole('link', {
        name: 'Provider settings',
      }),
    ).not.toBeInTheDocument();
  } else
    expect(
      screen.getByRole('link', {
        name: 'Provider settings',
      }),
    ).toHaveAttribute('href', '/integrations');
});

it('continues without a provider only after confirmation, under the match revision the title shows', async () => {
  let unlinked = false;
  const unlink = vi.fn(async ({ request }: { request: Request }) => {
    expect(request.headers.get('If-Match')).toBe('"shown-rev"');
    unlinked = true;
    return HttpResponse.json({
      providerId: 'igdb',
      providerName: 'IGDB',
      isAvailable: true,
      capabilities: [
        'metadata',
      ],
      revision: 'unlinked-rev',
      state: 'Unlinked',
      metadataNeedsRefresh: false,
    });
  });
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: [
          title,
        ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/turok/enrichment-assessment`, () =>
      HttpResponse.json(
        unlinked
          ? titleWith([
              satisfied('Identity:igdb', 'ProviderSuppressed', [
                'ReviewProvider',
              ]),
            ])
          : title,
      ),
    ),
    http.get('http://localhost/api/titles/turok/provider-matches', () =>
      HttpResponse.json([
        {
          providerId: 'igdb',
          providerName: 'IGDB',
          isAvailable: true,
          capabilities: [
            'metadata',
          ],
          revision: 'match-rev',
          state: 'NeedsReview',
          metadataNeedsRefresh: false,
        },
      ]),
    ),
    http.delete('http://localhost/api/titles/turok/provider-matches/igdb', unlink),
  );
  const cache = renderWorkspace();
  cache.setQueryData(
    [
      'provider-matches',
      'turok',
    ],
    [
      {
        providerId: 'igdb',
        providerName: 'IGDB',
        isAvailable: true,
        capabilities: [
          'metadata',
        ],
        revision: 'shown-rev',
        state: 'NeedsReview',
        metadataNeedsRefresh: false,
      },
    ],
  );
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Continue without IGDB',
    }),
  );
  const dialog = await screen.findByRole('dialog', {
    name: 'Continue without IGDB?',
  });
  expect(
    within(dialog).getByText(
      'Automatic matching with IGDB stops for this title. You can find a match later.',
    ),
  ).toBeInTheDocument();
  await userEvent.click(
    within(dialog).getByRole('button', {
      name: 'Cancel',
    }),
  );
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(unlink).not.toHaveBeenCalled();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Continue without IGDB',
    }),
  );
  await userEvent.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Continue without IGDB?',
      }),
    ).getByRole('button', {
      name: 'Continue without IGDB',
    }),
  );
  const summary = await screen.findByText('Nothing outstanding · Continuing without IGDB');
  expect(unlink).toHaveBeenCalledOnce();
  // Nothing else is queued, so Next title is disabled and focus lands on the gap summary.
  expect(
    screen.getByRole('button', {
      name: 'Next title',
    }),
  ).toBeDisabled();
  await waitFor(() => expect(summary).toHaveFocus());
});

it('leaves an artwork role empty only after confirmation, under the role revision the title shows', async () => {
  const nothingUsable = unsatisfied('Artwork:Hero', 'NeedsReview', 'NothingUsable', [
    'ReviewArtwork',
    'LeaveEmpty',
  ]);
  let left = false;
  const leave = vi.fn(async ({ request }: { request: Request }) => {
    expect(request.headers.get('If-Match')).toBe('"shown-rev"');
    expect(await request.json()).toEqual({
      mode: 'Empty',
    });
    left = true;
    return HttpResponse.json({
      role: 'Hero',
      mode: 'Empty',
      revision: 'hero-rev-2',
      pinnedAssetId: null,
      pendingJobId: null,
    });
  });
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: [
          titleWith([
            nothingUsable,
          ]),
        ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/turok/enrichment-assessment`, () =>
      HttpResponse.json(
        titleWith([
          left
            ? satisfied('Artwork:Hero', 'LeftEmpty', [
                'ReviewArtwork',
              ])
            : nothingUsable,
        ]),
      ),
    ),
    // The role as the title shows it: the decisions read it when they mount.
    http.get('http://localhost/api/titles/turok/artwork', () =>
      HttpResponse.json({
        revision: 'family-rev',
        roles: [
          {
            role: 'Hero',
            mode: 'Automatic',
            revision: 'shown-rev',
            pinnedAssetId: null,
            pendingJobId: null,
          },
        ],
      }),
    ),
    http.put('http://localhost/api/titles/turok/artwork/Hero', leave),
  );
  renderWorkspace();
  expect(await screen.findByText('Composition review: Hero')).toBeInTheDocument();
  expect(currentRow()).toHaveAttribute(
    'aria-label',
    'Banner: No usable banner was found. Choose a saved image, upload one, or leave it empty.',
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Leave banner empty',
    }),
  );
  const dialog = await screen.findByRole('dialog', {
    name: 'Leave banner empty?',
  });
  expect(
    within(dialog).getByText(
      'The consumer app shows no banner for this title and automatic runs will not fill it. Use Return to automatic to undo.',
    ),
  ).toBeInTheDocument();
  await userEvent.click(
    within(dialog).getByRole('button', {
      name: 'Cancel',
    }),
  );
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(leave).not.toHaveBeenCalled();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Leave banner empty',
    }),
  );
  await userEvent.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Leave banner empty?',
      }),
    ).getByRole('button', {
      name: 'Leave banner empty',
    }),
  );
  const summary = await screen.findByText('Nothing outstanding · Banner left empty');
  expect(leave).toHaveBeenCalledOnce();
  // Nothing else is queued, so Next title is disabled and focus lands on the gap summary.
  expect(
    screen.getByRole('button', {
      name: 'Next title',
    }),
  ).toBeDisabled();
  await waitFor(() => expect(summary).toHaveFocus());
});

it('targets the provider behind a metadata row, never an identity reviewed earlier', async () => {
  fixtures(
    titleWith([
      satisfied('Identity:igdb', 'ProviderSuppressed', [
        'ReviewProvider',
      ]),
      steamGridDbReview,
      unsatisfied('Metadata:Genre', 'NeedsReview', 'ProviderSuppressed', [
        'ReviewMetadata',
        'AcceptOmission',
        'ReviewProvider',
      ]),
    ]),
  );
  renderWorkspace('/enrichment?title=turok');
  expect(await screen.findByText('Target provider: steamgriddb')).toBeInTheDocument();
  const genre = screen.getByRole('listitem', {
    name: 'Genre: IGDB is not linked for this title, so this field cannot be fetched.',
  });
  await userEvent.click(
    within(genre).getByRole('button', {
      name: 'Review genre',
    }),
  );
  expect(await screen.findByText('Metadata editor')).toBeInTheDocument();
  await userEvent.click(
    within(genre).getByRole('button', {
      name: 'Review IGDB match',
    }),
  );
  expect(await screen.findByText('Target provider: igdb')).toBeInTheDocument();
  expect(screen.getByText(/^Location:/).textContent).toBe(
    'Location: /enrichment?title=turok&editor=match&requirement=Metadata%3AGenre',
  );
});

it('keeps the queue selection and highlight when the curator keeps editing, then changes title once on Discard', async () => {
  const turok = titleWith([
    metadata,
  ]);
  const tron = {
    ...titleWith([
      metadata,
    ]),
    titleId: 'tron',
    titleName: 'Tron',
  };
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: [
          turok,
          tron,
        ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
      HttpResponse.json(params.titleId === 'tron' ? tron : turok),
    ),
  );
  renderGuardedWorkspace([
    '/enrichment?title=turok',
  ]);
  await userEvent.type(
    await screen.findByRole('textbox', {
      name: 'Description draft',
    }),
    'A deliberate edit',
  );
  await userEvent.click(
    within(queueNav()).getByRole('button', {
      name: /Tron/,
    }),
  );
  await userEvent.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).getByRole('button', {
      name: 'Keep editing',
    }),
  );
  expect(screen.getByText(/^Location:/).textContent).toBe('Location: /enrichment?title=turok');
  expect(
    within(queueNav()).getByRole('button', {
      name: /Turok/,
    }),
  ).toHaveAttribute('aria-current', 'true');
  expect(
    screen.getByRole('textbox', {
      name: 'Description draft',
    }),
  ).toHaveValue('A deliberate edit');
  await userEvent.click(
    within(queueNav()).getByRole('button', {
      name: /Tron/,
    }),
  );
  await userEvent.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).getByRole('button', {
      name: 'Discard',
    }),
  );
  expect(await screen.findByText('Location: /enrichment?title=tron')).toBeInTheDocument();
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
});

it('never starts the Next title page fetch when the curator keeps editing, and advances once on Discard', async () => {
  const cursors: (string | null)[] = [];
  const turok = titleWith([
    metadata,
  ]);
  server.use(
    http.get(endpoint, ({ request }) => {
      const cursor = new URL(request.url).searchParams.get('cursor');
      cursors.push(cursor);
      return HttpResponse.json({
        items: cursor
          ? [
              {
                ...turok,
                titleId: 'tron',
                titleName: 'Tron',
              },
            ]
          : [
              turok,
            ],
        hasNextPage: (cursor ? null : 'page-two') !== null,
        nextCursor: cursor ? null : 'page-two',
      });
    }),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
      HttpResponse.json({
        ...turok,
        titleId: params.titleId,
      }),
    ),
  );
  renderGuardedWorkspace([
    '/enrichment?title=turok',
  ]);
  await userEvent.type(
    await screen.findByRole('textbox', {
      name: 'Description draft',
    }),
    'A deliberate edit',
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Next title',
    }),
  );
  await userEvent.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).getByRole('button', {
      name: 'Keep editing',
    }),
  );
  expect(cursors).not.toContain('page-two');
  expect(screen.getByText(/^Location:/).textContent).toBe('Location: /enrichment?title=turok');
  expect(
    screen.getByRole('textbox', {
      name: 'Description draft',
    }),
  ).toHaveValue('A deliberate edit');
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Next title',
    }),
  );
  await userEvent.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).getByRole('button', {
      name: 'Discard',
    }),
  );
  expect(await screen.findByText('Location: /enrichment?title=tron')).toBeInTheDocument();
  expect(cursors.filter((cursor) => cursor === 'page-two')).toHaveLength(1);
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
});

it('guards browser Back with the same dialog while a draft is open', async () => {
  const turok = titleWith([
    metadata,
  ]);
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: [
          turok,
          {
            ...turok,
            titleId: 'tron',
            titleName: 'Tron',
          },
        ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
      HttpResponse.json({
        ...turok,
        titleId: params.titleId,
      }),
    ),
  );
  const router = renderGuardedWorkspace([
    '/enrichment?title=tron',
    '/enrichment?title=turok',
  ]);
  await userEvent.type(
    await screen.findByRole('textbox', {
      name: 'Description draft',
    }),
    'A deliberate edit',
  );
  await act(async () => {
    await router.navigate(-1);
  });
  await userEvent.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).getByRole('button', {
      name: 'Keep editing',
    }),
  );
  expect(router.state.location.search).toBe('?title=turok');
  expect(
    screen.getByRole('textbox', {
      name: 'Description draft',
    }),
  ).toHaveValue('A deliberate edit');
});

it('focuses the chosen title heading, but not on first load', async () => {
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: [
          title,
          {
            ...title,
            titleId: 'tron',
            titleName: 'Tron',
          },
        ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
      HttpResponse.json({
        ...title,
        titleId: params.titleId,
        titleName: params.titleId === 'tron' ? 'Tron' : 'Turok',
      }),
    ),
  );
  renderWorkspace('/enrichment?title=turok');
  expect(
    await screen.findByRole('heading', {
      level: 2,
      name: 'Turok',
    }),
  ).not.toHaveFocus();
  await userEvent.click(
    within(queueNav()).getByRole('button', {
      name: /Tron/,
    }),
  );
  await waitFor(() =>
    expect(
      screen.getByRole('heading', {
        level: 2,
        name: 'Tron',
      }),
    ).toHaveFocus(),
  );
});

describe('run panel', () => {
  const tron = {
    ...title,
    titleId: 'tron',
    titleName: 'Tron',
  };
  const reviewItem = {
    titleId: 'tron',
    titleName: 'Tron',
    completion: 'Done',
    metadataOutcome: 'Succeeded',
    followUpJobId: null,
    roles: [],
    now: {
      needsReview: true,
      canEnrichAutomatically: false,
      hasActiveWork: false,
      hasOperationalBlocker: false,
    },
  };

  it('shows only while a run is the activity, and dismissing it removes the activity', async () => {
    fixtures();
    renderWorkspace('/enrichment?view=NeedsReview&title=turok&activity=run-1');
    const panel = await screen.findByRole('region', {
      name: 'Enrichment run',
    });
    await userEvent.click(
      within(panel).getByRole('button', {
        name: 'Dismiss run',
      }),
    );
    expect(
      await screen.findByText('Location: /enrichment?view=NeedsReview&title=turok'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('region', {
        name: 'Enrichment run',
      }),
    ).not.toBeInTheDocument();
  });

  it('opens a title from the run in the workbench while keeping the run', async () => {
    server.use(
      http.get(endpoint, () =>
        HttpResponse.json({
          items: [
            title,
          ],
          hasNextPage: false,
          nextCursor: null,
        }),
      ),
      http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
        HttpResponse.json(params.titleId === 'tron' ? tron : title),
      ),
      http.get(`${jobEndpoint}/run-1`, () =>
        HttpResponse.json(
          artworkJobFixture(
            finishedRun('run-1', {
              review: 1,
            }),
          ),
        ),
      ),
      http.get(`${jobEndpoint}/run-1/artwork-summary`, () => {
        const { roles, review, failed, unfinished, working } = finishedRun('run-1', {
          review: 1,
        });
        return HttpResponse.json({
          roles,
          review,
          failed,
          unfinished,
          working,
        });
      }),
      http.get(`${jobEndpoint}/run-1/artwork-items`, () =>
        HttpResponse.json({
          hasNextPage: false,
          items: [
            reviewItem,
          ],
          nextCursor: null,
        }),
      ),
    );
    renderWorkspace('/enrichment?title=turok&activity=run-1');
    const panel = await screen.findByRole('region', {
      name: 'Enrichment run',
    });
    await userEvent.click(
      await within(panel).findByRole('button', {
        name: 'Review 1 in this run',
      }),
    );
    await userEvent.click(
      await within(panel).findByRole('button', {
        name: 'Tron',
      }),
    );
    expect(
      await screen.findByText('Location: /enrichment?title=tron&activity=run-1'),
    ).toBeInTheDocument();
    expect(
      await screen.findByRole('heading', {
        level: 2,
        name: 'Tron',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('region', {
        name: 'Enrichment run',
      }),
    ).toBeInTheDocument();
  });

  it('points the activity at the continuation run a retry queues', async () => {
    fixtures();
    server.use(
      http.get(`${jobEndpoint}/run-1`, () =>
        HttpResponse.json(
          artworkJobFixture(
            finishedRun('run-1', {
              phase: 'CompletedWithErrors',
              failed: 2,
            }),
          ),
        ),
      ),
      http.get(`${jobEndpoint}/run-1/artwork-summary`, () => {
        const { roles, review, failed, unfinished, working } = finishedRun('run-1', {
          phase: 'CompletedWithErrors',
          failed: 2,
        });
        return HttpResponse.json({
          roles,
          review,
          failed,
          unfinished,
          working,
        });
      }),
      http.post(runEndpoint, () =>
        HttpResponse.json(
          {
            jobId: 'run-2',
          },
          {
            status: 202,
          },
        ),
      ),
    );
    renderWorkspace('/enrichment?title=turok&activity=run-1');
    const panel = await screen.findByRole('region', {
      name: 'Enrichment run',
    });
    await userEvent.click(
      await within(panel).findByRole('button', {
        name: 'Retry 2 failed',
      }),
    );
    expect(
      await screen.findByText('Location: /enrichment?title=turok&activity=run-2'),
    ).toBeInTheDocument();
  });

  it('drops a title from the run’s review count after a workbench decision resolves it', async () => {
    let accepted = false;
    const omitted = titleWith(
      [
        satisfied('Metadata:Description', 'AcceptedOmission', [
          'ReviewMetadata',
        ]),
      ],
      {
        revision: 'rev-2',
        acceptedOmissions: [
          'Description',
        ],
      },
    );
    server.use(
      http.get(endpoint, () =>
        HttpResponse.json({
          items: [
            accepted
              ? omitted
              : titleWith([
                  metadata,
                ]),
          ],
          hasNextPage: false,
          nextCursor: null,
        }),
      ),
      http.get(`${titlesEndpoint}/turok/enrichment-assessment`, () =>
        HttpResponse.json(
          accepted
            ? omitted
            : titleWith([
                metadata,
              ]),
        ),
      ),
      http.put(`${titlesEndpoint}/turok/enrichment-omissions/Description`, () => {
        accepted = true;
        return HttpResponse.json(written);
      }),
      http.get(`${jobEndpoint}/run-1`, () =>
        HttpResponse.json(
          artworkJobFixture(
            finishedRun('run-1', {
              review: accepted ? 0 : 1,
            }),
          ),
        ),
      ),
      http.get(`${jobEndpoint}/run-1/artwork-summary`, () => {
        const { roles, review, failed, unfinished, working } = finishedRun('run-1', {
          review: accepted ? 0 : 1,
        });
        return HttpResponse.json({
          roles,
          review,
          failed,
          unfinished,
          working,
        });
      }),
    );
    renderWorkspace('/enrichment?view=All&title=turok&activity=run-1');
    const panel = await screen.findByRole('region', {
      name: 'Enrichment run',
    });
    expect(
      await within(panel).findByText('1 needs review now · 0 failed · 0 unfinished'),
    ).toBeInTheDocument();
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Keep description empty',
      }),
    );
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Keep description empty?',
        }),
      ).getByRole('button', {
        name: 'Keep description empty',
      }),
    );
    expect(
      await within(panel).findByText('0 need review now · 0 failed · 0 unfinished'),
    ).toBeInTheDocument();
    expect(
      within(panel).queryByRole('button', {
        name: /^Review \d+ in this run$/,
      }),
    ).not.toBeInTheDocument();
  });
});

describe('draft-safe selection', () => {
  const turok = titleWith([
    metadata,
  ]);
  const tron = {
    ...titleWith([
      metadata,
    ]),
    titleId: 'tron',
    titleName: 'Tron',
  };
  const serveTitles = (order: () => EnrichmentAssessmentDto[]) =>
    server.use(
      http.get(endpoint, () =>
        HttpResponse.json({
          items: order(),
          hasNextPage: false,
          nextCursor: null,
        }),
      ),
      http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
        HttpResponse.json(params.titleId === 'tron' ? tron : turok),
      ),
    );
  const discardDialogs = () =>
    screen.queryAllByRole('dialog', {
      name: 'Discard unsaved changes?',
    });

  it('shows one dialog and drops the draft when the Navbar reopens the workspace and the curator discards', async () => {
    serveTitles(() => [
      turok,
      tron,
    ]);
    const router = renderGuardedWorkspace([
      '/enrichment?title=turok',
    ]);
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Description draft',
      }),
      'A deliberate edit',
    );
    await act(async () => {
      await router.navigate('/enrichment');
    });
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard unsaved changes?',
        }),
      ).getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(await screen.findByText('Location: /enrichment?title=turok')).toBeInTheDocument();
    await waitFor(() =>
      expect(
        screen.getByRole('textbox', {
          name: 'Description draft',
        }),
      ).toHaveValue(''),
    );
    expect(discardDialogs()).toHaveLength(0);
  });

  it('drops the draft after the curator re-chooses the selected queue row and discards', async () => {
    serveTitles(() => [
      turok,
      tron,
    ]);
    renderGuardedWorkspace([
      '/enrichment?title=turok',
    ]);
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Description draft',
      }),
      'A deliberate edit',
    );
    await userEvent.click(
      within(queueNav()).getByRole('button', {
        name: /Turok/,
      }),
    );
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard unsaved changes?',
        }),
      ).getByRole('button', {
        name: 'Discard',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('textbox', {
          name: 'Description draft',
        }),
      ).toHaveValue(''),
    );
    expect(screen.getByText('Location: /enrichment?title=turok')).toBeInTheDocument();
    expect(discardDialogs()).toHaveLength(0);
  });

  it('applies a queue choice only once its navigation goes through', async () => {
    server.use(
      http.get(endpoint, ({ request }) =>
        new URL(request.url).searchParams.has('cursor')
          ? new HttpResponse(null, {
              status: 503,
            })
          : HttpResponse.json({
              items: [
                tron,
                turok,
              ],
              hasNextPage: true,
              nextCursor: 'page-two',
            }),
      ),
      http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
        HttpResponse.json(params.titleId === 'tron' ? tron : turok),
      ),
    );
    renderGuardedWorkspace([
      '/enrichment?title=turok',
    ]);
    await screen.findByRole('textbox', {
      name: 'Description draft',
    });
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Next title',
      }),
    );
    expect(
      await screen.findByText('Could not load the next title. Try again.'),
    ).toBeInTheDocument();
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Description draft',
      }),
      'A deliberate edit',
    );
    await userEvent.click(
      within(queueNav()).getByRole('button', {
        name: /Tron/,
      }),
    );
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard unsaved changes?',
        }),
      ).getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(screen.getByText('Could not load the next title. Try again.')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', {
        level: 2,
        name: 'Turok',
      }),
    ).toBeInTheDocument();
    await userEvent.click(
      within(queueNav()).getByRole('button', {
        name: /Tron/,
      }),
    );
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard unsaved changes?',
        }),
      ).getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(await screen.findByText('Location: /enrichment?title=tron')).toBeInTheDocument();
    const heading = await screen.findByRole('heading', {
      level: 2,
      name: 'Tron',
    });
    await waitFor(() => expect(heading).toHaveFocus());
    // The closed dialog must not hand focus back to the queue row afterwards.
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(heading).toHaveFocus();
    expect(screen.queryByText('Could not load the next title. Try again.')).not.toBeInTheDocument();
  });

  it('keeps the implicitly selected title and its draft when the curator keeps editing and a poll reorders the queue', async () => {
    delayedRoute.replace = true;
    let reordered = false;
    serveTitles(() =>
      reordered
        ? [
            tron,
            turok,
          ]
        : [
            turok,
            tron,
          ],
    );
    renderGuardedWorkspace([
      '/enrichment',
    ]);
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Description draft',
      }),
      'A deliberate edit',
    );
    await userEvent.click(
      within(queueNav()).getByRole('button', {
        name: /Tron/,
      }),
    );
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard unsaved changes?',
        }),
      ).getByRole('button', {
        name: 'Keep editing',
      }),
    );
    // From here the implicit title write would really navigate, so it must wait for the draft rather than prompt.
    delayedRoute.replace = false;
    reordered = true;
    await act(async () => {
      await caches[caches.length - 1].invalidateQueries({
        queryKey: [
          'enrichment-workspace',
          'list',
        ],
      });
    });
    await waitFor(() =>
      expect(
        within(queueNav())
          .getAllByText(/^(Turok|Tron)$/)
          .map((name) => name.textContent),
      ).toEqual([
        'Tron',
        'Turok',
      ]),
    );
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(
      screen.getByRole('heading', {
        level: 2,
        name: 'Turok',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('textbox', {
        name: 'Description draft',
      }),
    ).toHaveValue('A deliberate edit');
    expect(
      within(queueNav()).getByRole('button', {
        name: /Turok/,
      }),
    ).toHaveAttribute('aria-current', 'true');
    expect(screen.getByText('Location: /enrichment')).toBeInTheDocument();
    expect(discardDialogs()).toHaveLength(0);
  });
});

describe('layout', () => {
  it.each([
    [
      false,
      'rail',
    ],
    [
      true,
      'drawer',
    ],
  ] as const)('follows the one JS breakpoint (mobile: %s) with a %s layout instead of a parallel media rule', async (mobile, layout) => {
    viewport.mobile = mobile;
    fixtures();
    renderWorkspace('/enrichment?title=turok');
    await screen.findByText('Inline provider decisions');
    const anchor = mobile
      ? screen.getByRole('button', {
          name: 'Choose title, current title: Turok',
        })
      : queueNav();
    expect(anchor.closest('[data-queue]')).toHaveAttribute('data-queue', layout);
    const styles = readFileSync(`${import.meta.dirname}/EnrichmentWorkspace.module.css`, 'utf8');
    expect(styles).not.toMatch(/@media/);
    expect(styles).toContain(
      ".layout[data-queue='rail'] { grid-template-columns: minmax(230px, 290px) minmax(0, 1fr);",
    );
    expect(styles).toContain(".layout[data-queue='rail'] .queue { position: sticky;");
  });
});

it('counts only the view’s titles in the queue header, not a title kept after it left the view', async () => {
  let resolved = false;
  server.use(
    http.get(`${endpoint}/stats`, () =>
      HttpResponse.json({
        ...counts,
        needsReview: resolved ? 0 : 1,
      }),
    ),
    http.get(endpoint, () =>
      HttpResponse.json({
        items: resolved
          ? []
          : [
              title,
            ],
        hasNextPage: false,
        nextCursor: null,
      }),
    ),
    http.get(`${titlesEndpoint}/turok/enrichment-assessment`, () =>
      HttpResponse.json(
        resolved
          ? titleWith([], {
              revision: 'rev-2',
            })
          : title,
      ),
    ),
  );
  const cache = renderWorkspace('/enrichment?title=turok', false);
  expect(await within(queueNav()).findByText('1 title')).toBeInTheDocument();
  resolved = true;
  await act(async () => {
    await cache.invalidateQueries({
      queryKey: [
        'enrichment-workspace',
      ],
    });
  });
  expect(await within(queueNav()).findByText('No longer in this view')).toBeInTheDocument();
  expect(within(queueNav()).getByText('0 titles')).toBeInTheDocument();
  expect(
    within(queueNav()).getByRole('button', {
      name: /Turok/,
    }),
  ).toHaveAttribute('aria-current', 'true');
  await waitFor(() =>
    expect(
      within(
        screen.getByRole('tab', {
          name: /Needs review/,
        }),
      ).getByText('0'),
    ).toBeInTheDocument(),
  );
});

describe('keyboard', () => {
  const turok = titleWith([
    metadata,
  ]);
  const tron = {
    ...titleWith([
      metadata,
    ]),
    titleId: 'tron',
    titleName: 'Tron',
  };
  const serveTitles = (
    items: EnrichmentAssessmentDto[] = [
      turok,
      tron,
    ],
  ) =>
    server.use(
      http.get(endpoint, () =>
        HttpResponse.json({
          items,
          hasNextPage: false,
          nextCursor: null,
        }),
      ),
      http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
        HttpResponse.json(items.find((item) => item.titleId === params.titleId) ?? turok),
      ),
    );
  const location = () => screen.getByText(/^Location:/).textContent;
  const focusHeading = async (name: string) => {
    const heading = await screen.findByRole('heading', {
      level: 2,
      name,
    });
    act(() => heading.focus());
    return heading;
  };
  const discardDialogs = () =>
    screen.queryAllByRole('dialog', {
      name: 'Discard unsaved changes?',
    });

  it('moves the queue selection with j and k like a click, focusing the chosen title', async () => {
    serveTitles();
    renderWorkspace('/enrichment?title=turok');
    await focusHeading('Turok');
    await userEvent.keyboard('j');
    expect(await screen.findByText('Location: /enrichment?title=tron')).toBeInTheDocument();
    await waitFor(() =>
      expect(
        screen.getByRole('heading', {
          level: 2,
          name: 'Tron',
        }),
      ).toHaveFocus(),
    );
    expect(
      within(queueNav()).getByRole('button', {
        name: /Tron/,
      }),
    ).toHaveAttribute('aria-current', 'true');
    await userEvent.keyboard('j');
    expect(location()).toBe('Location: /enrichment?title=tron');
    await userEvent.keyboard('k');
    expect(await screen.findByText('Location: /enrichment?title=turok')).toBeInTheDocument();
    await waitFor(() =>
      expect(
        screen.getByRole('heading', {
          level: 2,
          name: 'Turok',
        }),
      ).toHaveFocus(),
    );
    await userEvent.keyboard('k');
    expect(location()).toBe('Location: /enrichment?title=turok');
  });

  it('asks once before j leaves a draft: Keep editing keeps the selection, highlight and draft; Discard moves once', async () => {
    serveTitles();
    renderGuardedWorkspace([
      '/enrichment?title=turok',
    ]);
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Description draft',
      }),
      'A deliberate edit',
    );
    await focusHeading('Turok');
    await userEvent.keyboard('j');
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard unsaved changes?',
        }),
      ).getByRole('button', {
        name: 'Keep editing',
      }),
    );
    await waitFor(() => expect(discardDialogs()).toHaveLength(0));
    expect(location()).toBe('Location: /enrichment?title=turok');
    expect(
      within(queueNav()).getByRole('button', {
        name: /Turok/,
      }),
    ).toHaveAttribute('aria-current', 'true');
    expect(
      screen.getByRole('textbox', {
        name: 'Description draft',
      }),
    ).toHaveValue('A deliberate edit');
    await focusHeading('Turok');
    await userEvent.keyboard('j');
    expect(discardDialogs()).toHaveLength(1);
    await userEvent.click(
      within(discardDialogs()[0]).getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(await screen.findByText('Location: /enrichment?title=tron')).toBeInTheDocument();
    await waitFor(() => expect(discardDialogs()).toHaveLength(0));
  });

  it('advances with n through the same path as Next title, never fetching the next page when the curator keeps editing', async () => {
    const cursors: (string | null)[] = [];
    server.use(
      http.get(endpoint, ({ request }) => {
        const cursor = new URL(request.url).searchParams.get('cursor');
        cursors.push(cursor);
        return HttpResponse.json({
          items: cursor
            ? [
                tron,
              ]
            : [
                turok,
              ],
          hasNextPage: (cursor ? null : 'page-two') !== null,
          nextCursor: cursor ? null : 'page-two',
        });
      }),
      http.get(`${titlesEndpoint}/:titleId/enrichment-assessment`, ({ params }) =>
        HttpResponse.json(params.titleId === 'tron' ? tron : turok),
      ),
    );
    renderGuardedWorkspace([
      '/enrichment?title=turok',
    ]);
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Description draft',
      }),
      'A deliberate edit',
    );
    await focusHeading('Turok');
    await userEvent.keyboard('n');
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard unsaved changes?',
        }),
      ).getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(cursors).not.toContain('page-two');
    expect(location()).toBe('Location: /enrichment?title=turok');
    await focusHeading('Turok');
    await userEvent.keyboard('n');
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard unsaved changes?',
        }),
      ).getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(await screen.findByText('Location: /enrichment?title=tron')).toBeInTheDocument();
    expect(cursors.filter((cursor) => cursor === 'page-two')).toHaveLength(1);
    await waitFor(() => expect(discardDialogs()).toHaveLength(0));
  });

  it('opens the focused row, else the current requirement, with Enter and leaves focused controls their own Enter', async () => {
    fixtures(titleWith(chronoTriggerRequirements));
    renderWorkspace('/enrichment?title=turok');
    await focusHeading('Turok');
    await userEvent.keyboard('{Enter}');
    expect(
      await screen.findByText(
        'Location: /enrichment?title=turok&editor=metadata&requirement=Metadata%3ADescription',
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('listitem', {
        name: `Description: ${metadataCopy}`,
      }),
    ).toHaveFocus();
    act(() =>
      screen
        .getByRole('listitem', {
          name: 'IGDB: Matched',
        })
        .focus(),
    );
    await userEvent.keyboard('{Enter}');
    expect(
      await screen.findByText(
        'Location: /enrichment?title=turok&editor=match&requirement=Identity%3Aigdb',
      ),
    ).toBeInTheDocument();
    expect(await screen.findByText('Target provider: igdb')).toBeInTheDocument();
    // The blocked SteamGridDB row offers only provider settings: Enter neither reviews nor follows the link.
    act(() =>
      screen
        .getByRole('listitem', {
          name: /^SteamGridDB:/,
        })
        .focus(),
    );
    await userEvent.keyboard('{Enter}');
    expect(location()).toBe(
      'Location: /enrichment?title=turok&editor=match&requirement=Identity%3Aigdb',
    );
    act(() =>
      screen
        .getByRole('button', {
          name: 'Keep description empty',
        })
        .focus(),
    );
    await userEvent.keyboard('{Enter}');
    const confirmation = await screen.findByRole('dialog', {
      name: 'Keep description empty?',
    });
    expect(location()).toBe(
      'Location: /enrichment?title=turok&editor=match&requirement=Identity%3Aigdb',
    );
    await userEvent.click(
      within(confirmation).getByRole('button', {
        name: 'Cancel',
      }),
    );
  });

  it('leaves Enter to the open editor and to sliders, never reviewing from inside them', async () => {
    fixtures(
      titleWith([
        metadata,
        unsatisfied('Artwork:Backdrop', 'NeedsReview', 'ArtworkDecisionRequired', [
          'ReviewArtwork',
        ]),
      ]),
    );
    renderWorkspace('/enrichment?title=turok&editor=artwork&role=Backdrop');
    await screen.findByText('Composition review: Backdrop');
    const start = 'Location: /enrichment?title=turok&editor=artwork&role=Backdrop';
    for (const name of [
      'Focal point',
      'Compose artwork',
    ]) {
      const target =
        name === 'Focal point'
          ? screen.getByRole('slider', {
              name,
            })
          : screen.getByRole('region', {
              name,
            });
      expect(target.closest('[role="tabpanel"]')).not.toBeNull();
      act(() => target.focus());
      await userEvent.keyboard('{Enter}');
      expect(location()).toBe(start);
    }
    await focusHeading('Turok');
    await userEvent.keyboard('{Enter}');
    await waitFor(() =>
      expect(location()).toMatch(/editor=metadata.*requirement=Metadata%3ADescription/),
    );
  });

  it('lists the shortcuts from ? or the header link and returns focus when the list closes', async () => {
    serveTitles();
    renderWorkspace('/enrichment?title=turok');
    const heading = await focusHeading('Turok');
    await userEvent.keyboard('?');
    const help = await screen.findByRole('dialog', {
      name: 'Keyboard shortcuts',
    });
    expect(
      within(help)
        .getAllByRole('row')
        .slice(1)
        .map((row) => row.textContent),
    ).toEqual([
      'jSelect the next title in the queue',
      'kSelect the previous title in the queue',
      'EnterOpen the current requirement',
      'nNext title',
      '?Show keyboard shortcuts',
    ]);
    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(heading).toHaveFocus());
    const link = screen.getByRole('button', {
      name: 'Show keyboard shortcuts',
    });
    expect(link).toHaveTextContent('Keyboard shortcuts ?');
    expect(within(link).getByText('?').tagName).toBe('KBD');
    await userEvent.click(link);
    expect(
      await screen.findByRole('dialog', {
        name: 'Keyboard shortcuts',
      }),
    ).toBeInTheDocument();
  });

  it('keeps shortcuts inert while typing, while a dialog is open, and outside the workspace', async () => {
    serveTitles();
    renderWorkspace('/enrichment?title=turok');
    const heading = await focusHeading('Turok');
    const search = screen.getByRole('textbox', {
      name: 'Find a title in enrichment',
    });
    await userEvent.click(search);
    await userEvent.keyboard('jn?');
    expect(search).toHaveValue('jn?');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    fireEvent.keyDown(document.body, {
      key: 'j',
    });
    fireEvent.keyDown(screen.getByText(/^Location:/), {
      key: 'n',
    });
    expect(location()).toBe('Location: /enrichment?title=turok');
    act(() => heading.focus());
    await userEvent.keyboard('?');
    const help = await screen.findByRole('dialog', {
      name: 'Keyboard shortcuts',
    });
    fireEvent.keyDown(heading, {
      key: 'j',
    });
    fireEvent.keyDown(heading, {
      key: 'n',
    });
    fireEvent.keyDown(heading, {
      key: 'Enter',
    });
    expect(location()).toBe('Location: /enrichment?title=turok');
    expect(screen.getAllByRole('dialog')).toEqual([
      help,
    ]);
  });

  it('on mobile, n moves to the next title and focuses the title chooser', async () => {
    viewport.mobile = true;
    serveTitles();
    renderWorkspace('/enrichment?title=turok');
    await focusHeading('Turok');
    await userEvent.keyboard('n');
    expect(await screen.findByText('Location: /enrichment?title=tron')).toBeInTheDocument();
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Choose title, current title: Tron',
        }),
      ).toHaveFocus(),
    );
  });
});
