import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { platformFixtures } from '../../test/msw/fixtures/platforms';
import { server } from '../../test/msw/server';
import { managedSystemKeys, useManagedSystems } from './useManagedSystems';
import { platformKeys, usePlatforms } from './usePlatforms';

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

function harness() {
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return {
    client,
    wrapper,
  };
}
const page = (items: unknown[], nextCursor: string | null = null) => ({
  items,
  nextCursor,
  hasNextPage: nextCursor !== null,
});
const managed = (key: string) => ({
  key,
  name: key,
  shortName: key,
  manufacturer: null,
  enabled: true,
  state: 'Ready',
  message: null,
  catalogCount: 0,
  ownedTitles: 0,
  trackedTitles: 0,
});

describe('complete reference choices', () => {
  it.each([
    'systems',
    'system-setup',
  ] as const)('collects %s beyond the actual 100-item page boundary before publishing', async (route) => {
    const rows = Array.from(
      {
        length: 101,
      },
      (_, index) =>
        route === 'systems'
          ? {
              ...platformFixtures.single,
              key: `local-${index}`,
            }
          : managed(`local-${index}`),
    );
    const cursors: (string | null)[] = [];
    let release!: () => void;
    const resumed = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get(`/api/${route}`, async ({ request }) => {
        const query = new URL(request.url).searchParams;
        expect(query.get('limit')).toBe('100');
        cursors.push(query.get('cursor'));
        if (query.get('cursor')) {
          await resumed;
          return HttpResponse.json(page(rows.slice(100)));
        }
        return HttpResponse.json(page(rows.slice(0, 100), 'next'));
      }),
    );
    const { client, wrapper } = harness();
    const useRows = route === 'systems' ? usePlatforms : useManagedSystems;
    const { result, unmount } = renderHook(() => useRows(), {
      wrapper,
    });
    await waitFor(() =>
      expect(cursors).toEqual([
        null,
        'next',
      ]),
    );
    expect(result.current.data).toBeUndefined();
    expect(result.current.isPending).toBe(true);
    await act(async () => {
      release();
      await resumed;
    });
    await waitFor(() => expect(result.current.data).toHaveLength(101));
    expect(result.current.data?.at(-1)?.key).toBe('local-100');
    expect(cursors).toEqual([
      null,
      'next',
    ]);
    unmount();
    client.clear();
  });
  it.each([
    'systems',
    'system-setup',
  ] as const)('preserves cached complete %s after a later page fails', async (route) => {
    const old =
      route === 'systems'
        ? [
            platformFixtures.single,
          ]
        : [
            managed('old'),
          ];
    const { client, wrapper } = harness();
    const key = route === 'systems' ? platformKeys.list() : managedSystemKeys.all;
    client.setQueryData(key, old);
    server.use(
      http.get(`/api/${route}`, ({ request }) =>
        new URL(request.url).searchParams.has('cursor')
          ? HttpResponse.json(
              {
                errorCode: 'Server.Unavailable',
              },
              {
                status: 503,
              },
            )
          : HttpResponse.json(
              page(
                [
                  route === 'systems'
                    ? {
                        ...platformFixtures.single,
                        key: 'new',
                      }
                    : managed('new'),
                ],
                'later',
              ),
            ),
      ),
    );
    const useRows = route === 'systems' ? usePlatforms : useManagedSystems;
    const { result, unmount } = renderHook(() => useRows(), {
      wrapper,
    });
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.data).toEqual(old);
    expect(client.getQueryData(key)).toEqual(old);
    unmount();
    client.clear();
  });
  it.each([
    'systems',
    'system-setup',
  ] as const)('cancels %s traversal between pages after its last observer unmounts', async (route) => {
    let requestSignal: AbortSignal | undefined;
    let release!: () => void;
    const pending = new Promise<void>((resolve) => {
      release = resolve;
    });
    let calls = 0;
    server.use(
      http.get(`/api/${route}`, async ({ request }) => {
        calls += 1;
        if (new URL(request.url).searchParams.has('cursor')) {
          requestSignal = request.signal;
          await pending;
        }
        return HttpResponse.json(page([], 'later'));
      }),
    );
    const { client, wrapper } = harness();
    const useRows = route === 'systems' ? usePlatforms : useManagedSystems;
    const { unmount } = renderHook(() => useRows(), {
      wrapper,
    });
    await waitFor(() => expect(requestSignal).toBeInstanceOf(AbortSignal));
    unmount();
    await waitFor(() => expect(requestSignal?.aborted).toBe(true));
    release();
    await pending;
    expect(calls).toBe(2);
    expect(
      client.getQueryData(route === 'systems' ? platformKeys.list() : managedSystemKeys.all),
    ).toBeUndefined();
    client.clear();
  });
});
