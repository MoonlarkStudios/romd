import { MantineProvider } from '@mantine/core';
import {
  client,
  createConsumerConnection,
  getConsumerReleaseAccess,
  replaceConsumerPassword,
  updateConsumerConnection,
  updateConsumerUserSettings,
} from '@romd/consumer-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, renderHook, screen, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Connections } from '../pages/Connections';
import { consumerQueryKeys, useCollections, useTitleDetail } from './useConsumerLibrary';
import { putPlaySessionSnapshot, useRecentlyPlayed } from './usePlayActivity';

const originalConfig = client.getConfig();
const fetchRequest = vi.fn<(request: Request) => Promise<Response>>();
const queryClients: QueryClient[] = [];

beforeEach(() => {
  fetchRequest.mockReset();
  client.setConfig({
    baseUrl: 'https://consumer.example.test',
    fetch: (input, init) => fetchRequest(new Request(input, init)),
    headers: {
      Authorization: 'Bearer player',
    },
  });
});

afterEach(() => {
  for (const queryClient of queryClients) queryClient.clear();
  queryClients.length = 0;
  client.setConfig(originalConfig);
});

function harness() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  queryClients.push(queryClient);
  return {
    queryClient,
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    ),
  };
}

function json(body: unknown, status = 200, headers?: HeadersInit) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': 'application/json',
      ...headers,
    },
  });
}

function page<T>(items: T[], nextCursor: string | null = null) {
  return {
    items,
    nextCursor,
    hasNextPage: nextCursor !== null,
  };
}

function collection(id: string) {
  return {
    id,
    name: id,
    description: null,
    system: null,
    coverUrl: null,
    heroUrl: null,
    itemCount: 1,
    isFeatured: false,
  };
}

describe('consumer collection HTTP traversal', () => {
  it('preserves filters and credentials through short and empty intermediate pages', async () => {
    const cursors: Array<string | null> = [];
    fetchRequest.mockImplementation(async (request) => {
      const url = new URL(request.url);
      expect(request.method).toBe('GET');
      expect(url.pathname).toBe('/api/collections');
      expect(url.searchParams.get('systemKey')).toBe('snes');
      expect(url.searchParams.get('limit')).toBe('100');
      expect(request.headers.get('Authorization')).toBe('Bearer player');
      const cursor = url.searchParams.get('cursor');
      cursors.push(cursor);
      return json(
        cursor === null
          ? page(
              [
                collection('first'),
              ],
              'opaque +/one=',
            )
          : cursor === 'opaque +/one='
            ? page([], 'opaque ?two=')
            : page([
                collection('last'),
              ]),
      );
    });
    const { result } = renderHook(() => useCollections('snes'), {
      wrapper: harness().wrapper,
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data?.map((item) => item.id)).toEqual([
      'first',
      'last',
    ]);
    expect(cursors).toEqual([
      null,
      'opaque +/one=',
      'opaque ?two=',
    ]);
  });

  it('accepts a full terminal page without an inferred extra request', async () => {
    fetchRequest.mockResolvedValue(
      json(
        page(
          Array.from(
            {
              length: 100,
            },
            (_, index) => collection(`row-${index}`),
          ),
        ),
      ),
    );
    const { result } = renderHook(() => useCollections(), {
      wrapper: harness().wrapper,
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toHaveLength(100);
    expect(fetchRequest).toHaveBeenCalledTimes(1);
  });

  it('accepts an empty terminal page', async () => {
    fetchRequest.mockResolvedValue(json(page([])));
    const { result } = renderHook(() => useCollections(), {
      wrapper: harness().wrapper,
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toEqual([]);
    expect(fetchRequest).toHaveBeenCalledTimes(1);
  });

  it('rejects a repeated continuation without publishing partial results', async () => {
    fetchRequest
      .mockResolvedValueOnce(
        json(
          page(
            [
              collection('first'),
            ],
            'same',
          ),
        ),
      )
      .mockResolvedValueOnce(
        json(
          page(
            [
              collection('second'),
            ],
            'same',
          ),
        ),
      );
    const { result } = renderHook(() => useCollections(), {
      wrapper: harness().wrapper,
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.data).toBeUndefined();
    expect(fetchRequest).toHaveBeenCalledTimes(2);
  });

  it.each([
    [],
    {
      items: [],
      nextCursor: null,
      hasNextPage: true,
    },
    {
      items: [],
      nextCursor: 'unexpected',
      hasNextPage: false,
    },
    {
      items: [],
      unexpected: null,
      hasNextPage: false,
    },
    {
      items: 'invalid',
      nextCursor: null,
      hasNextPage: false,
    },
  ])('rejects malformed page %#', async (body) => {
    fetchRequest.mockResolvedValue(json(body));
    const { result } = renderHook(() => useCollections(), {
      wrapper: harness().wrapper,
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.data).toBeUndefined();
  });

  it('preserves the previous complete cache when a later page fails', async () => {
    const state = harness();
    state.queryClient.setQueryData(consumerQueryKeys.collections(), [
      collection('cached'),
    ]);
    fetchRequest
      .mockResolvedValueOnce(
        json(
          page(
            [
              collection('replacement'),
            ],
            'next',
          ),
        ),
      )
      .mockResolvedValueOnce(
        json(
          {
            detail: 'Unavailable',
          },
          503,
        ),
      );
    const { result } = renderHook(() => useCollections(), {
      wrapper: state.wrapper,
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.data?.map((item) => item.id)).toEqual([
      'cached',
    ]);
    expect(fetchRequest).toHaveBeenCalledTimes(2);
  });
});

describe('consumer title and connection HTTP traversal', () => {
  it('completes embedded media and releases before exposing the title view', async () => {
    const requested: string[] = [];
    const media = (id: string) => ({
      id,
      type: 'Screenshot',
      url: `/media/${id}`,
      isPrimary: false,
    });
    const release = (id: string) => ({
      id,
      name: id,
      regions: [],
      languages: [],
      sizeBytes: '1',
      isComplete: true,
    });
    fetchRequest.mockImplementation(async (request) => {
      const url = new URL(request.url);
      requested.push(url.pathname);
      if (url.pathname === '/api/titles/title-1')
        return json({
          id: 'title-1',
          name: 'Title',
          system: {
            key: 'snes',
            name: 'SNES',
            compactLabel: 'SNES',
          },
          defaultReleaseId: 'release-2',
          media: page(
            [
              media('media-1'),
            ],
            'media cursor',
          ),
          releases: page(
            [
              release('release-1'),
            ],
            'release cursor',
          ),
        });
      if (url.pathname.endsWith('/media')) {
        expect(url.searchParams.get('cursor')).toBe('media cursor');
        return json(
          page([
            media('media-2'),
          ]),
        );
      }
      expect(url.pathname).toBe('/api/titles/title-1/releases');
      expect(url.searchParams.get('cursor')).toBe('release cursor');
      return json(
        page([
          release('release-2'),
        ]),
      );
    });
    const { result } = renderHook(() => useTitleDetail('title-1'), {
      wrapper: harness().wrapper,
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data?.media.map((item) => item.id)).toEqual([
      'media-1',
      'media-2',
    ]);
    expect(result.current.data?.releases.map((item) => item.id)).toEqual([
      'release-1',
      'release-2',
    ]);
    expect(result.current.data?.defaultReleaseId).toBe('release-2');
    expect(requested.sort()).toEqual([
      '/api/titles/title-1',
      '/api/titles/title-1/media',
      '/api/titles/title-1/releases',
    ]);
  });

  it('rejects a foreign title before following its embedded continuations', async () => {
    fetchRequest.mockResolvedValue(
      json({
        id: 'foreign',
        media: page([], 'next'),
        releases: page([]),
      }),
    );
    const { result } = renderHook(() => useTitleDetail('title-1'), {
      wrapper: harness().wrapper,
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(fetchRequest).toHaveBeenCalledTimes(1);
    expect(result.current.data).toBeUndefined();
  });

  it('rejects a missing embedded page without fetching a replacement feed', async () => {
    fetchRequest.mockResolvedValue(
      json({
        id: 'title-1',
        releases: page([]),
      }),
    );
    const { result } = renderHook(() => useTitleDetail('title-1'), {
      wrapper: harness().wrapper,
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(fetchRequest).toHaveBeenCalledTimes(1);
    expect(result.current.data).toBeUndefined();
  });

  it('renders later connection history through the actual generated HTTP boundary', async () => {
    fetchRequest.mockImplementation(async (request) => {
      const url = new URL(request.url);
      expect(url.pathname).toBe('/api/me/connections');
      const cursor = url.searchParams.get('cursor');
      const connection = (id: string, name: string) => ({
        id,
        name,
        username: id,
        status: 'Revoked',
        createdAt: '2026-10-01T00:00:00Z',
        credentialUpdatedAt: '2026-10-01T00:00:00Z',
        lastUsedAt: null,
        format: 'original',
      });
      return json(
        cursor === null
          ? page(
              [
                connection('first', 'First device'),
              ],
              'history cursor',
            )
          : page([
              connection('last', 'Last device'),
            ]),
      );
    });
    const state = harness();
    render(
      <state.wrapper>
        <MantineProvider env="test">
          <Connections />
        </MantineProvider>
      </state.wrapper>,
    );

    expect(
      await screen.findByRole('heading', {
        name: 'Last device',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('heading', {
        name: 'First device',
      }),
    ).toBeInTheDocument();
    expect(fetchRequest).toHaveBeenCalledTimes(2);
    expect(new URL(fetchRequest.mock.calls[1][0].url).searchParams.get('cursor')).toBe(
      'history cursor',
    );
  });
});

describe('consumer resource HTTP semantics', () => {
  it('receives the created connection representation and canonical Location on 201', async () => {
    const connection = {
      id: 'connection-1',
      name: 'Deck',
      username: 'romd-user',
      status: 'Active',
      createdAt: '2026-10-01T00:00:00Z',
      credentialUpdatedAt: '2026-10-01T00:00:00Z',
      format: 'original',
    };
    fetchRequest.mockImplementation(async (request) => {
      expect(request.method).toBe('POST');
      expect(new URL(request.url).pathname).toBe('/api/me/connections');
      expect(await request.json()).toEqual({
        name: 'Deck',
        format: 'original',
      });
      return json(
        {
          connection,
          password: 'one-time-secret',
          endpoint: 'https://consumer.example.test/vfs/library/',
          rcloneConfig: '[romd]',
        },
        201,
        {
          Location: '/api/me/connections/connection-1',
        },
      );
    });

    const result = await createConsumerConnection({
      body: {
        name: 'Deck',
        format: 'original',
      },
    });

    expect(result.response.status).toBe(201);
    expect(result.response.headers.get('Location')).toBe('/api/me/connections/connection-1');
    expect(result.data?.connection).toEqual(connection);
    expect(result.data?.password).toBe('one-time-secret');
  });

  it('uses GET access, PUT password, and partial PATCH bodies on canonical self routes', async () => {
    const requests: Array<{
      method: string;
      path: string;
      body: string;
    }> = [];
    fetchRequest.mockImplementation(async (request) => {
      requests.push({
        method: request.method,
        path: new URL(request.url).pathname,
        body: await request.text(),
      });
      return request.url.endsWith('/password')
        ? new Response(null, {
            status: 204,
          })
        : json({});
    });

    await getConsumerReleaseAccess({
      path: {
        releaseId: 'release-1',
      },
    });
    await replaceConsumerPassword({
      body: {
        currentPassword: 'old',
        newPassword: 'new',
      },
    });
    await updateConsumerUserSettings({
      body: {
        theme: 'dark',
      },
    });
    await updateConsumerConnection({
      path: {
        connectionId: 'connection-1',
      },
      body: {
        name: 'Renamed',
      },
    });

    expect(requests).toEqual([
      {
        method: 'GET',
        path: '/api/releases/release-1/access',
        body: '',
      },
      {
        method: 'PUT',
        path: '/api/me/password',
        body: '{"currentPassword":"old","newPassword":"new"}',
      },
      {
        method: 'PATCH',
        path: '/api/me/settings',
        body: '{"theme":"dark"}',
      },
      {
        method: 'PATCH',
        path: '/api/me/connections/connection-1',
        body: '{"name":"Renamed"}',
      },
    ]);
  });

  it('keeps the recently played home rail bounded to its requested first page', async () => {
    fetchRequest.mockImplementation(async (request) => {
      const url = new URL(request.url);
      expect(url.pathname).toBe('/api/me/activity/recently-played');
      expect(url.searchParams.get('limit')).toBe('2');
      return json(
        page(
          [
            collection('recent'),
          ],
          'next page',
        ),
      );
    });
    const { result } = renderHook(() => useRecentlyPlayed(2), {
      wrapper: harness().wrapper,
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data?.map((item) => item.id)).toEqual([
      'recent',
    ]);
    expect(fetchRequest).toHaveBeenCalledTimes(1);
  });

  it('preserves the play snapshot PUT transport through the local activity abstraction', async () => {
    fetchRequest.mockImplementation(async (request) => {
      expect(request.method).toBe('PUT');
      expect(new URL(request.url).pathname).toBe('/api/me/activity/play-sessions/session-1');
      expect(await request.json()).toEqual({
        clientId: 'romd.web',
        titleId: 'title-1',
        releaseId: 'release-1',
        startedAt: '2026-10-01T00:00:00Z',
      });
      return json({
        sessionId: 'session-1',
      });
    });

    await putPlaySessionSnapshot('session-1', {
      clientId: 'romd.web',
      titleId: 'title-1',
      releaseId: 'release-1',
      startedAt: '2026-10-01T00:00:00Z',
    });

    expect(fetchRequest).toHaveBeenCalledTimes(1);
  });
});
