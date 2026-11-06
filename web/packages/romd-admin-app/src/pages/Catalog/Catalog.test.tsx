import type { CatalogTitle, PageOfCatalogTitle, SystemResourceDto } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { completePage } from '../../test/fixtures/referencePages';
import { render } from '../../test/utils/render';
import { Catalog } from './index';

vi.mock('../../hooks/api/useManagedSystems', () => ({
  useManagedSystems: () => ({
    data: [
      {
        key: 'plat-1',
        name: 'Super Nintendo Entertainment System',
        enabled: true,
      },
      {
        key: 'disabled',
        name: 'Disabled system',
        enabled: false,
      },
    ],
    isLoading: false,
  }),
}));

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    getMe: vi.fn(),
    listAdminSystems: vi.fn(),
    listMissingTrackedTitles: vi.fn(),
    listTrackedTitleUpgrades: vi.fn(),
    listCatalogTitles: vi.fn(),
    getCatalogStats: vi.fn().mockResolvedValue({
      data: {
        totalCount: 1,
      },
    }),
    replaceTitleTracking: vi.fn(),
  };
});

import {
  getCatalogStats,
  getMe,
  listAdminSystems,
  listCatalogTitles,
  listMissingTrackedTitles,
  listTrackedTitleUpgrades,
  replaceTitleTracking,
} from '@romd/admin-api-client';

const mockGetCurrentUser = getMe as ReturnType<typeof vi.fn>;
const mockListPlatforms = listAdminSystems as ReturnType<typeof vi.fn>;
const mockListMissing = listMissingTrackedTitles as ReturnType<typeof vi.fn>;
const mockListUpgrades = listTrackedTitleUpgrades as ReturnType<typeof vi.fn>;
const mockSearchCatalog = listCatalogTitles as ReturnType<typeof vi.fn>;
const mockSetTitleTracking = replaceTitleTracking as ReturnType<typeof vi.fn>;

const platforms: SystemResourceDto[] = [
  {
    key: 'plat-1',
    name: 'Super Nintendo Entertainment System',
    compactLabel: 'SNES',
    ownership: 'Romd',
    revision: 'a'.repeat(64),
    builtInVersion: 1,
    description: null,
    icon: null,
    manufacturers: [],
    retired: false,
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
  items: [
    catalogTitle,
  ],
  nextCursor: null,
  hasNextPage: false,
};

function renderCatalog(initialEntry = '/catalog') {
  return render(
    <Routes>
      <Route
        path="/catalog"
        element={<Catalog />}
      />
    </Routes>,
    {
      routerOptions: {
        initialEntries: [
          initialEntry,
        ],
      },
    },
  );
}

describe('Catalog', () => {
  beforeEach(() => {
    localStorage.clear();
    mockGetCurrentUser.mockResolvedValue({
      data: null,
      error: {
        status: 401,
      },
    });
    mockListPlatforms.mockResolvedValue({
      data: completePage(platforms),
      error: undefined,
    });
    mockSearchCatalog.mockResolvedValue({
      data: catalogPage,
      error: undefined,
    });
    mockListMissing.mockResolvedValue({
      data: [],
      error: undefined,
    });
    mockListUpgrades.mockResolvedValue({
      data: [],
      error: undefined,
    });
    mockSetTitleTracking.mockResolvedValue({
      data: undefined,
      error: undefined,
    });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('defaults to tracked titles without a platform restriction', async () => {
    renderCatalog();

    expect(
      await screen.findByRole('heading', {
        name: 'Catalog',
      }),
    ).toBeInTheDocument();
    expect(await screen.findByText('Super Mario World')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('All systems')).toBeInTheDocument();
    expect(
      screen.getByRole('tab', {
        name: 'Tracked titles',
      }),
    ).toHaveAttribute('aria-selected', 'true');
    expect(mockSearchCatalog).toHaveBeenCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          tracked: 'tracked',
        }),
      }),
    );
    expect(mockSearchCatalog).toHaveBeenCalledWith(
      expect.objectContaining({
        query: expect.not.objectContaining({
          systemKey: expect.any(String),
        }),
      }),
    );
  });

  it('searches by title name and clears only the search query', async () => {
    const user = userEvent.setup();
    renderCatalog('/catalog?systemKey=plat-1&genre=Action');
    await screen.findByText('Super Mario World');
    await user.type(
      screen.getByRole('textbox', {
        name: 'Search titles',
      }),
      'Mario',
    );
    await waitFor(() =>
      expect(mockSearchCatalog).toHaveBeenLastCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({
            query: 'Mario',
            systemKey: 'plat-1',
            genre: 'Action',
          }),
        }),
      ),
    );
    expect(mockSearchCatalog).toHaveBeenCalledTimes(2);
    await user.click(
      screen.getByRole('button', {
        name: 'Clear search',
      }),
    );
    expect(
      screen.getByRole('textbox', {
        name: 'Search titles',
      }),
    ).toHaveValue('');
    await waitFor(() =>
      expect(mockSearchCatalog).toHaveBeenLastCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({
            systemKey: 'plat-1',
            genre: 'Action',
            sortBy: 'name',
          }),
        }),
      ),
    );
    expect(mockSearchCatalog.mock.calls.at(-1)?.[0].query).not.toHaveProperty('query');
  });

  it('resets an empty filtered search while preserving the tracked scope', async () => {
    mockSearchCatalog.mockResolvedValue({
      data: {
        items: [],
        hasNextPage: false,
      },
      error: undefined,
    });
    const user = userEvent.setup();
    renderCatalog('/catalog?q=Missing&genre=Action');
    await user.click(
      await screen.findByRole('button', {
        name: 'Reset search and filters',
      }),
    );
    await waitFor(() =>
      expect(mockSearchCatalog).toHaveBeenLastCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({
            tracked: 'tracked',
            sortBy: 'name',
          }),
        }),
      ),
    );
    expect(
      screen.getByRole('textbox', {
        name: 'Search titles',
      }),
    ).toHaveValue('');
    expect(mockSearchCatalog.mock.calls.at(-1)?.[0].query).not.toHaveProperty('genre');
  });

  it('offers only enabled systems and uses consistent system labels', async () => {
    const user = userEvent.setup();
    renderCatalog();
    expect(
      await screen.findByRole('columnheader', {
        name: 'System',
      }),
    ).toBeInTheDocument();
    await user.type(
      screen.getByRole('combobox', {
        name: 'System',
      }),
      'System',
    );
    // Mantine's dropdown transition remains hidden in jsdom.
    expect(
      await screen.findByRole('option', {
        name: 'Super Nintendo Entertainment System',
        hidden: true,
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('option', {
        name: 'Disabled system',
        hidden: true,
      }),
    ).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('option', {
        name: 'Super Nintendo Entertainment System',
        hidden: true,
      }),
    );
    await waitFor(() =>
      expect(mockSearchCatalog).toHaveBeenLastCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({
            systemKey: 'plat-1',
          }),
        }),
      ),
    );
  });

  it('switches between grid and list views', async () => {
    const user = userEvent.setup();
    renderCatalog();

    expect(await screen.findByText('Super Mario World')).toBeInTheDocument();
    expect(screen.getByTestId('catalog-list')).toBeInTheDocument();

    await user.click(
      screen.getByRole('button', {
        name: 'Grid view',
      }),
    );
    expect(await screen.findByTestId('catalog-grid')).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'List view',
      }),
    );

    expect(await screen.findByTestId('catalog-list')).toBeInTheDocument();
    expect(screen.queryByTestId('catalog-grid')).not.toBeInTheDocument();
    expect(screen.getByText('Super Mario World')).toBeInTheDocument();
  });

  it('shows removable chips for applied secondary filters', async () => {
    renderCatalog('/catalog?genre=Action&tracked=tracked');

    expect(
      await screen.findByText('Action', {
        selector: '.mantine-Pill-label',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Clear filters',
      }),
    ).toBeInTheDocument();
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
    expect(
      await screen.findByDisplayValue('Super Nintendo Entertainment System'),
    ).toBeInTheDocument();
  });

  it('labels and sends DAT release completeness independently from title payload availability', async () => {
    renderCatalog('/catalog?releaseCompleteness=partial');

    expect(await screen.findByText('DAT: Files present for some DAT releases')).toBeInTheDocument();
    expect(
      screen.getByRole('combobox', {
        name: 'DAT release coverage',
      }),
    ).toBeInTheDocument();
    await waitFor(() => {
      expect(mockSearchCatalog).toHaveBeenCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({
            releaseCompleteness: 'partial',
          }),
        }),
      );
    });
  });

  it('selects the URL-addressable tracked view with an immutable tracked filter', async () => {
    const user = userEvent.setup();
    renderCatalog('/catalog?view=tracked&tracked=untracked&genre=Action');

    expect(await screen.findByText('Super Mario World')).toBeInTheDocument();
    expect(
      screen.getByRole('tab', {
        name: 'Tracked titles',
      }),
    ).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByPlaceholderText('Tracked')).not.toBeInTheDocument();
    expect(mockSearchCatalog).toHaveBeenCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          tracked: 'tracked',
        }),
      }),
    );

    await user.click(
      screen.getByRole('button', {
        name: 'Clear filters',
      }),
    );
    await waitFor(() => {
      expect(mockSearchCatalog).toHaveBeenLastCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({
            tracked: 'tracked',
          }),
        }),
      );
    });
  });

  it.each([
    'missing',
    'upgrades',
  ])('migrates legacy %s review links into the main catalog', async (review) => {
    renderCatalog(`/catalog?view=${review}&genre=Action`);
    expect(await screen.findByTestId('catalog-list')).toBeInTheDocument();
    await waitFor(() =>
      expect(mockSearchCatalog).toHaveBeenLastCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({
            releaseReview: review,
            genre: 'Action',
            tracked: 'tracked',
          }),
        }),
      ),
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(mockListMissing).not.toHaveBeenCalled();
    expect(mockListUpgrades).not.toHaveBeenCalled();
  });

  it('keeps the catalog visible while changing inline filters and supports grid results', async () => {
    const user = userEvent.setup();
    renderCatalog();
    await screen.findByTestId('catalog-list');
    await user.click(
      screen.getByRole('button', {
        name: 'Filters',
        exact: true,
      }),
    );
    expect(
      screen.getByRole('region', {
        name: 'Catalog filters',
      }),
    ).toBeVisible();
    expect(screen.getByTestId('catalog-list')).toBeVisible();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('combobox', {
        name: 'Tracked release review',
      }),
    );
    await user.click(
      screen.getByRole('option', {
        name: 'Preferred release missing',
        hidden: true,
      }),
    );
    await waitFor(() =>
      expect(mockSearchCatalog).toHaveBeenLastCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({
            releaseReview: 'upgrades',
          }),
        }),
      ),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Grid view',
      }),
    );
    expect(screen.getByTestId('catalog-grid')).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Filters (1)',
      }),
    );
    expect(
      screen.queryByRole('region', {
        name: 'Catalog filters',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByText('Preferred release missing', {
        selector: '.mantine-Pill-label',
      }),
    ).toBeVisible();
  });

  it('shows the exact total alongside the loaded count', async () => {
    vi.mocked(getCatalogStats).mockResolvedValue({
      data: {
        totalCount: 2418,
      },
    } as Awaited<ReturnType<typeof getCatalogStats>>);
    mockSearchCatalog.mockResolvedValueOnce({
      data: {
        ...catalogPage,
        nextCursor: 'next',
        hasNextPage: true,
      },
    });
    mockSearchCatalog.mockResolvedValue({
      data: {
        items: [
          {
            ...catalogTitle,
            id: 'title-2',
            name: 'Another title',
          },
        ],
        hasNextPage: false,
        totalCount: null,
      },
    });
    renderCatalog();
    expect(await screen.findByText('1 of 2,418 titles')).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Load More',
      }),
    );
    expect(await screen.findByText('2 of 2,418 titles')).toBeInTheDocument();
    expect(getCatalogStats).toHaveBeenCalledTimes(1);
  });
  it('shows results before counting finishes and lets a failed count be retried', async () => {
    let finishCount: ((value: unknown) => void) | undefined;
    (getCatalogStats as ReturnType<typeof vi.fn>).mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finishCount = resolve;
        }),
    );
    renderCatalog();
    expect(await screen.findByText(catalogTitle.name)).toBeInTheDocument();
    expect(await screen.findByText('1 shown · counting…')).toBeInTheDocument();
    finishCount?.({
      error: {
        message: 'Unavailable',
      },
    });
    expect(
      await screen.findByRole('button', {
        name: 'Retry count',
      }),
    ).toBeInTheDocument();
    (getCatalogStats as ReturnType<typeof vi.fn>).mockResolvedValue({
      data: {
        totalCount: 42,
      },
    });
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Retry count',
      }),
    );
    expect(await screen.findByText('1 of 42 titles')).toBeInTheDocument();
  });
});
