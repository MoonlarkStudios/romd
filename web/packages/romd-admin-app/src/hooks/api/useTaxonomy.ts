import type {
  LanguageDto,
  PageOfAliasDto,
  ProblemDetails,
  RegionDto,
  TaxonomyMergeImpactDto,
} from '@romd/admin-api-client';
import {
  createLanguageAlias,
  createRegionAlias,
  deleteLanguageAlias,
  deleteRegionAlias,
  listLanguageAliases,
  listLanguages,
  listRegionAliases,
  listRegions,
  mergeLanguage,
  mergeRegion,
  previewLanguageMerge,
  previewRegionMerge,
} from '@romd/admin-api-client';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

export const taxonomyKeys = {
  all: [
    'taxonomy',
  ] as const,
  regions: () =>
    [
      ...taxonomyKeys.all,
      'regions',
    ] as const,
  regionSearch: (query: string) =>
    [
      ...taxonomyKeys.regions(),
      'search',
      query,
    ] as const,
  regionAliases: (regionKey: string) =>
    [
      ...taxonomyKeys.regions(),
      'aliases',
      regionKey,
    ] as const,
  languages: () =>
    [
      ...taxonomyKeys.all,
      'languages',
    ] as const,
  languageSearch: (query: string) =>
    [
      ...taxonomyKeys.languages(),
      'search',
      query,
    ] as const,
  languageAliases: (languageKey: string) =>
    [
      ...taxonomyKeys.languages(),
      'aliases',
      languageKey,
    ] as const,
};

export class ApiClientError extends Error {
  constructor(
    message: string,
    public readonly status?: number,
  ) {
    super(message);
    this.name = 'ApiClientError';
  }
}

function isProblemDetails(error: unknown): error is ProblemDetails {
  return typeof error === 'object' && error !== null && ('detail' in error || 'title' in error);
}

function getApiErrorMessage(error: unknown, fallback: string): string {
  if (!isProblemDetails(error)) {
    return fallback;
  }

  return error.detail ?? error.title ?? fallback;
}

/** The largest page the collections serve. */
const TAXONOMY_PAGE_LIMIT = 100;

/** Aliases read per page after the first page each row embeds. */
const ALIAS_PAGE_LIMIT = 50;

interface TaxonomyPageResult<T> {
  data?: {
    items: T[];
    nextCursor?: string | null;
    hasNextPage: boolean;
  };
  error?: unknown;
  response?: Response;
}

/** Reads every page: the merge target picker needs every row, and a search every matching row, not one page. */
async function readAllPages<T>(
  fetchPage: (cursor: string | undefined) => Promise<TaxonomyPageResult<T>>,
  failure: string,
  cursor?: string,
): Promise<T[]> {
  const response = await fetchPage(cursor);
  if (response.error || !response.data) {
    throw new ApiClientError(
      getApiErrorMessage(response.error, failure),
      response.response?.status,
    );
  }

  const { items, nextCursor, hasNextPage } = response.data;
  return hasNextPage && nextCursor
    ? [
        ...items,
        ...(await readAllPages(fetchPage, failure, nextCursor)),
      ]
    : items;
}

/**
 * Every region, or with a query every region whose name, key or any alias matches it. The server searches, since a
 * row embeds only its first aliases.
 */
export function useRegions(query = '') {
  return useQuery({
    queryKey: query ? taxonomyKeys.regionSearch(query) : taxonomyKeys.regions(),
    queryFn: () =>
      readAllPages<RegionDto>(
        (cursor) =>
          listRegions({
            query: {
              cursor,
              limit: TAXONOMY_PAGE_LIMIT,
              ...(query
                ? {
                    query,
                  }
                : {}),
            },
          }),
        'Failed to fetch regions',
      ),
    placeholderData: keepPreviousData,
  });
}

/** Every language, or with a query every language whose name, key, code or any alias matches it. */
export function useLanguages(query = '') {
  return useQuery({
    queryKey: query ? taxonomyKeys.languageSearch(query) : taxonomyKeys.languages(),
    queryFn: () =>
      readAllPages<LanguageDto>(
        (cursor) =>
          listLanguages({
            query: {
              cursor,
              limit: TAXONOMY_PAGE_LIMIT,
              ...(query
                ? {
                    query,
                  }
                : {}),
            },
          }),
        'Failed to fetch languages',
      ),
    placeholderData: keepPreviousData,
  });
}

async function responseData<T>(
  request: Promise<{
    data?: T;
    error?: unknown;
    response?: Response;
  }>,
  failure: string,
): Promise<T> {
  const response = await request;
  if (response.error || !response.data) {
    throw new ApiClientError(
      getApiErrorMessage(response.error, failure),
      response.response?.status,
    );
  }

  return response.data;
}

/** The page of a region's aliases after the cursor, such as the one its row embeds. */
export function fetchRegionAliasPage(regionKey: string, cursor: string): Promise<PageOfAliasDto> {
  return responseData(
    listRegionAliases({
      path: {
        regionKey,
      },
      query: {
        cursor,
        limit: ALIAS_PAGE_LIMIT,
      },
    }),
    'Could not load more aliases.',
  );
}

/** The page of a language's aliases after the cursor, such as the one its row embeds. */
export function fetchLanguageAliasPage(
  languageKey: string,
  cursor: string,
): Promise<PageOfAliasDto> {
  return responseData(
    listLanguageAliases({
      path: {
        languageKey,
      },
      query: {
        cursor,
        limit: ALIAS_PAGE_LIMIT,
      },
    }),
    'Could not load more aliases.',
  );
}

/** What merging the region into the target would move; refused exactly where the merge is. */
export function previewRegionMergeImpact(sourceKey: string, targetKey: string) {
  return responseData<TaxonomyMergeImpactDto>(
    previewRegionMerge({
      path: {
        regionKey: sourceKey,
      },
      body: {
        targetKey,
      },
    }),
    'Could not load merge impact.',
  );
}

/** What merging the language into the target would move; refused exactly where the merge is. */
export function previewLanguageMergeImpact(sourceKey: string, targetKey: string) {
  return responseData<TaxonomyMergeImpactDto>(
    previewLanguageMerge({
      path: {
        languageKey: sourceKey,
      },
      body: {
        targetKey,
      },
    }),
    'Could not load merge impact.',
  );
}

export function useAddRegionAlias() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ regionKey, alias }: { regionKey: string; alias: string }) => {
      const response = await createRegionAlias({
        path: {
          regionKey,
        },
        body: {
          alias,
        },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to add alias'),
          response.response?.status,
        );
      }
    },
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: taxonomyKeys.regions(),
      }),
  });
}

export function useRemoveRegionAlias() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ regionKey, aliasId }: { regionKey: string; aliasId: string }) => {
      const response = await deleteRegionAlias({
        path: {
          regionKey,
          aliasId,
        },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to remove alias'),
          response.response?.status,
        );
      }
    },
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: taxonomyKeys.regions(),
      }),
  });
}

export function useMergeRegions() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ sourceKey, targetKey }: { sourceKey: string; targetKey: string }) => {
      const response = await mergeRegion({
        path: {
          regionKey: sourceKey,
        },
        body: {
          targetKey,
        },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to merge regions'),
          response.response?.status,
        );
      }
    },
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: taxonomyKeys.regions(),
      }),
  });
}

export function useAddLanguageAlias() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ languageKey, alias }: { languageKey: string; alias: string }) => {
      const response = await createLanguageAlias({
        path: {
          languageKey,
        },
        body: {
          alias,
        },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to add alias'),
          response.response?.status,
        );
      }
    },
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: taxonomyKeys.languages(),
      }),
  });
}

export function useRemoveLanguageAlias() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ languageKey, aliasId }: { languageKey: string; aliasId: string }) => {
      const response = await deleteLanguageAlias({
        path: {
          languageKey,
          aliasId,
        },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to remove alias'),
          response.response?.status,
        );
      }
    },
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: taxonomyKeys.languages(),
      }),
  });
}

export function useMergeLanguages() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ sourceKey, targetKey }: { sourceKey: string; targetKey: string }) => {
      const response = await mergeLanguage({
        path: {
          languageKey: sourceKey,
        },
        body: {
          targetKey,
        },
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to merge languages'),
          response.response?.status,
        );
      }
    },
    onSuccess: () =>
      queryClient.invalidateQueries({
        queryKey: taxonomyKeys.languages(),
      }),
  });
}
