import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { server } from '../../test/msw/server';
import { ProviderMatches } from './ProviderMatches';

const root = 'http://localhost/api/provider-matches';
const row = { providerId: 'example', providerName: 'Example provider', isAvailable: true, capabilities: ['search', 'resolve'], revision: '00000000-0000-0000-0000-000000000001', state: 'NeedsMatch', metadataNeedsRefresh: false };
const game = { id: '42', name: 'The matching game', url: 'https://example.com/games/42', year: 1994, platforms: ['SNES'] };
beforeEach(() => { client.setConfig({ baseUrl: 'http://localhost' }); server.use(http.get(`${root}/titles/title1`, () => HttpResponse.json([row]))); });
afterEach(() => client.setConfig({ baseUrl: '' }));
function show() {
  return render(<MantineProvider env="test"><QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><ProviderMatches titleId="title1" titleName="Game" /></QueryClientProvider></MantineProvider>);
}

it('renders server-supplied provider names and external game links', async () => {
  server.use(http.get(`${root}/titles/title1`, () => HttpResponse.json([{ ...row, state: 'Confirmed', externalId: '42', game }])));
  show();
  expect(await screen.findByText('Example provider')).toBeInTheDocument();
  const link = screen.getByRole('link', { name: /The matching game/ });
  expect(link).toHaveAttribute('href', game.url);
  expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  expect(screen.queryByText('IGDB')).not.toBeInTheDocument();
});

it('requires a preview selection before confirming a match and sends its revision', async () => {
  const save = vi.fn(async ({ request }: { request: Request }) => {
    expect(await request.json()).toEqual({ expectedRevision: row.revision, externalId: '42' });
    return new HttpResponse(null, { status: 204 });
  });
  server.use(http.post(`${root}/example/search`, () => HttpResponse.json([game])), http.put(`${root}/titles/title1/example`, save));
  show();
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Find match' }));
  expect(screen.getByRole('button', { name: 'Confirm match' })).toBeDisabled();
  await user.click(screen.getByRole('button', { name: 'Search provider games' }));
  await user.click(await screen.findByRole('button', { name: 'Select The matching game' }));
  expect(save).not.toHaveBeenCalled();
  await user.click(screen.getByRole('button', { name: 'Confirm match' }));
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
});

it('requires a reload and renewed selection after a match conflict', async () => {
  const latest = { ...row, revision: '00000000-0000-0000-0000-000000000002', state: 'Confirmed', externalId: '99', game: { ...game, id: '99', name: 'Another current match' } };
  let changed = false;
  const save = vi.fn(async ({ request }: { request: Request }) => {
    const body = await request.json();
    if (!changed) {
      expect(body).toEqual({ expectedRevision: row.revision, externalId: '42' });
      changed = true;
      return HttpResponse.json({ detail: 'The provider match changed.' }, { status: 409 });
    }
    expect(body).toEqual({ expectedRevision: latest.revision, externalId: '42' });
    return new HttpResponse(null, { status: 204 });
  });
  server.use(
    http.get(`${root}/titles/title1`, () => HttpResponse.json([changed ? latest : row])),
    http.post(`${root}/example/search`, () => HttpResponse.json([game])),
    http.put(`${root}/titles/title1/example`, save),
  );
  show();
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Find match' }));
  await user.click(screen.getByRole('button', { name: 'Search provider games' }));
  await user.click(await screen.findByRole('button', { name: 'Select The matching game' }));
  await user.click(screen.getByRole('button', { name: 'Confirm match' }));
  await user.click(await screen.findByRole('button', { name: 'Reload current match' }));
  expect(await screen.findByText(/Current match: Another current match/)).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Confirm match' })).toBeDisabled();
  expect(save).toHaveBeenCalledTimes(1);
  await user.click(screen.getByRole('button', { name: 'Search provider games' }));
  await user.click(await screen.findByRole('button', { name: 'Select The matching game' }));
  await user.click(screen.getByRole('button', { name: 'Confirm match' }));
  await waitFor(() => expect(save).toHaveBeenCalledTimes(2));
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
});
