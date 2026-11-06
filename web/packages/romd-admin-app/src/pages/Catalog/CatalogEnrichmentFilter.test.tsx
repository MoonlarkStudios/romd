import type { PageOfCatalogTitle, SearchCatalogData } from '@romd/admin-api-client';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes, useLocation, useNavigate, useNavigationType } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { completePage } from '../../test/fixtures/referencePages';
import { render } from '../../test/utils/render';
import { Catalog } from './index';

vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canUploadRoms: true,
    canManageTitles: true,
    canTriggerEnrichment: true,
    canManageUsers: false,
    role: 'Manager',
    hasRole: () => true,
  }),
}));
vi.mock('../../hooks/api/useManagedSystems', () => ({
  useManagedSystems: () => ({
    data: [
      {
        key: 'plat-1',
        name: 'Super Nintendo Entertainment System',
        enabled: true,
      },
    ],
    isLoading: false,
  }),
}));
vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getMe: vi.fn(),
  listAdminSystems: vi.fn(),
  listCatalogTitles: vi.fn(),
  getCatalogStats: vi.fn().mockResolvedValue({
    data: {
      totalCount: 1,
    },
  }),
  previewArtworkCompletion: vi.fn(),
}));

import {
  getMe,
  listAdminSystems,
  listCatalogTitles,
  previewArtworkCompletion,
} from '@romd/admin-api-client';

const page: PageOfCatalogTitle = {
  items: [
    {
      id: 'title-1',
      systemKey: 'plat-1',
      name: 'Super Mario World',
    },
  ],
  nextCursor: null,
  hasNextPage: false,
};

function Location() {
  const location = useLocation();
  return (
    <output
      data-testid="location"
      data-navigation={useNavigationType()}
    >
      {location.search}
    </output>
  );
}

function renderCatalog(initialEntry: string) {
  return render(
    <Routes>
      <Route
        path="/catalog"
        element={
          <>
            <Catalog />
            <Location />
          </>
        }
      />
      <Route
        path="/enrichment"
        element={<Location />}
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

const searchQueries = () =>
  vi
    .mocked(listCatalogTitles)
    .mock.calls.map(([options]) => (options as SearchCatalogData).query ?? {});
const lastSearch = () => searchQueries().at(-1);
const url = () => new URLSearchParams(screen.getByTestId('location').textContent ?? '');

async function openFilters() {
  const user = userEvent.setup();
  await screen.findByText('Super Mario World');
  await user.click(
    screen.getByRole('button', {
      name: /^Filters/,
    }),
  );
  return user;
}

beforeEach(() => {
  localStorage.clear();
  (getMe as ReturnType<typeof vi.fn>).mockResolvedValue({
    data: null,
    error: {
      status: 401,
    },
  });
  (listAdminSystems as ReturnType<typeof vi.fn>).mockResolvedValue({
    data: completePage([]),
    error: undefined,
  });
  (listCatalogTitles as ReturnType<typeof vi.fn>).mockResolvedValue({
    data: page,
    error: undefined,
  });
  (previewArtworkCompletion as ReturnType<typeof vi.fn>).mockResolvedValue({
    data: {
      titles: 1,
      roles: [],
    },
    error: undefined,
  });
});
afterEach(() => {
  vi.resetAllMocks();
});

describe('Catalog enrichment URL state', () => {
  it('normalizes an old flag-valued enrichmentStatus bookmark before the first request', async () => {
    renderCatalog(
      '/catalog?view=all&genre=Action&enrichmentStatus=NeedsReview&sortBy=rating&systemKey=plat-1',
    );
    await waitFor(() => expect(listCatalogTitles).toHaveBeenCalled());
    expect(searchQueries()[0]).toMatchObject({
      enrichment: [
        'NeedsReview',
      ],
      genre: 'Action',
      sortBy: 'rating',
      systemKey: 'plat-1',
      tracked: 'all',
    });
    await waitFor(() =>
      expect(url().getAll('enrichment')).toEqual([
        'NeedsReview',
      ]),
    );
    expect(searchQueries().every((query) => !('enrichmentStatus' in query))).toBe(true);
    expect(url().has('enrichmentStatus')).toBe(false);
    expect(
      Object.fromEntries(
        [
          ...url(),
        ].filter(([key]) => key !== 'enrichment'),
      ),
    ).toEqual({
      view: 'all',
      genre: 'Action',
      sortBy: 'rating',
      systemKey: 'plat-1',
    });
    expect(screen.getByTestId('location')).toHaveAttribute('data-navigation', 'REPLACE');
    expect(screen.getByText('Enrichment: Needs review')).toBeInTheDocument();
  });

  it('keeps a last-attempt value as its own filter', async () => {
    renderCatalog('/catalog?view=all&enrichmentStatus=LowConfidence');
    expect(await screen.findByText('Last attempt: Low confidence')).toBeInTheDocument();
    expect(searchQueries()[0]).toMatchObject({
      enrichmentStatus: 'LowConfidence',
    });
    expect(searchQueries()[0]).not.toHaveProperty('enrichment');
  });

  it('never sends an unknown status or flag', async () => {
    renderCatalog('/catalog?view=all&enrichmentStatus=Resolved&enrichment=Unknown&genre=Action');
    await waitFor(() => expect(listCatalogTitles).toHaveBeenCalled());
    expect(searchQueries()[0]).toMatchObject({
      genre: 'Action',
    });
    expect(searchQueries()[0]).not.toHaveProperty('enrichment');
    expect(searchQueries()[0]).not.toHaveProperty('enrichmentStatus');
    await waitFor(() => expect(url().toString()).toBe('view=all&genre=Action'));
  });

  it('dedupes repeated flags into the fixed chip order', async () => {
    renderCatalog(
      '/catalog?view=all&enrichment=Processing&enrichment=NeedsReview&enrichment=Processing',
    );
    await waitFor(() => expect(listCatalogTitles).toHaveBeenCalled());
    expect(searchQueries()[0]).toMatchObject({
      enrichment: [
        'NeedsReview',
        'Processing',
      ],
    });
    await waitFor(() =>
      expect(url().getAll('enrichment')).toEqual([
        'NeedsReview',
        'Processing',
      ]),
    );
  });
});

describe('Catalog enrichment filter', () => {
  it('requires every selected flag and encodes each as a repeated parameter', async () => {
    renderCatalog('/catalog?view=all');
    const user = await openFilters();
    expect(screen.getByText('Titles with all selected statuses')).toBeInTheDocument();
    expect(
      within(
        screen.getByRole('region', {
          name: 'Catalog filters',
        }),
      )
        .getAllByRole('checkbox')
        .map((box) => box.getAttribute('value')),
    ).toEqual([
      'NeedsReview',
      'Blocked',
      'Processing',
      'Automatic',
      'Settled',
    ]);
    await user.click(
      screen.getByRole('checkbox', {
        name: 'Blocked',
      }),
    );
    await user.click(
      screen.getByRole('checkbox', {
        name: 'Needs review',
      }),
    );
    await waitFor(() =>
      expect(lastSearch()).toMatchObject({
        enrichment: [
          'NeedsReview',
          'Blocked',
        ],
      }),
    );
    expect(url().getAll('enrichment')).toEqual([
      'NeedsReview',
      'Blocked',
    ]);
    expect(
      screen.getByRole('button', {
        name: 'Filters (2)',
      }),
    ).toBeInTheDocument();

    await user.click(
      screen.getByRole('button', {
        name: 'Filters (2)',
      }),
    );
    const pill = screen.getByText('Enrichment: Needs review').closest('.mantine-Pill-root');
    expect(screen.getByText('Enrichment: Blocked')).toBeInTheDocument();
    await user.click(
      within(pill as HTMLElement).getByRole('button', {
        hidden: true,
      }),
    );
    await waitFor(() =>
      expect(lastSearch()).toMatchObject({
        enrichment: [
          'Blocked',
        ],
      }),
    );
  });

  it('makes Settled exclusive: choosing it clears and disables the flags until it is cleared', async () => {
    renderCatalog('/catalog?view=all');
    const user = await openFilters();
    await user.click(
      screen.getByRole('checkbox', {
        name: 'Needs review',
      }),
    );
    await user.click(
      screen.getByRole('checkbox', {
        name: /^Settled/,
      }),
    );
    await waitFor(() =>
      expect(lastSearch()).toMatchObject({
        enrichment: [
          'Settled',
        ],
      }),
    );
    expect(url().getAll('enrichment')).toEqual([
      'Settled',
    ]);
    for (const name of [
      'Needs review',
      'Blocked',
      'Processing',
      'Automatic',
    ]) {
      expect(
        screen.getByRole('checkbox', {
          name,
        }),
      ).not.toBeChecked();
      expect(
        screen.getByRole('checkbox', {
          name,
        }),
      ).toBeDisabled();
    }

    await user.click(
      screen.getByRole('checkbox', {
        name: /^Settled/,
      }),
    );
    await waitFor(() => expect(lastSearch()).not.toHaveProperty('enrichment'));
    expect(url().has('enrichment')).toBe(false);
    for (const name of [
      'Needs review',
      'Blocked',
      'Processing',
      'Automatic',
    ])
      expect(
        screen.getByRole('checkbox', {
          name,
        }),
      ).toBeEnabled();
  });

  it('filters by the last attempt with human labels', async () => {
    renderCatalog('/catalog?view=all');
    const user = await openFilters();
    await user.click(
      screen.getByRole('combobox', {
        name: 'Last attempt',
      }),
    );
    const options = within(
      screen.getByRole('listbox', {
        name: 'Last attempt',
        hidden: true,
      }),
    ).getAllByRole('option', {
      hidden: true,
    });
    expect(options.map((option) => option.textContent)).toEqual([
      'Never attempted',
      'Pending',
      'Completed',
      'Failed',
      'Not found',
      'Low confidence',
    ]);
    await user.click(
      screen.getByRole('option', {
        name: 'Not found',
        hidden: true,
      }),
    );
    await waitFor(() =>
      expect(lastSearch()).toMatchObject({
        enrichmentStatus: 'NotFound',
      }),
    );
    expect(lastSearch()).not.toHaveProperty('enrichment');
    expect(screen.getByText('Last attempt: Not found')).toBeInTheDocument();
  });
});

describe('Legacy catalog enrichment links', () => {
  it('opens a link to the retired catalog run view as that run in the enrichment workspace', async () => {
    renderCatalog('/catalog?view=all&artworkJob=saved-run');
    await waitFor(() => expect(url().get('activity')).toBe('saved-run'));
    expect(screen.getByTestId('location')).toHaveAttribute('data-navigation', 'REPLACE');
    expect(previewArtworkCompletion).not.toHaveBeenCalled();
  });
});

function BackButton() {
  const navigate = useNavigate();
  return (
    <button
      type="button"
      onClick={() => navigate(-1)}
    >
      Back
    </button>
  );
}

it('replaces filter history and returns from title details to the same search', async () => {
  const user = userEvent.setup();
  render(
    <Routes>
      <Route
        path="/previous"
        element={<div>Previous page</div>}
      />
      <Route
        path="/catalog"
        element={
          <>
            <Catalog />
            <Location />
            <BackButton />
          </>
        }
      />
      <Route
        path="/titles/:id"
        element={<BackButton />}
      />
    </Routes>,
    {
      routerOptions: {
        initialEntries: [
          '/previous',
          '/catalog',
        ],
        initialIndex: 1,
      },
    },
  );
  await screen.findByText('Super Mario World');
  await user.click(
    screen.getByRole('button', {
      name: 'Filters',
      exact: true,
    }),
  );
  await user.click(
    screen.getByRole('combobox', {
      name: 'Tracked release review',
    }),
  );
  await user.click(
    screen.getByRole('option', {
      name: 'Without a complete release',
      hidden: true,
    }),
  );
  await waitFor(() => expect(url().get('releaseReview')).toBe('missing'));
  await user.type(
    screen.getByRole('textbox', {
      name: 'Search titles',
    }),
    'Mario',
  );
  await waitFor(() => expect(url().get('q')).toBe('Mario'));
  expect(screen.getByTestId('location')).toHaveAttribute('data-navigation', 'REPLACE');
  await user.click(screen.getByText('Super Mario World'));
  await user.click(
    screen.getByRole('button', {
      name: 'Back',
    }),
  );
  await screen.findByText('Super Mario World');
  expect(url().get('q')).toBe('Mario');
  expect(url().get('releaseReview')).toBe('missing');
  await user.click(
    screen.getByRole('button', {
      name: 'Back',
    }),
  );
  expect(await screen.findByText('Previous page')).toBeInTheDocument();
});
