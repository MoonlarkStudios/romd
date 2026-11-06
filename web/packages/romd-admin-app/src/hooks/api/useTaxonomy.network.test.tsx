import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { server } from '../../test/msw/server';
import { ApiClientError, fetchRegionAliasPage, useRegions } from './useTaxonomy';

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

it('reports a generated-client network failure and can retry after connectivity returns', async () => {
  const disconnected = vi.fn(() => HttpResponse.error());
  server.use(http.get('*/api/regions', disconnected));
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  const { result, unmount } = renderHook(() => useRegions(), {
    wrapper,
  });

  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.error).toBeInstanceOf(ApiClientError);
  expect(result.current.error?.message).toBe('Failed to fetch regions');
  expect((result.current.error as ApiClientError).status).toBeUndefined();
  expect(disconnected).toHaveBeenCalledOnce();

  server.use(
    http.get('*/api/regions', () =>
      HttpResponse.json({
        items: [],
        nextCursor: null,
        hasNextPage: false,
      }),
    ),
  );
  await act(async () => {
    await result.current.refetch();
  });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(result.current.data).toEqual([]);
  unmount();
  queryClient.clear();
});

it('reads every page of regions by following the cursor', async () => {
  const requested: Array<string | null> = [];
  const region = (key: string) => ({
    key,
    ownership: null,
    builtInVersion: null,
    name: key,
    description: null,
    sortOrder: 999,
    retired: false,
    isAutoCreated: true,
    canMerge: true,
    aliases: {
      items: [],
      nextCursor: null,
      hasNextPage: false,
    },
  });
  server.use(
    http.get('*/api/regions', ({ request }) => {
      const url = new URL(request.url);
      requested.push(url.searchParams.get('cursor'));
      expect(url.searchParams.get('limit')).toBe('100');
      expect(url.searchParams.has('query')).toBe(false);
      return HttpResponse.json(
        url.searchParams.get('cursor') === 'second'
          ? {
              items: [
                region('dat-b'),
              ],
              nextCursor: null,
              hasNextPage: false,
            }
          : {
              items: [
                region('dat-a'),
              ],
              nextCursor: 'second',
              hasNextPage: true,
            },
      );
    }),
  );
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  const { result, unmount } = renderHook(() => useRegions(), {
    wrapper,
  });

  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(result.current.data?.map((item) => item.key)).toEqual([
    'dat-a',
    'dat-b',
  ]);
  expect(requested).toEqual([
    null,
    'second',
  ]);
  unmount();
  queryClient.clear();
});

it('searches regions on the server and reads every matching page', async () => {
  const requested: Array<string | null> = [];
  server.use(
    http.get('*/api/regions', ({ request }) => {
      const url = new URL(request.url);
      requested.push(`${url.searchParams.get('query')}:${url.searchParams.get('cursor')}`);
      return HttpResponse.json(
        url.searchParams.get('cursor') === 'second'
          ? {
              items: [],
              nextCursor: null,
              hasNextPage: false,
            }
          : {
              items: [],
              nextCursor: 'second',
              hasNextPage: true,
            },
      );
    }),
  );
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  const { result, unmount } = renderHook(() => useRegions('needle'), {
    wrapper,
  });

  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(requested).toEqual([
    'needle:null',
    'needle:second',
  ]);
  unmount();
  queryClient.clear();
});

it('reports the server explanation of an unsearchable query', async () => {
  server.use(
    http.get('*/api/regions', () =>
      HttpResponse.json(
        {
          title: 'Bad Request',
          detail: 'The query must contain a letter or number.',
        },
        {
          status: 400,
        },
      ),
    ),
  );
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  const { result, unmount } = renderHook(() => useRegions('!!!'), {
    wrapper,
  });

  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.error?.message).toBe('The query must contain a letter or number.');
  expect((result.current.error as ApiClientError).status).toBe(400);
  unmount();
  queryClient.clear();
});

it('reads a page of region aliases after the cursor', async () => {
  server.use(
    http.get('*/api/regions/:regionKey/aliases', ({ request, params }) => {
      const url = new URL(request.url);
      expect(params.regionKey).toBe('dat-usa');
      expect(url.searchParams.get('cursor')).toBe('after-ten');
      expect(url.searchParams.get('limit')).toBe('50');
      return HttpResponse.json({
        items: [
          {
            id: 'a11',
            alias: 'estados unidos',
            ownership: 'Installation',
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      });
    }),
  );

  const page = await fetchRegionAliasPage('dat-usa', 'after-ten');

  expect(page.items.map((alias) => alias.alias)).toEqual([
    'estados unidos',
  ]);
  expect(page.hasNextPage).toBe(false);
});
