import type {
  ConsumerReleaseManifest,
  ListConsumerLibrarySystemsData,
} from '@romd/consumer-api-client';
import {
  getConsumerCollection,
  getConsumerCurrentLibrary,
  getConsumerCurrentUser,
  getConsumerLibrarySystem,
  getConsumerTitle,
  issueConsumerReleaseManifest,
  listConsumerCatalog,
  listConsumerCollections,
  listConsumerCollectionTitles,
  listConsumerLibrarySystems,
  listConsumerTitleMedia,
  listConsumerTitleReleases,
} from '@romd/consumer-api-client';
import { useInfiniteQuery, useMutation, useQuery } from '@tanstack/react-query';
import { collectConsumerPages } from '../services/collectConsumerPages';
import {
  ConsumerGameUnavailableError,
  ConsumerLibraryUnavailableError,
} from '../services/libraryErrors';
import type { ConsumerTitleDetail } from '../types/consumerTitle';

export interface ConsumerCatalogSearchParams {
  query?: string;
  systemKey?: string;
  genre?: string;
  completeness?: 'complete' | 'partial';
  sortBy?: 'name' | 'rating';
}

interface PagedQueryOptions {
  enabled?: boolean;
  limit?: number;
}

export const consumerQueryKeys = {
  currentUser: [
    'consumer',
    'currentUser',
  ] as const,
  currentLibrary: [
    'consumer',
    'currentLibrary',
  ] as const,
  platforms: [
    'consumer',
    'platforms',
  ] as const,
  platform: (systemKey: string | undefined) =>
    [
      'consumer',
      'platform',
      systemKey,
    ] as const,
  catalog: (params: ConsumerCatalogSearchParams) =>
    [
      'consumer',
      'catalog',
      params,
    ] as const,
  title: (titleId: string) =>
    [
      'consumer',
      'title',
      titleId,
    ] as const,
  playbackTitle: (accountId: string, titleId: string) =>
    [
      'consumer',
      'playbackTitle',
      accountId,
      titleId,
    ] as const,
  collections: (systemKey?: string) =>
    [
      'consumer',
      'collections',
      systemKey ?? 'all',
    ] as const,
  collectionTitles: (collectionId: string) =>
    [
      'consumer',
      'collectionTitles',
      collectionId,
    ] as const,
  collection: (collectionId: string) =>
    [
      'consumer',
      'collection',
      collectionId,
    ] as const,
};

export function useCurrentUser() {
  return useQuery({
    queryKey: consumerQueryKeys.currentUser,
    queryFn: async () => {
      const response = await getConsumerCurrentUser();
      if (response.error || !response.data) {
        throw new Error('Unable to load current user');
      }

      return response.data;
    },
  });
}

export function useCurrentLibrary() {
  return useQuery({
    queryKey: consumerQueryKeys.currentLibrary,
    queryFn: async () => {
      const response = await getConsumerCurrentLibrary();
      if (response.response?.status === 404) throw new ConsumerLibraryUnavailableError();
      if (response.error || !response.data) {
        throw new Error('Unable to load library context');
      }

      return response.data;
    },
    retry: (attempt, error) => !(error instanceof ConsumerLibraryUnavailableError) && attempt < 1,
  });
}

export function usePlatforms(
  sortBy: NonNullable<ListConsumerLibrarySystemsData['query']>['sortBy'],
  options?: PagedQueryOptions,
) {
  const { enabled = true, limit = 24 } = options ?? {};

  return useInfiniteQuery({
    queryKey: [
      ...consumerQueryKeys.platforms,
      sortBy,
      limit,
    ],
    queryFn: async ({ pageParam, signal }) => {
      const response = await listConsumerLibrarySystems({
        query: {
          limit,
          sortBy,
          cursor: pageParam,
        },
        signal,
      });

      if (response.response?.status === 404) throw new ConsumerLibraryUnavailableError();
      if (response.error || !response.data) {
        throw new Error('Unable to load platforms');
      }

      return response.data;
    },
    getNextPageParam: (lastPage) =>
      lastPage.hasNextPage ? (lastPage.nextCursor ?? undefined) : undefined,
    initialPageParam: undefined as string | undefined,
    enabled,
    retry: (attempt, error) => !(error instanceof ConsumerLibraryUnavailableError) && attempt < 1,
  });
}

export function usePlatformDetail(systemKey: string | undefined) {
  return useQuery({
    queryKey: consumerQueryKeys.platform(systemKey),
    enabled: Boolean(systemKey),
    queryFn: async ({ signal }) => {
      if (!systemKey) return null;
      const response = await getConsumerLibrarySystem({
        path: {
          systemKey,
        },
        signal,
      });
      if (response.response?.status === 404) return null;
      if (response.error || !response.data) throw new Error('Unable to load console');
      return response.data;
    },
  });
}

export function useCatalogSearch(params: ConsumerCatalogSearchParams, options?: PagedQueryOptions) {
  const { enabled = true, limit = 24 } = options ?? {};

  return useInfiniteQuery({
    queryKey: consumerQueryKeys.catalog(params),
    queryFn: async ({ pageParam }) => {
      const response = await listConsumerCatalog({
        query: {
          ...params,
          cursor: pageParam,
          limit,
        },
      });

      if (response.error || !response.data) {
        throw new Error('Unable to load catalog');
      }

      return response.data;
    },
    getNextPageParam: (lastPage) =>
      lastPage.hasNextPage ? (lastPage.nextCursor ?? undefined) : undefined,
    initialPageParam: undefined as string | undefined,
    enabled,
  });
}

export function useTitleDetail(
  titleId: string | undefined,
  options?: {
    playbackAccountId: string;
  },
) {
  const playbackAccountId = options?.playbackAccountId;
  return useQuery({
    queryKey: playbackAccountId
      ? consumerQueryKeys.playbackTitle(playbackAccountId, titleId ?? '')
      : consumerQueryKeys.title(titleId ?? ''),
    // The player keeps these inputs for the mounted session and reads them again on return.
    staleTime: playbackAccountId ? 'static' : undefined,
    gcTime: playbackAccountId ? 0 : undefined,
    queryFn: async ({ signal }): Promise<ConsumerTitleDetail> => {
      if (!titleId) {
        throw new Error('Title ID is required');
      }

      const response = await getConsumerTitle({
        path: {
          titleId,
        },
        signal,
      });

      if (
        response.error ||
        !response.data ||
        response.data.id !== titleId ||
        !response.data.media ||
        !response.data.releases
      ) {
        if (response.response?.status === 404) throw new ConsumerGameUnavailableError();
        throw new Error('Unable to load title');
      }

      const title = response.data;
      const [media, releases] = await Promise.all([
        collectConsumerPages(async (cursor) => {
          const page = await listConsumerTitleMedia({
            path: {
              titleId,
            },
            query: {
              cursor,
              limit: 100,
            },
            signal,
          });
          if (page.error || !page.data) throw new Error('Unable to load title media');
          return page.data;
        }, title.media),
        collectConsumerPages(async (cursor) => {
          const page = await listConsumerTitleReleases({
            path: {
              titleId,
            },
            query: {
              cursor,
              limit: 100,
            },
            signal,
          });
          if (page.error || !page.data) throw new Error('Unable to load title releases');
          return page.data;
        }, title.releases),
      ]);
      return {
        ...title,
        media,
        releases,
      };
    },
    enabled: Boolean(titleId),
  });
}

export function useCollections(systemKey?: string, enabled = true) {
  return useQuery({
    queryKey: consumerQueryKeys.collections(systemKey),
    enabled,
    queryFn: ({ signal }) =>
      collectConsumerPages(async (cursor) => {
        const response = await listConsumerCollections({
          query: {
            systemKey,
            cursor,
            limit: 100,
          },
          signal,
        });
        if (response.error || !response.data) throw new Error('Unable to load collections');
        return response.data;
      }),
  });
}

export function useCollectionTitles(collectionId: string | undefined, options?: PagedQueryOptions) {
  const { enabled = true, limit = 24 } = options ?? {};

  return useInfiniteQuery({
    queryKey: consumerQueryKeys.collectionTitles(collectionId ?? ''),
    queryFn: async ({ pageParam }) => {
      if (!collectionId) {
        throw new Error('Collection ID is required');
      }

      const response = await listConsumerCollectionTitles({
        path: {
          collectionId,
        },
        query: {
          limit,
          cursor: pageParam,
        },
      });

      if (response.error || !response.data) {
        throw new Error('Unable to load collection titles');
      }

      return response.data;
    },
    getNextPageParam: (lastPage) =>
      lastPage.hasNextPage ? (lastPage.nextCursor ?? undefined) : undefined,
    initialPageParam: undefined as string | undefined,
    enabled: enabled && Boolean(collectionId),
  });
}

export function useCollectionDetail(collectionId: string | undefined) {
  return useQuery({
    queryKey: consumerQueryKeys.collection(collectionId ?? ''),
    queryFn: async () => {
      if (!collectionId) {
        throw new Error('Collection ID is required');
      }

      const response = await getConsumerCollection({
        path: {
          collectionId,
        },
      });

      if (response.error || !response.data) {
        throw new Error('Unable to load collection');
      }

      return response.data;
    },
    enabled: Boolean(collectionId),
  });
}

export function useIssueReleaseManifest() {
  return useMutation({
    mutationFn: async (releaseId: string): Promise<ConsumerReleaseManifest> => {
      const response = await issueConsumerReleaseManifest({
        path: {
          releaseId,
        },
      });

      if (response.error || !response.data) {
        throw new Error('Unable to issue Release manifest');
      }

      return response.data;
    },
  });
}
