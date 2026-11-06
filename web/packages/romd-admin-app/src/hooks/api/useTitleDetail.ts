import type {
  TitleDetail,
  TitleMediaRef,
  TitleRelease,
  TitleReleaseSource,
} from '@romd/admin-api-client';
import {
  getTitle,
  listTitleMedia,
  listTitleReleaseSources,
  listTitleReleases,
  listTitleSourceReferences,
} from '@romd/admin-api-client';
import { queryOptions, useQuery } from '@tanstack/react-query';
import { collectPages } from '../../utils/collectPages';

export type TitleReleaseView = Omit<TitleRelease, 'sources'> & {
  sources: TitleReleaseSource[];
};
export type TitleDetailView = Omit<TitleDetail, 'media' | 'releases'> & {
  media: TitleMediaRef[];
  releases: TitleReleaseView[];
};

/** Expands the title's embedded pages before callers compare, edit or display the whole graph. */
export async function normalizeTitleDetail(
  title: TitleDetail,
  signal?: AbortSignal,
): Promise<TitleDetailView> {
  const [media, releases] = await Promise.all([
    collectPages(async (cursor) => {
      const response = await listTitleMedia({
        path: {
          titleId: title.id,
        },
        query: {
          cursor,
          limit: 100,
        },
        signal,
      });
      if (response.error || !response.data)
        throw new Error('Could not load the complete title media.');
      return response.data;
    }, title.media),
    collectPages(async (cursor) => {
      const response = await listTitleReleases({
        path: {
          titleId: title.id,
        },
        query: {
          cursor,
          limit: 100,
        },
        signal,
      });
      if (response.error || !response.data) throw new Error('Could not load every title release.');
      return response.data;
    }, title.releases),
  ]);
  const completeReleases = await Promise.all(
    releases.map(
      async (release): Promise<TitleReleaseView> => ({
        ...release,
        sources: await collectPages(async (cursor) => {
          const response = await listTitleReleaseSources({
            path: {
              titleId: title.id,
            },
            query: {
              releaseId: release.id,
              cursor,
              limit: 100,
            },
            signal,
          });
          if (response.error || !response.data)
            throw new Error('Could not load the complete release provenance.');
          return response.data;
        }, release.sources),
      }),
    ),
  );
  return {
    ...title,
    media,
    releases: completeReleases,
  };
}

export const titleDetailKeys = {
  all: [
    'titleDetail',
  ] as const,
  detail: (titleId: string) =>
    [
      ...titleDetailKeys.all,
      titleId,
    ] as const,
  // A child of detail(id) so title-level invalidations refresh the backing sources too,
  // while the detail cache entry itself stays untouched.
  sourceReferences: (titleId: string) =>
    [
      ...titleDetailKeys.detail(titleId),
      'sourceReferences',
    ] as const,
};

/** The title representation, including releases, files and the `revision` its writes send in `If-Match`. */
export const titleDetailQuery = (titleId: string) =>
  queryOptions({
    queryKey: titleDetailKeys.detail(titleId),
    queryFn: async ({ signal }) => {
      if (!titleId) throw new Error('Title ID is required');

      const response = await getTitle({
        signal,
        path: {
          titleId,
        },
      });

      if (response.error || !response.data) throw new Error('Failed to fetch title details');
      return normalizeTitleDetail(response.data, signal);
    },
  });

/**
 * Fetches the title representation.
 *
 * @example
 * ```tsx
 * const { data: title, isLoading, isError } = useTitleDetail(titleId);
 * ```
 */
export function useTitleDetail(titleId: string | undefined) {
  return useQuery({
    ...titleDetailQuery(titleId ?? ''),
    enabled: !!titleId,
  });
}

/**
 * Fetches the catalog sources backing a title, including dormant
 * (discontinued / disabled) sources that no longer surface in libraries.
 */
export function useTitleSourceReferences(titleId: string | undefined) {
  return useQuery({
    queryKey: titleDetailKeys.sourceReferences(titleId ?? ''),
    queryFn: async ({ signal }) => {
      if (!titleId) throw new Error('Title ID is required');

      return collectPages(async (cursor) => {
        const response = await listTitleSourceReferences({
          path: {
            titleId,
          },
          query: {
            cursor,
            limit: 100,
          },
          signal,
        });
        if (response.error || !response.data)
          throw new Error('Failed to fetch title source references');
        return response.data;
      });
    },
    enabled: !!titleId,
  });
}
