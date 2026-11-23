import { MantineProvider } from '@mantine/core';
import {
  type CatalogTitle,
  client,
  type EnrichmentWorkbenchCounts,
  type SetEnrichmentOmissionRequest,
  type TitleProviderMatch,
} from '@romd/admin-api-client';
import { CatalogFixture } from '@romd/consumer-ui/testing';
import { adminCssVariablesResolver } from '@romd/foundation/theme';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render as renderRaw, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { DraftGuardProvider } from '../../components/Administration/DraftGuard';
import { AuthContext } from '../../contexts/AuthContext';
import { providerMatchKeys } from '../../hooks/api/titleCache';
import { catalogSearchKeys } from '../../hooks/api/useCatalogSearch';
import { enrichmentWorkspaceKeys } from '../../hooks/api/useEnrichmentWorkspace';
import { authFixtures } from '../../test/msw/fixtures/auth';
import {
  assessmentFixture,
  satisfied,
  unsatisfied,
  withRequirements,
  writtenTitleFixture,
} from '../../test/msw/fixtures/enrichment';
import { server } from '../../test/msw/server';
import { render } from '../../test/utils/render';
import { finalPage } from '../../test/workspacePages';
import { theme } from '../../theme';
import { Catalog } from './index';

vi.mock('@mantine/hooks', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@mantine/hooks')>()),
  useElementSize: () => ({
    ref: () => {},
    width: 1200,
    height: 45,
  }),
}));

beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
});

const api = 'http://localhost/api';

function mockProviderReview() {
  const matchIssue = unsatisfied('Identity:igdb', 'NeedsReview', 'MatchDecisionRequired', [
    'ReviewProvider',
    'ContinueWithoutProvider',
  ]);
  const metadataIssue = unsatisfied('Metadata:Description', 'NeedsReview', 'NotProvided', [
    'ReviewMetadata',
  ]);
  const titles = [
    assessmentFixture(withRequirements(matchIssue, metadataIssue)),
    assessmentFixture(withRequirements(matchIssue), {
      titleId: 'earthbound',
      titleName: 'EarthBound',
    }),
    assessmentFixture(withRequirements(matchIssue), {
      titleId: 'metroid',
      titleName: 'Super Metroid',
    }),
  ];
  const record = {
    id: '100',
    name: 'Original game',
    url: 'https://example.com/original',
    year: 1990,
    platforms: [
      'Super Nintendo',
    ],
    releases: [
      {
        year: 1990,
        platform: 'Arcade',
      },
      {
        year: 1995,
        platform: '  SUPER Nintendo ',
      },
      {
        year: 1995,
        platform: 'Super Nintendo',
      },
    ],
  };
  const release = {
    id: '101',
    name: 'Modern rerelease',
    url: 'https://example.com/modern',
    year: 2025,
    platforms: [
      'Windows',
    ],
  };
  const match: TitleProviderMatch = {
    providerId: 'igdb',
    providerName: 'IGDB',
    isAvailable: true,
    capabilities: [
      'metadata',
    ],
    revision: 'match-1',
    state: 'NeedsReview',
    externalId: null,
    game: null,
    confidence: null,
    lastError: null,
    metadataNeedsRefresh: false,
    decision: {
      state: 'Ambiguous',
      explanation: 'Two related games',
      provenance: 'ProviderSearch',
      inputFingerprint: 'local-1',
      candidates: [
        {
          game: record,
          score: 90,
          eligible: true,
          evidence: [
            'Matching system',
          ],
          conflicts: [],
        },
        {
          game: release,
          score: 75,
          eligible: false,
          evidence: [],
          conflicts: [
            'Different system',
          ],
        },
      ],
    },
  };
  const saveDecision = vi.fn();
  server.use(
    http.get(`${api}/systems`, () => HttpResponse.json(finalPage([]))),
    http.get(`${api}/enrichment/workbench/stats`, () =>
      HttpResponse.json<EnrichmentWorkbenchCounts>({
        total: 3,
        needsReview: 3,
        automatic: 0,
        processing: 0,
        blocked: 0,
      }),
    ),
    http.get(`${api}/enrichment/workbench`, () => HttpResponse.json(finalPage(titles))),
    http.get(`${api}/titles/:id/enrichment-assessment`, ({ params }) =>
      HttpResponse.json(titles.find((title) => title.titleId === params.id)),
    ),
    http.get(`${api}/titles/:id/provider-matches`, () =>
      HttpResponse.json([
        match,
      ]),
    ),
    http.get(`${api}/metadata-providers/igdb/games`, ({ request }) =>
      HttpResponse.json({
        items:
          new URL(request.url).searchParams.get('query') === 'Empty search'
            ? []
            : [
                release,
              ],
        limit: 50,
        completeness: 'Complete',
      }),
    ),
    http.get(`${api}/titles/:id/artwork`, () =>
      HttpResponse.json({
        roles: [],
        revision: 'art-1',
      }),
    ),
    http.put(`${api}/titles/chrono-trigger/provider-matches/igdb`, async ({ request }) => {
      saveDecision(request.headers.get('If-Match'), await request.json());
      titles[0] = assessmentFixture(withRequirements(metadataIssue), {
        hasActiveWork: true,
      });
      return HttpResponse.json({
        ...match,
        game: record,
        externalId: record.id,
        pendingJobId: 'work-1',
      });
    }),
  );
  return {
    titles,
    match,
    metadataIssue,
    saveDecision,
  };
}

async function openProviderReview() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const router = createMemoryRouter(
    [
      {
        path: '/catalog',
        element: (
          <DraftGuardProvider>
            <Catalog />
          </DraftGuardProvider>
        ),
      },
    ],
    {
      initialEntries: [
        '/catalog?view=review',
        '/catalog?view=review&q=Chrono',
      ],
    },
  );
  renderRaw(
    <MantineProvider
      cssVariablesResolver={adminCssVariablesResolver}
      theme={theme}
      env="test"
    >
      <QueryClientProvider client={queryClient}>
        <AuthContext.Provider
          value={{
            user: authFixtures.currentUser,
            isAuthenticated: true,
            isLoading: false,
            error: null,
            retry: async () => {},
            login: async () => {},
            logout: async () => {},
          }}
        >
          <CatalogFixture>
            <RouterProvider router={router} />
          </CatalogFixture>
        </AuthContext.Provider>
      </QueryClientProvider>
    </MantineProvider>,
  );
  const user = userEvent.setup();
  await user.click(
    await screen.findByRole('button', {
      name: 'Start review',
    }),
  );
  const inspector = await screen.findByRole('complementary', {
    name: 'Chrono Trigger',
  });
  const panel = within(inspector);
  const candidate = await panel.findByRole('radio', {
    name: 'Select Original game',
  });
  return {
    user,
    queryClient,
    router,
    panel,
    inspector,
    candidate,
  };
}

it('starts review, compares system releases, restores suggestions after lookup, and continues', async () => {
  const { saveDecision } = mockProviderReview();
  const user = userEvent.setup();
  render(<Catalog />, {
    auth: {
      user: authFixtures.currentUser,
      isAuthenticated: true,
      isLoading: false,
      error: null,
      retry: async () => {},
      login: async () => {},
      logout: async () => {},
    },
    routerOptions: {
      initialEntries: [
        '/catalog?view=review',
      ],
    },
  });

  await user.click(
    await screen.findByRole('button', {
      name: 'Start review',
    }),
  );
  const review = within(
    await screen.findByRole('complementary', {
      name: 'Chrono Trigger',
    }),
  );
  expect(await review.findByText('Different system')).toBeVisible();
  expect(review.getByText('Super Nintendo release · 1995')).toBeVisible();
  expect(review.getByText('First released 1990')).toBeVisible();
  const original = review.getByRole('radio', {
    name: 'Select Original game',
  });
  screen
    .getByRole('button', {
      name: 'Review Chrono Trigger',
    })
    .focus();
  await user.keyboard('{Enter}');
  expect(original).toHaveFocus();

  const findAnother = review.getByText('Find another match');
  await user.click(findAnother);
  await user.click(
    review.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  expect(await review.findByText('1 match found')).toBeVisible();
  expect(
    review.getByRole('radio', {
      name: 'Select Modern rerelease',
    }),
  ).toBeVisible();
  expect(review.queryByText('Different system')).not.toBeInTheDocument();
  const search = review.getByRole('textbox', {
    name: 'Game name',
  });
  await user.clear(search);
  await user.type(search, 'Empty search');
  await user.click(
    review.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  expect(await review.findByText('No matches found')).toBeVisible();
  expect(original).not.toBeInTheDocument();
  await user.click(
    review.getByRole('button', {
      name: 'Show original suggestions',
    }),
  );
  expect(findAnother).toHaveFocus();
  expect(
    review.getByRole('radio', {
      name: 'Select Original game',
    }),
  ).not.toBeChecked();
  expect(
    review.getByRole('button', {
      name: 'Confirm match',
    }),
  ).toBeDisabled();
  await user.click(
    review.getByRole('radio', {
      name: 'Select Original game',
    }),
  );
  await user.click(
    review.getByRole('button', {
      name: 'Confirm match',
    }),
  );
  expect(
    await review.findByRole('heading', {
      name: 'Match confirmed',
    }),
  ).toHaveFocus();
  expect(saveDecision).toHaveBeenCalledExactlyOnceWith('"match-1"', {
    externalId: '100',
  });
  await user.click(
    review.getByRole('button', {
      name: 'Next decision',
    }),
  );
  expect(
    await review.findByRole('link', {
      name: 'Description',
    }),
  ).toBeVisible();
  await user.click(
    review.getByRole('button', {
      name: 'Skip for now',
    }),
  );

  const next = within(
    await screen.findByRole('complementary', {
      name: 'EarthBound',
    }),
  );
  await user.click(
    await next.findByRole('button', {
      name: 'Skip for now',
    }),
  );
  expect(
    await screen.findByRole('complementary', {
      name: 'Super Metroid',
    }),
  ).toHaveFocus();
  expect(
    screen.getByRole('button', {
      name: 'Review EarthBound',
    }),
  ).toBeVisible();
  expect(saveDecision).toHaveBeenCalledTimes(1);
});

it('keeps the inspector open during a save and never offers to discard an in-flight write', async () => {
  const { match, titles, metadataIssue } = mockProviderReview();
  const started = vi.fn();
  let finish!: () => void;
  const pending = new Promise<void>((resolve) => {
    finish = resolve;
  });
  server.use(
    http.put(`${api}/titles/chrono-trigger/provider-matches/igdb`, async ({ request }) => {
      started(request.headers.get('If-Match'), await request.json());
      await pending;
      return HttpResponse.json({
        ...match,
        state: 'Confirmed',
        externalId: '100',
        revision: 'match-2',
        pendingJobId: 'work-1',
      });
    }),
  );

  const { user, router, panel, queryClient, candidate } = await openProviderReview();
  await user.click(candidate);
  await user.click(
    panel.getByRole('button', {
      name: 'Confirm match',
    }),
  );
  await waitFor(() =>
    expect(started).toHaveBeenCalledExactlyOnceWith('"match-1"', {
      externalId: '100',
    }),
  );
  expect(
    panel.getByRole('button', {
      name: 'Preview next item',
    }),
  ).toBeDisabled();
  expect(
    panel.getByRole('button', {
      name: 'Close inspector',
    }),
  ).toBeDisabled();
  await user.keyboard('{Escape}');
  expect(router.state.location.search).toContain('title=chrono-trigger');
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

  // The browser's Back button goes through the app-wide guard.
  await act(() => router.navigate(-1));
  const dialog = screen.getByRole('dialog', {
    name: 'Saving your changes',
  });
  expect(
    within(dialog).queryByRole('button', {
      name: 'Discard',
    }),
  ).not.toBeInTheDocument();
  await user.click(
    within(dialog).getByRole('button', {
      name: 'Stay here',
    }),
  );

  // A refresh may observe the server's commit before its response reaches the picker.
  titles[0] = assessmentFixture(withRequirements(metadataIssue));
  await act(() =>
    queryClient.invalidateQueries({
      queryKey: enrichmentWorkspaceKeys.title('chrono-trigger'),
    }),
  );
  expect(candidate).toBeChecked();
  finish();
  expect(
    await panel.findByRole('heading', {
      name: 'Match confirmed',
    }),
  ).toHaveFocus();
  expect(
    panel.getByRole('link', {
      name: 'View job',
    }),
  ).toHaveAttribute('href', '/jobs/work-1');
  expect(
    panel.getByRole('button', {
      name: 'Preview next item',
    }),
  ).toBeEnabled();
});

it('can continue without the provider, using the shown revision and leaving the next decision in place', async () => {
  const { match, titles, metadataIssue } = mockProviderReview();
  const unlink = vi.fn();
  server.use(
    http.delete(`${api}/titles/chrono-trigger/provider-matches/igdb`, ({ request }) => {
      unlink(request.headers.get('If-Match'));
      titles[0] = assessmentFixture(withRequirements(metadataIssue));
      return HttpResponse.json({
        ...match,
        state: 'Unlinked',
        decision: null,
        revision: 'match-2',
      });
    }),
  );
  const { user, panel } = await openProviderReview();
  const action = panel.getByRole('button', {
    name: 'Continue without IGDB',
  });
  await user.click(action);
  const dialog = screen.getByRole('dialog', {
    name: 'Continue without IGDB?',
  });
  await user.click(
    within(dialog).getByRole('button', {
      name: 'Cancel',
    }),
  );
  expect(unlink).not.toHaveBeenCalled();
  expect(dialog).not.toBeInTheDocument();
  await user.click(action);
  const reopened = screen.getByRole('dialog', {
    name: 'Continue without IGDB?',
  });
  await user.click(
    within(reopened).getByRole('button', {
      name: 'Continue without IGDB',
    }),
  );
  expect(
    await panel.findByRole('heading', {
      name: 'Provider matching turned off',
    }),
  ).toHaveFocus();
  expect(unlink).toHaveBeenCalledExactlyOnceWith('"match-1"');
  expect(
    panel.queryByRole('link', {
      name: 'View job',
    }),
  ).not.toBeInTheDocument();
  await user.click(
    panel.getByRole('button', {
      name: 'Next decision',
    }),
  );
  expect(
    await panel.findByRole('link', {
      name: 'Description',
    }),
  ).toBeVisible();
});

it.each([
  'assessment',
  'provider',
])('keeps a draft visible until the curator acknowledges a changed %s', async (changed) => {
  const { match, titles, metadataIssue, saveDecision } = mockProviderReview();
  const { user, panel, inspector, candidate, queryClient } = await openProviderReview();
  await user.click(candidate);
  if (changed === 'assessment') {
    titles[0] = assessmentFixture(withRequirements(metadataIssue));
    await act(() =>
      queryClient.invalidateQueries({
        queryKey: enrichmentWorkspaceKeys.title('chrono-trigger'),
      }),
    );
  } else {
    await act(() => {
      queryClient.setQueryData(providerMatchKeys.title('chrono-trigger'), [
        {
          ...match,
          revision: 'match-2',
        },
      ]);
    });
  }
  expect(await panel.findByText('This provider decision changed')).toBeVisible();
  expect(candidate).toBeChecked();
  expect(candidate).toBeDisabled();
  expect(
    panel.queryByRole('button', {
      name: 'Confirm match',
    }),
  ).not.toBeInTheDocument();
  await user.click(
    panel.getByRole('button', {
      name: 'Show latest status',
    }),
  );
  expect(inspector).toHaveFocus();
  expect(saveDecision).not.toHaveBeenCalled();
  if (changed === 'assessment') {
    expect(
      await panel.findByRole('link', {
        name: 'Description',
      }),
    ).toBeVisible();
  } else {
    const fresh = await panel.findByRole('radio', {
      name: 'Select Original game',
    });
    expect(fresh).not.toBeChecked();
    await user.click(fresh);
    await user.click(
      panel.getByRole('button', {
        name: 'Confirm match',
      }),
    );
    await waitFor(() =>
      expect(saveDecision).toHaveBeenCalledExactlyOnceWith('"match-2"', {
        externalId: '100',
      }),
    );
    await panel.findByRole('heading', {
      name: 'Match confirmed',
    });
  }
});

afterEach(() => {
  client.setConfig({
    baseUrl: '',
  });
});

it('updates the checklist, queue, count and Catalog row after a Needs review decision', async () => {
  const api = 'http://localhost/api';
  const title = writtenTitleFixture({
    revision: 'rev-1',
  });
  const titleUrl = `${api}/titles/${title.id}`;
  const description = 'Metadata:Description';
  let assessment = assessmentFixture(
    withRequirements(
      unsatisfied(description, 'NeedsReview', 'NotProvided', [
        'ReviewMetadata',
        'AcceptOmission',
      ]),
    ),
  );
  const saveDecision = vi.fn();
  server.use(
    http.get(`${api}/system-setup`, () => HttpResponse.json(finalPage([]))),
    http.get(`${api}/systems`, () => HttpResponse.json(finalPage([]))),
    http.get(`${api}/catalog/stats`, () =>
      HttpResponse.json({
        totalCount: 1,
      }),
    ),
    http.get(`${api}/catalog`, () =>
      HttpResponse.json(
        finalPage<CatalogTitle>([
          {
            id: title.id,
            name: title.name,
            systemKey: title.systemKey,
            isTracked: true,
            enrichmentAssessment: assessment,
          },
        ]),
      ),
    ),
    http.get(`${api}/enrichment/workbench/stats`, () =>
      HttpResponse.json<EnrichmentWorkbenchCounts>({
        total: 1,
        needsReview: Number(assessment.needsReview),
        automatic: 0,
        processing: 0,
        blocked: 0,
      }),
    ),
    http.get(`${api}/enrichment/workbench`, () =>
      HttpResponse.json(
        finalPage(
          assessment.needsReview
            ? [
                assessment,
              ]
            : [],
        ),
      ),
    ),
    http.get(titleUrl, () =>
      HttpResponse.json({
        ...title,
        revision: assessment.revision,
      }),
    ),
    http.get(`${titleUrl}/artwork`, () =>
      HttpResponse.json({
        roles: [],
        revision: 'art-1',
      }),
    ),
    http.get(`${titleUrl}/enrichment-assessment`, () => HttpResponse.json(assessment)),
    http.put<never, SetEnrichmentOmissionRequest>(
      `${titleUrl}/enrichment-omissions/Description`,
      async ({ request }) => {
        const validator = request.headers.get('If-Match');
        const decision = await request.json();
        saveDecision(validator, decision);
        if (validator !== `"${assessment.revision}"` || !decision.accepted)
          return new HttpResponse(null, {
            status: 412,
          });

        assessment = assessmentFixture(
          withRequirements(satisfied(description, 'AcceptedOmission')),
          {
            revision: 'rev-2',
            acceptedOmissions: [
              'Description',
            ],
          },
        );
        return HttpResponse.json({
          ...title,
          revision: assessment.revision,
        });
      },
    ),
  );

  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
      },
    },
  });
  // Keep All titles fresh across view switches so remounting cannot hide missed invalidation.
  cache.setQueryDefaults(catalogSearchKeys.all, {
    staleTime: Infinity,
    gcTime: Infinity,
  });
  const user = userEvent.setup();
  render(
    <QueryClientProvider client={cache}>
      <Catalog />
    </QueryClientProvider>,
    {
      auth: {
        user: authFixtures.currentUser,
        isAuthenticated: true,
        isLoading: false,
        error: null,
        retry: async () => {},
        login: async () => {},
        logout: async () => {},
      },
      routerOptions: {
        initialEntries: [
          '/catalog?view=all',
        ],
      },
    },
  );

  const catalogStatus = async (status: string) => {
    const table = await screen.findByRole('table', {
      name: 'Catalog titles',
    });
    const row = await within(table).findByRole('row', {
      name: /Chrono Trigger/,
    });
    return within(row).findByText(status);
  };
  expect(await catalogStatus('Needs review')).toBeVisible();
  const views = within(
    screen.getByRole('navigation', {
      name: 'Catalog views',
    }),
  );
  await user.click(
    await views.findByRole('link', {
      name: 'Needs review 1',
    }),
  );
  const queue = within(
    await screen.findByRole('table', {
      name: 'Titles in the review queue',
    }),
  );
  await user.click(
    queue.getByRole('button', {
      name: 'Review Chrono Trigger',
    }),
  );
  const inspector = within(
    await screen.findByRole('complementary', {
      name: title.name,
    }),
  );
  expect(
    await inspector.findByRole('link', {
      name: 'Description',
    }),
  ).toBeVisible();
  expect(inspector.getByText('1 outstanding')).toBeVisible();

  await user.click(
    inspector.getByRole('button', {
      name: 'Keep description empty',
    }),
  );
  const confirmation = within(
    await screen.findByRole('dialog', {
      name: 'Keep description empty?',
    }),
  );
  await user.click(
    confirmation.getByRole('button', {
      name: 'Keep description empty',
    }),
  );

  expect(await inspector.findByText('No outstanding requirements')).toBeVisible();
  expect(inspector.getByText('Settled')).toBeVisible();
  expect(
    inspector.queryByRole('link', {
      name: 'Description',
    }),
  ).not.toBeInTheDocument();
  expect(saveDecision).toHaveBeenCalledExactlyOnceWith('"rev-1"', {
    accepted: true,
  });
  expect(
    await views.findByRole('link', {
      name: 'Needs review 0',
    }),
  ).toBeVisible();
  expect(await queue.findByText('No longer in this view')).toBeVisible();
  await user.click(
    inspector.getByRole('button', {
      name: 'Close inspector',
    }),
  );
  expect(await screen.findByText('Nothing needs review')).toBeVisible();

  await user.click(
    views.getByRole('link', {
      name: 'All titles',
    }),
  );
  expect(await catalogStatus('Settled')).toBeVisible();
});
