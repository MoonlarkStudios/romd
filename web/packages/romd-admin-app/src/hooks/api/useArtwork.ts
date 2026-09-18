import { getArtworkProviders, getArtworkSelections, getSavedArtwork } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export const artworkKeys = {
  providers: ['artwork', 'providers'] as const,
  selections: (titleId: string) => ['artwork', 'selections', titleId] as const,
  saved: (titleId: string) => ['artwork', 'saved', titleId] as const,
};

export function useSavedArtwork(titleId: string) {
  return useQuery({ queryKey: artworkKeys.saved(titleId), queryFn: async ({ signal }) => {
    const response = await getSavedArtwork({ path: { titleId }, signal });
    if (response.error || !response.data) throw new Error('Could not load saved artwork.');
    return response.data;
  } });
}

export function useArtworkProviders(titleId?: string, role?: string) {
  return useQuery({ queryKey: [...artworkKeys.providers, titleId, role], queryFn: async ({ signal }) => {
    const response = await getArtworkProviders({ query: { titleId, role }, signal });
    if (response.error || !response.data) throw new Error('Could not load artwork providers.');
    return response.data;
  } });
}

export function useArtworkSelections(titleId: string) {
  return useQuery({ queryKey: artworkKeys.selections(titleId), queryFn: async ({ signal }) => {
    const response = await getArtworkSelections({ path: { titleId }, signal });
    if (response.error || !response.data) throw new Error('Could not load artwork selections.');
    return response.data;
  }, refetchInterval: (query) => query.state.data?.some((selection) => selection.pendingJobId) ? 2000 : false });
}
