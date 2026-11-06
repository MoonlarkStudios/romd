import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import type { ReactNode } from 'react';
import { beforeEach, expect, it } from 'vitest';
import { server } from '../../test/msw/server';
import { type CatalogSearchParams, useCatalogCount, useCatalogSearch } from './useCatalogSearch';

beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
});

async function requestedSearch(params: CatalogSearchParams): Promise<URLSearchParams> {
  const requests: URLSearchParams[] = [];
  server.use(
    http.get('http://localhost/api/catalog', ({ request }) => {
      requests.push(new URL(request.url).searchParams);
      return HttpResponse.json({
        items: [],
        nextCursor: null,
        hasNextPage: false,
      });
    }),
  );
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useCatalogSearch(params), {
    wrapper,
  });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(requests).toHaveLength(1);
  return requests[0];
}

it('sends each enrichment flag as a repeated parameter', async () => {
  const search = await requestedSearch({
    enrichment: [
      'NeedsReview',
      'Processing',
    ],
    enrichmentStatus: 'Failed',
  });
  expect(search.toString()).toContain('enrichment=NeedsReview&enrichment=Processing');
  expect(search.getAll('enrichment')).toEqual([
    'NeedsReview',
    'Processing',
  ]);
  expect(search.get('enrichmentStatus')).toBe('Failed');
});

it('omits the enrichment parameter when no flag is selected', async () => {
  const search = await requestedSearch({
    enrichment: [],
    genre: 'Action',
  });
  expect(search.has('enrichment')).toBe(false);
  expect(search.get('genre')).toBe('Action');
});

it('retains previous results while a new search is pending', async () => {
  let finishSearch: (() => void) | undefined;
  const pending = new Promise<void>((resolve) => {
    finishSearch = resolve;
  });
  server.use(
    http.get('http://localhost/api/catalog', async ({ request }) => {
      const searching = new URL(request.url).searchParams.has('query');
      if (searching) await pending;
      return HttpResponse.json({
        items: [
          {
            id: searching ? 'new' : 'old',
            name: searching ? 'Mario' : 'Zelda',
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      });
    }),
  );
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  const { result, rerender } = renderHook(
    ({ query }: { query?: string }) =>
      useCatalogSearch({
        query,
      }),
    {
      wrapper,
      initialProps: {
        query: undefined,
      } as {
        query?: string;
      },
    },
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  rerender({
    query: 'Mario',
  });
  await waitFor(() => expect(result.current.isPlaceholderData).toBe(true));
  expect(result.current.data?.pages[0].items[0].name).toBe('Zelda');
  expect(result.current.isFetching).toBe(true);
  finishSearch?.();
  await waitFor(() => expect(result.current.data?.pages[0].items[0].name).toBe('Mario'));
  expect(result.current.isPlaceholderData).toBe(false);
});

it('counts the same filter scope and reuses the total when only sorting changes', async () => {
  const requests: URLSearchParams[] = [];
  server.use(
    http.get('http://localhost/api/catalog/stats', ({ request }) => {
      requests.push(new URL(request.url).searchParams);
      return HttpResponse.json({
        totalCount: 123,
      });
    }),
  );
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  const params = {
    query: 'Mario',
    systemKey: 'snes',
    genre: 'Platformer',
    tracked: 'tracked',
    releaseReview: 'missing',
    releaseCompleteness: 'partial',
    enrichment: [
      'NeedsReview',
    ],
    artworkStatus: 'Poster',
    enrichmentStatus: 'Failed',
  };
  const { result, rerender } = renderHook(
    ({ sortBy }) =>
      useCatalogCount({
        ...params,
        sortBy,
      }),
    {
      wrapper,
      initialProps: {
        sortBy: 'name',
      },
    },
  );
  await waitFor(() => expect(result.current.data).toBe(123));
  expect(Object.fromEntries(requests[0])).toEqual({
    ...params,
    enrichment: 'NeedsReview',
  });
  rerender({
    sortBy: 'rating',
  });
  expect(result.current.data).toBe(123);
  expect(requests).toHaveLength(1);
});
