import { client } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, expect, it } from 'vitest';
import { datFixtures } from '../../test/msw/fixtures/dats';
import { server } from '../../test/msw/server';
import { render } from '../../test/utils/render';
import { NewSystemModal } from './NewSystemModal';

beforeEach(() =>
  client.setConfig({
    baseUrl: window.location.origin,
  }),
);
afterEach(() =>
  client.setConfig({
    baseUrl: '',
  }),
);

it('offers a locally registered manufacturer after the first 100 companies', async () => {
  const rows = Array.from(
    {
      length: 101,
    },
    (_, index) => ({
      key: `local-${index}`,
      name: `Company ${index}`,
      description: null,
      retired: false,
      ownership: 'User',
      builtInVersion: null,
      revision: 'a'.repeat(64),
    }),
  );
  const requests: URL[] = [];
  server.use(
    http.get('/api/companies', ({ request }) => {
      const url = new URL(request.url);
      requests.push(url);
      return HttpResponse.json(
        url.searchParams.has('cursor')
          ? {
              items: rows.slice(100),
              nextCursor: null,
              hasNextPage: false,
            }
          : {
              items: rows.slice(0, 100),
              nextCursor: 'next',
              hasNextPage: true,
            },
      );
    }),
  );
  render(
    <NewSystemModal
      target={datFixtures.list[0]}
      rememberAlias={false}
      onClose={() => {}}
    />,
  );
  const input = screen.getByRole('combobox', {
    name: 'Manufacturer',
    exact: true,
  });
  await waitFor(() => expect(input).toBeEnabled());
  await userEvent.type(input, 'Company 100');
  expect(
    await screen.findByRole('option', {
      name: 'Company 100',
      exact: true,
    }),
  ).toBeInTheDocument();
  expect(requests).toHaveLength(2);
  expect(requests.every((url) => url.searchParams.get('limit') === '100')).toBe(true);
});
