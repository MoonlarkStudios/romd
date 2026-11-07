import type { ArtworkRole, ArtworkSelectionState, TitleArtwork } from '@romd/admin-api-client';
import {
  getTitleArtwork,
  listArtworkProviders,
  listTitleArtworkAssets,
} from '@romd/admin-api-client';
import { type QueryClient, queryOptions, useQuery } from '@tanstack/react-query';
import { collectPages } from '../../utils/collectPages';

export const artworkKeys = {
  providers: [
    'artwork',
    'providers',
  ] as const,
  titleProviders: (titleId: string) =>
    [
      'artwork',
      'providers',
      titleId,
    ] as const,
  selections: (titleId: string) =>
    [
      'artwork',
      'selections',
      titleId,
    ] as const,
  saved: (titleId: string) =>
    [
      'artwork',
      'saved',
      titleId,
    ] as const,
  candidates: (titleId: string) =>
    [
      'artwork',
      'candidates',
      titleId,
    ] as const,
  collect: (titleId: string) =>
    [
      'artwork',
      'collect',
      titleId,
    ] as const,
};

export function useSavedArtwork(titleId: string) {
  return useQuery({
    queryKey: artworkKeys.saved(titleId),
    queryFn: async ({ signal }) => {
      return collectPages(async (cursor) => {
        const response = await listTitleArtworkAssets({
          path: {
            titleId,
          },
          query: {
            cursor,
            limit: 100,
          },
          signal,
        });
        if (response.error || !response.data) throw new Error('Could not load saved artwork.');
        return response.data;
      });
    },
  });
}

export function useArtworkProviders(titleId?: string, role?: ArtworkRole) {
  return useQuery({
    queryKey: [
      ...artworkKeys.providers,
      titleId,
      role,
    ],
    queryFn: async ({ signal }) => {
      const response = await listArtworkProviders({
        query: {
          titleId,
          role,
        },
        signal,
      });
      if (response.error || !response.data) throw new Error('Could not load artwork providers.');
      return response.data;
    },
  });
}

/**
 * The title's artwork family: every role's selection, each with its own revision for role writes, and the family
 * revision that guards saved assets, rejections and hidden candidates.
 */
export const artworkSelectionsQuery = (titleId: string) =>
  queryOptions({
    queryKey: artworkKeys.selections(titleId),
    queryFn: async ({ signal }) => {
      const response = await getTitleArtwork({
        path: {
          titleId,
        },
        signal,
      });
      if (response.error || !response.data) throw new Error('Could not load artwork selections.');
      return response.data;
    },
  });

export function useArtworkSelections(titleId: string) {
  return useQuery({
    ...artworkSelectionsQuery(titleId),
    refetchInterval: (query) =>
      query.state.data?.roles.some((selection) => selection.pendingJobId) ? 2000 : false,
  });
}

/**
 * Records what an artwork write returned so the next write sends its new revision, first cancelling reads that
 * started before the write and would otherwise restore the older one. A role write changes the family revision
 * too, which only a read of the family returns.
 */
export async function storeArtwork(
  cache: QueryClient,
  titleId: string,
  written: TitleArtwork | ArtworkSelectionState,
): Promise<void> {
  await cache.cancelQueries({
    queryKey: artworkKeys.selections(titleId),
  });
  cache.setQueryData<TitleArtwork>(artworkKeys.selections(titleId), (family) =>
    'roles' in written
      ? written
      : family && {
          ...family,
          roles: family.roles.map((role) => (role.role === written.role ? written : role)),
        },
  );
}
