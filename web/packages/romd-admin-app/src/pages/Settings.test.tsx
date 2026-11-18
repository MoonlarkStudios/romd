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

it.each([
  'v0.4.0',
  'v0.4.0-rc.1',
])('shows the reported server version %s', async (version) => {
  server.use(
    http.get('*/api/server/info', () =>
      HttpResponse.json({
        version,
      }),
    ),
  );

  render(<Settings />);

  expect(await screen.findByText(`Version ${version}`)).toBeVisible();
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
