import type {
  EnrichmentAssessmentDto,
  MetadataLayerDto,
  TitleDetail as TitleDetailDto,
  TitleEnrichmentStateResponse,
} from '@romd/admin-api-client';
import { focusManager } from '@tanstack/react-query';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes, useLocation } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AuthContextValue } from '../../contexts/AuthContext';
import { installReferenceCatalog } from '../../test/fixtures/referenceCatalog';
import { writtenTitleFixture } from '../../test/msw/fixtures/enrichment';
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
    getTitleEnrichmentAssessment: vi.fn(),
    getAdminCatalogSnapshot: vi.fn(),
    getTitle: vi.fn(),
    getTitleEnrichmentState: vi.fn(),
    listTitleSourceReferences: vi.fn(),
    mergeTitles: vi.fn(),
    moveGame: vi.fn(),
    listCatalogTitles: vi.fn(),
    updateTitleEnrichment: vi.fn(),
    deleteTitleContentRating: vi.fn(),
    replaceTitleContentRating: vi.fn(),
    replaceTitleTracking: vi.fn(),
    triggerTitleEnrichment: vi.fn(),
    updateTitle: vi.fn(),
  };
});

import {
  confirmTitleProviderMatch,
  deleteTitleContentRating,
  getAdminCatalogSnapshot,
  getCatalogFilters,
  getTitle,
  getTitleEnrichmentAssessment,
  getTitleEnrichmentState,
  listCatalogTitles,
  listTitleProviderMatches,
  listTitleSourceReferences,
  mergeTitles,
  replaceTitleContentRating,
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
const mockDeleteTitleContentRating = deleteTitleContentRating as ReturnType<typeof vi.fn>;
const mockReplaceTitleContentRating = replaceTitleContentRating as ReturnType<typeof vi.fn>;
const mockSetTitleTracking = replaceTitleTracking as ReturnType<typeof vi.fn>;
const mockTriggerTitleEnrichment = triggerTitleEnrichment as ReturnType<typeof vi.fn>;
const mockUpdateTitle = updateTitle as ReturnType<typeof vi.fn>;

const title: TitleDetailDto = writtenTitleFixture({
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

const userLayer: MetadataLayerDto = {
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

const igdbLayer: MetadataLayerDto = {
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

const assessment: EnrichmentAssessmentDto = {
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
  const location = useLocation();
  return <output aria-label="Location">{location.search}</output>;
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

function renderTitleDetail(search = '', catalogReturnTo?: string) {
  return render(
    <Routes>
      <Route
        path="/titles/:titleId"
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
            pathname: '/titles/title-1',
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
        },
      ]),
      error: undefined,
    });
    mockGetCatalogSnapshot.mockResolvedValue(installReferenceCatalog(referenceCatalog).root());
    vi.mocked(getTitleEnrichmentAssessment).mockResolvedValue({
      data: assessment,
      error: undefined,
    } as Awaited<ReturnType<typeof getTitleEnrichmentAssessment>>);
    mockReplaceTitleContentRating.mockResolvedValue({
      data: title,
      error: undefined,
    });
    mockDeleteTitleContentRating.mockResolvedValue({
      data: title,
      error: undefined,
    });
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

  it('defaults to Metadata with enrichment details confined to their tab', async () => {
    renderTitleDetail();

    expect(
      await screen.findByRole('heading', {
        name: 'Super Mario World',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('tab', {
        name: 'Metadata',
      }),
    ).toHaveAttribute('aria-selected', 'true');
    expect(
      screen.getByRole('tab', {
        name: /Enrichment/,
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: 'Open enrichment workspace',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('tab', {
        name: 'Curation',
      }),
    ).not.toBeInTheDocument();
    expect(
      await screen.findByRole('button', {
        name: /genre: rpg/i,
      }),
    ).toBeInTheDocument();
    expect(screen.queryByText('Provider matches')).not.toBeInTheDocument();
    expect(listTitleProviderMatches).not.toHaveBeenCalled();
  });

  it('keeps the banner stable and preserves the catalog return URL across sections', async () => {
    const user = userEvent.setup();
    renderTitleDetail('', '/catalog?q=mario&system=snes');
    const hero = await screen.findByTestId('title-header');
    await user.click(
      await screen.findByRole('button', {
        name: 'View enrichment details',
      }),
    );
    expect(
      screen.getByRole('tab', {
        name: /Enrichment/,
      }),
    ).toHaveAttribute('aria-selected', 'true');
    expect(
      await screen.findByRole('link', {
        name: 'Open enrichment workspace',
      }),
    ).toHaveAttribute('href', '/enrichment?title=title-1');
    for (const name of [
      'Artwork',
      'Releases 1',
      'Ratings 1',
      'Metadata',
    ]) {
      await user.click(
        screen.getByRole('tab', {
          name,
        }),
      );
      expect(screen.getByTestId('title-header')).toBe(hero);
      expect(hero).not.toHaveAttribute('data-compact');
      expect(
        screen.getByRole('link', {
          name: 'Catalog',
        }),
      ).toHaveAttribute('href', '/catalog?q=mario&system=snes');
      expect(
        screen.queryByRole('link', {
          name: 'Open enrichment workspace',
        }),
      ).not.toBeInTheDocument();
    }
  });

  it('shows the current enrichment status chips in the hero', async () => {
    renderTitleDetail();

    const status = await screen.findByRole('group', {
      name: 'Enrichment status',
    });
    expect(
      within(status)
        .getAllByText(/./)
        .map((chip) => chip.textContent),
    ).toEqual([
      'Needs review',
      'Blocked',
      'Processing',
    ]);
    expect(getTitleEnrichmentAssessment).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          titleId: 'title-1',
        },
      }),
    );
  });

  it('opens provider matches from the Match tab link', async () => {
    renderTitleDetail('?tab=match');

    expect(
      await screen.findByRole('tab', {
        name: /Enrichment/,
      }),
    ).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByText('Provider matches')).toBeInTheDocument();
    expect(await screen.findByText('#12345')).toBeInTheDocument();
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
  ])('keeps the ?tab=%s link working', async (tab, label) => {
    renderTitleDetail(`?tab=${tab}`);

    expect(
      await screen.findByRole('tab', {
        name: label,
      }),
    ).toHaveAttribute('aria-selected', 'true');
  });

  it('rewrites old tab links to the editor param, preserving other params', async () => {
    renderTitleDetail('?tab=releases&release=game-1');

    expect(
      await screen.findByRole('tab', {
        name: 'Releases 1',
      }),
    ).toHaveAttribute('aria-selected', 'true');
    expect(
      screen.getByRole('status', {
        name: 'Location',
      }),
    ).toHaveTextContent('?release=game-1&editor=releases');
  });

  it('shows status chips in the compact artwork header', async () => {
    renderTitleDetail('?editor=artwork');

    expect(
      await screen.findByRole('tab', {
        name: 'Artwork',
      }),
    ).toHaveAttribute('aria-selected', 'true');
    const status = await screen.findByRole('group', {
      name: 'Enrichment status',
    });
    expect(
      within(status)
        .getAllByText(/./)
        .map((chip) => chip.textContent),
    ).toEqual([
      'Needs review',
      'Blocked',
      'Processing',
    ]);
  });

  it('falls back to Metadata for unknown tabs', async () => {
    renderTitleDetail('?tab=bogus');

    expect(
      await screen.findByRole('tab', {
        name: 'Metadata',
      }),
    ).toHaveAttribute('aria-selected', 'true');
    expect(
      await screen.findByRole('button', {
        name: /genre: rpg/i,
      }),
    ).toBeInTheDocument();
  });

  it('hides the Match tab from roles that cannot manage titles', async () => {
    permissions.canManageTitles = false;
    renderTitleDetail('?tab=match');

    expect(
      await screen.findByRole('tab', {
        name: 'Metadata',
      }),
    ).toHaveAttribute('aria-selected', 'true');
    expect(
      screen.queryByRole('tab', {
        name: 'Match',
      }),
    ).not.toBeInTheDocument();
    expect(listTitleProviderMatches).not.toHaveBeenCalled();
    expect(getTitleEnrichmentAssessment).not.toHaveBeenCalled();
  });

  it('stages source selection until Save changes', async () => {
    const user = userEvent.setup();
    renderTitleDetail();
    await user.click(
      await screen.findByRole('button', {
        name: /genre: rpg/i,
      }),
    );
    await user.click(
      await screen.findByRole('radio', {
        name: 'Choose source',
      }),
    );
    await user.click(
      await screen.findByRole('radio', {
        name: 'Select IGDB for Genre',
      }),
    );
    expect(mockUpdateTitleEnrichment).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() =>
      expect(mockUpdateTitleEnrichment).toHaveBeenCalledWith({
        path: {
          titleId: 'title-1',
        },
        headers: {
          'If-Match': '"title-revision-1"',
        },
        body: {
          overrides: {
            Genre: 'igdb',
          },
        },
      }),
    );
  });

  it('discards draft changes only after confirming cancellation', async () => {
    const user = userEvent.setup();
    renderTitleDetail();
    await user.click(
      await screen.findByRole('button', {
        name: /genre: rpg/i,
      }),
    );
    await user.click(
      await screen.findByRole('radio', {
        name: 'Custom',
        exact: true,
      }),
    );
    fireEvent.change(screen.getByLabelText('Genre user override'), {
      target: {
        value: 'Action',
      },
    });
    await user.click(
      screen.getByRole('button', {
        name: 'Cancel',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Discard',
      }),
    );
    expect(mockUpdateTitle).not.toHaveBeenCalled();
    expect(mockUpdateTitleEnrichment).not.toHaveBeenCalled();
    await user.click(
      await screen.findByRole('button', {
        name: /genre: rpg/i,
      }),
    );
    expect(
      await screen.findByRole('radio', {
        name: 'Automatic',
        exact: true,
      }),
    ).toBeChecked();
  });

  it('saves a custom value as a patch of that field alone', async () => {
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
    renderTitleDetail();
    await user.click(
      await screen.findByRole('button', {
        name: /genre: rpg/i,
      }),
    );
    await user.click(
      await screen.findByRole('radio', {
        name: 'Custom',
        exact: true,
      }),
    );
    fireEvent.change(screen.getByLabelText('Genre user override'), {
      target: {
        value: 'Action',
      },
    });
    mockUpdateTitleEnrichment.mockResolvedValueOnce({
      data: {
        ...title,
        revision: 'title-revision-2',
      },
      error: undefined,
      response: new Response(),
    });
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
    // The save's first write sends the revision the draft started from; the second chains that write's ETag.
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

  it('returns to automatic only after saving', async () => {
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
            genre: 'Action',
            publisher: 'My publisher',
          },
          igdbLayer,
        ],
      },
      error: undefined,
    });
    renderTitleDetail();
    await user.click(
      await screen.findByRole('button', {
        name: /genre: rpg/i,
      }),
    );
    await user.click(
      await screen.findByRole('radio', {
        name: 'Automatic',
        exact: true,
      }),
    );
    expect(mockUpdateTitleEnrichment).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() =>
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
      }),
    );
    expect(mockUpdateTitle).toHaveBeenCalledWith({
      path: {
        titleId: 'title-1',
      },
      headers: {
        'If-Match': '"title-revision-1"',
      },
      body: {
        genre: null,
      },
    });
  });

  it('keeps the draft open when saving fails', async () => {
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
        name: /genre: rpg/i,
      }),
    );
    await user.click(
      await screen.findByRole('radio', {
        name: 'Choose source',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    expect(await screen.findByText(/Could not finish saving/)).toBeInTheDocument();
    expect(
      screen.getByRole('radio', {
        name: 'Select IGDB for Genre',
      }),
    ).toHaveAttribute('aria-checked', 'true');
  });

  it('preserves explicit choices when applying a provider', async () => {
    const user = userEvent.setup();
    mockGetTitleEnrichmentState.mockResolvedValue({
      data: {
        ...enrichmentState,
        layers: [
          {
            ...userLayer,
            genre: 'Action',
          },
          igdbLayer,
        ],
        fieldSourceOverrides: {
          Rating: 'igdb',
        },
      },
      error: undefined,
    });
    renderTitleDetail();
    await user.click(
      await screen.findByRole('button', {
        name: 'Metadata actions',
      }),
    );
    await user.click(
      await screen.findByRole('menuitem', {
        name: 'Apply from provider',
      }),
    );
    expect(await screen.findAllByText('Keep existing')).toHaveLength(2);
    await user.click(
      await screen.findByRole('button', {
        name: 'Apply to 5 fields',
      }),
    );
    await waitFor(() =>
      expect(mockUpdateTitleEnrichment).toHaveBeenCalledWith({
        path: {
          titleId: 'title-1',
        },
        headers: {
          'If-Match': '"title-revision-1"',
        },
        body: {
          overrides: {
            Description: 'igdb',
            ReleaseDate: 'igdb',
            Publisher: 'igdb',
            Developer: 'igdb',
            Players: 'igdb',
          },
        },
      }),
    );
  });

  it('renames a title with a patch of the name alone', async () => {
    const user = userEvent.setup();
    mockGetTitleEnrichmentState.mockResolvedValue({
      data: {
        ...enrichmentState,
        layers: [
          {
            ...userLayer,
            genre: 'Action',
            rating: 88,
          },
          igdbLayer,
        ],
      },
      error: undefined,
    });
    renderTitleDetail();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit title name',
      }),
    );
    await user.clear(await screen.findByLabelText('Title name'));
    await user.type(screen.getByLabelText('Title name'), 'Mario World');
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );

    await waitFor(() => {
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
      });
    });
  });

  it('confirms external IDs from the Match tab', async () => {
    const user = userEvent.setup();
    renderTitleDetail();

    await user.click(
      await screen.findByRole('tab', {
        name: /Enrichment/,
      }),
    );
    await screen.findByText('Provider matches');
    await user.click(
      await screen.findByRole('button', {
        name: 'Confirm match',
      }),
    );

    await waitFor(() => {
      expect(confirmTitleProviderMatch).toHaveBeenCalledWith({
        path: {
          titleId: 'title-1',
          providerId: 'igdb',
        },
        headers: {
          'If-Match': '"revision"',
        },
      });
    });
  });

  it('shows per-board content ratings with provenance on the Ratings tab', async () => {
    const user = userEvent.setup();
    renderTitleDetail();

    await user.click(
      await screen.findByRole('tab', {
        name: /Ratings/,
      }),
    );

    // Effective ESRB rating with its source and classification.
    expect(await screen.findByText('All ages')).toBeInTheDocument();
    // Official mark rendered via the icon registry (hero strip + panel summary row).
    expect(screen.getAllByAltText('ESRB Everyone').length).toBeGreaterThan(0);
    // The board catalog loads independently of the title's effective ratings.
    expect(await screen.findByLabelText('ESRB user override')).toHaveValue('');
    // A board with no rating is still listed for managers so it can be set.
    expect(screen.getByLabelText('PEGI user override')).toBeInTheDocument();
  });

  it('offers each board its catalog categories youngest first, pending last and retired hidden', async () => {
    const user = userEvent.setup();
    renderTitleDetail();

    await user.click(
      await screen.findByRole('tab', {
        name: /Ratings/,
      }),
    );

    const options = (label: string) =>
      within(screen.getByLabelText(label))
        .getAllByRole('option')
        .map((option) => option.getAttribute('value'));
    await waitFor(() =>
      expect(options('ESRB user override')).toEqual([
        '',
        'E',
        'E10+',
        'T',
        'M',
        'RP',
      ]),
    );
    expect(options('PEGI user override')).toEqual([
      '',
      'PEGI 3',
      'PEGI 12',
    ]);
  });

  it('saves a user content rating override from the Ratings tab', async () => {
    const user = userEvent.setup();
    renderTitleDetail();

    await user.click(
      await screen.findByRole('tab', {
        name: /Ratings/,
      }),
    );
    fireEvent.change(await screen.findByLabelText('ESRB user override'), {
      target: {
        value: 'E10+',
      },
    });

    await waitFor(() => {
      expect(mockReplaceTitleContentRating).toHaveBeenCalledWith({
        path: {
          titleId: 'title-1',
          board: 'Esrb',
        },
        headers: {
          'If-Match': '"title-revision-1"',
        },
        body: {
          code: 'E10+',
        },
      });
    });
  });

  it('clears a user content rating override from the Ratings tab', async () => {
    const user = userEvent.setup();
    mockGetTitle.mockResolvedValue({
      data: {
        ...title,
        contentRatings: [
          {
            board: 'Esrb',
            code: 'E10+',
            designation: 'Rated',
            minimumAge: '10',
            sourceId: 'user',
            externalRatingId: null,
            descriptors: [],
            synopsis: null,
          },
        ],
      },
      error: undefined,
    });
    renderTitleDetail();

    await user.click(
      await screen.findByRole('tab', {
        name: /Ratings/,
      }),
    );
    const select = await screen.findByLabelText('ESRB user override');
    expect(select).toHaveValue('E10+');

    fireEvent.change(select, {
      target: {
        value: '',
      },
    });

    await waitFor(() => {
      expect(mockDeleteTitleContentRating).toHaveBeenCalledWith({
        path: {
          titleId: 'title-1',
          board: 'Esrb',
        },
        headers: {
          'If-Match': '"title-revision-1"',
        },
      });
    });
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

  it('shows backing sources within the Releases workspace', async () => {
    const user = userEvent.setup();
    renderTitleDetail();

    await screen.findByRole('heading', {
      name: 'Super Mario World',
    });

    const tabs = screen.getAllByRole('tab').map((tab) => tab.textContent);
    expect(tabs).toEqual([
      'Metadata',
      'Artwork',
      'Releases1',
      'Discs',
      'Ratings1',
      'Enrichment',
    ]);

    await user.click(
      screen.getByRole('tab', {
        name: 'Releases 1',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Sources (1)',
      }),
    );

    expect(await screen.findByText('No-Intro SNES')).toBeInTheDocument();
    expect(screen.getByText('1 entries')).toBeInTheDocument();
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

  it('merges against the target the form loaded on selection, never one read at click time', async () => {
    const user = userEvent.setup();
    const target: TitleDetailDto = {
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
    const target = (revision: string): TitleDetailDto => ({
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
