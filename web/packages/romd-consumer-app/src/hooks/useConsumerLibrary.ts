import type {
  ConsumerReleaseManifestDto,
  PageOfConsumerCollectionTitleDto,
  PageOfConsumerPlatformSummaryDto,
  PageOfConsumerTitleCardDto,
} from '@romd/consumer-api-client';
import {
  getConsumerCollection,
  getConsumerCurrentLibrary,
  getConsumerCurrentUser,
  getConsumerTitle,
  issueConsumerReleaseManifest,
  listConsumerCollections,
  listConsumerCollectionTitles,
  listConsumerLibrarySystems,
  searchConsumerCatalog,
} from '@romd/consumer-api-client';
import { useInfiniteQuery, useMutation, useQuery } from '@tanstack/react-query';

export interface ConsumerCatalogSearchParams {
  query?: string;
  systemKey?: string;
  genre?: string;
  completeness?: 'complete' | 'partial';
  sortBy?: string;
}

interface PagedQueryOptions {
  enabled?: boolean;
  limit?: number;
}

export const consumerQueryKeys = {
  currentUser: ['consumer', 'currentUser'] as const,
  currentLibrary: ['consumer', 'currentLibrary'] as const,
  platforms: ['consumer', 'platforms'] as const,
  catalog: (params: ConsumerCatalogSearchParams) => ['consumer', 'catalog', params] as const,
  title: (titleId: string) => ['consumer', 'title', titleId] as const,
  collections: (systemKey?: string) => ['consumer', 'collections', systemKey ?? 'all'] as const,
  collectionTitles: (collectionId: string) => ['consumer', 'collectionTitles', collectionId] as const,
  collection: (collectionId: string) => ['consumer', 'collection', collectionId] as const,
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
      if (response.error || !response.data) {
        throw new Error('Unable to load library context');
      }

      return response.data;
    },
  });
}

export function usePlatforms(options?: PagedQueryOptions) {
  const { enabled = true, limit = 24 } = options ?? {};

  return useInfiniteQuery({
    queryKey: consumerQueryKeys.platforms,
    queryFn: async ({ pageParam }) => {
      const response = await listConsumerLibrarySystems({
        query: {
          limit,
          cursor: pageParam,
        },
      });

      if (response.error || !response.data) {
        throw new Error('Unable to load platforms');
      }

      return response.data as PageOfConsumerPlatformSummaryDto;
    },
    getNextPageParam: (lastPage) => (lastPage.hasNextPage ? lastPage.nextCursor ?? undefined : undefined),
    initialPageParam: undefined as string | undefined,
    enabled,
  });
}

export function useCatalogSearch(params: ConsumerCatalogSearchParams, options?: PagedQueryOptions) {
  const { enabled = true, limit = 24 } = options ?? {};

  return useInfiniteQuery({
    queryKey: consumerQueryKeys.catalog(params),
    queryFn: async ({ pageParam }) => {
      const response = await searchConsumerCatalog({
        query: {
          ...params,
          cursor: pageParam,
          limit,
        },
      });

      if (response.error || !response.data) {
        throw new Error('Unable to load catalog');
      }

      return response.data as PageOfConsumerTitleCardDto;
    },
    getNextPageParam: (lastPage) => (lastPage.hasNextPage ? lastPage.nextCursor ?? undefined : undefined),
    initialPageParam: undefined as string | undefined,
    enabled,
  });
}

export function useTitleDetail(titleId: string | undefined) {
  return useQuery({
    queryKey: consumerQueryKeys.title(titleId ?? ''),
    queryFn: async () => {
      if (!titleId) {
        throw new Error('Title ID is required');
      }

      const response = await getConsumerTitle({
        path: {
          titleId,
        },
      });

      if (response.error || !response.data) {
        throw new Error('Unable to load title');
      }

      return response.data;
    },
    enabled: Boolean(titleId),
  });
}

export function useCollections(systemKey?: string) {
  return useQuery({
    queryKey: consumerQueryKeys.collections(systemKey),
    queryFn: async () => {
      const response = await listConsumerCollections({
        query: systemKey ? { systemKey } : undefined,
      });

      if (response.error || !response.data) {
        throw new Error('Unable to load collections');
      }

      return response.data;
    },
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

      return response.data as PageOfConsumerCollectionTitleDto;
    },
    getNextPageParam: (lastPage) => (lastPage.hasNextPage ? lastPage.nextCursor ?? undefined : undefined),
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
    mutationFn: async (releaseId: string): Promise<ConsumerReleaseManifestDto> => {
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
