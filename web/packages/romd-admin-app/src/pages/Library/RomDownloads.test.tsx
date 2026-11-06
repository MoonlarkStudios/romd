import { createDownloadTicket, getRom, type Rom } from '@romd/admin-api-client';
import { startBrowserDownload } from '@romd/delivery';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { romKeys } from '../../hooks/api/useRoms';
import { render } from '../../test/utils/render';
import { LibraryTable } from './LibraryTable';
import { RomRow } from './RomRow';

vi.mock('@romd/admin-api-client', async (original) => ({
  ...(await original<typeof import('@romd/admin-api-client')>()),
  getRom: vi.fn(),
  createDownloadTicket: vi.fn(),
}));
vi.mock('@romd/delivery', () => ({
  startBrowserDownload: vi.fn(),
}));

const rom: Rom = {
  id: 'disc',
  originalFilename: 'game.iso',
  size: '1024',
  sha1: 'a'.repeat(40),
  status: 'cataloged',
};
const detail: Rom = {
  ...rom,
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
};

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(getRom).mockResolvedValue({
    data: detail,
  } as never);
  vi.mocked(createDownloadTicket).mockResolvedValue({
    data: {
      downloadUrl: '/api/download-tickets/rvz',
    },
  } as never);
});

it.each([
  'row',
  'table',
] as const)('lazily discovers both per-file formats from the %s icon', async (surface) => {
  render(
    surface === 'row' ? (
      <RomRow rom={rom} />
    ) : (
      <LibraryTable
        data={[
          rom,
        ]}
        sort={null}
        onSortChange={() => {}}
      />
    ),
  );
  expect(getRom).not.toHaveBeenCalled();
  const user = userEvent.setup();
  await user.click(
    screen.getByRole('button', {
      name: 'Download formats for game.iso',
    }),
  );
  const dialog = await screen.findByRole('dialog', {
    name: 'Download formats',
  });
  await user.click(
    await within(dialog).findByRole('button', {
      name: 'Download RVZ',
    }),
  );
  expect(getRom).toHaveBeenCalledWith(
    expect.objectContaining({
      path: {
        romId: 'disc',
      },
      signal: expect.any(AbortSignal),
    }),
  );
  await waitFor(() =>
    expect(createDownloadTicket).toHaveBeenCalledWith({
      body: {
        target: 'Rom',
        id: 'disc',
        format: 'rvz',
      },
    }),
  );
  expect(startBrowserDownload).toHaveBeenCalledWith('/api/download-tickets/rvz', 'game.rvz');
});

it.each([
  'row',
  'table',
] as const)('offers the same formats from the %s overflow menu', async (surface) => {
  render(
    surface === 'row' ? (
      <RomRow
        rom={rom}
        compact
      />
    ) : (
      <LibraryTable
        data={[
          rom,
        ]}
        sort={null}
        onSortChange={() => {}}
      />
    ),
  );
  const user = userEvent.setup();
  await user.click(
    screen.getByRole('button', {
      name: 'File actions for game.iso',
    }),
  );
  await user.click(
    screen.getByRole('menuitem', {
      name: 'Download',
    }),
  );
  const dialog = await screen.findByRole('dialog', {
    name: 'Download formats',
  });
  expect(
    await within(dialog).findByRole('button', {
      name: 'Download Original',
    }),
  ).toBeEnabled();
  expect(
    within(dialog).getByRole('button', {
      name: 'Download RVZ',
    }),
  ).toBeEnabled();
  expect(createDownloadTicket).not.toHaveBeenCalled();
});

it('retries authoritative format discovery after a detail failure without preparing a guessed original', async () => {
  vi.mocked(getRom).mockResolvedValueOnce({
    error: {
      status: 404,
    },
  } as never);
  render(<RomRow rom={rom} />);
  const user = userEvent.setup();
  await user.click(
    screen.getByRole('button', {
      name: 'Download formats for game.iso',
    }),
  );
  await user.click(
    await screen.findByRole('button', {
      name: 'Retry download formats',
    }),
  );
  const dialog = screen.getByRole('dialog', {
    name: 'Download formats',
  });
  expect(
    await within(dialog).findByRole('button', {
      name: 'Download RVZ',
    }),
  ).toBeEnabled();
  expect(getRom).toHaveBeenCalledTimes(2);
  expect(createDownloadTicket).not.toHaveBeenCalled();
});

it('refreshes cached original-only detail when a download action opens under production cache settings', async () => {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 1000 * 60 * 5,
        retry: false,
      },
    },
  });
  queryClient.setQueryData(romKeys.detail(rom.id), {
    ...detail,
    formats: detail.formats?.filter((format) => format.format === 'original'),
  });
  render(
    <QueryClientProvider client={queryClient}>
      <RomRow rom={rom} />
    </QueryClientProvider>,
  );
  expect(getRom).not.toHaveBeenCalled();
  const user = userEvent.setup();
  await user.click(
    screen.getByRole('button', {
      name: 'Download formats for game.iso',
    }),
  );
  await waitFor(() => expect(getRom).toHaveBeenCalledTimes(1));
  const dialog = screen.getByRole('dialog', {
    name: 'Download formats',
  });
  expect(
    await within(dialog).findByRole('button', {
      name: 'Download RVZ',
    }),
  ).toBeEnabled();
  expect(createDownloadTicket).not.toHaveBeenCalled();
});
