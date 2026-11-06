import type { CollectionDetail, CollectionItemDto } from '@romd/admin-api-client';
import {
  createCollectionItem,
  deleteCollectionItem,
  getCollection,
  listCollectionItems,
  replaceCollectionItemOrder,
  updateCollectionItemNote,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, expect, it, vi } from 'vitest';
import { CollectionBuild } from '../../pages/Collections/CollectionBuild';
import { render } from '../../test/utils/render';
import {
  type CollectionDetailView,
  collectionKeys,
  useAddCollectionItem,
  useCollection,
  useRemoveCollectionItem,
  useReorderCollectionItems,
  useUpdateCollectionItemNote,
} from './useCollectionManagement';
import { libraryManagementKeys } from './useLibraryManagement';

vi.mock('@romd/admin-api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@romd/admin-api-client')>()),
  createCollectionItem: vi.fn(),
  deleteCollectionItem: vi.fn(),
  getCollection: vi.fn(),
  listCollectionItems: vi.fn(),
  replaceCollectionItemOrder: vi.fn(),
  updateCollectionItemNote: vi.fn(),
}));
vi.mock('./useCatalogSearch', () => ({
  useCatalogSearch: () => ({
    data: {
      pages: [
        {
          items: [
            {
              id: 'new-1',
              name: 'First new game',
              systemKey: 'snes',
            },
            {
              id: 'new-2',
              name: 'Second new game',
              systemKey: 'snes',
            },
          ],
        },
      ],
    },
    isPending: false,
    isError: false,
    hasNextPage: false,
  }),
}));
vi.mock('./usePlatforms', () => ({
  usePlatforms: () => ({
    data: [],
  }),
}));

const accepted: CollectionItemDto = {
  titleId: 'new-1',
  titleName: 'First new game',
  systemKey: 'snes',
};
const previous: CollectionDetailView = {
  id: 'collection-1',
  name: 'A collection',
  items: [
    {
      titleId: 'existing',
      titleName: 'Existing game',
      systemKey: 'snes',
    },
  ],
};
const reply = <T,>(data: T, status = 200) => ({
  data,
  request: new Request('http://localhost/api/collections/collection-1'),
  response: new Response(null, {
    status,
  }),
});
const unavailable = () => ({
  error: {
    status: 503,
    title: 'Unavailable',
    errorCode: 'Unavailable',
  },
  request: new Request('http://localhost/api/collections/collection-1'),
  response: new Response(null, {
    status: 503,
  }),
});
const cache = () =>
  new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        staleTime: Number.POSITIVE_INFINITY,
      },
      mutations: {
        retry: false,
      },
    },
  });

beforeEach(() => vi.resetAllMocks());

it.each([
  'parent',
  'continuation',
] as const)('keeps a 201 membership accepted when the %s refresh fails and leaves the complete cached view intact', async (failure) => {
  vi.mocked(createCollectionItem).mockResolvedValue(reply(accepted, 201));
  if (failure === 'parent') vi.mocked(getCollection).mockResolvedValue(unavailable());
  else {
    const partial: CollectionDetail = {
      ...previous,
      items: {
        items: [
          accepted,
        ],
        nextCursor: 'page-2',
        hasNextPage: true,
      },
    };
    vi.mocked(getCollection).mockResolvedValue(reply(partial));
    vi.mocked(listCollectionItems).mockResolvedValue(unavailable());
  }
  const client = cache();
  client.setQueryData(collectionKeys.detail(previous.id), previous);
  const invalidate = vi.spyOn(client, 'invalidateQueries');
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const { result, unmount } = renderHook(
    () => ({
      add: useAddCollectionItem(),
      collection: useCollection(previous.id),
    }),
    {
      wrapper,
    },
  );
  let created: unknown;
  await act(async () => {
    created = await result.current.add.mutateAsync({
      collectionId: previous.id,
      request: {
        titleId: accepted.titleId,
        note: null,
      },
    });
  });

  expect(created).toEqual(accepted);
  await waitFor(() => expect(result.current.collection.isError).toBe(true));
  expect(result.current.add.isSuccess).toBe(true);
  expect(client.getQueryData(collectionKeys.detail(previous.id))).toEqual(previous);
  expect(Array.isArray(result.current.collection.data?.items)).toBe(true);
  expect(invalidate).toHaveBeenCalledWith({
    queryKey: collectionKeys.detail(previous.id),
  });
  expect(invalidate).toHaveBeenCalledWith({
    queryKey: collectionKeys.lists(),
  });
  expect(invalidate).toHaveBeenCalledWith({
    queryKey: libraryManagementKeys.all,
  });
  if (failure === 'continuation')
    expect(listCollectionItems).toHaveBeenCalledWith({
      path: {
        collectionId: previous.id,
      },
      query: {
        cursor: 'page-2',
        limit: 100,
      },
    });
  unmount();
  client.clear();
});

it.each([
  [
    'remove',
    useRemoveCollectionItem,
    deleteCollectionItem,
  ],
  [
    'reorder',
    useReorderCollectionItems,
    replaceCollectionItemOrder,
  ],
  [
    'note',
    useUpdateCollectionItemNote,
    updateCollectionItemNote,
  ],
] as const)('keeps an accepted %s successful when its returned membership continuation fails', async (_name, useWrite, write) => {
  const partial: CollectionDetail = {
    ...previous,
    items: {
      items: [
        accepted,
      ],
      nextCursor: 'later-members',
      hasNextPage: true,
    },
  };
  vi.mocked(write).mockResolvedValue(reply(partial));
  vi.mocked(getCollection).mockResolvedValue(reply(partial));
  vi.mocked(listCollectionItems).mockResolvedValue(unavailable());
  const client = cache();
  client.setQueryData(collectionKeys.detail(previous.id), previous);
  const invalidate = vi.spyOn(client, 'invalidateQueries');
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const { result, unmount } = renderHook(
    () => ({
      write: useWrite(),
      collection: useCollection(previous.id),
    }),
    {
      wrapper,
    },
  );
  let returned: unknown;
  await act(async () => {
    returned = await result.current.write.mutateAsync({
      collectionId: previous.id,
      titleId: 'existing',
      titleIds: [
        'existing',
      ],
      note: 'Updated note',
    });
  });

  expect(returned).toEqual(partial);
  await waitFor(() => expect(result.current.collection.isError).toBe(true));
  expect(result.current.write.isSuccess).toBe(true);
  expect(write).toHaveBeenCalledTimes(1);
  expect(client.getQueryData(collectionKeys.detail(previous.id))).toEqual(previous);
  expect(listCollectionItems).toHaveBeenCalledWith({
    path: {
      collectionId: previous.id,
    },
    query: {
      cursor: 'later-members',
      limit: 100,
    },
  });
  expect(invalidate).toHaveBeenCalledWith({
    queryKey: collectionKeys.detail(previous.id),
  });
  expect(invalidate).toHaveBeenCalledWith({
    queryKey: collectionKeys.lists(),
  });
  expect(invalidate).toHaveBeenCalledWith({
    queryKey: libraryManagementKeys.all,
  });
  unmount();
  client.clear();
});

it('removes an accepted game from batch retry after a failed refresh and retries only the rejected game', async () => {
  vi.mocked(getCollection).mockResolvedValue(unavailable());
  vi.mocked(createCollectionItem)
    .mockResolvedValueOnce(reply(accepted, 201))
    .mockResolvedValueOnce({
      ...unavailable(),
      error: {
        status: 409,
        title: 'Conflict',
        detail: 'Please retry this game.',
        errorCode: 'Conflict',
      },
      response: new Response(null, {
        status: 409,
      }),
    })
    .mockResolvedValueOnce(
      reply(
        {
          ...accepted,
          titleId: 'new-2',
          titleName: 'Second new game',
        },
        201,
      ),
    );
  const client = cache();
  const user = userEvent.setup();
  const builder = (collection: CollectionDetailView) => (
    <QueryClientProvider client={client}>
      <CollectionBuild
        collection={collection}
        canEdit
        onBusyChange={vi.fn()}
      />
    </QueryClientProvider>
  );
  const { rerender, unmount } = render(builder(previous));
  await user.click(
    screen.getByRole('checkbox', {
      name: 'Select First new game',
    }),
  );
  await user.click(
    screen.getByRole('checkbox', {
      name: 'Select Second new game',
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Add 2 selected games',
    }),
  );

  expect(await screen.findByText(/Added 1 of 2 games/)).toBeInTheDocument();
  expect(
    screen.getByRole('checkbox', {
      name: 'Select First new game',
    }),
  ).toBeDisabled();
  expect(
    screen.getByRole('checkbox', {
      name: 'Select First new game',
    }),
  ).toBeChecked();
  expect(
    screen.getByRole('button', {
      name: 'Add 1 selected games',
    }),
  ).toBeEnabled();
  await user.click(
    screen.getByRole('button', {
      name: 'Add 1 selected games',
    }),
  );
  expect(await screen.findByText('Added 1 game to the collection.')).toBeInTheDocument();
  expect(
    vi.mocked(createCollectionItem).mock.calls.map(([options]) => options.body.titleId),
  ).toEqual([
    'new-1',
    'new-2',
    'new-2',
  ]);
  expect(
    screen.getByRole('button', {
      name: 'Add selected games',
    }),
  ).toBeDisabled();
  rerender(
    builder({
      ...previous,
      items: [
        ...previous.items,
        accepted,
        {
          ...accepted,
          titleId: 'new-2',
          titleName: 'Second new game',
        },
      ],
    }),
  );
  rerender(builder(previous));
  expect(
    screen.getByRole('checkbox', {
      name: 'Select First new game',
    }),
  ).toBeEnabled();
  unmount();
  client.clear();
});
