import { getRomById } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import { Route, Routes } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { RomDetail } from './RomDetail';

vi.mock('@romd/admin-api-client', async () => ({ ...(await vi.importActual('@romd/admin-api-client')), getRomById: vi.fn() }));
vi.mock('../../hooks/usePermissions', () => ({ usePermissions: () => ({ canManageTitles: false }) }));
beforeEach(() => vi.resetAllMocks());
function page() { render(<Routes><Route path="/roms/:romId" element={<RomDetail />} /></Routes>, { routerOptions: { initialEntries: ['/roms/file?tab=all&q=mario&status=cataloged'] } }); }
it('loads a deep link and links every match without losing inventory filters', async () => {
  vi.mocked(getRomById).mockResolvedValue({ data: { id: 'file', originalFilename: 'Mario.sfc', status: 'cataloged', size: '100', sha1: 'hash', matches: [{ datId: 'dat', datName: 'No-Intro', datGameId: 'release', gameName: 'Mario (USA)', romName: 'Mario.sfc', titleId: 'title', titleName: 'Mario', systemKey: 'system', platformName: 'SNES' }, { datId: 'other', datName: 'Other DAT', datGameId: 'other-game', gameName: 'Unassigned entry', romName: 'file.sfc' }] } } as never);
  page();
  expect(await screen.findByRole('heading', { name: 'Mario.sfc' })).toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'Mario' })).toHaveAttribute('href', '/titles/title');
  expect(screen.getByRole('link', { name: 'Mario (USA)' })).toHaveAttribute('href', '/titles/title?tab=releases&release=release');
  expect(screen.getByText('No title assigned')).toBeInTheDocument();
  expect(screen.getByRole('table', { name: 'ROM matches' })).toBeVisible();
  expect(screen.getAllByRole('columnheader').map((header) => header.textContent)).toEqual(['System', 'Title', 'Release / DAT entry', 'Source']);
  expect(screen.getByText('hash')).toBeVisible();
  expect(screen.getByRole('button', { name: 'Copy SHA1' })).toBeVisible();
  expect(screen.queryByText('Expected file: Mario.sfc')).not.toBeInTheDocument();
  expect(screen.getByText('Expected file: file.sfc')).toBeVisible();
  expect(screen.getAllByRole('heading').map((heading) => heading.textContent)).toEqual(['Mario.sfc', 'File details', 'Checksums', 'Matches']);
  expect(screen.getByRole('link', { name: 'Back to ROMs' })).toHaveAttribute('href', '/roms?tab=all&q=mario&status=cataloged');
  expect(screen.queryByRole('button', { name: 'File actions' })).not.toBeInTheDocument();
  expect(getRomById).toHaveBeenCalledWith(expect.objectContaining({ path: { romId: 'file' }, signal: expect.any(AbortSignal) }));
});
it('distinguishes a deleted file from request failures', async () => {
  vi.mocked(getRomById).mockResolvedValue({ response: { status: 404 } } as never);
  page();
  expect(await screen.findByText('ROM file not found')).toBeInTheDocument();
});
it('offers retry when file details fail', async () => {
  vi.mocked(getRomById).mockResolvedValue({ error: { status: 500 } } as never);
  page();
  expect(await screen.findByRole('button', { name: 'Retry file' })).toBeInTheDocument();
});
