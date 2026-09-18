import type { UpdateArtworkEnrichmentSettingsRequest } from '@romd/admin-api-client';
import { fillMissingTitleArtwork, getArtworkAcquisitionOutcomes, getArtworkEnrichmentSettings, updateArtworkEnrichmentSettings } from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

export const acquisitionKeys = {
  settings: ['artwork-enrichment', 'settings'] as const,
  title: (titleId: string) => ['artwork-enrichment', 'title', titleId] as const,
};

export function useArtworkEnrichmentSettings() {
  return useQuery({ queryKey: acquisitionKeys.settings, queryFn: async ({ signal }) => {
    const response = await getArtworkEnrichmentSettings({ signal });
    if (response.error || !response.data) throw new Error('Could not load artwork enrichment settings.');
    return response.data;
  } });
}

export function useSaveArtworkEnrichmentSettings() {
  const cache = useQueryClient();
  return useMutation({ mutationFn: async (body: UpdateArtworkEnrichmentSettingsRequest) => {
    const response = await updateArtworkEnrichmentSettings({ body });
    if (response.error || !response.data) throw new Error(response.error?.detail ?? 'Could not save artwork enrichment settings.');
    return response.data;
  }, onSuccess: (data) => { cache.setQueryData(acquisitionKeys.settings, data); } });
}

export function useArtworkAcquisition(titleId: string) {
  return useQuery({ queryKey: acquisitionKeys.title(titleId), queryFn: async ({ signal }) => {
    const response = await getArtworkAcquisitionOutcomes({ path: { titleId }, signal });
    if (response.error || !response.data) throw new Error('Could not load artwork acquisition results.');
    return response.data;
  }, refetchInterval: (query) => query.state.data?.activeJobId ? 2000 : false });
}

export function useFillMissingArtwork() {
  const cache = useQueryClient();
  return useMutation({ mutationFn: async (titleIds: string[]) => {
    const failed: string[] = [];
    for (const titleId of titleIds) {
      try {
        const response = await fillMissingTitleArtwork({ path: { titleId } });
        if (response.error) { failed.push(titleId); continue; }
        await cache.invalidateQueries({ queryKey: acquisitionKeys.title(titleId) });
      } catch { failed.push(titleId); }
    }
    await cache.invalidateQueries({ queryKey: ['jobs'] });
    return { queued: titleIds.length - failed.length, failed };
  } });
}
