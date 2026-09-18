import type {
  MetadataLayerDto,
  RatingBoardCatalogResponse,
  TitleDetail as TitleDetailDto,
  TitleEnrichmentStateResponse,
} from '@romd/admin-api-client';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { TitleDetail } from './index';

vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canUploadRoms: true,
    canManageTitles: true,
    canTriggerEnrichment: true,
    canManageUsers: false,
    role: 'Manager',
    hasRole: (role: string) => ['User', 'Contributor', 'Manager'].includes(role),
  }),
}));

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    associateExternalId: vi.fn(),
    confirmExternalId: vi.fn(),
    getTitleProviderMatches: vi.fn().mockResolvedValue({ data: [{ providerId: 'igdb', providerName: 'IGDB', isAvailable: true, capabilities: ['metadata'], revision: 'revision', state: 'AutoMatched', externalId: '42', metadataNeedsRefresh: false }], error: undefined }),
    confirmTitleProviderMatch: vi.fn().mockResolvedValue({ error: undefined }),
    getCatalogFilters: vi.fn(),
    getRatingBoards: vi.fn(),
    getTitleDetails: vi.fn(),
    getTitleEnrichmentState: vi.fn(),
    getTitleSourceReferences: vi.fn(),
    mergeTitles: vi.fn(),
    moveGame: vi.fn(),
    searchCatalog: vi.fn(),
    setFieldOverrides: vi.fn(),
    setTitleContentRating: vi.fn(),
    setTitleTracking: vi.fn(),
    triggerTitleEnrichment: vi.fn(),
    updateTitleMetadata: vi.fn(),
  };
});

import {
  associateExternalId,
  confirmExternalId,
  confirmTitleProviderMatch,
  getCatalogFilters,
  getRatingBoards,
  getTitleDetails,
  getTitleEnrichmentState,
  getTitleProviderMatches,
  getTitleSourceReferences,
  searchCatalog,
  setFieldOverrides,
  setTitleContentRating,
  setTitleTracking,
  triggerTitleEnrichment,
  updateTitleMetadata,
} from '@romd/admin-api-client';

const mockAssociateExternalId = associateExternalId as ReturnType<typeof vi.fn>;
const mockConfirmExternalId = confirmExternalId as ReturnType<typeof vi.fn>;
const mockGetCatalogFilters = getCatalogFilters as ReturnType<typeof vi.fn>;
const mockGetRatingBoards = getRatingBoards as ReturnType<typeof vi.fn>;
const mockGetTitleDetails = getTitleDetails as ReturnType<typeof vi.fn>;
const mockGetTitleEnrichmentState = getTitleEnrichmentState as ReturnType<typeof vi.fn>;
const mockGetTitleSourceReferences = getTitleSourceReferences as ReturnType<typeof vi.fn>;
const mockSearchCatalog = searchCatalog as ReturnType<typeof vi.fn>;
const mockSetFieldOverrides = setFieldOverrides as ReturnType<typeof vi.fn>;
const mockSetTitleContentRating = setTitleContentRating as ReturnType<typeof vi.fn>;
const mockSetTitleTracking = setTitleTracking as ReturnType<typeof vi.fn>;
const mockTriggerTitleEnrichment = triggerTitleEnrichment as ReturnType<typeof vi.fn>;
const mockUpdateTitleMetadata = updateTitleMetadata as ReturnType<typeof vi.fn>;

const title: TitleDetailDto = {
  id: 'title-1',
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
      minimumAge: '0',
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
  completionPercent: '100',
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
      revision: null,
      isComplete: true,
      files: [],
    },
  ],
};

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
  layers: [userLayer, igdbLayer],
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

const ratingBoardCatalog: RatingBoardCatalogResponse = {
  boards: [
    {
      board: 'Esrb',
      name: 'Esrb',
      categories: [
        { code: 'E', designation: 'Rated', minimumAge: '0' },
        { code: 'E10+', designation: 'Rated', minimumAge: '10' },
        { code: 'T', designation: 'Rated', minimumAge: '13' },
        { code: 'M', designation: 'Rated', minimumAge: '17' },
      ],
    },
    {
      board: 'Pegi',
      name: 'Pegi',
      categories: [
        { code: 'PEGI 3', designation: 'Rated', minimumAge: '3' },
        { code: 'PEGI 12', designation: 'Rated', minimumAge: '12' },
      ],
    },
  ],
};

function renderTitleDetail() {
  return render(
    <Routes>
      <Route path="/titles/:titleId" element={<TitleDetail />} />
      <Route path="/settings" element={<div>settings probe</div>} />
    </Routes>,
    { routerOptions: { initialEntries: ['/titles/title-1'] } },
  );
}

describe('TitleDetail', () => {
  beforeEach(() => {
    mockGetTitleDetails.mockResolvedValue({ data: title, error: undefined });
    mockGetCatalogFilters.mockResolvedValue({
      data: {
        genres: [
          { value: 'Action', count: 2 },
          { value: 'RPG', count: 1 },
        ],
        years: [],
        manufacturers: [],
        regions: [],
        languages: [],
        contentRatings: [
          { value: 'E', count: 1 },
          { value: 'T', count: 3 },
        ],
        platforms: [],
        enrichmentStatuses: [],
      },
      error: undefined,
    });
    mockGetTitleEnrichmentState.mockResolvedValue({ data: enrichmentState, error: undefined });
    mockGetTitleSourceReferences.mockResolvedValue({
      data: [
        {
          catalogSourceId: 'catsrc-1',
          kind: 'Dat',
          name: 'No-Intro SNES',
          status: 'Active',
          entryCount: '1',
        },
      ],
      error: undefined,
    });
    mockGetRatingBoards.mockResolvedValue({ data: ratingBoardCatalog, error: undefined });
    mockSetTitleContentRating.mockResolvedValue({ data: title, error: undefined });
    mockUpdateTitleMetadata.mockResolvedValue({ data: title, error: undefined });
    mockSetFieldOverrides.mockResolvedValue({ data: undefined, error: undefined });
    mockAssociateExternalId.mockResolvedValue({ data: title, error: undefined });
    mockConfirmExternalId.mockResolvedValue({ data: undefined, error: undefined });
    vi.mocked(getTitleProviderMatches).mockResolvedValue({ data: [{ providerId: 'igdb', providerName: 'IGDB', isAvailable: true, capabilities: ['metadata'], revision: 'revision', state: 'AutoMatched', externalId: '12345', metadataNeedsRefresh: false }], error: undefined } as Awaited<ReturnType<typeof getTitleProviderMatches>>);
    vi.mocked(confirmTitleProviderMatch).mockResolvedValue({ error: undefined } as Awaited<ReturnType<typeof confirmTitleProviderMatch>>);
    mockTriggerTitleEnrichment.mockResolvedValue({ data: undefined, error: undefined });
    mockSetTitleTracking.mockResolvedValue({ data: undefined, error: undefined });
    mockSearchCatalog.mockResolvedValue({ data: { items: [], nextCursor: null, hasNextPage: false }, error: undefined });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('uses Metadata as the default inspector and removes legacy curation tabs', async () => {
    renderTitleDetail();

    expect(await screen.findByRole('heading', { name: 'Super Mario World' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Metadata' })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    expect(screen.queryByRole('tab', { name: 'Enrichment' })).not.toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Curation' })).not.toBeInTheDocument();
    expect(await screen.findByText('Provider matches')).toBeInTheDocument();
    expect(await screen.findByText('#12345')).toBeInTheDocument();
  });

  it('stages source selection until Save changes', async () => {
    const user = userEvent.setup();
    renderTitleDetail();
    await user.click(await screen.findByRole('button', { name: /genre: rpg/i }));
    await user.click(await screen.findByRole('radio', { name: 'Choose source' }));
    await user.click(await screen.findByRole('radio', { name: 'Select IGDB for Genre' }));
    expect(mockSetFieldOverrides).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(mockSetFieldOverrides).toHaveBeenCalledWith({
      path: { titleId: 'title-1' }, body: { overrides: { Genre: 'igdb' } },
    }));
  });

  it('discards draft changes when cancelled', async () => {
    const user = userEvent.setup();
    renderTitleDetail();
    await user.click(await screen.findByRole('button', { name: /genre: rpg/i }));
    await user.click(await screen.findByRole('radio', { name: 'Custom', exact: true }));
    fireEvent.change(screen.getByLabelText('Genre user override'), { target: { value: 'Action' } });
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(mockUpdateTitleMetadata).not.toHaveBeenCalled();
    expect(mockSetFieldOverrides).not.toHaveBeenCalled();
    await user.click(await screen.findByRole('button', { name: /genre: rpg/i }));
    expect(await screen.findByRole('radio', { name: 'Automatic', exact: true })).toBeChecked();
  });

  it('saves custom values while preserving other field overrides', async () => {
    const user = userEvent.setup();
    mockGetTitleEnrichmentState.mockResolvedValue({ data: {
      ...enrichmentState, fieldSourceOverrides: { Genre: 'igdb' },
      layers: [{ ...userLayer, publisher: 'My publisher' }, igdbLayer],
    }, error: undefined });
    renderTitleDetail();
    await user.click(await screen.findByRole('button', { name: /genre: rpg/i }));
    await user.click(await screen.findByRole('radio', { name: 'Custom', exact: true }));
    fireEvent.change(screen.getByLabelText('Genre user override'), { target: { value: 'Action' } });
    expect(mockUpdateTitleMetadata).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(mockUpdateTitleMetadata).toHaveBeenCalledWith({
      path: { titleId: 'title-1' }, body: expect.objectContaining({ genre: 'Action', publisher: 'My publisher' }),
    }));
    expect(mockSetFieldOverrides).toHaveBeenCalledWith({ path: { titleId: 'title-1' }, body: { overrides: { Genre: '' } } });
  });

  it('returns to automatic only after saving', async () => {
    const user = userEvent.setup();
    mockGetTitleEnrichmentState.mockResolvedValue({ data: {
      ...enrichmentState, fieldSourceOverrides: { Genre: 'igdb' },
      layers: [{ ...userLayer, genre: 'Action', publisher: 'My publisher' }, igdbLayer],
    }, error: undefined });
    renderTitleDetail();
    await user.click(await screen.findByRole('button', { name: /genre: rpg/i }));
    await user.click(await screen.findByRole('radio', { name: 'Automatic', exact: true }));
    expect(mockSetFieldOverrides).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(mockSetFieldOverrides).toHaveBeenCalledWith({ path: { titleId: 'title-1' }, body: { overrides: { Genre: '' } } }));
    expect(mockUpdateTitleMetadata).toHaveBeenCalledWith({ path: { titleId: 'title-1' }, body: expect.objectContaining({ genre: null, publisher: 'My publisher' }) });
  });

  it('keeps the draft open when saving fails', async () => {
    const user = userEvent.setup();
    mockSetFieldOverrides.mockResolvedValueOnce({ error: { message: 'Failure' } });
    renderTitleDetail();
    await user.click(await screen.findByRole('button', { name: /genre: rpg/i }));
    await user.click(await screen.findByRole('radio', { name: 'Choose source' }));
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText(/Could not finish saving/)).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Select IGDB for Genre' })).toHaveAttribute('aria-checked', 'true');
  });

  it('preserves explicit choices when applying a provider', async () => {
    const user = userEvent.setup();
    mockGetTitleEnrichmentState.mockResolvedValue({ data: {
      ...enrichmentState,
      layers: [{ ...userLayer, genre: 'Action' }, igdbLayer],
      fieldSourceOverrides: { Rating: 'igdb' },
    }, error: undefined });
    renderTitleDetail();
    await user.click(await screen.findByRole('button', { name: 'Metadata actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Apply from provider' }));
    expect(await screen.findAllByText('Keep existing')).toHaveLength(2);
    await user.click(await screen.findByRole('button', { name: 'Apply to 5 fields' }));
    await waitFor(() => expect(mockSetFieldOverrides).toHaveBeenCalledWith({
      path: { titleId: 'title-1' },
      body: { overrides: { Description: 'igdb', ReleaseDate: 'igdb', Publisher: 'igdb', Developer: 'igdb', Players: 'igdb' } },
    }));
  });

  it('renames a title without replacing other custom values', async () => {
    const user = userEvent.setup();
    mockGetTitleEnrichmentState.mockResolvedValue({ data: {
      ...enrichmentState, layers: [{ ...userLayer, genre: 'Action', rating: 88 }, igdbLayer],
    }, error: undefined });
    renderTitleDetail();
    await user.click(await screen.findByRole('button', { name: 'Edit title name' }));
    await user.clear(await screen.findByLabelText('Title name'));
    await user.type(screen.getByLabelText('Title name'), 'Mario World');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => {
      expect(mockUpdateTitleMetadata).toHaveBeenCalledWith({
        path: { titleId: 'title-1' },
        body: expect.objectContaining({
          name: 'Mario World',
          genre: 'Action',
          description: null,
          rating: 88,
        }),
      });
    });
  });

  it('confirms external IDs from the Metadata tab', async () => {
    const user = userEvent.setup();
    renderTitleDetail();

    await screen.findByText('Provider matches');
    await user.click(await screen.findByRole('button', { name: 'IGDB match actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Confirm match' }));

    await waitFor(() => {
      expect(confirmTitleProviderMatch).toHaveBeenCalledWith({ path: { titleId: 'title-1', providerId: 'igdb' }, body: { expectedRevision: 'revision' } });
    });
  });

  it('shows per-board content ratings with provenance on the Ratings tab', async () => {
    const user = userEvent.setup();
    renderTitleDetail();

    await user.click(await screen.findByRole('tab', { name: /Ratings/ }));

    // Effective ESRB rating with its source and classification.
    expect(await screen.findByText('All ages')).toBeInTheDocument();
    // Official mark rendered via the icon registry (hero strip + panel summary row).
    expect(screen.getAllByAltText('ESRB Everyone').length).toBeGreaterThan(0);
    expect(screen.getByLabelText('ESRB user override')).toHaveValue('');
    // A board with no rating is still listed for managers so it can be set.
    expect(screen.getByLabelText('PEGI user override')).toBeInTheDocument();
  });

  it('saves a user content rating override from the Ratings tab', async () => {
    const user = userEvent.setup();
    renderTitleDetail();

    await user.click(await screen.findByRole('tab', { name: /Ratings/ }));
    fireEvent.change(await screen.findByLabelText('ESRB user override'), {
      target: { value: 'E10+' },
    });

    await waitFor(() => {
      expect(mockSetTitleContentRating).toHaveBeenCalledWith({
        path: { titleId: 'title-1' },
        body: { board: 'Esrb', code: 'E10+' },
      });
    });
  });

  it('clears a user content rating override from the Ratings tab', async () => {
    const user = userEvent.setup();
    mockGetTitleDetails.mockResolvedValue({
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

    await user.click(await screen.findByRole('tab', { name: /Ratings/ }));
    const select = await screen.findByLabelText('ESRB user override');
    expect(select).toHaveValue('E10+');

    fireEvent.change(select, { target: { value: '' } });

    await waitFor(() => {
      expect(mockSetTitleContentRating).toHaveBeenCalledWith({
        path: { titleId: 'title-1' },
        body: { board: 'Esrb', code: null },
      });
    });
  });

  it('tracks an untracked title from the hero toggle', async () => {
    const user = userEvent.setup();
    mockGetTitleDetails.mockResolvedValue({ data: { ...title, isTracked: false }, error: undefined });
    renderTitleDetail();

    await user.click(await screen.findByRole('button', { name: 'Track title' }));

    await waitFor(() => {
      expect(mockSetTitleTracking).toHaveBeenCalledWith({
        path: { titleId: 'title-1' },
        body: { tracked: true },
      });
    });
  });

  it('untracks a tracked title from the hero toggle', async () => {
    const user = userEvent.setup();
    mockGetTitleDetails.mockResolvedValue({ data: { ...title, isTracked: true }, error: undefined });
    renderTitleDetail();

    await user.click(await screen.findByRole('button', { name: 'Untrack title' }));

    await waitFor(() => {
      expect(mockSetTitleTracking).toHaveBeenCalledWith({
        path: { titleId: 'title-1' },
        body: { tracked: false },
      });
    });
  });

  it('shows backing sources within the Releases workspace', async () => {
    const user = userEvent.setup();
    renderTitleDetail();

    await screen.findByRole('heading', { name: 'Super Mario World' });

    const tabs = screen.getAllByRole('tab').map((tab) => tab.textContent);
    expect(tabs).toEqual(['Metadata', 'Releases1', 'Artwork', 'Ratings1']);

    await user.click(screen.getByRole('tab', { name: 'Releases 1' }));
    await user.click(await screen.findByRole('button', { name: 'Catalog sources (1)' }));

    expect(await screen.findByText('No-Intro SNES')).toBeInTheDocument();
    expect(screen.getByText('1 entries')).toBeInTheDocument();
    expect(mockGetTitleSourceReferences).toHaveBeenCalledWith({ path: { titleId: 'title-1' } });
  });

  it('moves destructive curation into the title action menu', async () => {
    const user = userEvent.setup();
    renderTitleDetail();

    await user.click(await screen.findByRole('button', { name: 'Title actions' }));

    expect(await screen.findByText('Merge title...')).toBeInTheDocument();
    expect(screen.getByText('Move release...')).toBeInTheDocument();
  });
});
