import type {
  CatalogFilters,
  CreateLibraryRequest,
  JobReference,
  LibraryDto,
  LibraryTitleReleaseDiagnosticsDto,
  ProblemDetails,
  Title,
  UpdateLibraryRequest,
} from '@romd/admin-api-client';
import {
  createLibrary,
  deleteLibrary,
  forceMaterializeLibrary,
  getCatalogFilters,
  getLibraryById,
  getLibraryTitleReleaseDiagnostics,
  getTitleById,
  listLibraries,
  updateLibrary,
} from '@romd/admin-api-client';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCreateJob } from './useCreateJob';

export const libraryManagementKeys = {
  all: ['libraries'] as const,
  lists: () => [...libraryManagementKeys.all, 'list'] as const,
  list: () => [...libraryManagementKeys.lists()] as const,
  details: () => [...libraryManagementKeys.all, 'detail'] as const,
  detail: (libraryId: string) => [...libraryManagementKeys.details(), libraryId] as const,
  diagnostics: (libraryId: string, titleId: string) =>
    [...libraryManagementKeys.all, 'diagnostics', libraryId, titleId] as const,
};

export const catalogFacetKeys = {
  all: ['catalogFacets'] as const,
  filters: () => [...catalogFacetKeys.all, 'filters'] as const,
};

export const titleLookupKeys = {
  all: ['titleLookup'] as const,
  detail: (titleId: string) => [...titleLookupKeys.all, titleId] as const,
};

export interface TitleLabel {
  id: string;
  name: string;
}

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

export function useLibraries(enabled = true) {
  return useQuery({
    enabled,
    queryKey: libraryManagementKeys.list(),
    queryFn: async () => {
      const response = await listLibraries();
      if (response.error) {
        throw new ApiClientError('Failed to fetch libraries', response.response.status);
      }

      return response.data as LibraryDto[];
    },
  });
}

export function useLibrary(libraryId: string | null) {
  return useQuery({
    queryKey: libraryManagementKeys.detail(libraryId ?? ''),
    queryFn: async () => {
      if (!libraryId) {
        throw new ApiClientError('Library ID is required');
      }

      const response = await getLibraryById({ path: { libraryId } });
      if (response.error) {
        throw new ApiClientError('Failed to fetch library', response.response.status);
      }

      return response.data as LibraryDto;
    },
    enabled: libraryId !== null,
  });
}

export function useCatalogFilterFacets() {
  return useQuery({
    queryKey: catalogFacetKeys.filters(),
    queryFn: async () => {
      const response = await getCatalogFilters();
      if (response.error) {
        throw new ApiClientError('Failed to fetch catalog filters', response.response.status);
      }

      return response.data as CatalogFilters;
    },
  });
}

export function useLibraryTitleReleaseDiagnostics(
  libraryId: string | null,
  titleId: string | null,
) {
  return useQuery({
    queryKey: libraryManagementKeys.diagnostics(libraryId ?? '', titleId ?? ''),
    queryFn: async () => {
      if (!libraryId || !titleId) {
        throw new ApiClientError('Library and title IDs are required');
      }

      const response = await getLibraryTitleReleaseDiagnostics({
        path: { libraryId, titleId },
      });
      if (response.error) {
        throw new ApiClientError('Failed to fetch release diagnostics', response.response.status);
      }

      return response.data as LibraryTitleReleaseDiagnosticsDto[];
    },
    enabled: libraryId !== null && titleId !== null,
  });
}

export function useTitleLabelMap(titleIds: string[]): Map<string, string> {
  const uniqueTitleIds = Array.from(new Set(titleIds.filter((titleId) => titleId.length > 0)));
  const titleQueries = useQueries({
    queries: uniqueTitleIds.map((titleId) => ({
      queryKey: titleLookupKeys.detail(titleId),
      queryFn: async (): Promise<TitleLabel | null> => {
        const response = await getTitleById({ path: { titleId } });
        if (response.error || !response.data) {
          return null;
        }

        const title = response.data as Title;
        return { id: title.id, name: title.name };
      },
      retry: false,
      staleTime: 5 * 60 * 1000,
    })),
  });

  return new Map(
    titleQueries.flatMap((query) =>
      query.data ? ([[query.data.id, query.data.name]] as const) : [],
    ),
  );
}

export function useCreateLibrary() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (request: CreateLibraryRequest) => {
      const response = await createLibrary({ body: request });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to create library'),
          response.response.status,
        );
      }

      return response.data as LibraryDto;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: libraryManagementKeys.all });
    },
  });
}

export function useUpdateLibrary() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({
      libraryId,
      request,
    }: {
      libraryId: string;
      request: UpdateLibraryRequest;
    }) => {
      const response = await updateLibrary({
        path: { libraryId },
        body: request,
      });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to update library'),
          response.response.status,
        );
      }

      return response.data as LibraryDto;
    },
    onSuccess: (library) => {
      queryClient.invalidateQueries({ queryKey: libraryManagementKeys.all });
      queryClient.invalidateQueries({ queryKey: libraryManagementKeys.detail(library.id) });
    },
  });
}

export function useDeleteLibrary() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (libraryId: string) => {
      const response = await deleteLibrary({ path: { libraryId } });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to delete library'),
          response.response.status,
        );
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: libraryManagementKeys.all });
    },
  });
}

export function useForceMaterializeLibrary() {
  const queryClient = useQueryClient();
  const { seedJob } = useCreateJob();

  return useMutation({
    mutationFn: async (libraryId: string): Promise<JobReference> => {
      const response = await forceMaterializeLibrary({ path: { libraryId } });
      if (response.error || !response.data) {
        throw new ApiClientError('Failed to rebuild library', response.response.status);
      }

      return response.data;
    },
    onSuccess: (jobReference, libraryId) => {
      seedJob(jobReference, { invalidateDelayMs: 0 });
      queryClient.invalidateQueries({ queryKey: libraryManagementKeys.all });
      queryClient.invalidateQueries({ queryKey: libraryManagementKeys.detail(libraryId) });
    },
  });
}
