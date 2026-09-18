import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { server } from '../../test/msw/server';
import { ArtworkAcquisition } from './ArtworkAcquisition';

const endpoint = 'http://localhost/api/enrichment/artwork/title1';
const state = { activeJobId: null, outcomes: [{ role: 'Hero', status: 'Unavailable', sourceId: null, updatedAt: '2026-09-09T12:00:00Z' }] };
function mount() {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<MantineProvider env="test"><QueryClientProvider client={cache}><ArtworkAcquisition titleId="title1" /></QueryClientProvider></MantineProvider>);
}
beforeEach(() => {
  client.setConfig({ baseUrl: 'http://localhost' });
  server.use(http.get(endpoint, () => HttpResponse.json(state)));
});
afterEach(() => client.setConfig({ baseUrl: '' }));
it('shows a missing-artwork outcome and queues an artwork-only retry', async () => {
  const queue = vi.fn(() => new HttpResponse(null, { status: 202 }));
  server.use(http.post(endpoint, queue));
  mount();
  await screen.findByText('No suitable artwork found');
  await userEvent.setup().click(screen.getByRole('button', { name: 'Fill missing artwork' }));
  await waitFor(() => expect(queue).toHaveBeenCalledOnce());
});
it('recovers an active job on page load and prevents duplicate requests', async () => {
  server.use(http.get(endpoint, () => HttpResponse.json({ ...state, activeJobId: 'job1' })));
  mount();
  await screen.findByText('Enrichment in progress');
  expect(screen.getByRole('button', { name: 'Fill missing artwork' })).toBeDisabled();
  expect(screen.getByRole('link', { name: 'View job' })).toHaveAttribute('href', '/jobs/job1');
});
it('reports acquisition failure without marking metadata failed', async () => {
  server.use(http.get(endpoint, () => HttpResponse.json({ ...state, outcomes: [{ ...state.outcomes[0], status: 'Failed' }] })));
  mount();
  await screen.findByText('Acquisition failed');
  expect(screen.getByRole('button', { name: 'Fill missing artwork' })).toBeEnabled();
});

it.each(['steamgriddb', 'igdb', null])('reports the recorded artwork source %s without inventing provenance', async sourceId => {
  server.use(http.get(endpoint, () => HttpResponse.json({ ...state, outcomes: [{ role: 'Logo', status: 'Updated', sourceId, updatedAt: state.outcomes[0].updatedAt }] })));
  mount();
  const expected = sourceId === 'steamgriddb' ? 'Automatically selected from SteamGridDB' : sourceId === 'igdb' ? 'Automatically selected from IGDB' : 'Automatically selected';
  expect(await screen.findByText(expected)).toBeInTheDocument();
  expect(screen.getByText('Logo')).toBeInTheDocument();
  expect(document.querySelector('time')).toHaveAttribute('datetime', state.outcomes[0].updatedAt);
});
