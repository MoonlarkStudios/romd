import {
  listCollections,
  listLibraries,
  listSystemDats,
  listTrackedTitles,
  listUsers,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, expect, it, vi } from 'vitest';
import { datFixtures } from '../../test/msw/fixtures/dats';
import { useCollections } from './useCollectionManagement';
import { useDatsByPlatform } from './useDats';
import { useLibraries } from './useLibraryManagement';
import { useMissingTrackedTitles } from './useTrackedCollection';
import { useUsers } from './useUsers';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  listCollections: vi.fn(),
  listLibraries: vi.fn(),
  listSystemDats: vi.fn(),
  listTrackedTitles: vi.fn(),
  listUsers: vi.fn(),
}));

const response = <T,>(items: T[], nextCursor: string | null = null) => ({
  data: {
    items,
    nextCursor,
    hasNextPage: nextCursor !== null,
  },
  error: undefined,
  request: new Request('http://localhost'),
  response: new Response(),
});
function wrapper({ children }: { children: ReactNode }) {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  return <QueryClientProvider client={cache}>{children}</QueryClientProvider>;
}

beforeEach(() => vi.resetAllMocks());

it('drains library, collection and user lookups before exposing arrays to selection controls', async () => {
  const library = {
    id: 'library-2',
    name: 'Later library',
    configurationState: 'Valid',
    isDefault: false,
    needsMaterialization: false,
    itemCount: 0,
    createdAt: '2026-10-21T00:00:00Z',
  };
  const user = {
    id: 'user-2',
    email: 'later@example.com',
    roles: [
      'User',
    ],
    libraryId: null,
    createdAt: '2026-10-21T00:00:00Z',
    updatedAt: null,
  };
  vi.mocked(listLibraries)
    .mockResolvedValueOnce(response([], 'libraries-2'))
    .mockResolvedValueOnce(
      response([
        library,
      ]),
    );
  vi.mocked(listCollections)
    .mockResolvedValueOnce(response([], 'collections-2'))
    .mockResolvedValueOnce(
      response([
        {
          id: 'collection-2',
          name: 'Later collection',
        },
      ]),
    );
  vi.mocked(listUsers)
    .mockResolvedValueOnce(response([], 'users-2'))
    .mockResolvedValueOnce(
      response([
        user,
      ]),
    );
  const { result } = renderHook(
    () => ({
      libraries: useLibraries(),
      collections: useCollections('snes'),
      users: useUsers(),
    }),
    {
      wrapper,
    },
  );

  await waitFor(() =>
    expect(
      result.current.libraries.isSuccess &&
        result.current.collections.isSuccess &&
        result.current.users.isSuccess,
    ).toBe(true),
  );
  expect(result.current.libraries.data).toEqual([
    library,
  ]);
  expect(result.current.collections.data?.map((item) => item.id)).toEqual([
    'collection-2',
  ]);
  expect(result.current.users.data).toEqual([
    user,
  ]);
  expect(listCollections).toHaveBeenLastCalledWith({
    query: {
      systemKey: 'snes',
      cursor: 'collections-2',
      limit: 100,
    },
  });
  expect(listLibraries).toHaveBeenLastCalledWith({
    query: {
      cursor: 'libraries-2',
      limit: 100,
    },
  });
  expect(listUsers).toHaveBeenLastCalledWith({
    query: {
      cursor: 'users-2',
      limit: 100,
    },
  });
});

it('retains the system scope while loading DAT continuations', async () => {
  vi.mocked(listSystemDats)
    .mockResolvedValueOnce(response([], 'dats-2'))
    .mockResolvedValueOnce(
      response([
        datFixtures.list[0],
      ]),
    );
  const { result } = renderHook(() => useDatsByPlatform('snes'), {
    wrapper,
  });
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(result.current.data).toEqual([
    datFixtures.list[0],
  ]);
  expect(listSystemDats).toHaveBeenLastCalledWith({
    path: {
      systemKey: 'snes',
    },
    query: {
      cursor: 'dats-2',
      limit: 100,
    },
    signal: expect.any(AbortSignal),
  });
});

it('fails a tracked list when a later scoped page fails rather than exposing partial titles', async () => {
  vi.mocked(listTrackedTitles)
    .mockResolvedValueOnce(response([], 'missing-2'))
    .mockResolvedValueOnce({
      error: {
        status: 503,
        title: 'Unavailable',
      },
      request: new Request('http://localhost'),
      response: new Response(null, {
        status: 503,
      }),
    });
  const { result } = renderHook(() => useMissingTrackedTitles(), {
    wrapper,
  });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.data).toBeUndefined();
  expect(listTrackedTitles).toHaveBeenLastCalledWith({
    query: {
      status: 'missing',
      cursor: 'missing-2',
      limit: 100,
    },
  });
});
