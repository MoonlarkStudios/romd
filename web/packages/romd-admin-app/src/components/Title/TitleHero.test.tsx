import { MantineProvider } from '@mantine/core';
import type { ArchiveFormat } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { TitleDetailView } from '../../hooks/api/useTitleDetail';
import { finalPage } from '../../test/workspacePages';
import { TitleHero } from './TitleHero';

const permissions = vi.hoisted(() => ({
  canManageTitles: true,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => permissions,
}));

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getAdminSystem: vi.fn(),
  listTitleSourceReferences: vi.fn(),
  createDownloadTicket: vi.fn(),
}));
vi.mock('@romd/delivery', () => ({
  startBrowserDownload: vi.fn(),
}));

import {
  createDownloadTicket,
  getAdminSystem,
  listTitleSourceReferences,
} from '@romd/admin-api-client';
import { startBrowserDownload } from '@romd/delivery';

const title: TitleDetailView = {
  revision: 'title-1',
  releases: [],
  media: [],
  id: 'chrono',
  systemKey: 'snes',
  name: 'Chrono Trigger',
  enrichmentStatus: 'Completed',
  genre: 'Role-playing (RPG)',
  releaseDate: '1995-03-11',
};
const original: ArchiveFormat = {
  format: 'original',
  label: 'Original',
};
const rvz: ArchiveFormat = {
  format: 'rvz',
  label: 'RVZ',
};
const chd: ArchiveFormat = {
  format: 'chd',
  label: 'CHD',
};
/** A title with a complete release, offering the archive formats the server lists. */
const ownedTitle = (archiveFormats: ArchiveFormat[]): TitleDetailView => ({
  ...title,
  archiveFormats,
  releases: [
    {
      id: 'release',
      datId: 'catalog',
      name: 'Chrono Trigger',
      sources: [],
      isComplete: true,
    },
  ],
});
function renderHero(props: { title?: TitleDetailView } = {}) {
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
      },
    },
  });
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <TitleHero
            title={title}
            titleId="chrono"
            {...props}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
  return client;
}

describe('Title hero', () => {
  beforeEach(() => {
    permissions.canManageTitles = true;
    vi.mocked(getAdminSystem).mockResolvedValue({
      data: undefined,
      error: {},
      response: new Response(null, {
        status: 404,
      }),
    } as Awaited<ReturnType<typeof getAdminSystem>>);
    vi.mocked(listTitleSourceReferences).mockResolvedValue({
      data: finalPage([]),
      error: undefined,
      response: new Response(),
    } as Awaited<ReturnType<typeof listTitleSourceReferences>>);
  });

  it('shows the title identity and opens the Name field in Metadata', () => {
    renderHero();
    expect(
      screen.getByRole('heading', {
        name: 'Chrono Trigger',
      }),
    ).toBeVisible();
    expect(screen.getByText('1995')).toBeVisible();
    expect(screen.getByText('Role-playing (RPG)')).toBeVisible();
    expect(
      screen.getByRole('link', {
        name: 'Edit title name',
      }),
    ).toHaveAttribute('href', '/titles/chrono/metadata?field=Name');
  });

  it('keeps the title identity visible without curation access', () => {
    permissions.canManageTitles = false;
    renderHero();
    expect(
      screen.getByRole('heading', {
        name: 'Chrono Trigger',
      }),
    ).toBeVisible();
    expect(
      screen.queryByRole('link', {
        name: 'Edit title name',
      }),
    ).not.toBeInTheDocument();
  });

  it('uses a fresh RVZ-where-available title ticket instead of the stale GET link', async () => {
    vi.mocked(createDownloadTicket).mockResolvedValue({
      data: {
        downloadUrl: '/api/download-tickets/title-ticket',
        expiresAt: '2026-10-01T22:00:00Z',
      },
    } as never);
    renderHero({
      title: ownedTitle([
        original,
        rvz,
        chd,
      ]),
    });
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Download title ZIP',
      }),
    );
    await user.click(
      screen.getByRole('menuitem', {
        name: 'RVZ where available',
      }),
    );
    await waitFor(() =>
      expect(startBrowserDownload).toHaveBeenCalledWith('/api/download-tickets/title-ticket'),
    );
    expect(createDownloadTicket).toHaveBeenCalledWith({
      body: {
        target: 'Title',
        id: 'chrono',
        format: 'rvz',
      },
    });
    expect(
      screen.queryByRole('link', {
        name: 'Download best available version',
      }),
    ).not.toBeInTheDocument();
  });
  it('offers only the formats the server lists for the title, with its labels', async () => {
    renderHero({
      title: ownedTitle([
        original,
        {
          format: 'rvz',
          label: 'Disc image',
        },
      ]),
    });
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Download title ZIP',
      }),
    );
    expect(screen.getAllByRole('menuitem').map((item) => item.textContent)).toEqual([
      'Original',
      'Disc image where available',
    ]);
  });

  it('downloads a cartridge-only title with one action instead of a format menu', async () => {
    vi.mocked(createDownloadTicket)
      .mockReset()
      .mockResolvedValue({
        data: {
          downloadUrl: '/api/download-tickets/cartridge',
        },
      } as never);
    renderHero({
      title: ownedTitle([
        original,
      ]),
    });
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Download title ZIP',
      }),
    );
    expect(screen.queryByRole('menuitem')).not.toBeInTheDocument();
    await waitFor(() =>
      expect(startBrowserDownload).toHaveBeenCalledWith('/api/download-tickets/cartridge'),
    );
    expect(createDownloadTicket).toHaveBeenCalledExactlyOnceWith({
      body: {
        target: 'Title',
        id: 'chrono',
        format: 'original',
      },
    });
  });

  it('re-authorizes original downloads and never hands off failed title preparations', async () => {
    vi.mocked(startBrowserDownload).mockClear();
    vi.mocked(createDownloadTicket)
      .mockReset()
      .mockResolvedValueOnce({
        error: {
          status: 403,
        },
      } as never)
      .mockResolvedValueOnce({
        data: {
          downloadUrl: '/api/download-tickets/original-fresh',
        },
      } as never);
    renderHero({
      title: ownedTitle([
        original,
        rvz,
        chd,
      ]),
    });
    const user = userEvent.setup();
    const button = await screen.findByRole('button', {
      name: 'Download title ZIP',
    });
    await user.click(button);
    await user.click(
      screen.getByRole('menuitem', {
        name: 'Original',
      }),
    );
    await waitFor(() => expect(createDownloadTicket).toHaveBeenCalledTimes(1));
    expect(startBrowserDownload).not.toHaveBeenCalled();
    await user.click(button);
    await user.click(
      screen.getByRole('menuitem', {
        name: 'Original',
      }),
    );
    await waitFor(() =>
      expect(startBrowserDownload).toHaveBeenCalledWith('/api/download-tickets/original-fresh'),
    );
    expect(createDownloadTicket).toHaveBeenNthCalledWith(2, {
      body: {
        target: 'Title',
        id: 'chrono',
        format: 'original',
      },
    });
  });

  it('authorizes CHD title ZIPs afresh and refuses a failed preparation before retry', async () => {
    vi.mocked(startBrowserDownload).mockClear();
    vi.mocked(createDownloadTicket)
      .mockReset()
      .mockResolvedValueOnce({
        error: {
          status: 403,
        },
      } as never)
      .mockResolvedValueOnce({
        data: {
          downloadUrl: '/api/download-tickets/title-chd-fresh',
        },
      } as never);
    renderHero({
      title: ownedTitle([
        original,
        rvz,
        chd,
      ]),
    });
    const user = userEvent.setup();
    const button = await screen.findByRole('button', {
      name: 'Download title ZIP',
    });
    await user.click(button);
    await user.click(
      screen.getByRole('menuitem', {
        name: 'CHD where available',
      }),
    );
    await waitFor(() => expect(createDownloadTicket).toHaveBeenCalledTimes(1));
    expect(startBrowserDownload).not.toHaveBeenCalled();
    await waitFor(() => expect(button).not.toHaveAttribute('data-loading'));
    await user.click(button);
    await user.click(
      screen.getByRole('menuitem', {
        name: 'CHD where available',
      }),
    );
    await waitFor(() =>
      expect(startBrowserDownload).toHaveBeenCalledExactlyOnceWith(
        '/api/download-tickets/title-chd-fresh',
      ),
    );
    for (const attempt of [
      1,
      2,
    ])
      expect(createDownloadTicket).toHaveBeenNthCalledWith(attempt, {
        body: {
          target: 'Title',
          id: 'chrono',
          format: 'chd',
        },
      });
  });
});
