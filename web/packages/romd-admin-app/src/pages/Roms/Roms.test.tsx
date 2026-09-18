import { getLibraryStats, listRoms, listUnroutedDats } from '@romd/admin-api-client';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes, useLocation } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { LegacyRomRedirect, Roms } from './index';

vi.mock('@romd/admin-api-client', async () => ({ ...(await vi.importActual('@romd/admin-api-client')), getLibraryStats: vi.fn(), listRoms: vi.fn(), listUnroutedDats: vi.fn() }));
vi.mock('../../hooks/usePermissions', () => ({ usePermissions: () => ({ canUploadRoms: true }) }));
vi.mock('../Inbox', () => ({ Inbox: () => <div>Attention queue</div> }));
vi.mock('../Import', () => ({ ImportPage: () => <div>Import session</div> }));
const result = (data: unknown) => ({ data }) as never;
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(getLibraryStats).mockResolvedValue(result({ unidentifiedCount: 2 }));
  vi.mocked(listUnroutedDats).mockResolvedValue(result([]));
  vi.mocked(listRoms).mockImplementation(async (options) => result({ items: [{ id: options?.query?.cursor ? 'two' : 'one', originalFilename: options?.query?.search ? 'Mario.sfc' : 'Game.sfc', size: '100', status: 'Cataloged', sha1: 'hash' }], hasNextPage: !options?.query?.cursor && !options?.query?.search, nextCursor: 'next' }));
});
it('starts with attention and opens inventory only on demand', async () => {
  render(<Roms />);
  expect(screen.getByText('Attention queue')).toBeInTheDocument();
  expect(listRoms).not.toHaveBeenCalled();
  await userEvent.click(screen.getByRole('tab', { name: 'All files' }));
  expect(await screen.findByRole('link', { name: 'Game.sfc' })).toHaveAttribute('href', '/roms/one?tab=all');
  expect(screen.getByRole('link', { name: 'Import ROMs' })).toHaveAttribute('href', '/roms/import?tab=all');
});
it('shows lowercase API statuses correctly in file details', async () => {
  vi.mocked(listRoms).mockResolvedValue(result({ items: [{ id: 'one', originalFilename: 'Game.sfc', size: '100', status: 'cataloged', sha1: 'hash' }], hasNextPage: false }));
  render(<Roms />, { routerOptions: { initialEntries: ['/roms?tab=all'] } });
  expect(await screen.findByRole('link', { name: 'Game.sfc' })).toHaveAttribute('href', '/roms/one?tab=all');
  expect(within(screen.getByRole('table', { name: 'ROM files' })).getByText('Cataloged')).toBeInTheDocument();
});
it('searches on the server and resets pagination when the filename changes', async () => {
  render(<Roms />, { routerOptions: { initialEntries: ['/roms?tab=all'] } });
  await userEvent.click(await screen.findByRole('button', { name: 'Load more files' }));
  await waitFor(() => expect(listRoms).toHaveBeenCalledWith(expect.objectContaining({ query: expect.objectContaining({ cursor: 'next' }) })));
  await userEvent.type(screen.getByRole('textbox', { name: 'Search ROM filenames' }), 'Mario');
  expect(await screen.findByRole('link', { name: 'Mario.sfc' })).toBeInTheDocument();
  expect(listRoms).toHaveBeenLastCalledWith(expect.objectContaining({ query: expect.objectContaining({ cursor: undefined, search: 'Mario' }), signal: expect.any(AbortSignal) }));
});
function Location() { const location = useLocation(); return <div>{location.pathname}{location.search}</div>; }
it.each([['/import?q=test', '/roms/import?q=test', true], ['/inbox?q=test', '/roms?q=test&tab=attention', false]])('redirects %s while preserving query context', (from, to, importing) => {
  render(<Routes><Route path={importing ? '/import' : '/inbox'} element={<LegacyRomRedirect importing={Boolean(importing)} />} /><Route path="/roms/*" element={<Location />} /></Routes>, { routerOptions: { initialEntries: [String(from)] } });
  expect(screen.getByText(String(to))).toBeInTheDocument();
});
