import type {
  CatalogTitle,
  PageOfCatalogTitle,
  SystemResourceDto,
  TrackedCollectionTitleDto,
} from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { Catalog } from './index';

vi.mock('../../hooks/api/useManagedSystems', () => ({
  useManagedSystems: () => ({
    data: [
      { key: 'plat-1', name: 'Super Nintendo Entertainment System', enabled: true },
      { key: 'disabled', name: 'Disabled system', enabled: false },
    ],
    isLoading: false,
  }),
}));

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    getCurrentUser: vi.fn(),
    listAdminSystems: vi.fn(),
    listMissingTrackedTitles: vi.fn(),
    listTrackedTitleUpgrades: vi.fn(),
    searchCatalog: vi.fn(),
    setTitleTracking: vi.fn(),
  };
});

import {
  getCurrentUser,
  listAdminSystems,
  listMissingTrackedTitles,
  listTrackedTitleUpgrades,
  searchCatalog,
  setTitleTracking,
} from '@romd/admin-api-client';

const mockGetCurrentUser = getCurrentUser as ReturnType<typeof vi.fn>;
const mockListPlatforms = listAdminSystems as ReturnType<typeof vi.fn>;
const mockListMissing = listMissingTrackedTitles as ReturnType<typeof vi.fn>;
const mockListUpgrades = listTrackedTitleUpgrades as ReturnType<typeof vi.fn>;
const mockSearchCatalog = searchCatalog as ReturnType<typeof vi.fn>;
const mockSetTitleTracking = setTitleTracking as ReturnType<typeof vi.fn>;

const platforms: SystemResourceDto[] = [
  {
    key: 'plat-1',
    name: 'Super Nintendo Entertainment System',
    compactLabel: 'SNES',
    ownership: 'Romd', builtInVersion: 1, description: null, icon: null, manufacturers: [], retired: false,
  },
];

const catalogTitle: CatalogTitle = {
  id: 'title-1',
  systemKey: 'plat-1',
  name: 'Super Mario World',
  localPayloadVersionCount: '1',
  totalVersionCount: '1',
  coverUrl: null,
};

const catalogPage: PageOfCatalogTitle = {
  items: [catalogTitle],
  nextCursor: null,
  hasNextPage: false,
};

const missingTitle: TrackedCollectionTitleDto = {
  titleId: 'missing-1',
  systemKey: 'plat-1',
  platformName: 'Super Nintendo Entertainment System',
  titleName: 'EarthBound',
  isSatisfied: false,
  hasUpgrade: false,
  isPinned: false,
};

const upgradeTitle: TrackedCollectionTitleDto = {
  titleId: 'upgrade-1',
  systemKey: 'plat-1',
  platformName: 'Super Nintendo Entertainment System',
  titleName: 'Chrono Trigger',
  isSatisfied: true,
  hasUpgrade: true,
  isPinned: false,
  desiredRelease: { catalogReleaseId: 'release-2', name: 'USA Rev 2' },
  ownedRelease: { catalogReleaseId: 'release-1', name: 'Europe Rev 1' },
};

function renderCatalog(initialEntry = '/catalog') {
  return render(
    <Routes>
      <Route path="/catalog" element={<Catalog />} />
    </Routes>,
    { routerOptions: { initialEntries: [initialEntry] } },
  );
}

describe('Catalog', () => {
  beforeEach(() => {
    localStorage.clear();
    mockGetCurrentUser.mockResolvedValue({ data: null, error: { status: 401 } });
    mockListPlatforms.mockResolvedValue({ data: platforms, error: undefined });
    mockSearchCatalog.mockResolvedValue({ data: catalogPage, error: undefined });
    mockListMissing.mockResolvedValue({ data: [missingTitle], error: undefined });
    mockListUpgrades.mockResolvedValue({ data: [upgradeTitle], error: undefined });
    mockSetTitleTracking.mockResolvedValue({ data: undefined, error: undefined });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('defaults to tracked titles without a platform restriction', async () => {
    renderCatalog();

    expect(await screen.findByRole('heading', { name: 'Catalog' })).toBeInTheDocument();
    expect(await screen.findByText('Super Mario World')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('All systems')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Tracked titles' })).toHaveAttribute('aria-selected', 'true');
    expect(mockSearchCatalog).toHaveBeenCalledWith(expect.objectContaining({ query: expect.objectContaining({ tracked: 'tracked' }) }));
    expect(mockSearchCatalog).toHaveBeenCalledWith(
      expect.objectContaining({
        query: expect.not.objectContaining({ systemKey: expect.any(String) }),
      }),
    );
  });

  it('offers only enabled systems and uses consistent system labels', async () => {
    const user = userEvent.setup();
    renderCatalog();
    expect(await screen.findByRole('columnheader', { name: 'System' })).toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: 'System' }), 'System');
    // Mantine's dropdown transition remains hidden in jsdom.
    expect(await screen.findByRole('option', { name: 'Super Nintendo Entertainment System', hidden: true })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'Disabled system', hidden: true })).not.toBeInTheDocument();
    await user.click(screen.getByRole('option', { name: 'Super Nintendo Entertainment System', hidden: true }));
    await waitFor(() => expect(mockSearchCatalog).toHaveBeenLastCalledWith(
      expect.objectContaining({ query: expect.objectContaining({ systemKey: 'plat-1' }) }),
    ));
  });

  it('switches between grid and list views', async () => {
    const user = userEvent.setup();
    renderCatalog();

    expect(await screen.findByText('Super Mario World')).toBeInTheDocument();
    expect(screen.getByTestId('catalog-list')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Grid view' }));
    expect(await screen.findByTestId('catalog-grid')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'List view' }));

    expect(await screen.findByTestId('catalog-list')).toBeInTheDocument();
    expect(screen.queryByTestId('catalog-grid')).not.toBeInTheDocument();
    expect(screen.getByText('Super Mario World')).toBeInTheDocument();
  });

  it('shows removable chips for applied secondary filters', async () => {
    renderCatalog('/catalog?genre=Action&tracked=tracked');

    expect(await screen.findByText('Action')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Clear filters' })).toBeInTheDocument();
  });

  it('honors the platform URL filter on the global catalog', async () => {
    renderCatalog('/catalog?systemKey=plat-1&enrichmentStatus=None');

    await waitFor(() => {
      expect(mockSearchCatalog).toHaveBeenCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({
            enrichmentStatus: 'None',
            systemKey: 'plat-1',
          }),
        }),
      );
    });
    expect(await screen.findByDisplayValue('Super Nintendo Entertainment System')).toBeInTheDocument();
  });

  it('labels and sends DAT release completeness independently from title payload availability', async () => {
    renderCatalog('/catalog?releaseCompleteness=partial');

    expect(await screen.findByText('DAT: Files present for some DAT releases')).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'DAT release coverage' })).not.toBeInTheDocument();
    await waitFor(() => {
      expect(mockSearchCatalog).toHaveBeenCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({ releaseCompleteness: 'partial' }),
        }),
      );
    });
  });

  it('selects the URL-addressable tracked view with an immutable tracked filter', async () => {
    const user = userEvent.setup();
    renderCatalog('/catalog?view=tracked&tracked=untracked&genre=Action');

    expect(await screen.findByText('Super Mario World')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Tracked titles' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByPlaceholderText('Tracked')).not.toBeInTheDocument();
    expect(mockSearchCatalog).toHaveBeenCalledWith(
      expect.objectContaining({ query: expect.objectContaining({ tracked: 'tracked' }) }),
    );

    await user.click(screen.getByRole('button', { name: 'Clear filters' }));
    await waitFor(() => {
      expect(mockSearchCatalog).toHaveBeenLastCalledWith(
        expect.objectContaining({ query: expect.objectContaining({ tracked: 'tracked' }) }),
      );
    });
  });

  it('renders missing tracked titles from the dedicated missing endpoint', async () => {
    renderCatalog('/catalog?view=missing');

    expect(await screen.findByTestId('catalog-missing-list')).toBeInTheDocument();
    expect(screen.getByText('EarthBound')).toHaveAttribute('href', '/titles/missing-1');
    expect(screen.getByText('No complete release')).toBeInTheDocument();
    expect(mockListMissing).toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('renders upgrade state and release context from the dedicated upgrades endpoint', async () => {
    renderCatalog('/catalog?view=upgrades');

    expect(await screen.findByTestId('catalog-upgrades-list')).toBeInTheDocument();
    expect(screen.getByText('Chrono Trigger')).toHaveAttribute('href', '/titles/upgrade-1');
    expect(screen.getByText('Preferred release missing', { selector: '.mantine-Badge-label' })).toBeInTheDocument();
    expect(screen.getByText('Preferred: USA Rev 2 · Owned: Europe Rev 1')).toBeInTheDocument();
    expect(mockListUpgrades).toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('renders an empty missing view', async () => {
    mockListMissing.mockResolvedValue({ data: [], error: undefined });

    renderCatalog('/catalog?view=missing');

    expect(await screen.findByText('Every tracked title has a complete release.')).toBeInTheDocument();
  });

  it('renders an upgrades endpoint error without substituting local-payload filters', async () => {
    mockListUpgrades.mockResolvedValue({ data: undefined, error: { status: 500 } });

    renderCatalog('/catalog?view=upgrades');

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Failed to load tracked title upgrades. Please try again.',
    );
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });
});
