import { getIgdbProviderSettings, getSteamGridDbSettings } from '@romd/admin-api-client';
import { queryOptions } from '@tanstack/react-query';

export const igdbSettingsQuery = queryOptions({
  queryKey: ['metadata-providers', 'igdb'] as const,
  queryFn: async ({ signal }) => {
    const response = await getIgdbProviderSettings({ signal });
    if (response.error || !response.data) throw new Error('Could not load IGDB settings.');
    return response.data;
  },
  refetchOnWindowFocus: false,
});
export const steamGridDbSettingsQuery = queryOptions({
  queryKey: ['artwork-providers', 'steamgriddb', 'settings'] as const,
  queryFn: async ({ signal }) => {
    const response = await getSteamGridDbSettings({ signal });
    if (response.error || !response.data) throw new Error('Could not load SteamGridDB settings.');
    return response.data;
  },
  refetchOnWindowFocus: false,
});
