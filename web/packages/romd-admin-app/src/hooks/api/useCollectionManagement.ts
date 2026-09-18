import type {
  AddCollectionItemRequest,
  CollectionDetail,
  CollectionSummary,
  CreateCollectionRequest,
  ProblemDetails,
  UpdateCollectionRequest,
} from '@romd/admin-api-client';
import {
  addCollectionItem,
  createCollection,
  deleteCollection,
  getCollectionDetail,
  listCollections,
  removeCollectionItem,
  reorderCollectionItems,
  updateCollection,
  updateCollectionItemNote,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { libraryManagementKeys } from './useLibraryManagement';

export const collectionKeys = {
  all: ['collections'] as const,
  lists: () => [...collectionKeys.all, 'list'] as const,
  list: (systemKey?: string) => [...collectionKeys.lists(), systemKey ?? 'all'] as const,
  details: () => [...collectionKeys.all, 'detail'] as const,
  detail: (collectionId: string) => [...collectionKeys.details(), collectionId] as const,
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
      const response = await listCollections({
        query: systemKey ? { systemKey } : undefined,
      });
      if (response.error) {
        throw new ApiClientError('Failed to fetch collections', response.response.status);
      }

      return response.data as CollectionSummary[];
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

      const response = await getCollectionDetail({ path: { collectionId } });
      if (response.error) {
        throw new ApiClientError('Failed to fetch collection', response.response.status);
      }

      return response.data as CollectionDetail;
    },
    enabled: collectionId !== null,
  });
}

export function useCreateCollection() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: CreateCollectionRequest) => {
      const response = await createCollection({ body: request });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to create collection'),
          response.response.status,
        );
      }

      return response.data as CollectionSummary;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: collectionKeys.lists() });
      queryClient.invalidateQueries({ queryKey: libraryManagementKeys.all });
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
        path: { collectionId },
        body: request,
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to update collection'),
          response.response.status,
        );
      }

      return response.data as CollectionSummary;
    },
    onSuccess: (collection) => {
      queryClient.invalidateQueries({ queryKey: collectionKeys.lists() });
      queryClient.invalidateQueries({ queryKey: libraryManagementKeys.all });
      queryClient.invalidateQueries({ queryKey: collectionKeys.detail(collection.id) });
    },
  });
}

export function useDeleteCollection() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (collectionId: string) => {
      const response = await deleteCollection({ path: { collectionId } });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to delete collection'),
          response.response.status,
        );
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: collectionKeys.lists() });
      queryClient.invalidateQueries({ queryKey: libraryManagementKeys.all });
    },
  });
}

/**
 * Item mutations all return the full, updated CollectionDetail. We push that
 * straight into the detail cache for a flash-free update and invalidate the
 * list so item-count badges refresh.
 */
function useItemMutation<TVars extends { collectionId: string }>(
  mutationFn: (vars: TVars) => Promise<CollectionDetail>,
) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn,
    onSuccess: (detail, vars) => {
      queryClient.setQueryData(collectionKeys.detail(vars.collectionId), detail);
      queryClient.invalidateQueries({ queryKey: collectionKeys.lists() });
      queryClient.invalidateQueries({ queryKey: libraryManagementKeys.all });
    },
  });
}

export function useAddCollectionItem() {
  return useItemMutation(
    async ({
      collectionId,
      request,
    }: {
      collectionId: string;
      request: AddCollectionItemRequest;
    }) => {
      const response = await addCollectionItem({
        path: { collectionId },
        body: request,
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to add title to collection'),
          response.response.status,
        );
      }

      return response.data as CollectionDetail;
    },
  );
}

export function useRemoveCollectionItem() {
  return useItemMutation(
    async ({ collectionId, titleId }: { collectionId: string; titleId: string }) => {
      const response = await removeCollectionItem({
        path: { collectionId, titleId },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to remove title from collection'),
          response.response.status,
        );
      }

      return response.data as CollectionDetail;
    },
  );
}

export function useReorderCollectionItems() {
  return useItemMutation(
    async ({ collectionId, titleIds }: { collectionId: string; titleIds: string[] }) => {
      const response = await reorderCollectionItems({
        path: { collectionId },
        body: { titleIds },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to reorder collection'),
          response.response.status,
        );
      }

      return response.data as CollectionDetail;
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
        path: { collectionId, titleId },
        body: { note },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to update note'),
          response.response.status,
        );
      }

      return response.data as CollectionDetail;
    },
  );
}
