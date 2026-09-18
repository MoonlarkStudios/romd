import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { IgdbProviderCard } from '../../components/Integrations/IgdbProviderCard';
import { server } from '../../test/msw/server';

const auth = vi.hoisted(() => ({ roles: ['Admin'] }));
vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({ user: { roles: auth.roles } }),
}));

const endpoint = 'http://localhost/api/admin/metadata-providers/igdb';
const savedSettings = {
  enabled: true,
  clientId: 'saved-client',
  hasClientSecret: true,
  isConfigured: true,
  managedByDeployment: false,
  configurationError: null,
  lastTestedAt: null,
  lastTestSucceeded: null,
  lastTestMessage: null,
};

function renderCard() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MantineProvider>
      <QueryClientProvider client={queryClient}>
        <IgdbProviderCard />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('IGDB provider settings', () => {
  beforeEach(() => {
    client.setConfig({ baseUrl: 'http://localhost' });
    auth.roles = ['Admin'];
    server.use(http.get(endpoint, () => HttpResponse.json(savedSettings)));
  });

  afterEach(() => {
    client.setConfig({ baseUrl: '' });
  });

  it('shows configuration without retrieving or prefilling the saved secret', async () => {
    renderCard();
    expect(await screen.findByLabelText('Twitch Client ID')).toHaveValue('saved-client');
    expect(screen.getByLabelText('Twitch Client Secret')).toHaveValue('');
    expect(screen.getByText('A client secret is saved. Leave blank to keep it.')).toBeInTheDocument();
  });

  it('hides provider management and does not fetch credentials for non-admins', () => {
    const getSettings = vi.fn();
    auth.roles = ['Manager'];
    server.use(http.get(endpoint, getSettings));
    renderCard();
    expect(screen.queryByText('Metadata Providers')).not.toBeInTheDocument();
    expect(getSettings).not.toHaveBeenCalled();
  });

  it('saves changed credentials, clears the secret, and requires saving before testing', async () => {
    const user = userEvent.setup();
    const save = vi.fn();
    server.use(http.put(endpoint, async ({ request }) => {
      save(await request.json());
      return HttpResponse.json({ ...savedSettings, clientId: 'replacement-client' });
    }));
    renderCard();
    const clientId = await screen.findByLabelText('Twitch Client ID');
    await user.clear(clientId);
    await user.type(clientId, 'replacement-client');
    expect(screen.getByRole('button', { name: 'Save IGDB settings' })).toBeDisabled();
    await user.type(screen.getByLabelText('Twitch Client Secret'), 'replacement-secret');
    expect(screen.getByRole('button', { name: 'Test connection' })).toBeDisabled();
    expect(screen.getByText('Save your changes before testing the connection.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save IGDB settings' }));
    await waitFor(() => expect(save).toHaveBeenCalledWith({
      revision: '00000000-0000-0000-0000-000000000000',
      enabled: true,
      clientId: 'replacement-client',
      clientSecret: 'replacement-secret',
      clearClientSecret: false,
    }));
    await waitFor(() => expect(screen.getByLabelText('Twitch Client Secret')).toHaveValue(''));
    expect(screen.getByRole('button', { name: 'Test connection' })).toBeEnabled();
  });

  it('preserves a saved secret when disabling enrichment', async () => {
    const user = userEvent.setup();
    const save = vi.fn();
    server.use(http.put(endpoint, async ({ request }) => {
      save(await request.json());
      return HttpResponse.json({ ...savedSettings, enabled: false });
    }));
    renderCard();
    await user.click(await screen.findByRole('switch', { name: 'Enable IGDB enrichment' }));
    await user.click(screen.getByRole('button', { name: 'Save IGDB settings' }));
    await waitFor(() => expect(save).toHaveBeenCalledWith({
      revision: '00000000-0000-0000-0000-000000000000',
      enabled: false, clientId: 'saved-client', clientSecret: null, clearClientSecret: false,
    }));
  });

  it('removes the saved secret only through an explicit saved change', async () => {
    const user = userEvent.setup();
    const save = vi.fn();
    server.use(http.put(endpoint, async ({ request }) => {
      save(await request.json());
      return HttpResponse.json({ ...savedSettings, enabled: false, hasClientSecret: false, isConfigured: false });
    }));
    renderCard();
    await user.click(await screen.findByRole('switch', { name: 'Enable IGDB enrichment' }));
    await user.click(screen.getByRole('checkbox', { name: 'Remove saved client secret' }));
    expect(save).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Save IGDB settings' }));
    await waitFor(() => expect(save).toHaveBeenCalledWith({
      revision: '00000000-0000-0000-0000-000000000000',
      enabled: false, clientId: 'saved-client', clientSecret: null, clearClientSecret: true,
    }));
    await waitFor(() => expect(screen.queryByRole('checkbox', { name: 'Remove saved client secret' })).not.toBeInTheDocument());
  });

  it('tests saved settings without sending credentials and displays failed test status', async () => {
    const user = userEvent.setup();
    const body = vi.fn();
    server.use(http.post(`${endpoint}/test-connection`, async ({ request }) => {
      body(await request.text());
      return HttpResponse.json({
        ...savedSettings,
        lastTestedAt: '2026-09-07T12:00:00Z',
        lastTestSucceeded: false,
        lastTestMessage: 'IGDB rejected the credentials.',
      });
    }));
    renderCard();
    await user.click(await screen.findByRole('button', { name: 'Test connection' }));
    expect(await screen.findByText('IGDB rejected the credentials.')).toBeInTheDocument();
    expect(body).toHaveBeenCalledWith('');
  });

  it('keeps deployment-managed configuration read-only', async () => {
    server.use(http.get(endpoint, () => HttpResponse.json({ ...savedSettings, managedByDeployment: true })));
    renderCard();
    expect(await screen.findByText('Managed by deployment')).toBeInTheDocument();
    expect(screen.getByLabelText('Twitch Client ID')).toBeDisabled();
    expect(screen.getByLabelText('Twitch Client Secret')).toBeDisabled();
    expect(screen.getByRole('switch', { name: 'Enable IGDB enrichment' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save IGDB settings' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Test connection' })).toBeEnabled();
  });

  it('recovers from a settings query failure with Retry', async () => {
    const user = userEvent.setup();
    server.use(http.get(endpoint, () => HttpResponse.json({ title: 'Unavailable' }, { status: 503 })));
    renderCard();
    expect(await screen.findByText('Could not load IGDB settings.')).toBeInTheDocument();
    server.use(http.get(endpoint, () => HttpResponse.json(savedSettings)));
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByLabelText('Twitch Client ID')).toHaveValue('saved-client');
  });

  it('keeps edits after a failed save so the administrator can retry', async () => {
    const user = userEvent.setup();
    server.use(http.put(endpoint, () => HttpResponse.json({ detail: 'Settings could not be saved.' }, { status: 503 })));
    renderCard();
    await user.type(await screen.findByLabelText('Twitch Client Secret'), 'replacement-secret');
    await user.click(screen.getByRole('button', { name: 'Save IGDB settings' }));
    expect(await screen.findByText('Settings could not be saved.')).toBeInTheDocument();
    expect(screen.getByLabelText('Twitch Client Secret')).toHaveValue('replacement-secret');
    expect(screen.getByRole('button', { name: 'Save IGDB settings' })).toBeEnabled();
  });
});
