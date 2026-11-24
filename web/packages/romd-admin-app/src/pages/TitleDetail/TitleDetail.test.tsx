import type {
  EnrichmentAssessment,
  MetadataLayer,
  TitleDetail as TitleDetailContract,
  TitleEnrichmentStateResponse,
} from '@romd/admin-api-client';
import { focusManager } from '@tanstack/react-query';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes, useLocation, useNavigate } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AuthContextValue } from '../../contexts/AuthContext';
import { installReferenceCatalog } from '../../test/fixtures/referenceCatalog';
import { unsatisfied, writtenTitleFixture } from '../../test/msw/fixtures/enrichment';
import { render } from '../../test/utils/render';
import { finalPage } from '../../test/workspacePages';
import type { AssembledReferenceCatalog } from '../../utils/referenceCatalog';
import { TitleDetail } from './index';

const permissions = vi.hoisted(() => ({
  canManageTitles: true,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canUploadRoms: true,
    canManageTitles: permissions.canManageTitles,
    canTriggerEnrichment: true,
    canManageUsers: false,
    role: 'Manager',
    hasRole: (role: string) =>
      [
        'User',
        'Contributor',
        'Manager',
      ].includes(role),
  }),
}));

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    listTitleProviderMatches: vi.fn().mockResolvedValue({
      data: [
        {
          providerId: 'igdb',
          providerName: 'IGDB',
          isAvailable: true,
          capabilities: [
            'metadata',
          ],
          revision: 'revision',
          state: 'AutoMatched',
          externalId: '42',
          metadataNeedsRefresh: false,
        },
      ],
      error: undefined,
    }),
    confirmTitleProviderMatch: vi.fn().mockResolvedValue({
      error: undefined,
    }),
    getCatalogFilters: vi.fn(),
    getDatTitleGrouping: vi.fn(),
    getTitleEnrichmentAssessment: vi.fn(),
    getAdminCatalogSnapshot: vi.fn(),
    getTitle: vi.fn(),
    getTitleArtwork: vi.fn(),
    listArtworkProviders: vi.fn(),
    listTitleArtworkAssets: vi.fn(),
    getTitleEnrichmentState: vi.fn(),
    listTitleSourceReferences: vi.fn(),
    mergeTitles: vi.fn(),
    moveGame: vi.fn(),
    listCatalogTitles: vi.fn(),
    updateTitleEnrichment: vi.fn(),
    replaceTitleTracking: vi.fn(),
    triggerTitleEnrichment: vi.fn(),
    updateTitle: vi.fn(),
  };
});

import {
  confirmTitleProviderMatch,
  getAdminCatalogSnapshot,
  getCatalogFilters,
  getDatTitleGrouping,
  getTitle,
  getTitleArtwork,
  getTitleEnrichmentAssessment,
  getTitleEnrichmentState,
  listArtworkProviders,
  listCatalogTitles,
  listTitleArtworkAssets,
  listTitleProviderMatches,
  listTitleSourceReferences,
  mergeTitles,
  replaceTitleTracking,
  triggerTitleEnrichment,
  updateTitle,
  updateTitleEnrichment,
} from '@romd/admin-api-client';

const mockGetCatalogFilters = getCatalogFilters as ReturnType<typeof vi.fn>;
const mockGetCatalogSnapshot = getAdminCatalogSnapshot as ReturnType<typeof vi.fn>;
const mockGetTitle = getTitle as ReturnType<typeof vi.fn>;
const mockGetTitleEnrichmentState = getTitleEnrichmentState as ReturnType<typeof vi.fn>;
const mockGetTitleSourceReferences = listTitleSourceReferences as ReturnType<typeof vi.fn>;
const mockMergeTitles = mergeTitles as ReturnType<typeof vi.fn>;
const mockSearchCatalog = listCatalogTitles as ReturnType<typeof vi.fn>;
const mockUpdateTitleEnrichment = updateTitleEnrichment as ReturnType<typeof vi.fn>;
const mockSetTitleTracking = replaceTitleTracking as ReturnType<typeof vi.fn>;
const mockTriggerTitleEnrichment = triggerTitleEnrichment as ReturnType<typeof vi.fn>;
const mockUpdateTitle = updateTitle as ReturnType<typeof vi.fn>;

const title: TitleDetailContract = writtenTitleFixture({
  id: 'title-1',
  revision: 'title-revision-1',
  systemKey: 'plat-1',
  name: 'Super Mario World',
  enrichmentStatus: 'Completed',
  description: 'Mario returns to Dinosaur Land.',
  genre: 'RPG',
  releaseDate: '1991-11-21',
  publisher: 'Nintendo',
  developer: 'Nintendo EAD',
  players: 2,
  rating: 94,
  contentRatings: [
    {
      board: 'Esrb',
      code: 'E',
      designation: 'Rated',
      minimumAge: 0,
      sourceId: 'igdb',
      externalRatingId: '123',
      descriptors: [],
      synopsis: null,
    },
  ],
  createdAt: '2024-01-01T00:00:00Z',
  lastEnrichedAt: '2024-01-02T00:00:00Z',
  fieldProvenance: {
    Description: 'igdb',
    Genre: 'igdb',
    ReleaseDate: 'igdb',
    Publisher: 'igdb',
    Developer: 'igdb',
    Players: 'igdb',
    Rating: 'igdb',
    ContentRatings: 'igdb',
  },
  media: [],
  hasLocalPayload: true,
  completionPercent: 100,
  releases: [
    {
      id: 'game-1',
      datId: 'dat-1',
      name: 'Super Mario World (USA)',
      description: 'Super Mario World (USA)',
      year: '1991',
      manufacturer: 'Nintendo',
      region: 'USA',
      language: 'English',
      revisionLabel: null,
      isComplete: true,
      files: [],
      sources: [],
    },
  ],
});

const userLayer: MetadataLayer = {
  sourceId: 'user',
  sourceType: 'User',
  description: null,
  publisher: null,
  developer: null,
  genre: null,
  releaseDate: null,
  players: null,
  rating: null,
  updatedAt: '2024-01-03T00:00:00Z',
};

const igdbLayer: MetadataLayer = {
  sourceId: 'igdb',
  sourceType: 'Provider',
  description: 'Mario returns to Dinosaur Land.',
  publisher: 'Nintendo',
  developer: 'Nintendo EAD',
  genre: 'RPG',
  releaseDate: '1991-11-21',
  players: 2,
  rating: 94,
  updatedAt: '2024-01-02T00:00:00Z',
};

const enrichmentState: TitleEnrichmentStateResponse = {
  titleId: 'title-1',
  name: 'Super Mario World',
  enrichmentStatus: 'Completed',
  fieldProvenance: title.fieldProvenance,
  fieldSourceOverrides: {},
  layers: [
    userLayer,
    igdbLayer,
  ],
  externalIds: [
    {
      provider: 'IGDB',
      externalId: '12345',
      matchConfidence: 0.91,
      isConfirmed: false,
      createdAt: '2024-01-02T00:00:00Z',
    },
  ],
  lastEnrichedAt: '2024-01-02T00:00:00Z',
};

const assessment: EnrichmentAssessment = {
  titleId: 'title-1',
  titleName: 'Super Mario World',
  platformName: 'Super Nintendo',
  revision: 'revision',
  needsReview: true,
  hasOperationalBlocker: true,
  hasActiveWork: true,
  canEnrichAutomatically: false,
  requirements: [],
  acceptedOmissions: [],
};

function rating(
  board: string,
  code: string,
  minimumAge: number | null,
  designation: string | null = 'Rated',
  retired = false,
) {
  return {
    board,
    code,
    name: code,
    description: null,
    icon: null,
    designation,
    minimumAge,
    retired,
  };
}

// The snapshot orders ratings by key, not by age, so the picker re-sorts them.
const referenceCatalog: Partial<AssembledReferenceCatalog> = {
  schemaVersion: 1,
  builtInVersion: 1,
  revision: 'a'.repeat(64),
  systems: [],
  ratingBoards: [
    {
      key: 'Esrb',
      name: 'ESRB',
      description: null,
    },
    {
      key: 'Pegi',
      name: 'PEGI',
      description: null,
    },
  ],
  ratings: [
    rating('Esrb', 'E', 0),
    rating('Esrb', 'E10+', 10),
    rating('Esrb', 'M', 17),
    rating('Esrb', 'RP', null, 'RatingPending'),
    rating('Esrb', 'T', 13),
    rating('Esrb', 'X', 18, 'Rated', true),
    rating('Pegi', 'PEGI 12', 12),
    rating('Pegi', 'PEGI 3', 3),
  ],
};

function LocationProbe() {
  const { pathname, search, hash } = useLocation();
  const navigate = useNavigate();
  return (
    <>
      <output aria-label="Location">
        {pathname}
        {search}
        {hash}
      </output>
      <button
        type="button"
        onClick={() => void navigate(-1)}
      >
        Previous location
      </button>
      <button
        type="button"
        onClick={() => void navigate(1)}
      >
        Next location
      </button>
    </>
  );
}

function titleSection(name: string) {
  return within(
    screen.getByRole('navigation', {
      name: 'Title sections',
    }),
  ).getByRole('link', {
    name,
  });
}

function locationProbe() {
  return screen.getByRole('status', {
    name: 'Location',
  });
}

// The reference catalog snapshot, which feeds the re-rate picker, is read only for a signed-in user.
const signedInAuth: AuthContextValue = {
  user: {
    id: 'admin-1',
    email: 'admin@localhost',
    roles: [
      'Admin',
    ],
    libraryId: null,
  },
  isAuthenticated: true,
  isLoading: false,
  error: null,
  retry: async () => {},
  login: async () => {},
  logout: async () => {},
};

function renderTitleDetail(search = '', catalogReturnTo?: string, section?: string) {
  return render(
    <Routes>
      <Route
        path="/titles/:titleId/:section?"
        element={
          <>
            <TitleDetail />
            <LocationProbe />
          </>
        }
      />
      <Route
        path="/settings"
        element={<div>settings probe</div>}
      />
    </Routes>,
    {
      auth: signedInAuth,
      routerOptions: {
        initialEntries: [
          {
            pathname: section ? `/titles/title-1/${section}` : '/titles/title-1',
            search,
            state: {
              catalogReturnTo,
            },
          },
        ],
      },
    },
  );
}

describe('TitleDetail', () => {
  beforeEach(() => {
    permissions.canManageTitles = true;
    mockGetTitle.mockResolvedValue({
      data: title,
      error: undefined,
    });
    vi.mocked(getTitleArtwork).mockResolvedValue({
      data: {
        revision: 'artwork-revision',
        roles: [],
      },
      error: undefined,
    } as Awaited<ReturnType<typeof getTitleArtwork>>);
    vi.mocked(listArtworkProviders).mockResolvedValue({
      data: [],
      error: undefined,
    } as Awaited<ReturnType<typeof listArtworkProviders>>);
    vi.mocked(listTitleArtworkAssets).mockResolvedValue({
      data: finalPage([]),
      error: undefined,
    } as Awaited<ReturnType<typeof listTitleArtworkAssets>>);
    mockGetCatalogFilters.mockResolvedValue({
      data: {
        genres: [
          {
            value: 'Action',
            count: 2,
          },
          {
            value: 'RPG',
            count: 1,
          },
        ],
        years: [],
        manufacturers: [],
        regions: [],
        languages: [],
        contentRatings: [
          {
            value: 'E',
            count: 1,
          },
          {
            value: 'T',
            count: 3,
          },
        ],
        platforms: [],
        enrichmentStatuses: [],
      },
      error: undefined,
    });
    mockGetTitleEnrichmentState.mockResolvedValue({
      data: enrichmentState,
      error: undefined,
    });
    mockGetTitleSourceReferences.mockResolvedValue({
      data: finalPage([
        {
          catalogSourceId: 'catsrc-1',
          kind: 'Dat',
          name: 'No-Intro SNES',
          status: 'Active',
          entryCount: 1,
          datId: 'dat-1',
          systemKey: 'plat-1',
          hasActiveDefinition: true,
        },
      ]),
      error: undefined,
    });
    mockGetCatalogSnapshot.mockResolvedValue(installReferenceCatalog(referenceCatalog).root());
    vi.mocked(getTitleEnrichmentAssessment).mockResolvedValue({
      data: assessment,
      error: undefined,
    } as Awaited<ReturnType<typeof getTitleEnrichmentAssessment>>);
    mockUpdateTitle.mockResolvedValue({
      data: title,
      error: undefined,
      response: new Response(),
    });
    mockUpdateTitleEnrichment.mockResolvedValue({
      data: title,
      error: undefined,
      response: new Response(),
    });
    vi.mocked(listTitleProviderMatches).mockResolvedValue({
      data: [
        {
          providerId: 'igdb',
          providerName: 'IGDB',
          isAvailable: true,
          capabilities: [
            'metadata',
          ],
          revision: 'revision',
          state: 'AutoMatched',
          externalId: '12345',
          metadataNeedsRefresh: false,
        },
      ],
      error: undefined,
    } as Awaited<ReturnType<typeof listTitleProviderMatches>>);
    vi.mocked(confirmTitleProviderMatch).mockResolvedValue({
      error: undefined,
    } as Awaited<ReturnType<typeof confirmTitleProviderMatch>>);
    mockTriggerTitleEnrichment.mockResolvedValue({
      data: undefined,
      error: undefined,
    });
    mockSetTitleTracking.mockResolvedValue({
      data: undefined,
      error: undefined,
    });
    mockSearchCatalog.mockResolvedValue({
      data: {
        items: [],
        nextCursor: null,
        hasNextPage: false,
      },
      error: undefined,
    });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('opens Metadata at the canonical title URL', async () => {
    const user = userEvent.setup();
    renderTitleDetail();
    expect(
      await screen.findByRole('heading', {
        name: 'Super Mario World',
      }),
    ).toBeInTheDocument();
    expect(locationProbe()).toHaveTextContent('/titles/title-1/metadata');
    expect(titleSection('Metadata')).toHaveAttribute('aria-current', 'page');
    const status = screen.getByRole('complementary', {
      name: 'Title status',
    });
    const metadata = screen.getByRole('form', {
      name: 'Title metadata',
    });
    expect(
      status.compareDocumentPosition(metadata) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    const summary = within(status).getByText('Enrichment status');
    const disclosure = summary.closest('details');
    expect(disclosure).not.toHaveAttribute('open');
    await user.click(summary);
    expect(disclosure).toHaveAttribute('open');
    expect(
      within(status).getByRole('button', {
        name: 'Enrich title',
      }),
    ).toBeVisible();
    expect(
      await screen.findByRole('textbox', {
        name: 'Description',
      }),
    ).toHaveValue(title.description);
    expect(
      screen.queryByRole('dialog', {
        name: 'Match IGDB',
      }),
    ).toBeNull();
  });

  it('preserves the hero and Catalog return URL across sections and browser history', async () => {
    const user = userEvent.setup();
    const catalogReturn = '/catalog?q=mario&system=snes&title=title-1';
    renderTitleDetail('', catalogReturn);
    const hero = await screen.findByTestId('title-header');
    for (const [name, path] of [
      [
        'Artwork',
        'artwork',
      ],
      [
        'Releases 1',
        'releases',
      ],
      [
        'Ratings 1',
        'ratings',
      ],
      [
        'Metadata',
        'metadata',
      ],
    ]) {
      await user.click(titleSection(name));
      expect(locationProbe()).toHaveTextContent(`/titles/title-1/${path}`);
      expect(titleSection(name)).toHaveAttribute('aria-current', 'page');
      expect(screen.getByTestId('title-header')).toBe(hero);
      expect(
        screen.getByRole('link', {
          name: 'Catalog',
        }),
      ).toHaveAttribute('href', catalogReturn);
      expect(
        screen.getByRole('complementary', {
          name: 'Title status',
        }),
      ).toBeInTheDocument();
    }
    await user.click(
      screen.getByRole('button', {
        name: 'Previous location',
      }),
    );
    expect(locationProbe()).toHaveTextContent('/titles/title-1/ratings');
    await user.click(
      screen.getByRole('button', {
        name: 'Next location',
      }),
    );
    expect(locationProbe()).toHaveTextContent('/titles/title-1/metadata');
  });

  it('shows the current assessment in the status summary', async () => {
    renderTitleDetail('', undefined, 'artwork');
    const rail = await screen.findByRole('complementary', {
      name: 'Title status',
    });
    const groups = await within(rail).findAllByRole('group', {
      name: 'Enrichment status',
    });
    for (const group of groups) {
      expect(
        within(group)
          .getAllByText(/./)
          .map((chip) => chip.textContent),
      ).toEqual([
        'Needs review',
        'Blocked',
        'Processing',
      ]);
    }
    expect(getTitleEnrichmentAssessment).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          titleId: 'title-1',
        },
      }),
    );
  });

  it('focuses Identifiers and sources from the legacy match link', async () => {
    renderTitleDetail('?tab=match');
    const sources = await screen.findByRole('region', {
      name: 'Identifiers and sources',
    });
    expect(locationProbe()).toHaveTextContent(
      '/titles/title-1/metadata?match=1#identifiers-and-sources',
    );
    expect(sources).toHaveFocus();
    expect(await within(sources).findByText('12345')).toBeVisible();
  });

  it('opens the requested provider from an identity review link', async () => {
    vi.mocked(getTitleEnrichmentAssessment).mockResolvedValue({
      data: {
        ...assessment,
        requirements: [
          unsatisfied('Identity:igdb', 'NeedsReview', 'MatchDecisionRequired', [
            'ReviewProvider',
          ]),
        ],
      },
      error: undefined,
    } as Awaited<ReturnType<typeof getTitleEnrichmentAssessment>>);
    renderTitleDetail('?requirement=Identity%3Aigdb&match=1', undefined, 'metadata');
    const picker = await screen.findByRole('dialog', {
      name: 'Match IGDB',
    });
    expect(
      within(picker).getByRole('textbox', {
        name: 'Game name',
      }),
    ).toHaveValue('Super Mario World');
    expect(locationProbe()).toHaveTextContent('#identifiers-and-sources');
  });

  it('links new sources from the provider row reached from another section', async () => {
    const user = userEvent.setup();
    vi.mocked(listTitleProviderMatches).mockResolvedValue({
      data: [
        {
          providerId: 'igdb',
          providerName: 'IGDB',
          isAvailable: true,
          capabilities: [
            'metadata',
          ],
          revision: 'revision',
          state: 'NeedsMatch',
          externalId: null,
          game: null,
          confidence: null,
          lastError: null,
          metadataNeedsRefresh: false,
        },
      ],
      error: undefined,
    } as Awaited<ReturnType<typeof listTitleProviderMatches>>);
    renderTitleDetail('', undefined, 'releases');
    const rail = within(
      await screen.findByRole('complementary', {
        name: 'Title status',
      }),
    );
    await user.click(rail.getByText('Enrichment status'));
    await user.click(
      rail.getByRole('link', {
        name: 'Manage sources',
      }),
    );

    const sources = await screen.findByRole('region', {
      name: 'Identifiers and sources',
    });
    expect(sources).toHaveFocus();
    const provider = within(
      await within(sources).findByRole('group', {
        name: 'IGDB',
      }),
    );
    await user.click(
      provider.getByRole('button', {
        name: 'Link source',
      }),
    );
    expect(
      await screen.findByRole('dialog', {
        name: 'Match IGDB',
      }),
    ).toBeVisible();
  });

  it.each([
    [
      'metadata',
      'Metadata',
    ],
    [
      'artwork',
      'Artwork',
    ],
    [
      'releases',
      'Releases 1',
    ],
    [
      'ratings',
      'Ratings 1',
    ],
  ])('normalizes the old tab link for %s', async (section, label) => {
    renderTitleDetail(`?tab=${section}`);
    expect(
      await screen.findByRole('heading', {
        name: 'Super Mario World',
      }),
    ).toBeInTheDocument();
    expect(locationProbe()).toHaveTextContent(`/titles/title-1/${section}`);
    expect(locationProbe().textContent).not.toContain('?tab=');
    expect(titleSection(label)).toHaveAttribute('aria-current', 'page');
  });

  it.each([
    [
      'metadata',
      'Metadata',
    ],
    [
      'artwork',
      'Artwork',
    ],
    [
      'releases',
      'Releases 1',
    ],
    [
      'ratings',
      'Ratings 1',
    ],
  ])('normalizes the old editor link for %s', async (section, label) => {
    renderTitleDetail(`?editor=${section}`);
    expect(
      await screen.findByRole('heading', {
        name: 'Super Mario World',
      }),
    ).toBeInTheDocument();
    expect(locationProbe()).toHaveTextContent(`/titles/title-1/${section}`);
    expect(locationProbe().textContent).not.toContain('?editor=');
    expect(titleSection(label)).toHaveAttribute('aria-current', 'page');
  });

  it.each([
    [
      '?tab=releases&release=game-1',
    ],
    [
      '?editor=releases&release=game-1',
    ],
  ])('preserves the release inspector selection when redirecting %s', async (search) => {
    renderTitleDetail(search);
    expect(
      await screen.findByRole('complementary', {
        name: 'Super Mario World (USA)',
      }),
    ).toBeInTheDocument();
    expect(locationProbe()).toHaveTextContent('/titles/title-1/releases?release=game-1');
    expect(titleSection('Releases 1')).toHaveAttribute('aria-current', 'page');
  });

  it('opens role review from a legacy Artwork link', async () => {
    renderTitleDetail('?editor=artwork&role=Logo');
    const editor = await screen.findByRole('region', {
      name: 'Logo editor',
    });
    expect(editor).toBeInTheDocument();
    expect(locationProbe()).toHaveTextContent('/titles/title-1/artwork?role=Logo');
    expect(titleSection('Artwork')).toHaveAttribute('aria-current', 'page');
  });

  it('redirects the old Discs path to the Releases disc-sets anchor', async () => {
    renderTitleDetail('', undefined, 'discs');
    expect(
      await screen.findByRole('heading', {
        name: 'Super Mario World',
      }),
    ).toBeInTheDocument();
    expect(locationProbe()).toHaveTextContent('/titles/title-1/releases#disc-sets');
    expect(titleSection('Releases 1')).toHaveAttribute('aria-current', 'page');
  });

  it('opens the status summary from the legacy Enrichment link', async () => {
    renderTitleDetail('?editor=enrichment');
    expect(
      await screen.findByRole('button', {
        name: 'Enrich title',
      }),
    ).toBeInTheDocument();
    expect(locationProbe()).toHaveTextContent('/titles/title-1/metadata#title-status');
    expect(titleSection('Metadata')).toHaveAttribute('aria-current', 'page');
  });

  it('focuses the field and highlights its requirement on a Catalog deep link', async () => {
    vi.mocked(getTitleEnrichmentAssessment).mockResolvedValue({
      data: {
        ...assessment,
        requirements: [
          unsatisfied('Metadata:Genre', 'NeedsReview', 'ProviderSuppressed', [
            'ReviewMetadata',
          ]),
        ],
      },
      error: undefined,
    } as Awaited<ReturnType<typeof getTitleEnrichmentAssessment>>);
    renderTitleDetail('?requirement=Metadata%3AGenre', undefined, 'metadata');

    const rail = await screen.findByRole('complementary', {
      name: 'Title status',
    });
    const requirement = await within(rail).findByRole('link', {
      name: 'Genre',
    });
    expect(requirement.closest('li')).toHaveAttribute('data-current', 'true');
    expect(await screen.findByLabelText('Genre')).toHaveFocus();
  });

  it('falls back to Metadata for an unknown section', async () => {
    renderTitleDetail('', undefined, 'bogus');
    expect(
      await screen.findByRole('textbox', {
        name: 'Description',
      }),
    ).toHaveValue(title.description);
    expect(locationProbe()).toHaveTextContent('/titles/title-1/metadata');
    expect(titleSection('Metadata')).toHaveAttribute('aria-current', 'page');
  });

  it('withholds the match picker and assessment query from read-only users', async () => {
    permissions.canManageTitles = false;
    renderTitleDetail('?tab=match');
    expect(
      await screen.findByRole('heading', {
        name: 'Super Mario World',
      }),
    ).toBeInTheDocument();
    expect(titleSection('Metadata')).toHaveAttribute('aria-current', 'page');
    expect(
      screen.queryByRole('dialog', {
        name: 'Match IGDB',
      }),
    ).toBeNull();
    expect(
      screen.queryByRole('heading', {
        name: 'External sources',
      }),
    ).toBeNull();
    expect(getTitleEnrichmentAssessment).not.toHaveBeenCalled();
    expect(listTitleProviderMatches).not.toHaveBeenCalled();
  });

  it('clears a locked source and chains its accepted revision when writing the edited field alone', async () => {
    const user = userEvent.setup();
    mockGetTitleEnrichmentState.mockResolvedValue({
      data: {
        ...enrichmentState,
        fieldSourceOverrides: {
          Genre: 'igdb',
        },
        layers: [
          {
            ...userLayer,
            publisher: 'My publisher',
          },
          igdbLayer,
        ],
      },
      error: undefined,
    });
    mockUpdateTitleEnrichment.mockResolvedValueOnce({
      data: {
        ...title,
        revision: 'title-revision-2',
      },
      error: undefined,
      response: new Response(),
    });
    renderTitleDetail();
    await screen.findByRole('button', {
      name: 'Genre source: IGDB · Locked',
    });
    await user.click(screen.getByLabelText('Genre'));
    await user.click(
      await screen.findByRole('option', {
        name: /^Action\b/,
      }),
    );
    expect(mockUpdateTitle).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );

    await waitFor(() =>
      expect(mockUpdateTitle).toHaveBeenCalledWith({
        path: {
          titleId: 'title-1',
        },
        headers: {
          'If-Match': '"title-revision-2"',
        },
        body: {
          genre: 'Action',
        },
      }),
    );
    expect(mockUpdateTitleEnrichment).toHaveBeenCalledWith({
      path: {
        titleId: 'title-1',
      },
      headers: {
        'If-Match': '"title-revision-1"',
      },
      body: {
        overrides: {
          Genre: '',
        },
      },
    });
  });

  it('preserves a staged source when its save fails', async () => {
    const user = userEvent.setup();
    mockUpdateTitleEnrichment.mockResolvedValueOnce({
      error: {
        message: 'Failure',
      },
      response: new Response(null, {
        status: 500,
      }),
    });
    renderTitleDetail();

    await user.click(
      await screen.findByRole('button', {
        name: 'Genre source: IGDB',
      }),
    );
    await user.click(
      screen.getByRole('radio', {
        name: 'Use IGDB',
      }),
    );
    await user.keyboard('{Escape}');
    const save = screen.getByRole('button', {
      name: 'Save changes',
    });
    await user.click(save);

    expect(await screen.findByText('Could not save your draft. Try again.')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Genre source: IGDB · Locked',
      }),
    ).toBeInTheDocument();
    expect(save).toBeEnabled();
  });

  it('routes the hero name action to the focused Metadata field and patches the name alone', async () => {
    const user = userEvent.setup();
    renderTitleDetail('', undefined, 'artwork');
    await user.click(
      await screen.findByRole('link', {
        name: 'Edit title name',
      }),
    );
    expect(locationProbe()).toHaveTextContent('/titles/title-1/metadata?field=Name');

    const name = await screen.findByRole('textbox', {
      name: 'Title name',
    });
    expect(name).toHaveFocus();
    await user.clear(name);
    await user.type(name, 'Mario World');
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );

    await waitFor(() =>
      expect(mockUpdateTitle).toHaveBeenCalledWith({
        path: {
          titleId: 'title-1',
        },
        headers: {
          'If-Match': '"title-revision-1"',
        },
        body: {
          name: 'Mario World',
        },
      }),
    );
  });

  it('protects the metadata draft before confirming an external ID in Identifiers and sources', async () => {
    const user = userEvent.setup();
    renderTitleDetail('', undefined, 'metadata');
    const sources = await screen.findByRole('region', {
      name: 'Identifiers and sources',
    });
    const confirm = await within(sources).findByRole('button', {
      name: 'Confirm match',
    });
    await user.type(
      screen.getByRole('textbox', {
        name: 'Title name',
      }),
      ' edit',
    );
    expect(confirm).toBeDisabled();
    expect(confirmTitleProviderMatch).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    await user.click(confirm);

    await waitFor(() =>
      expect(confirmTitleProviderMatch).toHaveBeenCalledWith({
        path: {
          titleId: 'title-1',
          providerId: 'igdb',
        },
        headers: {
          'If-Match': '"revision"',
        },
      }),
    );
  });

  it('loads active rating categories from the published catalog in age order', async () => {
    const user = userEvent.setup();
    renderTitleDetail('', undefined, 'ratings');
    const table = await screen.findByRole('table', {
      name: 'Age ratings',
    });
    expect(within(table).getByText('All ages')).toBeInTheDocument();
    await user.click(
      within(table).getByRole('button', {
        name: 'ESRB rating',
      }),
    );
    const editor = await screen.findByRole('complementary', {
      name: 'ESRB rating',
    });
    const optionValues = (select: HTMLElement) =>
      within(select)
        .getAllByRole('option')
        .map((option) => option.getAttribute('value'));
    expect(
      optionValues(
        await within(editor).findByRole('combobox', {
          name: 'Age rating',
        }),
      ),
    ).toEqual([
      '',
      'E',
      'E10+',
      'T',
      'M',
      'RP',
    ]);

    await user.click(
      await screen.findByRole('button', {
        name: 'Add rating',
      }),
    );
    const add = await screen.findByRole('complementary', {
      name: 'Add age rating',
    });
    await user.selectOptions(
      within(add).getByRole('combobox', {
        name: 'Rating board',
      }),
      'Pegi',
    );
    expect(
      optionValues(
        within(add).getByRole('combobox', {
          name: 'Age rating',
        }),
      ),
    ).toEqual([
      '',
      'PEGI 3',
      'PEGI 12',
    ]);
  });

  it('tracks an untracked title from the hero toggle', async () => {
    const user = userEvent.setup();
    mockGetTitle.mockResolvedValue({
      data: {
        ...title,
        isTracked: false,
      },
      error: undefined,
    });
    renderTitleDetail();

    await user.click(
      await screen.findByRole('button', {
        name: 'Track title',
      }),
    );

    await waitFor(() => {
      expect(mockSetTitleTracking).toHaveBeenCalledWith({
        path: {
          titleId: 'title-1',
        },
        body: {
          tracked: true,
        },
      });
    });
  });

  it('untracks a tracked title from the hero toggle', async () => {
    const user = userEvent.setup();
    mockGetTitle.mockResolvedValue({
      data: {
        ...title,
        isTracked: true,
      },
      error: undefined,
    });
    renderTitleDetail();

    await user.click(
      await screen.findByRole('button', {
        name: 'Untrack title',
      }),
    );

    await waitFor(() => {
      expect(mockSetTitleTracking).toHaveBeenCalledWith({
        path: {
          titleId: 'title-1',
        },
        body: {
          tracked: false,
        },
      });
    });
  });

  it('shows backing catalog sources and linked provider IDs in Metadata', async () => {
    const user = userEvent.setup();
    vi.mocked(listTitleProviderMatches).mockResolvedValue({
      data: [
        {
          providerId: 'igdb',
          providerName: 'IGDB',
          isAvailable: true,
          capabilities: [
            'metadata',
          ],
          revision: 'revision',
          state: 'AutoMatched',
          externalId: '12345',
          metadataNeedsRefresh: false,
          game: {
            id: '12345',
            name: 'Super Mario World',
            url: 'https://www.igdb.com/games/super-mario-world',
          },
        },
      ],
      error: undefined,
    } as Awaited<ReturnType<typeof listTitleProviderMatches>>);
    renderTitleDetail();

    const sources = await screen.findByRole('region', {
      name: 'Identifiers and sources',
    });
    const metadata = screen.getByRole('form', {
      name: 'Title metadata',
    });
    expect(
      sources.compareDocumentPosition(metadata) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    await user.click(within(sources).getByText('Catalog sources'));
    expect(
      await screen.findByRole('link', {
        name: 'No-Intro SNES',
      }),
    ).toHaveAttribute('href', '/systems/plat-1?dat=dat-1&sourceTitle=title-1');
    expect(screen.getByText('1 entries')).toBeInTheDocument();
    expect(within(sources).getByText('Auto-matched')).toBeInTheDocument();
    expect(
      await screen.findByRole('link', {
        name: /Open IGDB ID 12345/,
      }),
    ).toHaveAttribute('href', 'https://www.igdb.com/games/super-mario-world');
    expect(mockGetTitleSourceReferences).toHaveBeenCalledWith({
      path: {
        titleId: 'title-1',
      },
      query: {
        cursor: undefined,
        limit: 100,
      },
      signal: expect.any(AbortSignal),
    });
  });

  it('moves destructive curation into the title action menu', async () => {
    const user = userEvent.setup();
    renderTitleDetail();

    await user.click(
      await screen.findByRole('button', {
        name: 'Title actions',
      }),
    );

    expect(await screen.findByText('Merge title...')).toBeInTheDocument();
    expect(screen.getByText('Move release...')).toBeInTheDocument();
  });

  it('previews DAT family names and waits for the operator to approve the reviewed grouping', async () => {
    const user = userEvent.setup();
    const clone = {
      ...title,
      name: 'Streets of Rage 2',
    };
    mockGetTitle.mockResolvedValue({
      data: clone,
      error: undefined,
    });
    vi.mocked(getDatTitleGrouping).mockResolvedValue({
      data: {
        suggestion: {
          parentTitleId: 'parent',
          parentTitleName: 'Streets of Rage II',
          parentTitleRevision: 'reviewed-parent',
          cloneTitleRevision: 'reviewed-clone',
          datName: 'Mega Drive',
          datVersion: 'version-2',
          releaseNames: [
            'Streets of Rage II (Japan, Europe)',
            'Streets of Rage 2 (USA)',
          ],
          evidence: 'active-dat-evidence',
        },
      },
    } as Awaited<ReturnType<typeof getDatTitleGrouping>>);
    mockMergeTitles.mockResolvedValue({
      data: clone,
      error: undefined,
    });
    renderTitleDetail();

    await user.click(
      await screen.findByRole('button', {
        name: 'Title actions',
      }),
    );
    await user.click(await screen.findByText('Review DAT family…'));
    const review = await screen.findByRole('dialog', {
      name: 'Review DAT title grouping',
    });
    expect(
      await within(review).findByText('Streets of Rage II (Japan, Europe)'),
    ).toBeInTheDocument();
    expect(within(review).getByText('Streets of Rage 2 (USA)')).toBeInTheDocument();
    expect(
      within(review).getByRole('combobox', {
        name: 'Title to keep',
      }),
    ).toHaveValue('Streets of Rage 2');
    expect(mockMergeTitles).not.toHaveBeenCalled();

    await user.click(
      within(review).getByRole('button', {
        name: 'Group titles',
      }),
    );

    await waitFor(() =>
      expect(mockMergeTitles).toHaveBeenCalledWith({
        path: {
          titleId: clone.id,
        },
        headers: {
          'If-Match': '"reviewed-clone"',
        },
        body: {
          sourceTitleId: 'parent',
          sourceTitleRevision: 'reviewed-parent',
          datGroupingEvidence: 'active-dat-evidence',
        },
      }),
    );
  });

  it('merges against the target the form loaded on selection, never one read at click time', async () => {
    const user = userEvent.setup();
    const target: TitleDetailContract = {
      ...title,
      id: 'title-2',
      revision: 'target-revision-1',
      name: 'Mario World Deluxe',
    };
    let showTarget: (value: unknown) => void = () => {};
    mockGetTitle.mockImplementation(
      ({
        path,
      }: {
        path: {
          titleId: string;
        };
      }) =>
        path.titleId === 'title-2'
          ? new Promise((resolve) => {
              showTarget = resolve;
            })
          : Promise.resolve({
              data: title,
              error: undefined,
            }),
    );
    mockSearchCatalog.mockResolvedValue({
      data: {
        items: [
          {
            id: 'title-2',
            name: 'Mario World Deluxe',
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      },
      error: undefined,
    });
    const targetReads = () =>
      mockGetTitle.mock.calls.filter(([options]) => options.path.titleId === 'title-2').length;
    let targetReadsAtMerge = -1;
    mockMergeTitles.mockImplementation(async () => {
      targetReadsAtMerge = targetReads();
      return {
        data: {
          ...target,
          revision: 'target-revision-2',
        },
        response: new Response(),
      };
    });
    renderTitleDetail();
    await user.click(
      await screen.findByRole('button', {
        name: 'Title actions',
      }),
    );
    await user.click(await screen.findByText('Merge title...'));
    const dialog = await screen.findByRole('dialog', {
      name: 'Merge Title',
    });

    await user.type(within(dialog).getByLabelText('Merge into'), 'Mario');
    await user.click(
      await screen.findByRole(
        'option',
        {
          name: /Mario World Deluxe/,
        },
        {
          timeout: 2000,
        },
      ),
    );

    await waitFor(() =>
      expect(mockGetTitle).toHaveBeenCalledWith({
        signal: expect.any(AbortSignal),
        path: {
          titleId: 'title-2',
        },
      }),
    );
    const merge = within(dialog).getByRole('button', {
      name: 'Merge',
    });
    expect(merge).toBeDisabled();
    showTarget({
      data: target,
      error: undefined,
    });
    expect(
      await within(dialog).findByText('"Mario World Deluxe" has 1 release.'),
    ).toBeInTheDocument();
    await waitFor(() => expect(merge).toBeEnabled());
    const readsBeforeClick = targetReads();

    await user.click(merge);

    await waitFor(() =>
      expect(mockMergeTitles).toHaveBeenCalledWith({
        path: {
          titleId: 'title-2',
        },
        headers: {
          'If-Match': '"target-revision-1"',
        },
        body: {
          sourceTitleId: 'title-1',
          sourceTitleRevision: 'title-revision-1',
        },
      }),
    );
    expect(targetReadsAtMerge).toBe(readsBeforeClick);
  });

  it('keeps the target revision it showed when a background refetch changes the target', async () => {
    const user = userEvent.setup();
    const target = (revision: string): TitleDetailContract => ({
      ...title,
      id: 'title-2',
      revision,
      name: 'Mario World Deluxe',
    });
    let targetRevision = 'target-revision-1';
    mockGetTitle.mockImplementation(
      ({
        path,
      }: {
        path: {
          titleId: string;
        };
      }) =>
        Promise.resolve({
          data: path.titleId === 'title-2' ? target(targetRevision) : title,
          error: undefined,
        }),
    );
    mockMergeTitles.mockResolvedValue({
      data: target('target-revision-3'),
      response: new Response(),
    });
    const dialog = await selectMergeTarget(user);
    expect(
      await within(dialog).findByText('"Mario World Deluxe" has 1 release.'),
    ).toBeInTheDocument();

    targetRevision = 'target-revision-2';
    act(() => {
      focusManager.setFocused(false);
      focusManager.setFocused(true);
    });
    // Click only once the refetch has delivered the newer revision, so keeping the shown one is what is tested.
    await waitFor(async () => {
      const targetReads = mockGetTitle.mock.calls.flatMap(([options], index) =>
        options.path.titleId === 'title-2'
          ? [
              mockGetTitle.mock.results[index]?.value,
            ]
          : [],
      );
      expect(targetReads.length).toBeGreaterThan(1);
      expect((await targetReads.at(-1))?.data?.revision).toBe('target-revision-2');
    });
    focusManager.setFocused(undefined);
    await user.click(
      within(dialog).getByRole('button', {
        name: 'Merge',
      }),
    );

    await waitFor(() =>
      expect(mockMergeTitles.mock.lastCall?.[0].headers).toEqual({
        'If-Match': '"target-revision-1"',
      }),
    );
  });

  it('shows the reloaded target after a conflict, so the retry sends its new revision', async () => {
    const user = userEvent.setup();
    let targetRevision = 'target-revision-1';
    mockGetTitle.mockImplementation(
      ({
        path,
      }: {
        path: {
          titleId: string;
        };
      }) =>
        Promise.resolve({
          data:
            path.titleId === 'title-2'
              ? {
                  ...title,
                  id: 'title-2',
                  revision: targetRevision,
                  name: 'Mario World Deluxe',
                }
              : title,
          error: undefined,
        }),
    );
    mockMergeTitles
      .mockImplementationOnce(async () => {
        targetRevision = 'target-revision-2';
        return {
          error: {
            status: 412,
            errorCode: 'Titles.PreconditionFailed',
            traceId: 'trace',
          },
          response: new Response(null, {
            status: 412,
          }),
        };
      })
      .mockResolvedValueOnce({
        data: {
          ...title,
          id: 'title-2',
          revision: 'target-revision-3',
        },
        response: new Response(),
      });
    const dialog = await selectMergeTarget(user);
    const merge = await within(dialog).findByRole('button', {
      name: 'Merge',
    });
    await waitFor(() => expect(merge).toBeEnabled());

    await user.click(merge);
    // The conflict refetches the target, and the settled form shows the reloaded one.
    await waitFor(() =>
      expect(
        mockGetTitle.mock.calls.filter(([options]) => options.path.titleId === 'title-2').length,
      ).toBeGreaterThan(1),
    );
    await waitFor(() => expect(merge).toBeEnabled());
    await user.click(merge);

    await waitFor(() =>
      expect(mockMergeTitles.mock.calls.map(([options]) => options.headers)).toEqual([
        {
          'If-Match': '"target-revision-1"',
        },
        {
          'If-Match': '"target-revision-2"',
        },
      ]),
    );
  });

  it('disables the merge when a refetch of the shown target fails', async () => {
    const user = userEvent.setup();
    let targetFails = false;
    mockGetTitle.mockImplementation(
      ({
        path,
      }: {
        path: {
          titleId: string;
        };
      }) =>
        Promise.resolve(
          path.titleId === 'title-2' && targetFails
            ? {
                data: undefined,
                error: {
                  status: 500,
                },
              }
            : {
                data:
                  path.titleId === 'title-2'
                    ? {
                        ...title,
                        id: 'title-2',
                        revision: 'target-revision-1',
                        name: 'Mario World Deluxe',
                      }
                    : title,
                error: undefined,
              },
        ),
    );
    const dialog = await selectMergeTarget(user);
    const merge = await within(dialog).findByRole('button', {
      name: 'Merge',
    });
    await waitFor(() => expect(merge).toBeEnabled());

    targetFails = true;
    act(() => {
      focusManager.setFocused(false);
      focusManager.setFocused(true);
    });
    expect(
      await within(dialog).findByText('Could not load the selected title.', undefined, {
        timeout: 5000,
      }),
    ).toBeInTheDocument();
    focusManager.setFocused(undefined);
    expect(merge).toBeDisabled();
    expect(mockMergeTitles).not.toHaveBeenCalled();
  });

  it('shows the selected target load error and keeps the merge disabled', async () => {
    const user = userEvent.setup();
    mockGetTitle.mockImplementation(
      ({
        path,
      }: {
        path: {
          titleId: string;
        };
      }) =>
        Promise.resolve(
          path.titleId === 'title-2'
            ? {
                data: undefined,
                error: {
                  status: 500,
                },
              }
            : {
                data: title,
                error: undefined,
              },
        ),
    );
    const dialog = await selectMergeTarget(user);

    expect(
      await within(dialog).findByText('Could not load the selected title.'),
    ).toBeInTheDocument();
    expect(
      within(dialog).getByRole('button', {
        name: 'Merge',
      }),
    ).toBeDisabled();
    expect(mockMergeTitles).not.toHaveBeenCalled();
  });
});

/** Opens the merge form and selects "Mario World Deluxe" (title-2) as the target. */
async function selectMergeTarget(user: ReturnType<typeof userEvent.setup>) {
  mockSearchCatalog.mockResolvedValue({
    data: {
      items: [
        {
          id: 'title-2',
          name: 'Mario World Deluxe',
        },
      ],
      nextCursor: null,
      hasNextPage: false,
    },
    error: undefined,
  });
  renderTitleDetail();
  await user.click(
    await screen.findByRole('button', {
      name: 'Title actions',
    }),
  );
  await user.click(await screen.findByText('Merge title...'));
  const dialog = await screen.findByRole('dialog', {
    name: 'Merge Title',
  });
  await user.type(within(dialog).getByLabelText('Merge into'), 'Mario');
  await user.click(
    await screen.findByRole(
      'option',
      {
        name: /Mario World Deluxe/,
      },
      {
        timeout: 2000,
      },
    ),
  );
  return dialog;
}
