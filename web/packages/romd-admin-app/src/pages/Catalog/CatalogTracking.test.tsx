import { MantineProvider } from '@mantine/core';
import type { CatalogTitle, PageOfCatalogTitle, SystemResourceDto } from '@romd/admin-api-client';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { Catalog } from './index';

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
    getCurrentUser: vi.fn(),
    listAdminSystems: vi.fn(),
    searchCatalog: vi.fn(),
    setTitleTracking: vi.fn(),
    setTitlesTracking: vi.fn(),
  };
});

import {
  getCurrentUser,
  listAdminSystems,
  searchCatalog,
  setTitlesTracking,
  setTitleTracking,
} from '@romd/admin-api-client';

const mockGetCurrentUser = getCurrentUser as ReturnType<typeof vi.fn>;
const mockListPlatforms = listAdminSystems as ReturnType<typeof vi.fn>;
const mockSearchCatalog = searchCatalog as ReturnType<typeof vi.fn>;
const mockSetTitleTracking = setTitleTracking as ReturnType<typeof vi.fn>;
const mockSetTitlesTracking = setTitlesTracking as ReturnType<typeof vi.fn>;

const platforms: SystemResourceDto[] = [
  { key: 'plat-1', name: 'Super Nintendo Entertainment System', compactLabel: 'SNES', ownership: 'Romd', builtInVersion: 1, description: null, icon: null, manufacturers: [], retired: false },
];

const catalogTitle: CatalogTitle = {
  id: 'title-1',
  systemKey: 'plat-1',
  name: 'Super Mario World',
  localPayloadVersionCount: '1',
  totalVersionCount: '1',
  coverUrl: null,
  isTracked: false,
};

const catalogPage: PageOfCatalogTitle = { items: [catalogTitle], nextCursor: null, hasNextPage: false };

function renderCatalog(initialEntry = '/catalog') {
  return render(
    <Routes>
      <Route path="/catalog" element={<MantineProvider env="test"><Catalog /></MantineProvider>} />
    </Routes>,
    { routerOptions: { initialEntries: [initialEntry] } },
  );
}

describe('Catalog tracking affordance', () => {
  beforeEach(() => {
    localStorage.clear();
    mockGetCurrentUser.mockResolvedValue({ data: null, error: { status: 401 } });
    mockListPlatforms.mockResolvedValue({ data: platforms, error: undefined });
    mockSearchCatalog.mockResolvedValue({ data: catalogPage, error: undefined });
    mockSetTitleTracking.mockResolvedValue({ data: undefined, error: undefined });
    mockSetTitlesTracking.mockResolvedValue({ data: undefined, error: undefined });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('tracks a single title from the catalog card toggle', async () => {
    const user = userEvent.setup();
    renderCatalog();

    await screen.findByText('Super Mario World');
    await user.click(screen.getByRole('button', { name: 'Track Super Mario World' }));

    await waitFor(() => {
      expect(mockSetTitleTracking).toHaveBeenCalledWith({
        path: { titleId: 'title-1' },
        body: { tracked: true },
      });
    });
  });

  it('bulk-tracks selected titles', async () => {
    const user = userEvent.setup();
    renderCatalog();

    await screen.findByText('Super Mario World');
    await user.click(screen.getByRole('checkbox', { name: 'Select Super Mario World' }));
    await user.click(await screen.findByRole('button', { name: 'Track' }));

    await waitFor(() => {
      expect(mockSetTitlesTracking).toHaveBeenCalledWith({
        body: { titleIds: ['title-1'], tracked: true },
      });
    });
  });

  it('forwards the tracked filter to catalog search', async () => {
    renderCatalog('/catalog?tracked=tracked');

    await waitFor(() => {
      expect(mockSearchCatalog).toHaveBeenCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({ tracked: 'tracked' }),
        }),
      );
    });
  });

  it('requires confirmation to untrack and preserves the title when canceled', async () => {
    mockSearchCatalog.mockResolvedValue({ data: { ...catalogPage, items: [{ ...catalogTitle, isTracked: true }] } });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('button', { name: 'Actions for Super Mario World' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Untrack Super Mario World' }));
    expect(mockSetTitleTracking).not.toHaveBeenCalled();
    expect(await screen.findByText('Existing library membership is not changed by untracking yet.')).toBeInTheDocument();
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(mockSetTitleTracking).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Actions for Super Mario World' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Untrack Super Mario World' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Untrack', exact: true }));
    await waitFor(() => expect(mockSetTitleTracking).toHaveBeenCalledWith({ path: { titleId: 'title-1' }, body: { tracked: false } }));
  });

  it('confirms bulk untracking and retains the selection on failure', async () => {
    mockSearchCatalog.mockResolvedValue({ data: { ...catalogPage, items: [{ ...catalogTitle, isTracked: true }] } });
    mockSetTitlesTracking.mockResolvedValue({ error: { status: 500 } });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('checkbox', { name: 'Select loaded titles' }));
    await user.click(screen.getByRole('button', { name: 'Untrack', exact: true }));
    expect(mockSetTitlesTracking).not.toHaveBeenCalled();
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Untrack', exact: true }));
    expect(await screen.findByText('Could not untrack titles. Try again.')).toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('clears selection when changing catalog scope', async () => {
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('checkbox', { name: 'Select loaded titles' }));
    expect(screen.getByText('1 selected')).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'All titles' }));
    await waitFor(() => expect(mockSearchCatalog).toHaveBeenLastCalledWith(expect.objectContaining({ query: expect.objectContaining({ tracked: 'all' }) })));
    expect(screen.queryByText('1 selected')).not.toBeInTheDocument();
  });
});
