import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, expect, it } from 'vitest';
import { server } from '../../test/msw/server';
import { useArtworkProviders, useSavedArtwork } from './useArtwork';

let cache: QueryClient;
const endpoint = 'http://localhost/api/titles/title1/artwork-assets';
const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={cache}>{children}</QueryClientProvider>
);

beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
  cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
});
afterEach(() => {
  cache.clear();
  client.setConfig({
    baseUrl: '',
  });
});

it('loads all saved assets across an empty intermediate page before exposing the gallery', async () => {
  const cursors: (string | null)[] = [];
  server.use(
    http.get(endpoint, ({ request }) => {
      const cursor = new URL(request.url).searchParams.get('cursor');
      cursors.push(cursor);
      return HttpResponse.json(
        cursor === null
          ? {
              items: [
                {
                  id: 'first',
                },
              ],
              nextCursor: 'middle',
              hasNextPage: true,
            }
          : cursor === 'middle'
            ? {
                items: [],
                nextCursor: 'last',
                hasNextPage: true,
              }
            : {
                items: [
                  {
                    id: 'last',
                  },
                ],
                nextCursor: null,
                hasNextPage: false,
              },
      );
    }),
  );
  const { result } = renderHook(() => useSavedArtwork('title1'), {
    wrapper,
  });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(result.current.data?.map((item) => item.id)).toEqual([
    'first',
    'last',
  ]);
  expect(cursors).toEqual([
    null,
    'middle',
    'last',
  ]);
});

it('does not expose an incomplete gallery when a later saved-asset page fails', async () => {
  server.use(
    http.get(endpoint, ({ request }) =>
      new URL(request.url).searchParams.has('cursor')
        ? HttpResponse.json(
            {
              detail: 'Unavailable',
            },
            {
              status: 503,
            },
          )
        : HttpResponse.json({
            items: [
              {
                id: 'first',
              },
            ],
            nextCursor: 'last',
            hasNextPage: true,
          }),
    ),
  );
  const { result } = renderHook(() => useSavedArtwork('title1'), {
    wrapper,
  });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.data).toBeUndefined();
  expect(result.current.error?.message).toBe('Could not load saved artwork.');
});

it('rejects a repeated saved-asset cursor without publishing partial items', async () => {
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: [
          {
            id: 'same',
          },
        ],
        nextCursor: 'repeat',
        hasNextPage: true,
      }),
    ),
  );
  const { result } = renderHook(() => useSavedArtwork('title1'), {
    wrapper,
  });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.error?.message).toBe('The server repeated a page cursor.');
  expect(result.current.data).toBeUndefined();
});

it('rejects a saved-asset response without the required page contract', async () => {
  server.use(
    http.get(endpoint, () =>
      HttpResponse.json({
        items: [],
        nextCursor: null,
      }),
    ),
  );
  const { result } = renderHook(() => useSavedArtwork('title1'), {
    wrapper,
  });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.error?.message).toBe('The server returned an invalid page.');
});

it('uses the canonical provider collection and the declared role filter', async () => {
  const requests: URL[] = [];
  server.use(
    http.get('http://localhost/api/artwork-providers', ({ request }) => {
      requests.push(new URL(request.url));
      return HttpResponse.json([]);
    }),
  );
  const { result } = renderHook(() => useArtworkProviders('title1', 'Hero'), {
    wrapper,
  });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(requests[0].searchParams.get('titleId')).toBe('title1');
  expect(requests[0].searchParams.get('role')).toBe('Hero');
});
