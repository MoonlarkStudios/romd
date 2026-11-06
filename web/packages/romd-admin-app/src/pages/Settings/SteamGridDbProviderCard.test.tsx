import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SteamGridDbProviderCard } from '../../components/Integrations/SteamGridDbProviderCard';
import { server } from '../../test/msw/server';

const auth = vi.hoisted(() => ({
  roles: [
    'Admin',
  ],
}));
vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: {
      roles: auth.roles,
    },
  }),
}));
const endpoint = 'http://localhost/api/artwork-providers/steamgriddb/settings';
const settings = {
  revision: '9f184511-6fc6-44d4-812b-97362184eb12',
  enabled: true,
  hasApiKey: true,
  isConfigured: true,
  managedByDeployment: false,
  configurationError: null,
  lastTestedAt: null,
  lastTestSucceeded: null,
  lastTestMessage: null,
};
function settingsResponse<
  T extends {
    revision: string;
  },
>(value: T) {
  return HttpResponse.json(value, {
    headers: {
      ETag: `"${value.revision}"`,
    },
  });
}

function renderCard() {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  render(
    <MantineProvider>
      <QueryClientProvider client={cache}>
        <SteamGridDbProviderCard />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return cache;
}
describe('SteamGridDB settings', () => {
  beforeEach(() => {
    client.setConfig({
      baseUrl: 'http://localhost',
    });
    auth.roles = [
      'Admin',
    ];
    server.use(http.get(endpoint, () => settingsResponse(settings)));
  });
  afterEach(() =>
    client.setConfig({
      baseUrl: '',
    }),
  );
  it('keeps saved credentials blank and submits optimistic revision without caching plaintext', async () => {
    const requests: unknown[] = [];
    const validators: (string | null)[] = [];
    server.use(
      http.put(endpoint, async ({ request }) => {
        requests.push(await request.json());
        validators.push(request.headers.get('If-Match'));
        return settingsResponse(settings);
      }),
    );
    const cache = renderCard();
    const input = await screen.findByLabelText('SteamGridDB API key');
    expect(input).toHaveValue('');
    const user = userEvent.setup();
    await user.type(input, 'new-private-api-key');
    await user.click(
      screen.getByRole('button', {
        name: 'Save SteamGridDB settings',
      }),
    );
    await screen.findByText('SteamGridDB settings saved.');
    expect(requests[0]).toEqual({
      enabled: true,
      apiKey: 'new-private-api-key',
      clearApiKey: false,
    });
    expect(input).toHaveValue('');
    expect(validators).toEqual([
      `"${settings.revision}"`,
    ]);
    expect(
      JSON.stringify(
        cache
          .getQueryCache()
          .getAll()
          .map((query) => query.state.data),
      ),
    ).not.toContain('new-private-api-key');
    expect(cache.getMutationCache().getAll()).toHaveLength(0);
  });
  it('does not fetch provider settings for managers', () => {
    auth.roles = [
      'Manager',
    ];
    const get = vi.fn();
    server.use(http.get(endpoint, get));
    renderCard();
    expect(screen.queryByText('Artwork providers')).not.toBeInTheDocument();
    expect(get).not.toHaveBeenCalled();
  });
  it('chains the accepted save and test revisions into subsequent header-only writes', async () => {
    const validators: (string | null)[] = [];
    const bodies: unknown[] = [];
    let saves = 0;
    server.use(
      http.put(endpoint, async ({ request }) => {
        validators.push(request.headers.get('If-Match'));
        bodies.push(await request.json());
        saves += 1;
        return settingsResponse({
          ...settings,
          revision: `saved-${saves}`,
        });
      }),
      http.post(`${endpoint}/test-connection`, async ({ request }) => {
        validators.push(request.headers.get('If-Match'));
        expect(await request.text()).toBe('');
        return settingsResponse({
          ...settings,
          revision: 'tested-1',
          lastTestedAt: '2026-10-22T12:00:00Z',
          lastTestSucceeded: true,
        });
      }),
    );
    renderCard();
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('SteamGridDB API key'), 'first-key');
    await user.click(
      screen.getByRole('button', {
        name: 'Save SteamGridDB settings',
      }),
    );
    await screen.findByText('SteamGridDB settings saved.');
    await user.click(
      screen.getByRole('button', {
        name: 'Test SteamGridDB connection',
      }),
    );
    await screen.findByText('Connection successful');
    await user.type(screen.getByLabelText('SteamGridDB API key'), 'next-key');
    await user.click(
      screen.getByRole('button', {
        name: 'Save SteamGridDB settings',
      }),
    );
    await waitFor(() => expect(saves).toBe(2));
    expect(validators).toEqual([
      `"${settings.revision}"`,
      '"saved-1"',
      '"tested-1"',
    ]);
    for (const body of bodies) expect(body).not.toHaveProperty('revision');
  });
  it('retains the secret draft after a stale save while reloading the observed settings', async () => {
    let stale = false;
    const validators: (string | null)[] = [];
    server.use(
      http.get(endpoint, () =>
        settingsResponse({
          ...settings,
          revision: stale ? 'other-writer' : settings.revision,
        }),
      ),
      http.put(endpoint, ({ request }) => {
        validators.push(request.headers.get('If-Match'));
        stale = true;
        return HttpResponse.json(
          {
            detail: 'Settings changed. Reload before saving.',
          },
          {
            status: 412,
          },
        );
      }),
    );
    const cache = renderCard();
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('SteamGridDB API key'), 'retained-private-key');
    await user.click(
      screen.getByRole('button', {
        name: 'Save SteamGridDB settings',
      }),
    );
    await screen.findByText('Settings changed. Reload before saving.');
    expect(screen.getByLabelText('SteamGridDB API key')).toHaveValue('retained-private-key');
    await user.click(
      screen.getByRole('button', {
        name: 'Save SteamGridDB settings',
      }),
    );
    await waitFor(() => expect(validators).toHaveLength(2));
    expect(validators).toEqual([
      `"${settings.revision}"`,
      `"${settings.revision}"`,
    ]);
    expect(
      JSON.stringify(
        cache
          .getQueryCache()
          .getAll()
          .map((query) => query.state.data),
      ),
    ).not.toContain('retained-private-key');
    expect(cache.getMutationCache().getAll()).toHaveLength(0);
  });
  it('does not let a connection test rebase a retained conflicting draft', async () => {
    let stale = false;
    const validators: (string | null)[] = [];
    const bodies: unknown[] = [];
    const testConnection = vi.fn(() =>
      settingsResponse({
        ...settings,
        enabled: false,
        revision: 'tested-other-writer',
      }),
    );
    server.use(
      http.get(endpoint, () =>
        settingsResponse({
          ...settings,
          enabled: !stale,
          revision: stale ? 'other-writer' : settings.revision,
        }),
      ),
      http.put(endpoint, async ({ request }) => {
        validators.push(request.headers.get('If-Match'));
        bodies.push(await request.json());
        stale = true;
        return HttpResponse.json(
          {
            detail: 'Settings changed. Reload before saving.',
          },
          {
            status: 412,
          },
        );
      }),
      http.post(`${endpoint}/test-connection`, testConnection),
    );
    renderCard();
    const user = userEvent.setup();
    const enabled = await screen.findByRole('switch', {
      name: 'Enable SteamGridDB artwork',
    });
    await user.click(enabled);
    await user.click(
      screen.getByRole('button', {
        name: 'Save SteamGridDB settings',
      }),
    );
    await screen.findByText('Settings changed. Reload before saving.');
    await waitFor(() =>
      expect(
        screen.queryByText('Save your changes before testing the connection.'),
      ).not.toBeInTheDocument(),
    );
    expect(enabled).not.toBeChecked();
    const test = screen.getByRole('button', {
      name: 'Test SteamGridDB connection',
    });
    expect(test).toBeDisabled();
    await user.click(test);
    await user.type(screen.getByLabelText('SteamGridDB API key'), 'retained-private-key');
    await user.click(
      screen.getByRole('button', {
        name: 'Save SteamGridDB settings',
      }),
    );
    await waitFor(() => expect(validators).toHaveLength(2));
    expect(testConnection).not.toHaveBeenCalled();
    expect(validators).toEqual([
      `"${settings.revision}"`,
      `"${settings.revision}"`,
    ]);
    expect(bodies[1]).toEqual({
      enabled: false,
      apiKey: 'retained-private-key',
      clearApiKey: false,
    });
    expect(screen.getByLabelText('SteamGridDB API key')).toHaveValue('retained-private-key');
    expect(enabled).not.toBeChecked();
  });
  it('refuses to edit settings whose response has no observed revision validator', async () => {
    server.use(http.get(endpoint, () => HttpResponse.json(settings)));
    renderCard();
    await screen.findByText('Could not load SteamGridDB settings.');
    expect(screen.queryByLabelText('SteamGridDB API key')).not.toBeInTheDocument();
  });
  it('shows saved connection test results without changing credentials', async () => {
    server.use(
      http.post(`${endpoint}/test-connection`, () =>
        settingsResponse({
          ...settings,
          lastTestedAt: '2026-09-07T12:00:00Z',
          lastTestSucceeded: true,
          lastTestMessage: 'SteamGridDB is reachable.',
        }),
      ),
    );
    renderCard();
    await userEvent.setup().click(
      await screen.findByRole('button', {
        name: 'Test SteamGridDB connection',
      }),
    );
    await waitFor(() => expect(screen.getByText('Connection successful')).toBeInTheDocument());
  });
});
