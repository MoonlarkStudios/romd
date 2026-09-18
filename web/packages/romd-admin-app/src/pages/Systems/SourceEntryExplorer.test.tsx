import { getGameByDat, listSourceEntries } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { SourceEntryExplorer } from './SourceEntryExplorer';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  listSourceEntries: vi.fn(),
  getGameByDat: vi.fn(),
}));
beforeEach(() => vi.resetAllMocks());
it('loads bounded pages, links titles, and resets the cursor when searching', async () => {
  vi.mocked(listSourceEntries).mockImplementation(
    async (options) =>
      ({
        data: {
          items: [
            {
              gameId: options?.query?.cursor ? 'g2' : 'g1',
              entryId: 'e1',
              name: options?.query?.search || 'Game',
              titleId: 'title',
              titleName: 'Open game',
              hasLocalPayload: true,
              activeSources: 2,
            },
          ],
          nextCursor: options?.query?.cursor || options?.query?.search ? null : 'g1',
        },
      }) as never,
  );
  const user = userEvent.setup();
  render(<SourceEntryExplorer datId="dat" />);
  expect(
    await screen.findByRole('link', {
      name: 'Open game',
    }),
  ).toHaveAttribute('href', '/titles/title');
  expect(screen.getByText('2 active catalog sources')).toBeInTheDocument();
  expect(getGameByDat).not.toHaveBeenCalled();
  await user.click(
    screen.getByRole('button', {
      name: 'Load more entries',
    }),
  );
  await waitFor(() =>
    expect(listSourceEntries).toHaveBeenCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          cursor: 'g1',
          limit: 50,
        }),
      }),
    ),
  );
  await user.type(screen.getByLabelText('Search source entries'), 'Mario');
  await waitFor(() =>
    expect(listSourceEntries).toHaveBeenLastCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          cursor: undefined,
          search: 'Mario',
        }),
      }),
    ),
  );
  expect(await screen.findByText('Mario')).toBeInTheDocument();
});

it('loads expected files only for an expanded entry and allows retrying a failure', async () => {
  vi.mocked(listSourceEntries).mockResolvedValue({ data: { items: [{ gameId: 'game', entryId: 'entry', name: 'Mario', activeSources: 1 }], nextCursor: null } } as never);
  vi.mocked(getGameByDat).mockResolvedValueOnce({ error: { status: 500 } } as never).mockResolvedValue({ data: { id: 'game', name: 'Mario', roms: [{ id: 'rom', name: 'Mario.sfc', size: '100', crc: 'abcd1234' }], disks: [] } } as never);
  const user = userEvent.setup();
  render(<SourceEntryExplorer datId="dat" />);
  const toggle = await screen.findByRole('button', { name: 'Expected files for Mario' });
  expect(getGameByDat).not.toHaveBeenCalled();
  await user.click(toggle);
  await user.click(await screen.findByRole('button', { name: 'Retry files' }));
  expect(await screen.findByText('Mario.sfc')).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Copy CRC' })).toBeInTheDocument();
  expect(getGameByDat).toHaveBeenCalledWith(expect.objectContaining({ path: { datId: 'dat', gameId: 'game' }, signal: expect.any(AbortSignal) }));
  await user.click(toggle);
  expect(screen.queryByText('Mario.sfc')).not.toBeInTheDocument();
  expect(toggle).toHaveAttribute('aria-expanded', 'false');
});
it('keeps the title filter until the user asks to see all entries', async () => {
  vi.mocked(listSourceEntries).mockResolvedValue({
    data: {
      items: [],
      nextCursor: null,
    },
  } as never);
  const user = userEvent.setup();
  render(<SourceEntryExplorer datId="dat" />, {
    routerOptions: {
      initialEntries: [
        '/systems/one?dat=dat&sourceTitle=title',
      ],
    },
  });
  await waitFor(() =>
    expect(listSourceEntries).toHaveBeenCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          titleId: 'title',
        }),
      }),
    ),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Show all entries',
    }),
  );
  await waitFor(() =>
    expect(listSourceEntries).toHaveBeenLastCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          titleId: undefined,
        }),
      }),
    ),
  );
});
