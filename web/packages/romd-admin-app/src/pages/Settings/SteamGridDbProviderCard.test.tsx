import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SteamGridDbProviderCard } from '../../components/Integrations/SteamGridDbProviderCard';
import { server } from '../../test/msw/server';

const auth = vi.hoisted(() => ({ roles: ['Admin'] }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { roles: auth.roles } }) }));
const endpoint = 'http://localhost/api/admin/artwork-providers/steamgriddb';
const settings = { revision: '9f184511-6fc6-44d4-812b-97362184eb12', enabled: true, hasApiKey: true,
  isConfigured: true, managedByDeployment: false, configurationError: null, lastTestedAt: null, lastTestSucceeded: null, lastTestMessage: null };
function renderCard() {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<MantineProvider><QueryClientProvider client={cache}><SteamGridDbProviderCard /></QueryClientProvider></MantineProvider>);
  return cache;
}
describe('SteamGridDB settings', () => {
  beforeEach(() => {
    client.setConfig({ baseUrl: 'http://localhost' }); auth.roles = ['Admin'];
    server.use(http.get(endpoint, () => HttpResponse.json(settings)));
  });
  afterEach(() => client.setConfig({ baseUrl: '' }));
  it('keeps saved credentials blank and submits optimistic revision without caching plaintext', async () => {
    const requests: unknown[] = [];
    server.use(http.put(endpoint, async ({ request }) => {
      requests.push(await request.json()); return HttpResponse.json(settings);
    }));
    const cache = renderCard();
    const input = await screen.findByLabelText('SteamGridDB API key');
    expect(input).toHaveValue('');
    const user = userEvent.setup();
    await user.type(input, 'new-private-api-key');
    await user.click(screen.getByRole('button', { name: 'Save SteamGridDB settings' }));
    await screen.findByText('SteamGridDB settings saved.');
    expect(requests[0]).toEqual({ revision: settings.revision, enabled: true, apiKey: 'new-private-api-key', clearApiKey: false });
    expect(input).toHaveValue('');
    expect(JSON.stringify(cache.getQueryCache().getAll().map((query) => query.state.data))).not.toContain('new-private-api-key');
    expect(cache.getMutationCache().getAll()).toHaveLength(0);
  });
  it('does not fetch provider settings for managers', () => {
    auth.roles = ['Manager'];
    const get = vi.fn(); server.use(http.get(endpoint, get));
    renderCard();
    expect(screen.queryByText('Artwork providers')).not.toBeInTheDocument();
    expect(get).not.toHaveBeenCalled();
  });
  it('shows saved connection test results without changing credentials', async () => {
    server.use(http.post(`${endpoint}/test-connection`, () => HttpResponse.json({ ...settings,
      lastTestedAt: '2026-09-07T12:00:00Z', lastTestSucceeded: true, lastTestMessage: 'SteamGridDB is reachable.' })));
    renderCard();
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Test SteamGridDB connection' }));
    await waitFor(() => expect(screen.getByText('Connection successful')).toBeInTheDocument());
  });
});
