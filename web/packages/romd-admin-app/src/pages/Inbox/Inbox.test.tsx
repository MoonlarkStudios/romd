import type { LibraryStats, PageOfRom, SystemResourceDto, UnroutedDat } from '@romd/admin-api-client';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { Inbox } from './index';

const permissions = vi.hoisted(() => ({
  canUploadRoms: true,
  canManageTitles: true,
  canTriggerEnrichment: true,
  canManageUsers: false,
  role: 'Manager',
  hasRole: () => true,
}));

vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => permissions,
}));

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    listUnroutedDats: vi.fn(),
    listAdminSystems: vi.fn(),
    getLibraryStats: vi.fn(),
    listRoms: vi.fn(),
    assignDatPlatform: vi.fn(),
    addPlatformAlias: vi.fn(),
    createSystems: vi.fn(),
    getAdminCatalogSnapshot: vi.fn(),
    listAdminCompanies: vi.fn(),
    getCurrentUser: vi.fn(),
  };
});

import {
  addPlatformAlias,
  assignDatPlatform,
  createSystems,
  getAdminCatalogSnapshot,
  getCurrentUser,
  getLibraryStats,
  listAdminCompanies,
  listAdminSystems,
  listRoms,
  listUnroutedDats,
} from '@romd/admin-api-client';

const mockListUnroutedDats = listUnroutedDats as ReturnType<typeof vi.fn>;
const mockListPlatforms = listAdminSystems as ReturnType<typeof vi.fn>;
const mockGetLibraryStats = getLibraryStats as ReturnType<typeof vi.fn>;
const mockListRoms = listRoms as ReturnType<typeof vi.fn>;
const mockAssignDatPlatform = assignDatPlatform as ReturnType<typeof vi.fn>;
const mockAddPlatformAlias = addPlatformAlias as ReturnType<typeof vi.fn>;
const mockCreatePlatform = createSystems as ReturnType<typeof vi.fn>;
const mockGetCurrentUser = getCurrentUser as ReturnType<typeof vi.fn>;

const platforms: SystemResourceDto[] = [
  { key: 'p1', name: 'Super Nintendo Entertainment System', compactLabel: 'snes', ownership: 'Romd', builtInVersion: 1, description: null, icon: null, manufacturers: [], retired: false },
  { key: 'p2', name: 'Sega Genesis', compactLabel: 'genesis', ownership: 'Romd', builtInVersion: 1, description: null, icon: null, manufacturers: [], retired: false },
];

const unroutedDat: UnroutedDat = {
  dat: {
    id: 'd1',
    name: 'Nintendo - Super Famicom',
    description: 'Super Famicom DAT',
    type: 'NoIntro',
    systemKey: null,
    version: '20260101',
    gameCount: 1842,
    romCount: 2000,
    importedAt: '2026-06-10T00:00:00Z',
  },
  matchedRomFileCount: 312,
};

const stats: LibraryStats = {
  totalRomFiles: '900',
  catalogedCount: '500',
  unroutedCount: '312',
  unidentifiedCount: '88',
  totalSizeBytes: '1835008',
  platformBreakdown: [],
};

const emptyPage: PageOfRom = {
  items: [],
  nextCursor: null,
  hasNextPage: false,
};

describe('Inbox', () => {
  beforeEach(() => {
    vi.mocked(listAdminCompanies).mockResolvedValue({ data: [], error: undefined } as never);
    vi.mocked(getAdminCatalogSnapshot).mockResolvedValue({ data: { systems: [{ key: 'local-super-famicom', systemKey: 'local-super-famicom', name: 'Super Famicom' }] } } as never);
    permissions.canManageTitles = true;
    mockListUnroutedDats.mockResolvedValue({ data: [unroutedDat], error: undefined });
    mockListPlatforms.mockResolvedValue({ data: platforms, error: undefined });
    mockGetLibraryStats.mockResolvedValue({ data: stats, error: undefined });
    mockListRoms.mockResolvedValue({ data: emptyPage, error: undefined });
    mockAssignDatPlatform.mockResolvedValue({
      data: { gamesUpdated: 1842, newTitlesCreated: 1700, existingTitlesMatched: 142 },
      error: undefined,
    });
    mockAddPlatformAlias.mockResolvedValue({
      data: { id: 'alias-1', type: 'name', provider: null, value: 'Nintendo - Super Famicom' },
      error: undefined,
    });
    mockGetCurrentUser.mockResolvedValue({ data: null, error: { status: 401 } });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('renders both zones with counts', async () => {
    render(<Inbox />);

    await waitFor(() => {
      expect(screen.getByText('Unrouted DATs (1)')).toBeInTheDocument();
    });

    expect(screen.getByText('Nintendo - Super Famicom')).toBeInTheDocument();
    expect(screen.getByText(/blocking 312 matched ROMs/i)).toBeInTheDocument();
    expect(screen.getByText('312 ROMs waiting')).toBeInTheDocument();
    expect(screen.getByText('Unidentified files (88)')).toBeInTheDocument();
  });

  it('shows the caught-up state when both zones are empty', async () => {
    mockListUnroutedDats.mockResolvedValue({ data: [], error: undefined });
    mockGetLibraryStats.mockResolvedValue({
      data: { ...stats, unidentifiedCount: '0' },
      error: undefined,
    });

    render(<Inbox />);

    await waitFor(() => {
      expect(screen.getByText(/you're all caught up/i)).toBeInTheDocument();
    });
  });

  it('assigns a system and remembers the header as an alias by default', async () => {
    const user = userEvent.setup();
    render(<Inbox />);

    await waitFor(() => {
      expect(screen.getByText('Nintendo - Super Famicom')).toBeInTheDocument();
    });

    await user.click(screen.getByPlaceholderText('Assign to system...'));
    const option = await screen.findByRole('option', {
      name: 'Super Nintendo Entertainment System',
      hidden: true,
    });
    fireEvent.click(option);

    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await waitFor(() => {
      expect(mockAssignDatPlatform).toHaveBeenCalledWith(
        expect.objectContaining({ path: { datId: 'd1' }, body: { systemKey: 'p1' } }),
      );
    });
    await waitFor(() => {
      expect(mockAddPlatformAlias).toHaveBeenCalledWith(
        expect.objectContaining({
          path: { systemKey: 'p1' },
          body: { type: 'name', value: 'Nintendo - Super Famicom', provider: undefined },
        }),
      );
    });

    // The success toast continues the journey to the routed hub
    expect(
      await screen.findByRole('button', {
        name: /view super nintendo entertainment system/i,
      }),
    ).toBeInTheDocument();
  });

  it('skips the alias when remember is unchecked', async () => {
    const user = userEvent.setup();
    render(<Inbox />);

    await waitFor(() => {
      expect(screen.getByText('Nintendo - Super Famicom')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('checkbox'));

    await user.click(screen.getByPlaceholderText('Assign to system...'));
    const option = await screen.findByRole('option', {
      name: 'Sega Genesis',
      hidden: true,
    });
    fireEvent.click(option);

    await user.click(screen.getByRole('button', { name: 'Assign' }));

    await waitFor(() => {
      expect(mockAssignDatPlatform).toHaveBeenCalled();
    });
    expect(mockAddPlatformAlias).not.toHaveBeenCalled();
  });

  it('creates a new system seeded from the DAT header and assigns it', async () => {
    mockCreatePlatform.mockResolvedValue({
      data: { key: 'local-super-famicom', name: 'Super Famicom' },
      error: undefined,
    });

    const user = userEvent.setup();
    render(<Inbox />);

    await waitFor(() => {
      expect(screen.getByText('Nintendo - Super Famicom')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: /new system/i }));

    const nameInput = await screen.findByLabelText(/^name/i);
    expect(nameInput).toHaveValue('Nintendo - Super Famicom');
    expect(screen.getByLabelText(/system key/i)).toHaveValue('local-nintendo-super-famicom');

    await user.clear(nameInput);
    await user.type(nameInput, 'Super Famicom');
    expect(screen.getByLabelText(/system key/i)).toHaveValue('local-super-famicom');

    await user.click(screen.getByRole('button', { name: /create & assign/i }));

    await waitFor(() => {
      expect(mockCreatePlatform).toHaveBeenCalledWith(
        expect.objectContaining({
          body: { name: 'Super Famicom', key: 'local-super-famicom', compactLabel: 'Super Famicom', manufacturerKeys: [] },
        }),
      );
    });
    await waitFor(() => {
      expect(mockAssignDatPlatform).toHaveBeenCalledWith(
        expect.objectContaining({ path: { datId: 'd1' }, body: { systemKey: 'local-super-famicom' } }),
      );
    });
    // System name differs from the DAT header — the header is remembered as an alias
    await waitFor(() => {
      expect(mockAddPlatformAlias).toHaveBeenCalledWith(
        expect.objectContaining({
          path: { systemKey: 'local-super-famicom' },
          body: { type: 'name', value: 'Nintendo - Super Famicom', provider: undefined },
        }),
      );
    });
  });

  it('hides routing controls from non-managers', async () => {
    permissions.canManageTitles = false;

    render(<Inbox />);

    await waitFor(() => {
      expect(screen.getByText('Nintendo - Super Famicom')).toBeInTheDocument();
    });

    expect(screen.getByText(/a manager can route this dat/i)).toBeInTheDocument();
    expect(screen.queryByPlaceholderText('Assign to system...')).not.toBeInTheDocument();
  });
});
