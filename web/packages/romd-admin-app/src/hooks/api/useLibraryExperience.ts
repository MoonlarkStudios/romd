import {
  type LibraryAttachmentRequest,
  listLibraryAttachments,
  listLibraryPreviewTitles,
  replaceLibraryAttachments,
} from '@romd/admin-api-client';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { libraryManagementKeys } from './useLibraryManagement';

async function readAttachments(libraryId: string) {
  const response = await listLibraryAttachments({
    path: {
      libraryId,
    },
  });
  if (response.error || !response.data) throw new Error('Could not load attached collections.');
  return response.data;
}

async function saveAttachments(libraryId: string, collections: LibraryAttachmentRequest[]) {
  const response = await replaceLibraryAttachments({
    path: {
      libraryId,
    },
    body: {
      collections,
    },
  });
  if (response.error)
    throw new Error(response.error.detail ?? 'Could not save collection placement.');
}

export function useLibraryAttachments(libraryId: string) {
  return useQuery({
    queryKey: [
      ...libraryManagementKeys.all,
      'attachments',
      libraryId,
    ],
    queryFn: () => readAttachments(libraryId),
  });
}

export function useSetLibraryAttachments(libraryId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (collections: LibraryAttachmentRequest[]) =>
      saveAttachments(libraryId, collections),
    onSuccess: () =>
      client.invalidateQueries({
        queryKey: libraryManagementKeys.all,
      }),
  });
}

export function useAttachCollectionToLibraries(collectionId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (libraryIds: string[]) => {
      const results = await Promise.allSettled(
        libraryIds.map(async (libraryId) => {
          const current = await readAttachments(libraryId);
          if (current.some((attachment) => attachment.collectionId === collectionId)) return;
          await saveAttachments(libraryId, [
            ...current.map(({ collectionId: id, isFeatured }) => ({
              collectionId: id,
              isFeatured,
            })),
            {
              collectionId,
              isFeatured: true,
            },
          ]);
        }),
      );
      return results.flatMap((result, index) =>
        result.status === 'fulfilled'
          ? []
          : [
              {
                libraryId: libraryIds[index],
                message:
                  result.reason instanceof Error
                    ? result.reason.message
                    : 'Could not attach collection.',
              },
            ],
      );
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
      const response = await listLibraryPreviewTitles({
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
    getNextPageParam: (last) => (last.hasNextPage ? (last.nextCursor ?? undefined) : undefined),
  });
}
