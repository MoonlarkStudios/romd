import {
  getLibraryAttachments,
  getLibraryPreview,
  type LibraryAttachmentRequest,
  setLibraryAttachments,
} from '@romd/admin-api-client';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { libraryManagementKeys } from './useLibraryManagement';

export function useLibraryAttachments(libraryId: string) {
  return useQuery({
    queryKey: [
      ...libraryManagementKeys.all,
      'attachments',
      libraryId,
    ],
    queryFn: async () => {
      const response = await getLibraryAttachments({
        path: {
          libraryId,
        },
      });
      if (response.error || !response.data) throw new Error('Could not load attached collections.');
      return response.data;
    },
  });
}

export function useSetLibraryAttachments(libraryId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (collections: LibraryAttachmentRequest[]) => {
      const response = await setLibraryAttachments({
        path: {
          libraryId,
        },
        body: {
          collections,
        },
      });
      if (response.error)
        throw new Error(response.error.detail ?? 'Could not save collection placement.');
    },
    onSuccess: () =>
      client.invalidateQueries({
        queryKey: libraryManagementKeys.all,
      }),
  });
}

export function useLibraryPreview(libraryId: string, collectionId?: string) {
  return useInfiniteQuery({
    queryKey: [
      ...libraryManagementKeys.all,
      'preview',
      libraryId,
      collectionId,
    ],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) => {
      const response = await getLibraryPreview({
        path: {
          libraryId,
        },
        query: {
          collectionId,
          cursor: pageParam,
        },
      });
      if (response.error || !response.data) throw new Error('Could not load audience preview.');
      return response.data;
    },
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
}
