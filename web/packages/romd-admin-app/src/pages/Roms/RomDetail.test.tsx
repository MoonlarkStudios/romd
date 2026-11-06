import { createDownloadTicket, getRom, type Rom } from '@romd/admin-api-client';
import { startBrowserDownload } from '@romd/delivery';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { RomDetailPane } from '../Library/RomDetailPane';
import { RomDetail } from './RomDetail';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getRom: vi.fn(),
  createDownloadTicket: vi.fn(),
}));
vi.mock('@romd/delivery', () => ({
  startBrowserDownload: vi.fn(),
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canManageTitles: false,
  }),
}));
beforeEach(() => vi.resetAllMocks());
function page() {
  render(
    <Routes>
      <Route
        path="/roms/:romId"
        element={<RomDetail />}
      />
    </Routes>,
    {
      routerOptions: {
        initialEntries: [
          '/roms/file?tab=all&q=mario&status=cataloged',
        ],
      },
    },
  );
}
it('loads a deep link and links every match without losing inventory filters', async () => {
  vi.mocked(getRom).mockResolvedValue({
    data: {
      id: 'file',
      originalFilename: 'Mario.sfc',
      status: 'cataloged',
      size: '100',
      sha1: 'hash',
      matches: [
        {
          datId: 'dat',
          datName: 'No-Intro',
          datGameId: 'release',
          gameName: 'Mario (USA)',
          romName: 'Mario.sfc',
          titleId: 'title',
          titleName: 'Mario',
          systemKey: 'system',
          platformName: 'SNES',
        },
        {
          datId: 'other',
          datName: 'Other DAT',
          datGameId: 'other-game',
          gameName: 'Unassigned entry',
          romName: 'file.sfc',
        },
      ],
    },
  } as never);
  page();
  expect(
    await screen.findByRole('heading', {
      name: 'Mario.sfc',
    }),
  ).toBeInTheDocument();
  expect(
    screen.getByRole('link', {
      name: 'Mario',
    }),
  ).toHaveAttribute('href', '/titles/title');
  expect(
    screen.getByRole('link', {
      name: 'Mario (USA)',
    }),
  ).toHaveAttribute('href', '/titles/title?editor=releases&release=release');
  expect(screen.getByText('No title assigned')).toBeInTheDocument();
  expect(
    screen.getByRole('table', {
      name: 'ROM matches',
    }),
  ).toBeVisible();
  expect(screen.getAllByRole('columnheader').map((header) => header.textContent)).toEqual([
    'System',
    'Title',
    'Release / DAT entry',
    'Source',
  ]);
  expect(screen.getByText('hash')).toBeVisible();
  expect(
    screen.getByRole('button', {
      name: 'Copy SHA1',
    }),
  ).toBeVisible();
  expect(screen.queryByText('Expected file: Mario.sfc')).not.toBeInTheDocument();
  expect(screen.getByText('Expected file: file.sfc')).toBeVisible();
  expect(screen.getAllByRole('heading').map((heading) => heading.textContent)).toEqual([
    'Mario.sfc',
    'File details',
    'Checksums',
    'Matches',
  ]);
  expect(
    screen.getByRole('link', {
      name: 'Back to ROMs',
    }),
  ).toHaveAttribute('href', '/roms?tab=all&q=mario&status=cataloged');
  expect(
    screen.queryByRole('button', {
      name: 'File actions',
    }),
  ).not.toBeInTheDocument();
  expect(getRom).toHaveBeenCalledWith(
    expect.objectContaining({
      path: {
        romId: 'file',
      },
      signal: expect.any(AbortSignal),
    }),
  );
});
it('distinguishes a deleted file from request failures', async () => {
  vi.mocked(getRom).mockResolvedValue({
    response: {
      status: 404,
    },
  } as never);
  page();
  expect(await screen.findByText('ROM file not found')).toBeInTheDocument();
});
it('offers retry when file details fail', async () => {
  vi.mocked(getRom).mockResolvedValue({
    error: {
      status: 500,
    },
  } as never);
  page();
  expect(
    await screen.findByRole('button', {
      name: 'Retry file',
    }),
  ).toBeInTheDocument();
});

const disc = {
  id: 'disc',
  originalFilename: 'game.iso',
  size: '1024',
  status: 'cataloged',
  sha1: 'a'.repeat(40),
  formats: [
    {
      format: 'original',
      label: 'Original',
      state: 'available',
      filename: 'game.iso',
      sizeBytes: '1024',
      sha256: 'a'.repeat(64),
      profileVersion: 'rvz-import/v1',
      runtimeApplicability: [],
    },
    {
      format: 'rvz',
      label: 'RVZ',
      fileExtension: '.rvz',
      state: 'available',
      filename: 'game.rvz',
      sizeBytes: '512',
      sha256: 'b'.repeat(64),
      profileVersion: 'rvz-canonical/v1',
      runtimeApplicability: [
        'dolphin',
      ],
    },
  ],
} satisfies Rom;

it('prepares a fresh RVZ ticket on the deep-link detail and retries the same format', async () => {
  vi.mocked(getRom).mockResolvedValue({
    data: disc,
  } as never);
  vi.mocked(createDownloadTicket)
    .mockResolvedValueOnce({
      error: {
        status: 503,
      },
    } as never)
    .mockResolvedValueOnce({
      data: {
        downloadUrl: '/api/download-tickets/fresh',
      },
    } as never);
  page();
  const user = userEvent.setup();
  await user.click(
    await screen.findByRole('button', {
      name: 'Download RVZ',
    }),
  );
  await waitFor(() => expect(createDownloadTicket).toHaveBeenCalledTimes(1));
  expect(startBrowserDownload).not.toHaveBeenCalled();
  await user.click(
    screen.getByRole('button', {
      name: 'Download RVZ',
    }),
  );
  await waitFor(() =>
    expect(startBrowserDownload).toHaveBeenCalledWith('/api/download-tickets/fresh', 'game.rvz'),
  );
  expect(createDownloadTicket).toHaveBeenNthCalledWith(1, {
    body: {
      target: 'Rom',
      id: 'disc',
      format: 'rvz',
    },
  });
  expect(createDownloadTicket).toHaveBeenNthCalledWith(2, {
    body: {
      target: 'Rom',
      id: 'disc',
      format: 'rvz',
    },
  });
});

it('discovers formats for a library list item and downloads original through a ticket', async () => {
  vi.mocked(getRom).mockResolvedValue({
    data: disc,
  } as never);
  vi.mocked(createDownloadTicket).mockResolvedValue({
    data: {
      downloadUrl: '/api/download-tickets/original',
    },
  } as never);
  render(
    <RomDetailPane
      rom={{
        ...disc,
        formats: null,
      }}
    />,
  );
  const user = userEvent.setup();
  expect(
    await screen.findByRole('button', {
      name: 'Download RVZ',
    }),
  ).toBeEnabled();
  await user.click(
    screen.getByRole('button', {
      name: 'Download Original',
    }),
  );
  await waitFor(() =>
    expect(startBrowserDownload).toHaveBeenCalledWith('/api/download-tickets/original', 'game.iso'),
  );
  expect(getRom).toHaveBeenCalledWith(
    expect.objectContaining({
      path: {
        romId: 'disc',
      },
      signal: expect.any(AbortSignal),
    }),
  );
  expect(createDownloadTicket).toHaveBeenCalledWith({
    body: {
      target: 'Rom',
      id: 'disc',
      format: 'original',
    },
  });
});

it.each([
  'preparing',
  'unsupported',
] as const)('does not download an RVZ marked %s', async (state) => {
  render(
    <RomDetailPane
      rom={{
        ...disc,
        formats: [
          disc.formats[0],
          {
            ...disc.formats[1],
            state,
          },
        ],
      }}
    />,
  );
  expect(
    screen.getByRole('button', {
      name: `RVZ ${state}`,
    }),
  ).toBeDisabled();
  expect(createDownloadTicket).not.toHaveBeenCalled();
});

it('labels each format with the label the server sends', () => {
  render(
    <RomDetailPane
      rom={{
        ...disc,
        formats: [
          {
            ...disc.formats[0],
            label: 'Plain image',
          },
          {
            ...disc.formats[1],
            label: 'Dolphin image',
            state: 'preparing',
          },
        ],
      }}
    />,
  );
  expect(
    screen.getByRole('button', {
      name: 'Download Plain image',
    }),
  ).toBeEnabled();
  expect(
    screen.getByRole('button', {
      name: 'Dolphin image preparing',
    }),
  ).toBeDisabled();
});

const ps2Disc = {
  ...disc,
  formats: [
    disc.formats[0],
    {
      format: 'chd',
      label: 'CHD',
      fileExtension: '.chd',
      state: 'available',
      filename: 'game.chd',
      sizeBytes: '512',
      sha256: 'c'.repeat(64),
      md5: 'd'.repeat(32),
      sha1: 'e'.repeat(40),
      profileVersion: 'chd-observed/v1',
      runtimeApplicability: [
        'pcsx2',
      ],
    },
  ],
} satisfies Rom;

it('retries CHD preparation with the same ROM and format and hands off the delivered filename', async () => {
  vi.mocked(getRom).mockResolvedValue({
    data: ps2Disc,
  } as never);
  vi.mocked(createDownloadTicket)
    .mockResolvedValueOnce({
      error: {
        status: 503,
      },
    } as never)
    .mockResolvedValueOnce({
      data: {
        downloadUrl: '/api/download-tickets/chd-fresh',
      },
    } as never);
  page();
  const user = userEvent.setup();
  const button = await screen.findByRole('button', {
    name: 'Download CHD',
  });
  expect(screen.getByText('game.chd · 512 B')).toBeVisible();
  expect(
    screen.getByRole('heading', {
      name: 'game.iso',
    }),
  ).toBeVisible();
  expect(screen.getByText(disc.sha1)).toBeVisible();
  await user.click(button);
  await waitFor(() => expect(createDownloadTicket).toHaveBeenCalledTimes(1));
  expect(startBrowserDownload).not.toHaveBeenCalled();
  await waitFor(() => expect(button).not.toHaveAttribute('data-loading'));
  await user.click(button);
  await waitFor(() =>
    expect(startBrowserDownload).toHaveBeenCalledExactlyOnceWith(
      '/api/download-tickets/chd-fresh',
      'game.chd',
    ),
  );
  for (const attempt of [
    1,
    2,
  ])
    expect(createDownloadTicket).toHaveBeenNthCalledWith(attempt, {
      body: {
        target: 'Rom',
        id: 'disc',
        format: 'chd',
      },
    });
});

it.each([
  'preparing',
  'unsupported',
] as const)('does not prepare CHD marked %s', async (state) => {
  render(
    <RomDetailPane
      rom={{
        ...ps2Disc,
        formats: [
          {
            ...ps2Disc.formats[1],
            state,
          },
        ],
      }}
    />,
  );
  expect(
    screen.getByRole('button', {
      name: `CHD ${state}`,
    }),
  ).toBeDisabled();
  expect(createDownloadTicket).not.toHaveBeenCalled();
  expect(startBrowserDownload).not.toHaveBeenCalled();
});

it.each([
  'filename',
  'sizeBytes',
  'sha256',
] as const)('refuses available CHD with missing %s without guessing Original', async (field) => {
  render(
    <RomDetailPane
      rom={{
        ...ps2Disc,
        formats: [
          {
            ...ps2Disc.formats[1],
            [field]: null,
          },
        ],
      }}
    />,
  );
  const button = screen.getByRole('button', {
    name: 'Download CHD',
  });
  expect(button).toBeDisabled();
  await userEvent.click(button);
  expect(createDownloadTicket).not.toHaveBeenCalled();
  expect(startBrowserDownload).not.toHaveBeenCalled();
});

it('does not infer original delivery from an explicitly empty format list', () => {
  render(
    <RomDetailPane
      rom={{
        ...disc,
        formats: [],
      }}
    />,
  );
  expect(screen.getByText('No downloads available.')).toBeVisible();
  expect(
    screen.queryByRole('button', {
      name: /^Download/,
    }),
  ).not.toBeInTheDocument();
  expect(createDownloadTicket).not.toHaveBeenCalled();
});
