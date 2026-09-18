import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { server } from '../../test/msw/server';
import { ArtworkEnrichmentSettings } from './ArtworkEnrichmentSettings';

vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { roles: ['Admin'] } }) }));
const endpoint = 'http://localhost/api/enrichment/artwork/settings';
const settings = { revision: '9f184511-6fc6-44d4-812b-97362184eb12', fillPosters: true, fillHeroes: true, fillLogos: true, fillBackdrops: true, reviewBackdrops: true, providers: ['igdb'] };
function mount() {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<MantineProvider env="test"><QueryClientProvider client={cache}><ArtworkEnrichmentSettings /></QueryClientProvider></MantineProvider>);
}
beforeEach(() => {
  client.setConfig({ baseUrl: 'http://localhost' });
  server.use(http.get(endpoint, () => HttpResponse.json(settings)));
});
afterEach(() => client.setConfig({ baseUrl: '' }));

it('loads saved policy and saves with its revision without scheduling any jobs', async () => {
  const requests: unknown[] = [];
  server.use(http.put(endpoint, async ({ request }) => {
    requests.push(await request.json());
    return HttpResponse.json({ ...settings, revision: 'new-revision', fillHeroes: false });
  }));
  mount();
  const hero = await screen.findByRole('switch', { name: 'Automatically fill missing banners' });
  expect(hero).toBeChecked();
  await userEvent.setup().click(hero);
  await userEvent.setup().click(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() => expect(requests).toEqual([{ revision: settings.revision, fillPosters: true, fillHeroes: false, fillLogos: true, fillBackdrops: true, reviewBackdrops: true }]));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled());
});

it('does not show disabled providers as automatic choices', async () => {
  server.use(http.get(endpoint, () => HttpResponse.json({ ...settings, providers: [] })));
  mount();
  await screen.findByText('None enabled');
  expect(screen.queryByText('IGDB')).not.toBeInTheDocument();
});

it('keeps the draft and displays a conflicting-save error', async () => {
  server.use(http.put(endpoint, () => HttpResponse.json({ detail: 'Settings changed. Reload before saving.' }, { status: 409 })));
  mount();
  await userEvent.setup().click(await screen.findByRole('switch', { name: 'Automatically fill missing posters' }));
  await userEvent.setup().click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByText('Settings changed. Reload before saving.');
  expect(screen.getByRole('switch', { name: 'Automatically fill missing posters' })).not.toBeChecked();
});

it('saves the logo policy independently using the current revision', async () => {
  const save = vi.fn(async ({ request }: { request: Request }) => {
    const body = await request.json();
    expect(body).toEqual({ revision: settings.revision, fillPosters: true, fillHeroes: true, fillLogos: false, fillBackdrops: true, reviewBackdrops: true });
    return HttpResponse.json({ ...settings, revision: 'next-revision', fillLogos: false, fillBackdrops: true, reviewBackdrops: true });
  });
  server.use(http.put(endpoint, save));
  mount();
  const user = userEvent.setup();
  await user.click(await screen.findByRole('switch', { name: /^Automatically fill missing logos/ }));
  await user.click(screen.getByRole('button', { name: 'Save changes' }));
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled());
});
