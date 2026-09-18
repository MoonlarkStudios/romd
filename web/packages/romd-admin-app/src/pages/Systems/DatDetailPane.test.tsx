import type { Dat, DatGame } from '@romd/admin-api-client';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { DatDetailPane } from './DatDetailPane';

const permissions = vi.hoisted(() => ({
  canUploadRoms: true,
  canManageTitles: true,
  canTriggerEnrichment: true,
  canManageSources: true,
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
    listGamesByDat: vi.fn(),
    applySourceLifecycle: vi.fn(),
    listSourceEntries: vi.fn(),
    previewSourceLifecycle: vi.fn(),
  };
});

import {
  applySourceLifecycle,
  listGamesByDat,
  listSourceEntries,
  previewSourceLifecycle,
} from '@romd/admin-api-client';

const mockListGamesByDat = listGamesByDat as ReturnType<typeof vi.fn>;
const mockApplySourceLifecycle = applySourceLifecycle as ReturnType<typeof vi.fn>;

const dat: Dat = {
  id: 'dat-1',
  sourceId: 'src-1',
  catalogSourceId: 'catsrc-1',
  lifecycle: 'Active',
  sourceStatus: 'Active',
  name: 'No-Intro SNES',
  description: 'Super Nintendo Entertainment System',
  type: 'standard',
  systemKey: 'plat-1',
  version: '2024',
  gameCount: '2',
  romCount: '2',
  importedAt: '2024-01-01T00:00:00Z',
  sourceFile: {
    sizeBytes: '4096',
    sizeOnDiskBytes: '1024',
    isCompressed: true,
  },
};

const games: DatGame[] = [
  {
    id: 'g1',
    datId: 'dat-1',
    name: 'Super Mario World',
    year: '1990',
    manufacturer: 'Nintendo',
    isBios: false,
    roms: [
      {
        id: 'r1',
        gameId: 'g1',
        name: 'Super Mario World (USA).sfc',
        size: '524288',
        crc: 'b19ed489',
        md5: 'd0f8f8f8',
        sha1: '6b47bb75d16514b6a476aa0c73a683a2a4c18765',
        status: null,
      },
    ],
    disks: [],
  },
  {
    id: 'g2',
    datId: 'dat-1',
    name: '[BIOS] Super Game Boy',
    isBios: true,
    roms: [
      {
        id: 'r2',
        gameId: 'g2',
        name: 'sgb.boot.rom',
        size: '256',
        crc: '0d4c8e0e',
      },
      {
        id: 'r3',
        gameId: 'g2',
        name: 'sgb2.boot.rom',
        size: '256',
        crc: '0d4c8e0f',
      },
    ],
    disks: [],
  },
];

function pageResponse() {
  return {
    data: {
      items: games,
      hasNextPage: false,
      nextCursor: null,
    },
    error: undefined,
  };
}

describe('DatDetailPane', () => {
  beforeEach(() => {
    permissions.canManageSources = true;
    mockListGamesByDat.mockResolvedValue(pageResponse());
    vi.mocked(listSourceEntries).mockResolvedValue({
      data: {
        items: [
          {
            gameId: 'g1',
            entryId: 'e1',
            name: 'Super Mario World',
            titleId: 't1',
            titleName: 'Mario',
            hasLocalPayload: true,
            activeSources: 1,
          },
        ],
        nextCursor: null,
      },
    } as never);
    vi.mocked(previewSourceLifecycle).mockResolvedValue({
      data: {
        name: dat.name,
        action: 'Disabled',
        reviewToken: 'review',
        versions: 1,
        stillCovered: 0,
        ownedWithoutDefinition: 1,
        personalWithoutDefinition: 0,
        catalogOnlyWithoutDefinition: 0,
        busy: false,
      },
    } as never);
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('loads file details on demand with BIOS excluded and a file count per game', async () => {
    render(
      <DatDetailPane
        dat={dat}
        onReplace={() => {}}
      />,
    );

    expect(await screen.findByText('Super Mario World')).toBeInTheDocument();
    expect(mockListGamesByDat).not.toHaveBeenCalled();
    await userEvent.click(
      screen.getByRole('radio', {
        name: 'File details',
      }),
    );
    await waitFor(() => expect(mockListGamesByDat).toHaveBeenCalled());

    // The initial load uses the BIOS-excluding default.
    expect(mockListGamesByDat).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          datId: 'dat-1',
        },
        query: expect.objectContaining({
          bios: 'Exclude',
        }),
      }),
    );

    // File count = roms + disks; the BIOS entry carries two ROMs.
    const biosRow = screen.getByText('[BIOS] Super Game Boy').closest('tr');
    expect(biosRow).not.toBeNull();
    expect(within(biosRow as HTMLElement).getByText('2')).toBeInTheDocument();

    // The header surfaces the stored source file's on-disk size.
    expect(screen.getByText('1 KB')).toBeInTheDocument();
  });

  it('refetches with the chosen BIOS filter when the toggle changes', async () => {
    const user = userEvent.setup();
    render(
      <DatDetailPane
        dat={dat}
        onReplace={() => {}}
      />,
    );

    await user.click(
      screen.getByRole('radio', {
        name: 'File details',
      }),
    );
    await screen.findByText('Super Mario World');

    await user.click(
      screen.getByRole('radio', {
        name: 'BIOS',
      }),
    );

    await waitFor(() => {
      expect(mockListGamesByDat).toHaveBeenCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({
            bios: 'Only',
          }),
        }),
      );
    });
  });

  it('reveals the ROM files with hashes only when a game row is expanded', async () => {
    const user = userEvent.setup();
    render(
      <DatDetailPane
        dat={dat}
        onReplace={() => {}}
      />,
    );

    await user.click(
      screen.getByRole('radio', {
        name: 'File details',
      }),
    );
    await screen.findByText('Super Mario World');

    // Collapsed by default — file detail is not mounted.
    expect(screen.queryByText('Super Mario World (USA).sfc')).not.toBeInTheDocument();

    await user.click(screen.getByText('Super Mario World'));

    expect(await screen.findByText('Super Mario World (USA).sfc')).toBeInTheDocument();
    expect(screen.getByText('b19ed489')).toBeInTheDocument();
    // Size is formatted from the string byte count.
    expect(screen.getByText('512 KB')).toBeInTheDocument();
  });

  it('surfaces the source status in the header', async () => {
    render(
      <DatDetailPane
        dat={{
          ...dat,
          sourceStatus: 'Discontinued',
        }}
        onReplace={() => {}}
      />,
    );

    await screen.findByText('Super Mario World');

    expect(screen.getByText('Discontinued')).toBeInTheDocument();
  });

  it('shows the Status menu to users who can manage sources', async () => {
    render(
      <DatDetailPane
        dat={dat}
        onReplace={() => {}}
      />,
    );

    await screen.findByText('Super Mario World');

    expect(
      screen.getByRole('button', {
        name: 'Status',
      }),
    ).toBeInTheDocument();
  });

  it('hides the Status menu from users who cannot manage sources', async () => {
    permissions.canManageSources = false;
    render(
      <DatDetailPane
        dat={dat}
        onReplace={() => {}}
      />,
    );

    await screen.findByText('Super Mario World');

    expect(
      screen.queryByRole('button', {
        name: 'Status',
      }),
    ).not.toBeInTheDocument();
    // The read-only actions stay available.
    expect(
      screen.getByRole('button', {
        name: /download source/i,
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: /upload new version/i,
      }),
    ).toBeInTheDocument();
  });

  it('offers the other two statuses behind a confirmation gate', async () => {
    const user = userEvent.setup();
    mockApplySourceLifecycle.mockResolvedValue({
      data: {
        status: 'Disabled',
        changed: true,
      },
      error: undefined,
    });
    render(
      <DatDetailPane
        dat={dat}
        onReplace={() => {}}
      />,
    );

    await screen.findByText('Super Mario World');

    await user.click(
      screen.getByRole('button', {
        name: 'Status',
      }),
    );

    // An Active source can only move away from Active.
    expect(await screen.findByText('Disable Source')).toBeInTheDocument();
    expect(screen.getByText('Mark Discontinued')).toBeInTheDocument();
    expect(screen.getByText('Delete source permanently')).toBeInTheDocument();
    expect(screen.queryByText('Re-enable Source')).not.toBeInTheDocument();

    await user.click(screen.getByText('Disable Source'));

    // The confirmation modal explains the consequences before anything changes.
    const modal = await screen.findByRole('dialog');
    expect(
      await within(modal).findByText('Titles with local ROMs, without a remaining definition'),
    ).toBeInTheDocument();
    expect(mockApplySourceLifecycle).not.toHaveBeenCalled();

    await user.click(
      within(modal).getByRole('button', {
        name: 'Disable source',
      }),
    );

    await waitFor(() => {
      expect(mockApplySourceLifecycle).toHaveBeenCalledWith({
        path: {
          datId: 'dat-1',
        },
        body: {
          action: 'Disabled',
          reviewToken: 'review',
        },
      });
    });
  });

  it('offers re-enabling for a disabled source', async () => {
    const user = userEvent.setup();
    render(
      <DatDetailPane
        dat={{
          ...dat,
          sourceStatus: 'Disabled',
        }}
        onReplace={() => {}}
      />,
    );

    await screen.findByText('Super Mario World');

    await user.click(
      screen.getByRole('button', {
        name: 'Status',
      }),
    );

    expect(await screen.findByText('Re-enable Source')).toBeInTheDocument();
    expect(screen.getByText('Mark Discontinued')).toBeInTheDocument();
    expect(screen.getByText('Delete source permanently')).toBeInTheDocument();
    expect(screen.queryByText('Disable Source')).not.toBeInTheDocument();
  });
});
