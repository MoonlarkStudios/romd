import { getIgdbProviderSettings, getSteamGridDbSettings } from '@romd/admin-api-client';
import { queryOptions } from '@tanstack/react-query';
import { revisionFromETag } from '../../hooks/api/titleConflict';

/** A settings revision is usable only after observing the matching strong response validator. */
export function observedProviderSettings<
  T extends {
    revision: string;
  },
>(settings: T, response?: Response): T {
  const revision = revisionFromETag(response?.headers.get('ETag'));
  if (!revision || revision !== settings.revision)
    throw new Error('Reload settings to read their current revision.');
  return settings;
}

export const igdbSettingsQuery = queryOptions({
  queryKey: [
    'metadata-providers',
    'igdb',
  ] as const,
  queryFn: async ({ signal }) => {
    const response = await getIgdbProviderSettings({
      signal,
    });
    if (response.error || !response.data) throw new Error('Could not load IGDB settings.');
    return observedProviderSettings(response.data, response.response);
  },
  refetchOnWindowFocus: false,
});
export const steamGridDbSettingsQuery = queryOptions({
  queryKey: [
    'artwork-providers',
    'steamgriddb',
    'settings',
  ] as const,
  queryFn: async ({ signal }) => {
    const response = await getSteamGridDbSettings({
      signal,
    });
    if (response.error || !response.data) throw new Error('Could not load SteamGridDB settings.');
    return observedProviderSettings(response.data, response.response);
  },
  refetchOnWindowFocus: false,
});
