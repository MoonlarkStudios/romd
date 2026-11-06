import type {
  AddCollectionItemRequest,
  CollectionDetail,
  CollectionItemDto,
  CollectionSummary,
  CreateCollectionRequest,
  ProblemDetails,
  UpdateCollectionRequest,
} from '@romd/admin-api-client';
import {
  createCollection,
  createCollectionItem,
  deleteCollection,
  deleteCollectionItem,
  getCollection,
  listCollectionItems,
  listCollections,
  replaceCollectionItemOrder,
  updateCollection,
  updateCollectionItemNote,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { collectPages } from '../../utils/collectPages';
import { libraryManagementKeys } from './useLibraryManagement';

export type CollectionDetailView = Omit<CollectionDetail, 'items'> & {
  items: CollectionDetail['items']['items'];
};

export async function normalizeCollectionDetail(
  detail: CollectionDetail,
): Promise<CollectionDetailView> {
  const items = await collectPages(async (cursor) => {
    const response = await listCollectionItems({
      path: {
        collectionId: detail.id,
      },
      query: {
        cursor,
        limit: 100,
      },
    });
    if (response.error || !response.data)
      throw new ApiClientError('Could not load every collection member', response.response?.status);
    return response.data;
  }, detail.items);
  return {
    ...detail,
    items,
  };
}

async function loadCollectionDetail(collectionId: string): Promise<CollectionDetailView> {
  const response = await getCollection({
    path: {
      collectionId,
    },
  });
  if (response.error || !response.data)
    throw new ApiClientError('Failed to fetch collection', response.response?.status);
  return normalizeCollectionDetail(response.data);
}

export const collectionKeys = {
  all: [
    'collections',
  ] as const,
  lists: () =>
    [
      ...collectionKeys.all,
      'list',
    ] as const,
  list: (systemKey?: string) =>
    [
      ...collectionKeys.lists(),
      systemKey ?? 'all',
    ] as const,
  details: () =>
    [
      ...collectionKeys.all,
      'detail',
    ] as const,
  detail: (collectionId: string) =>
    [
      ...collectionKeys.details(),
      collectionId,
    ] as const,
};

export class ApiClientError extends Error {
  constructor(
    message: string,
    public readonly status?: number,
  ) {
    super(message);
    this.name = 'ApiClientError';
  }
}

function isProblemDetails(error: unknown): error is ProblemDetails {
  return typeof error === 'object' && error !== null && ('detail' in error || 'title' in error);
}

function getApiErrorMessage(error: unknown, fallback: string): string {
  if (!isProblemDetails(error)) {
    return fallback;
  }

  return error.detail ?? error.title ?? fallback;
}

export function useCollections(systemKey?: string) {
  return useQuery({
    queryKey: collectionKeys.list(systemKey),
    queryFn: async () => {
      return collectPages(async (cursor) => {
        const response = await listCollections({
          query: {
            systemKey,
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data)
          throw new ApiClientError('Failed to fetch collections', response.response?.status);
        return response.data;
      });
    },
  });
}

export function useCollection(collectionId: string | null) {
  return useQuery({
    queryKey: collectionKeys.detail(collectionId ?? ''),
    queryFn: async () => {
      if (!collectionId) {
        throw new ApiClientError('Collection ID is required');
      }

      return loadCollectionDetail(collectionId);
    },
    enabled: collectionId !== null,
  });
}

export function useCreateCollection() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: CreateCollectionRequest) => {
      const response = await createCollection({
        body: request,
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to create collection'),
          response.response?.status,
        );
      }

      return response.data as CollectionSummary;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: collectionKeys.lists(),
      });
      queryClient.invalidateQueries({
        queryKey: libraryManagementKeys.all,
      });
    },
  });
}

export function useUpdateCollection() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({
      collectionId,
      request,
    }: {
      collectionId: string;
      request: UpdateCollectionRequest;
    }) => {
      const response = await updateCollection({
        path: {
          collectionId,
        },
        body: request,
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to update collection'),
          response.response?.status,
        );
      }

      return response.data as CollectionSummary;
    },
    onSuccess: (collection) => {
      queryClient.invalidateQueries({
        queryKey: collectionKeys.lists(),
      });
      queryClient.invalidateQueries({
        queryKey: libraryManagementKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: collectionKeys.detail(collection.id),
      });
    },
  });
}

export function useDeleteCollection() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (collectionId: string) => {
      const response = await deleteCollection({
        path: {
          collectionId,
        },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to delete collection'),
          response.response?.status,
        );
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: collectionKeys.lists(),
      });
      queryClient.invalidateQueries({
        queryKey: libraryManagementKeys.all,
      });
    },
  });
}

/**
 * The accepted wire response determines write success. Hydration must finish before
 * replacing the complete local detail view, and its failure belongs to the read query.
 */
function useItemMutation<
  TVars extends {
    collectionId: string;
  },
>(mutationFn: (vars: TVars) => Promise<CollectionDetail>) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn,
    onSuccess: async (detail, vars) => {
      try {
        const complete = await normalizeCollectionDetail(detail);
        queryClient.setQueryData(collectionKeys.detail(vars.collectionId), complete);
      } catch {
        void queryClient.invalidateQueries({
          queryKey: collectionKeys.detail(vars.collectionId),
        });
      }
      void queryClient.invalidateQueries({
        queryKey: collectionKeys.lists(),
      });
      void queryClient.invalidateQueries({
        queryKey: libraryManagementKeys.all,
      });
    },
  });
}

export function useAddCollectionItem() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({
      collectionId,
      request,
    }: {
      collectionId: string;
      request: AddCollectionItemRequest;
    }) => {
      const response = await createCollectionItem({
        path: {
          collectionId,
        },
        body: request,
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to add title to collection'),
          response.response?.status,
        );
      }

      return response.data as CollectionItemDto;
    },
    onSuccess: async (_item, { collectionId }) => {
      try {
        const detail = await loadCollectionDetail(collectionId);
        queryClient.setQueryData(collectionKeys.detail(collectionId), detail);
      } catch {
        // The membership is committed. Let the parent query report a refresh failure
        // without treating the accepted POST as a write that should be retried.
        void queryClient.invalidateQueries({
          queryKey: collectionKeys.detail(collectionId),
        });
      }
      void queryClient.invalidateQueries({
        queryKey: collectionKeys.lists(),
      });
      void queryClient.invalidateQueries({
        queryKey: libraryManagementKeys.all,
      });
    },
  });
}

export function useRemoveCollectionItem() {
  return useItemMutation(
    async ({ collectionId, titleId }: { collectionId: string; titleId: string }) => {
      const response = await deleteCollectionItem({
        path: {
          collectionId,
          titleId,
        },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to remove title from collection'),
          response.response?.status,
        );
      }

      if (!response.data) throw new ApiClientError('The collection response was empty');
      return response.data;
    },
  );
}

export function useReorderCollectionItems() {
  return useItemMutation(
    async ({ collectionId, titleIds }: { collectionId: string; titleIds: string[] }) => {
      const response = await replaceCollectionItemOrder({
        path: {
          collectionId,
        },
        body: {
          titleIds,
        },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to reorder collection'),
          response.response?.status,
        );
      }

      if (!response.data) throw new ApiClientError('The collection response was empty');
      return response.data;
    },
  );
}

export function useUpdateCollectionItemNote() {
  return useItemMutation(
    async ({
      collectionId,
      titleId,
      note,
    }: {
      collectionId: string;
      titleId: string;
      note: string | null;
    }) => {
      const response = await updateCollectionItemNote({
        path: {
          collectionId,
          titleId,
        },
        body: {
          note,
        },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to update note'),
          response.response?.status,
        );
      }

      if (!response.data) throw new ApiClientError('The collection response was empty');
      return response.data;
    },
  );
}
