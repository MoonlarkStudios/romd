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

export function usePlayLaterTitles(enabled = true) {
  const accountId = usePlayLaterAccountId();
  const cache = useQueryClient();
  const view = useMemo(() => {
    const queryKey = [
      ...playLaterAccountKey(accountId),
      'titles',
    ];
    const cached = cache.getQueryData<InfiniteData<PageOfConsumerTitleCard>>(queryKey);
    return {
      queryKey,
      firstCachedPage: cached?.pages[0],
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
    // Each successful response gets a distinct page reference, even when the
    // server returns exactly the same titles as the cached page.
    structuralSharing: false,
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
      return result.data;
    },
    getNextPageParam: (page) => (page.hasNextPage ? (page.nextCursor ?? undefined) : undefined),
  });
  // A newly mounted shelf or list must confirm visibility, even if the
  // previous view's cached titles are still available to the query.
  const firstPage = query.data?.pages[0];
  const verified = firstPage !== undefined && firstPage !== view.firstCachedPage;
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
    onSuccess: async (_, { request }) => {
      // Reset the private list as well as membership so revisiting an inactive
      // shelf or view cannot flash the old result.
      if (request.isCurrent())
        await queryClient.resetQueries({
          queryKey: accountKey,
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
    change: (save: boolean) => {
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
        });
    },
  };
}
