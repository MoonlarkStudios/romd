import type { TitleDetail, TitleMediaRef, UpdateTitleMetadataRequest } from '@romd/admin-api-client';
import {
  associateExternalId,
  client,
  deleteTitleMedia,
  mergeTitles,
  moveGame,
  setPrimaryMedia,
  setTitlesTracking,
  setTitleTracking,
  triggerTitleEnrichment,
  updateTitleMetadata,
  uploadTitleMedia,
} from '@romd/admin-api-client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { type UploadProgress, uploadWithProgress } from './uploadWithProgress';
import { artworkKeys } from './useArtwork';
import { catalogSearchKeys } from './useCatalogSearch';
import { coverageStatsKeys } from './useCoverageStats';
import { jobKeys } from './useJobs';
import { managedSystemKeys } from './useManagedSystems';
import { titleDetailKeys } from './useTitleDetail';
import { titleEnrichmentKeys } from './useTitleEnrichment';
import { trackedCollectionKeys } from './useTrackedCollection';

/**
 * Hook to trigger metadata enrichment for a title from IGDB.
 * The enrichment endpoint returns 202 with no body — the job will
 * arrive via SignalR and be handled by the notification reactor.
 */
export function useTriggerEnrichment() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (titleId: string) => {
      const response = await triggerTitleEnrichment({ path: { titleId } });
      if (response.error) {
        throw new Error('Failed to trigger enrichment');
      }
      return response.data;
    },
    onMutate: async (titleId) => {
      await queryClient.cancelQueries({ queryKey: titleDetailKeys.detail(titleId) });
      const previousTitle = queryClient.getQueryData<TitleDetail>(titleDetailKeys.detail(titleId));
      if (previousTitle) {
        queryClient.setQueryData(titleDetailKeys.detail(titleId), {
          ...previousTitle,
          enrichmentStatus: 'Pending',
        });
      }
      return { previousTitle, titleId };
    },
    onError: (_err, titleId, context) => {
      if (context?.previousTitle) {
        queryClient.setQueryData(titleDetailKeys.detail(titleId), context.previousTitle);
      }
    },
    onSuccess: (_data, titleId) => {
      queryClient.invalidateQueries({ queryKey: titleDetailKeys.detail(titleId) });
      queryClient.invalidateQueries({ queryKey: jobKeys.all });
    },
  });
}

interface SetTrackingParams {
  titleId: string;
  tracked: boolean;
}

/**
 * Hook to track / untrack a title (include it in the curated collection target).
 * Optimistically flips `isTracked`, then invalidates the title, catalog, and dashboard
 * stats so coverage and enrichment counts recompute.
 */
export function useSetTitleTracking() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, tracked }: SetTrackingParams) => {
      const response = await setTitleTracking({ path: { titleId }, body: { tracked } });
      if (response.error) {
        throw new Error('Failed to update tracking');
      }
    },
    onMutate: async ({ titleId, tracked }) => {
      await queryClient.cancelQueries({ queryKey: titleDetailKeys.detail(titleId) });
      const previousTitle = queryClient.getQueryData<TitleDetail>(titleDetailKeys.detail(titleId));
      if (previousTitle) {
        queryClient.setQueryData(titleDetailKeys.detail(titleId), {
          ...previousTitle,
          isTracked: tracked,
        });
      }
      return { previousTitle, titleId };
    },
    onError: (_err, { titleId }, context) => {
      if (context?.previousTitle) {
        queryClient.setQueryData(titleDetailKeys.detail(titleId), context.previousTitle);
      }
    },
    onSuccess: (_data, { titleId }) => {
      queryClient.invalidateQueries({ queryKey: titleDetailKeys.detail(titleId) });
      queryClient.invalidateQueries({ queryKey: catalogSearchKeys.all });
      queryClient.invalidateQueries({ queryKey: managedSystemKeys.all });
      queryClient.invalidateQueries({ queryKey: trackedCollectionKeys.all });
      queryClient.invalidateQueries({ queryKey: coverageStatsKeys.all });
      queryClient.invalidateQueries({ queryKey: ['systemStats'] });
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
      const response = await setTitlesTracking({ body: { titleIds, tracked } });
      if (response.error) {
        throw new Error('Failed to update tracking');
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: catalogSearchKeys.all });
      queryClient.invalidateQueries({ queryKey: managedSystemKeys.all });
      queryClient.invalidateQueries({ queryKey: trackedCollectionKeys.all });
      queryClient.invalidateQueries({ queryKey: coverageStatsKeys.all });
      queryClient.invalidateQueries({ queryKey: ['systemStats'] });
    },
  });
}

interface UpdateMetadataParams {
  titleId: string;
  metadata: UpdateTitleMetadataRequest;
}

/**
 * Hook to update title metadata.
 * Invalidates title detail on success.
 */
export function useUpdateTitleMetadata() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, metadata }: UpdateMetadataParams) => {
      const response = await updateTitleMetadata({
        path: { titleId },
        body: metadata,
      });
      if (response.error) {
        throw new Error('Failed to update metadata');
      }
      return response.data as TitleDetail;
    },
    onSuccess: async (data, { titleId }) => {
      queryClient.setQueryData(titleDetailKeys.detail(titleId), data);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: titleEnrichmentKeys.detail(titleId) }),
        queryClient.invalidateQueries({ queryKey: catalogSearchKeys.all }),
      ]);
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
 * Hook to upload media for a title.
 */
export function useUploadTitleMedia() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, file, type, onProgress }: UploadMediaParams) => {
      if (onProgress) return uploadWithProgress<TitleMediaRef>(`${client.getConfig().baseUrl ?? ''}/api/titles/${encodeURIComponent(titleId)}/media`, file, { query: { type }, onProgress });
      const response = await uploadTitleMedia({
        path: { titleId },
        query: { type },
        body: { file },
      });
      if (response.error) {
        throw new Error('Failed to upload media');
      }
      return response.data as TitleMediaRef;
    },
    onSuccess: (_data, { titleId }) => {
      queryClient.invalidateQueries({ queryKey: titleDetailKeys.detail(titleId) });
      queryClient.invalidateQueries({ queryKey: artworkKeys.saved(titleId) });
    },
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
    mutationFn: async ({ titleId, mediaId }: SetPrimaryMediaParams) => {
      const response = await setPrimaryMedia({
        path: { titleId, mediaId },
      });
      if (response.error) {
        throw new Error('Failed to set primary media');
      }
      return response.data as TitleDetail;
    },
    onSuccess: (data, { titleId }) => {
      queryClient.setQueryData(titleDetailKeys.detail(titleId), data);
      queryClient.invalidateQueries({ queryKey: catalogSearchKeys.all });
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
    mutationFn: async ({ titleId, mediaId }: DeleteMediaParams) => {
      const response = await deleteTitleMedia({
        path: { titleId, mediaId },
      });
      if (response.error) {
        throw new Error('Failed to delete media');
      }
      return response.data as TitleDetail;
    },
    onSuccess: (data, { titleId }) => {
      queryClient.setQueryData(titleDetailKeys.detail(titleId), data);
      queryClient.invalidateQueries({ queryKey: artworkKeys.saved(titleId) });
    },
  });
}

interface AssociateExternalIdParams {
  titleId: string;
  source: string;
  externalId: string;
}

/**
 * Hook to associate an external ID (e.g., IGDB) with a title.
 */
export function useAssociateExternalId() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, source, externalId }: AssociateExternalIdParams) => {
      const response = await associateExternalId({
        path: { titleId },
        body: {
          provider: source,
          externalId,
        },
      });
      if (response.error) {
        throw new Error('Failed to associate external ID');
      }
      return response.data as TitleDetail;
    },
    onSuccess: (data, { titleId }) => {
      queryClient.setQueryData(titleDetailKeys.detail(titleId), data);
      queryClient.invalidateQueries({ queryKey: titleEnrichmentKeys.detail(titleId) });
      queryClient.invalidateQueries({ queryKey: jobKeys.all });
    },
  });
}

interface MergeTitlesParams {
  sourceTitleId: string;
  targetTitleId: string;
}

/**
 * Hook to merge one title into another.
 * The source title will be deleted after merge.
 */
export function useMergeTitles() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ sourceTitleId, targetTitleId }: MergeTitlesParams) => {
      const response = await mergeTitles({
        body: { sourceTitleId, targetTitleId },
      });
      if (response.error) {
        throw new Error('Failed to merge titles');
      }
      return response.data as TitleDetail;
    },
    onSuccess: (data, { sourceTitleId, targetTitleId }) => {
      // Remove the source title from cache (it's been deleted)
      queryClient.removeQueries({ queryKey: titleDetailKeys.detail(sourceTitleId) });
      // Update the target title cache
      queryClient.setQueryData(titleDetailKeys.detail(targetTitleId), data);
      // Invalidate catalog to reflect changes
      queryClient.invalidateQueries({ queryKey: catalogSearchKeys.all });
    },
  });
}

interface MoveGameParams {
  gameId: string;
  targetTitleId: string;
}

/**
 * Hook to move a game (release) from one title to another.
 */
export function useMoveGame() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ gameId, targetTitleId }: MoveGameParams) => {
      const response = await moveGame({
        path: { gameId },
        body: { targetTitleId },
      });
      if (response.error) {
        throw new Error('Failed to move game');
      }
      return response.data;
    },
    onSuccess: () => {
      // Invalidate all title details since we don't know which titles were affected
      queryClient.invalidateQueries({ queryKey: titleDetailKeys.all });
      queryClient.invalidateQueries({ queryKey: catalogSearchKeys.all });
    },
  });
}
