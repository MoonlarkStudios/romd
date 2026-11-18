import { client } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, expect, it } from 'vitest';
import { server } from '../test/msw/server';
import { render } from '../test/utils/render';
import { Settings } from './Settings';

beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
});

afterEach(() => {
  client.setConfig({
    baseUrl: '',
  });
});

const build = {
  tag: 'v0.4.0-rc.1',
  revision: '0123456789abcdef0123456789abcdef01234567',
};

it.each([
  'v0.4.0',
  'v0.4.0-rc.1',
])('shows the installed release %s beside the running build', async (release) => {
  server.use(
    http.get('*/api/server/info', () =>
      HttpResponse.json({
        release,
        build,
      }),
    ),
  );

  render(<Settings />);

  expect(await screen.findByText(`Version ${release}`)).toBeVisible();
  expect(screen.getByText('Build v0.4.0-rc.1 (0123456789ab)')).toBeVisible();
});

it('shows only the build when the server does not run from a release bundle', async () => {
  server.use(
    http.get('*/api/server/info', () =>
      HttpResponse.json({
        release: null,
        build: {
          tag: 'development',
          revision: 'development',
        },
      }),
    ),
  );

  render(<Settings />);

  expect(await screen.findByText('Build development')).toBeVisible();
  expect(screen.queryByText(/^Version/)).toBeNull();
});

it('reports when the server version is unavailable', async () => {
  server.use(
    http.get('*/api/server/info', () =>
      HttpResponse.json(
        {},
        {
          status: 503,
        },
      ),
    ),
  );

  render(<Settings />);

  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Version unavailable'));
});
