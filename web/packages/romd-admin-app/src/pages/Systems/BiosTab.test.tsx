import type { Bios } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { BiosTab } from './BiosTab';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    listBiosByPlatform: vi.fn(),
    backfillBiosCatalog: vi.fn(),
  };
});

import { backfillBiosCatalog, listBiosByPlatform } from '@romd/admin-api-client';

const mockListBiosByPlatform = listBiosByPlatform as ReturnType<typeof vi.fn>;
const mockBackfillBiosCatalog = backfillBiosCatalog as ReturnType<typeof vi.fn>;

const bios: Bios[] = [
  {
    id: 'b1',
    systemKey: 'plat-1',
    name: '[BIOS] PSX (USA)',
    totalRoms: '1',
    ownedRoms: '1',
    isOwned: true,
    requiredBytes: '524288', // 512 KB
    ownedBytes: '524288',
    onDiskBytes: '262144', // 256 KB on disk (compressed)
  },
  {
    id: 'b2',
    systemKey: 'plat-1',
    name: '[BIOS] PSX (Japan)',
    totalRoms: '2',
    ownedRoms: '1',
    isOwned: false,
    requiredBytes: '1048576',
    ownedBytes: '524288',
    onDiskBytes: '524288',
  },
  {
    id: 'b3',
    systemKey: 'plat-1',
    name: '[BIOS] PSX (Europe)',
    totalRoms: '1',
    ownedRoms: '0',
    isOwned: false,
    requiredBytes: '524288',
    ownedBytes: '0',
    onDiskBytes: '0',
  },
];

describe('BiosTab', () => {
  beforeEach(() => {
    mockListBiosByPlatform.mockResolvedValue({ data: bios, error: undefined });
    mockBackfillBiosCatalog.mockResolvedValue({
      data: { biosEntriesCreated: '2', gamesGrouped: '3' },
      error: undefined,
    });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('lists BIOS entries with ownership status and counts', async () => {
    render(<BiosTab systemKey="plat-1" />);

    expect(await screen.findByText('[BIOS] PSX (USA)')).toBeInTheDocument();

    expect(mockListBiosByPlatform).toHaveBeenCalledWith(
      expect.objectContaining({ path: { systemKey: 'plat-1' } }),
    );

    // Owned / Partial / Missing derived from ownedRoms vs totalRoms
    expect(screen.getByText('Owned')).toBeInTheDocument();
    expect(screen.getByText('Partial')).toBeInTheDocument();
    expect(screen.getByText('Missing')).toBeInTheDocument();

    expect(screen.getByText('1/1')).toBeInTheDocument();
    expect(screen.getByText('1/2')).toBeInTheDocument();
    expect(screen.getByText('0/1')).toBeInTheDocument();

    // Size column leads with actual on-disk bytes, with the DAT-required size as context.
    expect(screen.getByText('256 KB')).toBeInTheDocument(); // b1 on disk (compressed)
    expect(screen.getByText('of 1 MB')).toBeInTheDocument(); // b2 required (1 MB across 2 ROMs)
  });

  it('shows an empty state with a sync action when there are no BIOS entries', async () => {
    mockListBiosByPlatform.mockResolvedValue({ data: [], error: undefined });

    render(<BiosTab systemKey="plat-1" />);

    expect(await screen.findByText('No BIOS entries')).toBeInTheDocument();
    // The empty state is the primary place to trigger grouping when nothing is grouped yet.
    expect(screen.getByRole('button', { name: 'Sync BIOS catalog' })).toBeInTheDocument();
  });

  it('re-syncs the catalog from the maintenance menu and refreshes the list', async () => {
    const user = userEvent.setup();
    render(<BiosTab systemKey="plat-1" />);

    await screen.findByText('[BIOS] PSX (USA)');
    mockListBiosByPlatform.mockClear();

    // Re-sync is a quiet repair affordance now that grouping is automatic, not a primary button.
    await user.click(screen.getByRole('button', { name: 'BIOS maintenance' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Re-sync BIOS catalog' }));

    await waitFor(() => {
      expect(mockBackfillBiosCatalog).toHaveBeenCalledTimes(1);
    });
    // Success invalidates the BIOS list, triggering a refetch.
    await waitFor(() => {
      expect(mockListBiosByPlatform).toHaveBeenCalled();
    });
  });
});
