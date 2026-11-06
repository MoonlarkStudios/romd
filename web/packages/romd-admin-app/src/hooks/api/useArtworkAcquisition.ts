import type { UpdateArtworkEnrichmentSettingsRequest } from '@romd/admin-api-client';
import {
  fillMissingTitleArtwork,
  getArtworkEnrichmentSettings,
  getTitleArtworkAcquisition,
  replaceArtworkEnrichmentSettings,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { invalidateEnrichmentAssessments } from './enrichmentWorkspaceCache';
import { acquisitionKeys, invalidateTitle } from './titleCache';
import { ifMatch, revisionFromETag } from './titleConflict';

export function useArtworkEnrichmentSettings() {
  return useQuery({
    queryKey: acquisitionKeys.settings,
    queryFn: async ({ signal }) => {
      const response = await getArtworkEnrichmentSettings({
        signal,
      });
      if (response.error || !response.data)
        throw new Error('Could not load artwork enrichment settings.');
      if (
        !response.data.revision ||
        revisionFromETag(response.response?.headers.get('ETag')) !== response.data.revision
      )
        throw new Error('Could not read the artwork settings revision. Reload settings.');
      return response.data;
    },
  });
}

export function useSaveArtworkEnrichmentSettings() {
  const cache = useQueryClient();
  return useMutation({
    mutationFn: async ({
      revision,
      ...body
    }: UpdateArtworkEnrichmentSettingsRequest & {
      revision: string;
    }) => {
      const response = await replaceArtworkEnrichmentSettings({
        body,
        headers: ifMatch(revision),
      });
      if (response.error || !response.data) {
        if (response.response?.status === 412)
          await cache.invalidateQueries({
            queryKey: acquisitionKeys.settings,
          });
        throw new Error(response.error?.detail ?? 'Could not save artwork enrichment settings.');
      }
      if (
        !response.data.revision ||
        revisionFromETag(response.response?.headers.get('ETag')) !== response.data.revision
      )
        throw new Error('Could not read the accepted artwork settings revision. Reload settings.');
      return response.data;
    },
    onSuccess: async (data) => {
      await cache.cancelQueries({
        queryKey: acquisitionKeys.settings,
      });
      cache.setQueryData(acquisitionKeys.settings, data);
      return invalidateEnrichmentAssessments(cache);
    },
  });
}

export function useArtworkAcquisition(titleId: string) {
  return useQuery({
    queryKey: acquisitionKeys.title(titleId),
    queryFn: async ({ signal }) => {
      const response = await getTitleArtworkAcquisition({
        path: {
          titleId,
        },
        signal,
      });
      if (response.error || !response.data)
        throw new Error('Could not load artwork acquisition results.');
      return response.data;
    },
    refetchInterval: (query) => (query.state.data?.activeJobId ? 2000 : false),
  });
}

export function useFillMissingArtwork() {
  const cache = useQueryClient();
  return useMutation({
    mutationFn: async (titleIds: string[]) => {
      const failed: string[] = [];
      for (const titleId of titleIds) {
        try {
          const response = await fillMissingTitleArtwork({
            path: {
              titleId,
            },
          });
          if (response.error) {
            failed.push(titleId);
            continue;
          }
          await invalidateTitle(cache, titleId);
        } catch {
          failed.push(titleId);
        }
      }
      await cache.invalidateQueries({
        queryKey: [
          'jobs',
        ],
      });
      return {
        queued: titleIds.length - failed.length,
        failed,
      };
    },
  });
}
