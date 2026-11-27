import { notifications } from '@mantine/notifications';
import {
  type ConsumerTitleCard,
  deleteConsumerPlayLaterTitle,
  getConsumerPlayLaterMembership,
  listConsumerPlayLaterTitles,
  type PageOfConsumerTitleCard,
  replaceConsumerPlayLaterTitle,
} from '@romd/consumer-api-client';
import {
  type InfiniteData,
  useInfiniteQuery,
  useIsMutating,
  useMutation,
  useQueries,
  useQueryClient,
} from '@tanstack/react-query';
import { useMemo } from 'react';
import { bindAccountRequest } from '../auth/userManager';
import { useAuth } from '../contexts/AuthContext';
import { playLaterAccountKey } from './playLaterQueryKeys';

function usePlayLaterAccountId() {
  const { user, isAuthenticated } = useAuth();
  return isAuthenticated ? (user?.id ?? '') : '';
}

type PlayLaterPage = PageOfConsumerTitleCard & {
  readToken: symbol;
};

const playLaterTitlesKey = (accountId: string) =>
  [
    ...playLaterAccountKey(accountId),
    'titles',
  ] as const;

export function usePlayLaterTitles(enabled = true) {
  const accountId = usePlayLaterAccountId();
  const cache = useQueryClient();
  const view = useMemo(() => {
    const queryKey = playLaterTitlesKey(accountId);
    const cached = cache.getQueryData<InfiniteData<PlayLaterPage>>(queryKey);
    return {
      queryKey,
      cachedReadToken: cached?.pages[0]?.readToken,
    };
  }, [
    accountId,
    cache,
  ]);
  const query = useInfiniteQuery({
    queryKey: view.queryKey,
    enabled: enabled && Boolean(accountId),
    staleTime: 0,
    refetchOnMount: 'always',
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam, signal }) => {
      const { fetch } = bindAccountRequest(accountId);
      const result = await listConsumerPlayLaterTitles({
        query: {
          limit: 18,
          cursor: pageParam,
        },
        fetch,
        signal,
      });
      if (result.error || !result.data) throw new Error('Unable to load Play Later.');
      return {
        ...result.data,
        readToken: Symbol(),
      };
    },
    getNextPageParam: (page) => (page.hasNextPage ? (page.nextCursor ?? undefined) : undefined),
  });
  // A newly mounted shelf or list must confirm visibility, even if the
  // previous view's cached titles are still available to the query. Only a
  // server read changes the token, even if a removal edits a cached page.
  const readToken = query.data?.pages[0]?.readToken;
  const verified = readToken !== undefined && readToken !== view.cachedReadToken;
  return {
    data: verified ? query.data : undefined,
    isLoading: enabled && Boolean(accountId) && !verified && !query.isError,
    isError: query.isError,
    isRefetching: query.isRefetching,
    isFetchingNextPage: query.isFetchingNextPage,
    hasNextPage: verified && query.hasNextPage,
    fetchNextPage: query.fetchNextPage,
    refetch: query.refetch,
  };
}

export interface PlayLaterMembership {
  saved?: boolean;
  retry?: () => void;
}

type PlayLaterApplied = (isCurrent: () => boolean) => void;

export function usePlayLaterMembership(
  pages: readonly {
    items: readonly Pick<ConsumerTitleCard, 'id'>[];
  }[] = [],
) {
  const accountId = usePlayLaterAccountId();
  // Match Browse's page boundaries so loading another page can reuse the
  // membership queries already in cache.
  const batches = pages.map(({ items }) =>
    [
      ...new Set(items.map(({ id }) => id)),
    ].sort(),
  );
  const queries = useQueries({
    queries: batches.map((titleIds) => ({
      queryKey: [
        ...playLaterAccountKey(accountId),
        'membership',
        titleIds,
      ],
      enabled: Boolean(accountId) && titleIds.length > 0,
      staleTime: 0,
      queryFn: async ({ signal }: { signal: AbortSignal }) => {
        const { fetch } = bindAccountRequest(accountId);
        const result = await getConsumerPlayLaterMembership({
          query: {
            titleIds,
          },
          fetch,
          signal,
        });
        if (result.error || !result.data) throw new Error('Unable to check Play Later.');
        return new Set(result.data.titleIds);
      },
    })),
  });

  const membership = new Map<string, PlayLaterMembership>();
  batches.forEach((titleIds, index) => {
    const query = queries[index];
    for (const titleId of titleIds) {
      membership.set(
        titleId,
        query.isError
          ? {
              retry: () => void query.refetch(),
            }
          : {
              saved: query.data?.has(titleId),
            },
      );
    }
  });
  return membership;
}

export function useChangePlayLater(titleId: string) {
  const accountId = usePlayLaterAccountId();
  const queryClient = useQueryClient();
  const accountKey = playLaterAccountKey(accountId);
  const titlesKey = playLaterTitlesKey(accountId);
  const mutationKey = [
    ...accountKey,
    'setMembership',
    titleId,
  ];
  const pending =
    useIsMutating({
      mutationKey,
      exact: true,
    }) > 0;
  const { mutate } = useMutation({
    mutationKey,
    retry: false,
    networkMode: 'always',
    mutationFn: async ({
      save,
      request,
    }: {
      save: boolean;
      request: ReturnType<typeof bindAccountRequest>;
      onApplied?: PlayLaterApplied;
    }) => {
      const change = save ? replaceConsumerPlayLaterTitle : deleteConsumerPlayLaterTitle;
      const result = await change({
        path: {
          titleId,
        },
        fetch: request.fetch,
      });
      if (result.error || result.response?.status !== 204) {
        throw new Error('Unable to update Play Later.');
      }
    },
    onSuccess: async (_, { request, save, onApplied }) => {
      if (!request.isCurrent()) return;
      // Cancellation is local and keeps a GET started before the write from
      // restoring stale membership while the replacement GET is still pending.
      await queryClient.cancelQueries({
        queryKey: titlesKey,
        exact: true,
      });
      if (!request.isCurrent()) return;
      onApplied?.(request.isCurrent);
      const list = queryClient.getQueryCache().find({
        queryKey: titlesKey,
        exact: true,
      });
      const activeList = list?.isActive() ? list : undefined;
      if (activeList?.state.status === 'success' && !save) {
        queryClient.setQueryData<InfiniteData<PlayLaterPage>>(
          titlesKey,
          (data) =>
            data && {
              ...data,
              pages: data.pages.map((page) => ({
                ...page,
                items: page.items.filter((title) => title.id !== titleId),
              })),
            },
        );
      }
      // Drop inactive list data and membership as before. Keep the active
      // list's page depth and refresh it independently of the write lock.
      void queryClient.resetQueries({
        queryKey: accountKey,
        predicate: (query) => !activeList || query.queryKey[3] !== titlesKey[3],
      });
      if (activeList)
        void queryClient.invalidateQueries({
          queryKey: titlesKey,
          exact: true,
        });
    },
    onError: (_, { request }) => {
      if (request.isCurrent()) {
        notifications.show({
          color: 'red',
          message: 'Play Later could not be updated. Try again.',
        });
      }
    },
  });

  return {
    available: Boolean(accountId),
    pending,
    change: (save: boolean, onApplied?: PlayLaterApplied) => {
      // An action in another view can already be pending before this control
      // has rendered the new mutation state.
      if (
        !accountId ||
        queryClient.isMutating({
          mutationKey,
          exact: true,
        })
      )
        return;
      const request = bindAccountRequest(accountId);
      if (request.isCurrent())
        mutate({
          save,
          request,
          onApplied,
        });
    },
  };
}
