import { MantineProvider } from '@mantine/core';
import { client, type EnrichmentRequirement } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DraftGuardProvider } from '../../components/Administration/DraftGuard';
import { isInView, type WorkbenchView } from '../../components/Title/titleWorkbenchModel';
import { useCatalogSearch } from '../../hooks/api/useCatalogSearch';
import {
  assessmentFixture,
  chronoTriggerRequirements,
  satisfied,
  unsatisfied,
  writtenTitleFixture,
} from '../../test/msw/fixtures/enrichment';
import { server } from '../../test/msw/server';
import { CatalogCard } from '../Catalog/CatalogCard';
import { TitleDetail } from '../TitleDetail';
import { EnrichmentWorkspace } from './EnrichmentWorkspace';

vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canManageTitles: true,
    canConfigureIntegrations: true,
  }),
}));
vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: {
      roles: [
        'Manager',
      ],
    },
  }),
}));

const api = 'http://localhost/api';
const game = {
  id: '7',
  name: 'Chrono Trigger',
  url: 'https://www.steamgriddb.com/game/7',
  year: 1995,
};
let requirements: EnrichmentRequirement[];
let revision: number;
let steamGridDbMatch: 'unconfigured' | 'proposed' | 'confirmed';
const assessment = () =>
  assessmentFixture(requirements, {
    titleId: 'chrono',
    revision: `rev-${revision}`,
    acceptedOmissions: requirements
      .filter((row) => row.satisfiedBy === 'AcceptedOmission')
      .map((row) => row.subject),
  });
/** The title representation at the current revision. */
const chronoTitle = () =>
  writtenTitleFixture({
    id: 'chrono',
    revision: `rev-${revision}`,
    media: [
      {
        id: 'cover1',
        type: 'Cover',
        sourceId: 'user',
        url: '/media/cover1',
        isPrimary: false,
      },
    ],
  });
/** One server change: rows replaced by key, and a new revision. */
function advance(...rows: EnrichmentRequirement[]) {
  requirements = requirements.map((row) => rows.find((change) => change.key === row.key) ?? row);
  revision += 1;
}
/** The server's side of each step. Handlers apply them, and later tests replay them to start mid-story. */
const descriptionSaved = () =>
  advance(
    satisfied('Metadata:Description', 'Present', [
      'ReviewMetadata',
    ]),
  );
const omissionSet = (field: string, accepted: boolean) => {
  const key = `Metadata:${field}`;
  advance(
    accepted
      ? satisfied(key, 'AcceptedOmission', [
          'ReviewMetadata',
        ])
      : unsatisfied(key, 'NeedsReview', 'NotProvided', [
          'ReviewMetadata',
          'AcceptOmission',
        ]),
  );
};
const steamGridDbProposed = () => {
  steamGridDbMatch = 'proposed';
  advance(
    unsatisfied('Identity:steamgriddb', 'NeedsReview', 'MatchDecisionRequired', [
      'ReviewProvider',
      'ContinueWithoutProvider',
    ]),
    unsatisfied('Artwork:Logo', 'Blocked', 'AwaitingIdentity', [
      'ReviewProvider',
    ]),
    satisfied('Artwork:Backdrop', 'Present', [
      'ReviewArtwork',
    ]),
    satisfied('Operation:Processing', 'Idle'),
  );
};
const steamGridDbConfirmed = () => {
  steamGridDbMatch = 'confirmed';
  advance(
    satisfied('Identity:steamgriddb', 'Trusted', [
      'ReviewProvider',
    ]),
    unsatisfied('Artwork:Logo', 'Automatic', 'NotAttempted', [
      'Enrich',
    ]),
  );
};
const genreFoundMissing = () =>
  advance(
    unsatisfied('Metadata:Genre', 'NeedsReview', 'NotProvided', [
      'ReviewMetadata',
      'AcceptOmission',
    ]),
  );
const bannerFoundUnusable = () =>
  advance(
    unsatisfied('Artwork:Hero', 'NeedsReview', 'NothingUsable', [
      'ReviewArtwork',
      'LeaveEmpty',
    ]),
  );
const bannerLeftEmpty = () =>
  advance(
    satisfied('Artwork:Hero', 'LeftEmpty', [
      'ReviewArtwork',
    ]),
  );
const bannerReturnedToAutomatic = () =>
  advance(
    unsatisfied('Artwork:Hero', 'Automatic', 'NotAttempted', [
      'Enrich',
    ]),
  );
const posterAwaitingDecision = () =>
  advance(
    unsatisfied('Artwork:Poster', 'NeedsReview', 'ArtworkDecisionRequired', [
      'ReviewArtwork',
    ]),
  );
const posterPinned = () =>
  advance(
    satisfied('Artwork:Poster', 'Present', [
      'ReviewArtwork',
    ]),
  );
const selections = () =>
  [
    'Poster',
    'Backdrop',
    'Hero',
    'Logo',
  ].map((role) => ({
    role,
    mode: 'Automatic',
    revision: `${role}-${revision}`,
    pinnedAssetId: null,
    pendingJobId: null,
  }));
const matches = () => [
  {
    providerId: 'igdb',
    providerName: 'IGDB',
    isAvailable: true,
    capabilities: [
      'metadata',
    ],
    revision: 'igdb-1',
    state: 'Confirmed',
    externalId: '1',
    game: {
      ...game,
      id: '1',
      url: 'https://www.igdb.com/games/chrono-trigger',
    },
    metadataNeedsRefresh: false,
  },
  steamGridDbMatch === 'unconfigured'
    ? {
        providerId: 'steamgriddb',
        providerName: 'SteamGridDB',
        isAvailable: false,
        capabilities: [
          'artwork',
        ],
        revision: 'sgdb-1',
        state: 'NeedsMatch',
        metadataNeedsRefresh: false,
      }
    : {
        providerId: 'steamgriddb',
        providerName: 'SteamGridDB',
        isAvailable: true,
        capabilities: [
          'artwork',
        ],
        revision: 'sgdb-1',
        state: steamGridDbMatch === 'confirmed' ? 'Confirmed' : 'AutoMatched',
        externalId: '7',
        game,
        metadataNeedsRefresh: false,
      },
];

beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
  requirements = chronoTriggerRequirements;
  revision = 1;
  steamGridDbMatch = 'unconfigured';
  server.use(
    http.get(`${api}/enrichment/workbench/stats`, () => {
      const current = assessment();
      return HttpResponse.json({
        total: 1,
        needsReview: Number(current.needsReview),
        automatic: Number(current.canEnrichAutomatically),
        processing: Number(current.hasActiveWork),
        blocked: Number(current.hasOperationalBlocker),
      });
    }),
    http.get(`${api}/enrichment/workbench`, ({ request }) => {
      const view = (new URL(request.url).searchParams.get('view') ??
        'NeedsReview') as WorkbenchView;
      return HttpResponse.json({
        items: isInView(assessment(), view)
          ? [
              assessment(),
            ]
          : [],
        hasNextPage: false,
        nextCursor: null,
      });
    }),
    http.get(`${api}/titles/chrono/enrichment-assessment`, () => HttpResponse.json(assessment())),
    http.put(`${api}/titles/chrono/enrichment-omissions/:field`, async ({ params, request }) => {
      const body = (await request.json()) as {
        accepted: boolean;
      };
      if (request.headers.get('If-Match') !== `"rev-${revision}"`)
        return HttpResponse.json(
          {
            detail: 'Changed',
          },
          {
            status: 412,
          },
        );
      omissionSet(String(params.field), body.accepted);
      return HttpResponse.json(chronoTitle());
    }),
    http.get(`${api}/catalog`, () => {
      const { needsReview, canEnrichAutomatically, hasActiveWork, hasOperationalBlocker } =
        assessment();
      return HttpResponse.json({
        items: [
          {
            id: 'chrono',
            systemKey: 'snes',
            name: 'Chrono Trigger',
            enrichmentStatus: 'Completed',
            enrichmentAssessment: {
              needsReview,
              canEnrichAutomatically,
              hasActiveWork,
              hasOperationalBlocker,
            },
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      });
    }),
    http.get(`${api}/catalog/filters`, () =>
      HttpResponse.json({
        genres: [],
        years: [],
        manufacturers: [],
        regions: [],
        languages: [],
        contentRatings: [],
        platforms: [],
        enrichmentStatuses: [],
      }),
    ),
    http.get(`${api}/systems`, () =>
      HttpResponse.json([
        {
          key: 'snes',
          name: 'Super Nintendo',
          compactLabel: 'SNES',
        },
      ]),
    ),
    http.get(`${api}/systems/snes`, () =>
      HttpResponse.json({
        key: 'snes',
        name: 'Super Nintendo',
        compactLabel: 'SNES',
      }),
    ),
    http.get(`${api}/titles/chrono`, () => HttpResponse.json(chronoTitle())),
    http.get(`${api}/titles/chrono/source-references`, () =>
      HttpResponse.json([
        {
          catalogSourceId: 'src-1',
          kind: 'Dat',
          name: 'No-Intro SNES',
          status: 'Active',
          entryCount: '1',
          hasActiveDefinition: true,
        },
      ]),
    ),
    http.get(`${api}/titles/chrono/enrichment`, () =>
      HttpResponse.json({
        titleId: 'chrono',
        name: 'Chrono Trigger',
        enrichmentStatus: 'Completed',
        fieldProvenance: {},
        fieldSourceOverrides: {},
        layers: [],
        externalIds: [],
      }),
    ),
    http.patch(`${api}/titles/chrono`, async ({ request }) => {
      const body = (await request.json()) as {
        description?: string | null;
      };
      if (body.description) descriptionSaved();
      return HttpResponse.json({
        ...chronoTitle(),
        description: body.description,
      });
    }),
    http.get(`${api}/titles/chrono/provider-matches`, () => HttpResponse.json(matches())),
    http.put(`${api}/titles/chrono/provider-matches/steamgriddb`, async ({ request }) => {
      expect(request.headers.get('If-Match')).toBe('"sgdb-1"');
      expect(await request.json()).toEqual({
        externalId: '7',
      });
      steamGridDbConfirmed();
      return HttpResponse.json(matches().find((match) => match.providerId === 'steamgriddb'));
    }),
    http.get(`${api}/titles/chrono/artwork`, () =>
      HttpResponse.json({
        revision: `family-${revision}`,
        roles: selections(),
      }),
    ),
    http.get(`${api}/titles/chrono/artwork-assets`, () =>
      HttpResponse.json({
        items: [],
        nextCursor: null,
        hasNextPage: false,
      }),
    ),
    http.get(`${api}/titles/chrono/artwork-rejections`, () =>
      HttpResponse.json({
        items: [],
        nextCursor: null,
        hasNextPage: false,
      }),
    ),
    http.get(`${api}/artwork-providers`, () => HttpResponse.json([])),
    http.get(`${api}/titles/chrono/artwork-acquisition`, () =>
      HttpResponse.json({
        outcomes: [],
        activeJobId: null,
      }),
    ),
    http.put(`${api}/titles/chrono/artwork/:role`, async ({ request, params }) => {
      const body = (await request.json()) as {
        mode: string;
        mediaId?: string | null;
      };
      expect(request.headers.get('If-Match')).toBe(`"${params.role}-${revision}"`);
      if (params.role === 'Hero' && body.mode === 'Empty') bannerLeftEmpty();
      else if (params.role === 'Hero' && body.mode === 'Automatic') bannerReturnedToAutomatic();
      else if (params.role === 'Poster' && body.mode === 'Pinned') {
        expect(body.mediaId).toBe('cover1');
        posterPinned();
      }
      return HttpResponse.json({
        role: params.role,
        mode: body.mode,
        revision: `${params.role}-${revision}`,
        pinnedAssetId: null,
        pendingJobId: null,
      });
    }),
  );
});
afterEach(() => {
  client.setConfig({
    baseUrl: '',
  });
});

function CatalogProbe() {
  const { data } = useCatalogSearch({
    query: 'Chrono',
  });
  return (
    <section aria-label="Catalog">
      {data?.pages
        .flatMap((page) => page.items)
        .map((title) => (
          <CatalogCard
            key={title.id}
            title={title}
          />
        ))}
    </section>
  );
}

function renderSurfaces(entry: string) {
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
            path: '/enrichment',
            element: (
              <>
                <EnrichmentWorkspace />
                <CatalogProbe />
              </>
            ),
          },
          {
            path: '/titles/:titleId',
            element: (
              <>
                <TitleDetail />
                <CatalogProbe />
              </>
            ),
          },
        ],
      },
    ],
    {
      initialEntries: [
        entry,
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
  return {
    cache,
    router,
  };
}

const statusLabels = /^(Needs review|Blocked|Processing|Automatic|Settled)$/;
const chipsIn = (element: HTMLElement) =>
  within(element)
    .getAllByText(statusLabels)
    .map((chip) => chip.textContent);
const queue = () =>
  screen.getByRole('navigation', {
    name: 'Enrichment title queue',
  });
/** Finds a tab from one pass over its own list, sparing a page-wide accessible-name search per tab. */
function tabStartingWith(list: HTMLElement, label: string) {
  const tab = within(list)
    .getAllByRole('tab')
    .find((candidate) => candidate.textContent?.startsWith(label));
  if (!tab) throw new Error(`No tab starts with ${label}.`);
  return tab;
}
const viewCount = (label: string) =>
  within(
    tabStartingWith(
      screen.getByRole('tablist', {
        name: 'Enrichment views',
      }),
      label,
    ),
  ).getByText(/^\d+$/).textContent;
const catalogChips = () =>
  chipsIn(
    screen.getByRole('region', {
      name: 'Catalog',
    }),
  );
type Counts = [
  needsReview: number,
  automatic: number,
  processing: number,
  blocked: number,
];

interface Agreement {
  chips: string[];
  outstanding: number;
  row: string;
  counts: Counts;
}

/** Every workspace surface shows the same title status, within one refresh of the server state. */
async function expectAgreement({ chips, outstanding, row, counts }: Agreement) {
  await waitFor(() => {
    expect(
      screen.getByRole('listitem', {
        name: row,
      }),
    ).toBeInTheDocument();
    expect(
      chipsIn(
        screen.getByRole('group', {
          name: 'Enrichment status',
        }),
      ),
    ).toEqual(chips);
    expect(
      chipsIn(
        screen.getByRole('group', {
          name: 'Chrono Trigger enrichment status',
        }),
      ),
    ).toEqual(chips);
    expect(within(queue()).getByText(`${outstanding} outstanding`)).toBeInTheDocument();
    expect(catalogChips()).toEqual(chips);
    expect([
      viewCount('Needs review'),
      viewCount('Can enrich automatically'),
      viewCount('Processing'),
      viewCount('Blocked'),
    ]).toEqual(counts.map(String));
  });
}

/**
 * The title page has no workspace refresh of its own, so its catalog card only follows a decision when the
 * decision's mutation itself refreshes the catalog.
 */
async function expectPageAgreement({ chips, row }: { chips: string[]; row: string }) {
  const enrichment = tabStartingWith(
    await screen.findByRole('tablist', {
      name: 'Title sections',
    }),
    'Enrichment',
  );
  if (enrichment.getAttribute('aria-selected') !== 'true') await userEvent.click(enrichment);
  // Only a precondition: finding the toggle by its text spares naming every button on the page.
  const completed = screen.queryByText(/^Completed \(\d+\)$/)?.closest('button');
  if (completed?.getAttribute('aria-expanded') === 'false') await userEvent.click(completed);
  await waitFor(() => {
    expect(
      screen.getByRole('listitem', {
        name: row,
      }),
    ).toBeInTheDocument();
    expect(
      chipsIn(
        screen.getByRole('group', {
          name: 'Enrichment status',
        }),
      ),
    ).toEqual(chips);
    expect(catalogChips()).toEqual(chips);
  });
}

const backgroundChange = (cache: QueryClient, change: () => void) =>
  act(async () => {
    change();
    await cache.invalidateQueries();
  });
const rowAction = (row: RegExp, action: string) =>
  within(
    screen.getByRole('listitem', {
      name: row,
    }),
  ).getByRole('button', {
    name: action,
  });

async function saveDescription(user: ReturnType<typeof userEvent.setup>) {
  const inspector = await screen.findByRole('dialog', {
    name: 'Edit Description',
  });
  await user.click(
    within(inspector).getByRole('radio', {
      name: 'Custom',
      exact: true,
    }),
  );
  await user.click(
    within(inspector).getByRole('textbox', {
      name: 'Description user override',
    }),
  );
  await user.paste('A time-travel epic.');
  await user.click(
    within(inspector).getByRole('button', {
      name: 'Save changes',
    }),
  );
}
async function confirmSteamGridDbMatch(user: ReturnType<typeof userEvent.setup>) {
  await user.click(rowAction(/^SteamGridDB:/, 'Review SteamGridDB match'));
  await user.click(
    await screen.findByRole('radio', {
      name: 'Select Chrono Trigger',
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Confirm Chrono Trigger',
    }),
  );
}
async function acceptGenreOmission(user: ReturnType<typeof userEvent.setup>) {
  await user.click(rowAction(/^Genre:/, 'Keep genre empty'));
  await user.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Keep genre empty?',
      }),
    ).getByRole('button', {
      name: 'Keep genre empty',
    }),
  );
}
async function leaveBannerEmpty(user: ReturnType<typeof userEvent.setup>) {
  await user.click(rowAction(/^Banner:/, 'Leave banner empty'));
  await user.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Leave banner empty?',
      }),
    ).getByRole('button', {
      name: 'Leave banner empty',
    }),
  );
}
async function applyPoster(user: ReturnType<typeof userEvent.setup>) {
  await user.click(rowAction(/^Poster:/, 'Review poster'));
  await user.click(
    await within(
      await screen.findByRole('complementary', {
        name: 'Poster images',
      }),
    ).findByRole('button', {
      name: 'Actions for Cover cover1',
    }),
  );
  await user.click(
    await screen.findByRole('menuitem', {
      name: 'Use as poster',
    }),
  );
  await user.click(
    within(
      await screen.findByRole('region', {
        name: 'Compose poster',
      }),
    ).getByRole('button', {
      name: 'Apply poster',
    }),
  );
}
async function revisitGenre(user: ReturnType<typeof userEvent.setup>) {
  await user.click(rowAction(/^Genre:/, 'Revisit genre'));
}

/**
 * Each story runs as consecutive tests that stay under the per-test timeout under parallel load. A later test replays
 * the server's side of the earlier steps, then first asserts exactly the agreement the previous test ended on.
 */
const steamGridDbMatchedAgreement: Agreement = {
  chips: [
    'Automatic',
  ],
  outstanding: 1,
  row: 'SteamGridDB: Matched to Chrono Trigger (1995)',
  counts: [
    0,
    1,
    0,
    0,
  ],
};
const bannerAutomaticAgreement: Agreement = {
  chips: [
    'Automatic',
  ],
  outstanding: 2,
  row: 'Banner: Will search for a banner on the next enrichment.',
  counts: [
    0,
    1,
    0,
    0,
  ],
};
const genreKeptEmptyPageAgreement = {
  chips: [
    'Automatic',
  ],
  row: 'Genre: Intentionally empty',
};
function replayThroughSteamGridDbConfirmation() {
  descriptionSaved();
  steamGridDbProposed();
  steamGridDbConfirmed();
}
function replayThroughGenreOmission() {
  replayThroughSteamGridDbConfirmation();
  genreFoundMissing();
  omissionSet('Genre', true);
}
function replayThroughBannerReturn() {
  replayThroughGenreOmission();
  bannerFoundUnusable();
  bannerLeftEmpty();
  bannerReturnedToAutomatic();
}

describe('keeps the checklist, queue, counts and catalog in agreement through every kind of resolution', () => {
  it('when a field is saved and a provider match is confirmed', async () => {
    const user = userEvent.setup();
    // An explicit requirement deep link opens its editor on first load.
    const { cache, router } = renderSurfaces(
      '/enrichment?view=NeedsReview&title=chrono&editor=metadata&requirement=Metadata%3ADescription',
    );

    // Scenario 8.1 before.
    await expectAgreement({
      chips: [
        'Needs review',
        'Blocked',
        'Processing',
      ],
      outstanding: 4,
      row: 'Description: Description is missing after enrichment.',
      counts: [
        1,
        0,
        1,
        1,
      ],
    });
    expect(
      screen.getByText('4 outstanding', {
        selector: '[tabindex="-1"]',
      }),
    ).toBeInTheDocument();

    // Save a field: scenario 8.1 after.
    await saveDescription(user);
    await expectAgreement({
      chips: [
        'Blocked',
        'Processing',
      ],
      outstanding: 3,
      row: 'Description: Present',
      counts: [
        0,
        0,
        1,
        1,
      ],
    });
    await waitFor(() =>
      expect(
        screen.getByRole('listitem', {
          name: 'SteamGridDB: SteamGridDB is enabled but has no credentials.',
        }),
      ).toHaveFocus(),
    );
    expect(
      screen.getByText('3 outstanding', {
        selector: '[tabindex="-1"]',
      }),
    ).toBeInTheDocument();
    expect(
      within(queue()).getByRole('button', {
        name: /Chrono Trigger/,
      }),
    ).toHaveAttribute('aria-current', 'true');
    expect(within(queue()).getByText('No longer in this view')).toBeInTheDocument();

    // The rest happens in All, where the title always has a queue row.
    await act(async () => {
      await router.navigate('/enrichment?view=All&title=chrono');
    });
    await backgroundChange(cache, steamGridDbProposed);
    await expectAgreement({
      chips: [
        'Needs review',
        'Blocked',
      ],
      outstanding: 2,
      row: 'SteamGridDB: Confirm the SteamGridDB match for this title.',
      counts: [
        1,
        0,
        0,
        1,
      ],
    });

    await confirmSteamGridDbMatch(user);
    await expectAgreement(steamGridDbMatchedAgreement);
  });

  it('when an omission is accepted and artwork is left empty, then returned to automatic', async () => {
    const user = userEvent.setup();
    replayThroughSteamGridDbConfirmation();
    const { cache } = renderSurfaces('/enrichment?view=All&title=chrono');
    await expectAgreement(steamGridDbMatchedAgreement);

    await backgroundChange(cache, genreFoundMissing);
    await expectAgreement({
      chips: [
        'Needs review',
        'Automatic',
      ],
      outstanding: 2,
      row: 'Genre: Genre is missing after enrichment.',
      counts: [
        1,
        1,
        0,
        0,
      ],
    });
    await acceptGenreOmission(user);
    await expectAgreement({
      chips: [
        'Automatic',
      ],
      outstanding: 1,
      row: 'Genre: Intentionally empty',
      counts: [
        0,
        1,
        0,
        0,
      ],
    });

    await backgroundChange(cache, bannerFoundUnusable);
    await expectAgreement({
      chips: [
        'Needs review',
        'Automatic',
      ],
      outstanding: 2,
      row: 'Banner: No usable banner was found. Choose a saved image, upload one, or leave it empty.',
      counts: [
        1,
        1,
        0,
        0,
      ],
    });
    await leaveBannerEmpty(user);
    await expectAgreement({
      chips: [
        'Automatic',
      ],
      outstanding: 1,
      row: 'Banner: Left empty by you',
      counts: [
        0,
        1,
        0,
        0,
      ],
    });

    await user.click(rowAction(/^Banner:/, 'Return banner to automatic'));
    await expectAgreement(bannerAutomaticAgreement);
  });

  it('when artwork is applied and an omission is revisited', async () => {
    const user = userEvent.setup();
    replayThroughBannerReturn();
    const { cache } = renderSurfaces('/enrichment?view=All&title=chrono');
    await expectAgreement(bannerAutomaticAgreement);

    await backgroundChange(cache, posterAwaitingDecision);
    await expectAgreement({
      chips: [
        'Needs review',
        'Automatic',
      ],
      outstanding: 3,
      row: 'Poster: Choose poster artwork or leave it empty.',
      counts: [
        1,
        1,
        0,
        0,
      ],
    });
    await applyPoster(user);
    await expectAgreement({
      chips: [
        'Automatic',
      ],
      outstanding: 2,
      row: 'Poster: Present',
      counts: [
        0,
        1,
        0,
        0,
      ],
    });

    await revisitGenre(user);
    await expectAgreement({
      chips: [
        'Needs review',
        'Automatic',
      ],
      outstanding: 3,
      row: 'Genre: Genre is missing after enrichment.',
      counts: [
        1,
        1,
        0,
        0,
      ],
    });
  });
});

describe('refreshes the catalog from every decision itself on the title page, where no workspace refresh can mask a gap', () => {
  it('when a field is saved, a provider match is confirmed and an omission is accepted', async () => {
    const user = userEvent.setup();
    const { cache } = renderSurfaces('/titles/chrono?editor=enrichment');
    await expectPageAgreement({
      chips: [
        'Needs review',
        'Blocked',
        'Processing',
      ],
      row: 'Description: Description is missing after enrichment.',
    });
    expect(
      screen.queryByRole('dialog', {
        name: 'Edit Description',
      }),
    ).not.toBeInTheDocument();

    await user.click(rowAction(/^Description:/, 'Review description'));
    await saveDescription(user);
    await expectPageAgreement({
      chips: [
        'Blocked',
        'Processing',
      ],
      row: 'Description: Present',
    });

    await backgroundChange(cache, steamGridDbProposed);
    await expectPageAgreement({
      chips: [
        'Needs review',
        'Blocked',
      ],
      row: 'SteamGridDB: Confirm the SteamGridDB match for this title.',
    });
    await confirmSteamGridDbMatch(user);
    await expectPageAgreement({
      chips: [
        'Automatic',
      ],
      row: 'SteamGridDB: Matched to Chrono Trigger (1995)',
    });

    await backgroundChange(cache, genreFoundMissing);
    await expectPageAgreement({
      chips: [
        'Needs review',
        'Automatic',
      ],
      row: 'Genre: Genre is missing after enrichment.',
    });
    await acceptGenreOmission(user);
    await expectPageAgreement(genreKeptEmptyPageAgreement);
  });

  it('when artwork is left empty or applied and an omission is revisited', async () => {
    const user = userEvent.setup();
    replayThroughGenreOmission();
    const { cache } = renderSurfaces('/titles/chrono?editor=enrichment');
    // The seam row is already completed, so wait for the loaded checklist's collapsed Completed group.
    await screen.findByText(/^Completed \(\d+\)$/);
    await expectPageAgreement(genreKeptEmptyPageAgreement);

    await backgroundChange(cache, bannerFoundUnusable);
    await expectPageAgreement({
      chips: [
        'Needs review',
        'Automatic',
      ],
      row: 'Banner: No usable banner was found. Choose a saved image, upload one, or leave it empty.',
    });
    await leaveBannerEmpty(user);
    await expectPageAgreement({
      chips: [
        'Automatic',
      ],
      row: 'Banner: Left empty by you',
    });

    await backgroundChange(cache, posterAwaitingDecision);
    await expectPageAgreement({
      chips: [
        'Needs review',
        'Automatic',
      ],
      row: 'Poster: Choose poster artwork or leave it empty.',
    });
    await applyPoster(user);
    await expectPageAgreement({
      chips: [
        'Automatic',
      ],
      row: 'Poster: Present',
    });

    await revisitGenre(user);
    await expectPageAgreement({
      chips: [
        'Needs review',
        'Automatic',
      ],
      row: 'Genre: Genre is missing after enrichment.',
    });
  });
});

it('keeps focus on the chosen title heading instead of opening the current requirement’s inspector', async () => {
  const user = userEvent.setup();
  renderSurfaces('/enrichment?view=NeedsReview&title=chrono');
  await screen.findByRole('listitem', {
    name: 'Description: Description is missing after enrichment.',
  });
  await user.click(
    within(queue()).getByRole('button', {
      name: /Chrono Trigger/,
    }),
  );
  const heading = screen.getByRole('heading', {
    level: 2,
    name: 'Chrono Trigger',
  });
  await waitFor(() => expect(heading).toHaveFocus());
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 100));
  });
  expect(heading).toHaveFocus();
  expect(
    screen.queryByRole('dialog', {
      name: 'Edit Description',
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.getByRole('tab', {
      name: 'Metadata',
    }),
  ).toHaveAttribute('aria-selected', 'true');
});
