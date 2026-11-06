import type {
  TitleDetail,
  TitleMediaRef,
  UpdateTitleMetadataRequest,
} from '@romd/admin-api-client';
import {
  batchSetTitleTracking,
  client,
  deleteTitleMedia,
  mergeTitles,
  moveGame,
  replaceTitleTracking,
  updateTitle,
  updateTitleMedia,
  uploadTitleMedia,
} from '@romd/admin-api-client';
import { type QueryClient, useMutation, useQueryClient } from '@tanstack/react-query';
import { cacheAcceptedTitle, invalidateTitle } from './titleCache';
import {
  ifMatch,
  revisionFromETag,
  shownTitleRevision,
  storeTitleRevision,
  TitleConflictError,
  titleWriteResult,
} from './titleConflict';
import { type UploadProgress, UploadRequestError, uploadWithProgress } from './uploadWithProgress';
import { catalogSearchKeys } from './useCatalogSearch';
import { coverageStatsKeys } from './useCoverageStats';
import { managedSystemKeys } from './useManagedSystems';
import { type TitleDetailView, titleDetailKeys } from './useTitleDetail';
import { trackedCollectionKeys } from './useTrackedCollection';

/** A one-click write lost to a concurrent change: reload the title so the curator can apply it again. */
function reloadAfterConflict(queryClient: QueryClient, error: Error, titleIds: string[]) {
  if (error instanceof TitleConflictError)
    for (const titleId of titleIds) void invalidateTitle(queryClient, titleId);
}

interface SetTrackingParams {
  titleId: string;
  tracked: boolean;
  pinnedCatalogReleaseId?: string | null;
}

/**
 * Hook to track / untrack a title (include it in the curated collection target).
 * Optimistically flips `isTracked`, then invalidates the title, catalog, and dashboard
 * stats so coverage and enrichment counts recompute.
 */
export function useSetTitleTracking() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, tracked, pinnedCatalogReleaseId }: SetTrackingParams) => {
      const response = await replaceTitleTracking({
        path: {
          titleId,
        },
        body: {
          tracked,
          ...(pinnedCatalogReleaseId !== undefined
            ? {
                pinnedCatalogReleaseId,
              }
            : {}),
        },
      });
      if (response.error) {
        throw new Error('Failed to update tracking');
      }
    },
    onMutate: async ({ titleId, tracked }) => {
      await queryClient.cancelQueries({
        queryKey: titleDetailKeys.detail(titleId),
      });
      const previousTitle = queryClient.getQueryData<TitleDetailView>(
        titleDetailKeys.detail(titleId),
      );
      if (previousTitle) {
        queryClient.setQueryData(titleDetailKeys.detail(titleId), {
          ...previousTitle,
          isTracked: tracked,
        });
      }
      return {
        previousTitle,
        titleId,
      };
    },
    onError: (_err, { titleId }, context) => {
      if (context?.previousTitle) {
        queryClient.setQueryData(titleDetailKeys.detail(titleId), context.previousTitle);
      }
    },
    onSuccess: (_data, { titleId }) => {
      queryClient.invalidateQueries({
        queryKey: titleDetailKeys.detail(titleId),
      });
      queryClient.invalidateQueries({
        queryKey: catalogSearchKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: [
          'enrichment-workspace',
        ],
      });
      queryClient.invalidateQueries({
        queryKey: managedSystemKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: trackedCollectionKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: coverageStatsKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: [
          'systemStats',
        ],
      });
    },
  });
}

interface SetTitlesTrackingParams {
  titleIds: string[];
  tracked: boolean;
}

/**
 * Hook to track / untrack many titles at once (catalog bulk affordance).
 * Invalidates the catalog and dashboard stats so coverage/enrichment counts recompute.
 */
export function useSetTitlesTracking() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleIds, tracked }: SetTitlesTrackingParams) => {
      const response = await batchSetTitleTracking({
        body: {
          titleIds,
          tracked,
        },
      });
      if (response.error) {
        throw new Error('Failed to update tracking');
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: catalogSearchKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: [
          'enrichment-workspace',
        ],
      });
      queryClient.invalidateQueries({
        queryKey: managedSystemKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: trackedCollectionKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: coverageStatsKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: [
          'systemStats',
        ],
      });
    },
  });
}

interface UpdateMetadataParams {
  titleId: string;
  /** The title revision the draft started from, or the one the previous write of the same save returned. */
  revision: string;
  metadata: UpdateTitleMetadataRequest;
}

/**
 * Hook to update title metadata.
 * Invalidates title detail on success; a stale `revision` rejects with `TitleConflictError`.
 */
export function useUpdateTitleMetadata() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, revision, metadata }: UpdateMetadataParams) =>
      titleWriteResult<TitleDetail>(
        await updateTitle({
          path: {
            titleId,
          },
          headers: ifMatch(revision),
          body: metadata,
        }),
        'Failed to update metadata',
      ),
    onSuccess: async (data, { titleId }) => {
      await cacheAcceptedTitle(queryClient, data);
      await invalidateTitle(queryClient, titleId);
    },
  });
}

interface UploadMediaParams {
  titleId: string;
  file: File;
  type: string;
  onProgress?: (progress: UploadProgress) => void;
}

/**
 * Hook to upload media for a title, guarded by the revision of the title the page shows. Each upload records
 * the title's new ETag before it resolves, so sequential uploads chain their validators.
 */
export function useUploadTitleMedia() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, file, type, onProgress }: UploadMediaParams) => {
      const headers = ifMatch(await shownTitleRevision(queryClient, titleId));
      if (onProgress) {
        let etag: string | null = null;
        const media = await uploadWithProgress<TitleMediaRef>(
          `${client.getConfig().baseUrl ?? ''}/api/titles/${encodeURIComponent(titleId)}/media`,
          file,
          {
            query: {
              type,
            },
            headers,
            onProgress,
            onETag: (value) => {
              etag = value;
            },
          },
        ).catch((reason: unknown) => {
          throw reason instanceof UploadRequestError && reason.status === 412
            ? new TitleConflictError()
            : reason;
        });
        await storeTitleRevision(queryClient, titleId, revisionFromETag(etag));
        return media;
      }
      const response = await uploadTitleMedia({
        path: {
          titleId,
        },
        query: {
          type,
        },
        headers,
        body: {
          file,
        },
      });
      const media = titleWriteResult(response, 'Failed to upload media');
      await storeTitleRevision(
        queryClient,
        titleId,
        revisionFromETag(response.response?.headers.get('ETag')),
      );
      return media;
    },
    onSuccess: (_data, { titleId }) => {
      void invalidateTitle(queryClient, titleId);
    },
    onError: (error, { titleId }) =>
      reloadAfterConflict(queryClient, error, [
        titleId,
      ]),
  });
}

interface SetPrimaryMediaParams {
  titleId: string;
  mediaId: string;
}

/**
 * Hook to set a media item as primary for a title.
 */
export function useSetPrimaryMedia() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, mediaId }: SetPrimaryMediaParams) =>
      titleWriteResult<TitleDetail>(
        await updateTitleMedia({
          path: {
            titleId,
            mediaId,
          },
          headers: ifMatch(await shownTitleRevision(queryClient, titleId)),
          body: {
            isPrimary: true,
          },
        }),
        'Failed to set primary media',
      ),
    onError: (error, { titleId }) =>
      reloadAfterConflict(queryClient, error, [
        titleId,
      ]),
    onSuccess: async (data) => {
      await cacheAcceptedTitle(queryClient, data);
      queryClient.invalidateQueries({
        queryKey: catalogSearchKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: [
          'enrichment-workspace',
        ],
      });
    },
  });
}

interface DeleteMediaParams {
  titleId: string;
  mediaId: string;
}

/**
 * Hook to delete a media item from a title.
 */
export function useDeleteTitleMedia() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, mediaId }: DeleteMediaParams) =>
      titleWriteResult<TitleDetail>(
        await deleteTitleMedia({
          path: {
            titleId,
            mediaId,
          },
          headers: ifMatch(await shownTitleRevision(queryClient, titleId)),
        }),
        'Failed to delete media',
      ),
    onError: (error, { titleId }) =>
      reloadAfterConflict(queryClient, error, [
        titleId,
      ]),
    onSuccess: async (data, { titleId }) => {
      await cacheAcceptedTitle(queryClient, data);
      void invalidateTitle(queryClient, titleId);
    },
  });
}

interface MergeTitlesParams {
  sourceTitleId: string;
  /** The revision of the source title the curator is merging from. */
  sourceRevision: string;
  targetTitleId: string;
  /** The revision of the target title the curator selected and saw. */
  targetRevision: string;
}

/** The errorCode of a merge whose source changed: its batch revision is stale. */
const MERGE_SOURCE_CHANGED = 'Titles.MergeSourceChanged';

/**
 * Hook to merge one title into another.
 * The source title will be deleted after merge. The target is the precondition's resource; the source travels
 * as a batch revision, and either being stale rejects with `TitleConflictError` once both titles are reloaded.
 */
export function useMergeTitles() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({
      sourceTitleId,
      sourceRevision,
      targetTitleId,
      targetRevision,
    }: MergeTitlesParams) => {
      const response = await mergeTitles({
        path: {
          titleId: targetTitleId,
        },
        headers: ifMatch(targetRevision),
        body: {
          sourceTitleId,
          sourceTitleRevision: sourceRevision,
        },
      });
      if (response.error?.errorCode === MERGE_SOURCE_CHANGED) throw new TitleConflictError();
      return titleWriteResult<TitleDetail>(response, 'Failed to merge titles');
    },
    onError: async (error, { sourceTitleId, targetTitleId }) => {
      if (!(error instanceof TitleConflictError)) return;
      // A retry must send revisions the curator sees again, so the target refetches even when nothing observes it.
      await Promise.all([
        invalidateTitle(queryClient, sourceTitleId),
        queryClient.refetchQueries({
          queryKey: titleDetailKeys.detail(targetTitleId),
          type: 'all',
        }),
      ]);
    },
    onSuccess: async (data, { sourceTitleId }) => {
      // Remove the source title from cache (it's been deleted)
      queryClient.removeQueries({
        queryKey: titleDetailKeys.detail(sourceTitleId),
      });
      // Update the target title cache
      await cacheAcceptedTitle(queryClient, data);
      // Invalidate catalog to reflect changes
      queryClient.invalidateQueries({
        queryKey: catalogSearchKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: [
          'enrichment-workspace',
        ],
      });
    },
  });
}

interface MoveGameParams {
  /** The game's current title, whose page shows it. */
  titleId: string;
  gameId: string;
  targetTitleId: string;
}

/**
 * Hook to move a game (release) from one title to another. A move changes no title row, so it carries no
 * precondition.
 */
export function useMoveGame() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, gameId, targetTitleId }: MoveGameParams) => {
      const response = await moveGame({
        path: {
          titleId,
          gameId,
        },
        body: {
          targetTitleId,
        },
      });
      if (response.error) {
        throw new Error('Failed to move game');
      }
      return response.data;
    },
    onSuccess: () => {
      // Invalidate all title details since we don't know which titles were affected
      queryClient.invalidateQueries({
        queryKey: titleDetailKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: catalogSearchKeys.all,
      });
      queryClient.invalidateQueries({
        queryKey: [
          'enrichment-workspace',
        ],
      });
    },
  });
}
