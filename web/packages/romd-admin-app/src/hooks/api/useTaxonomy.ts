import type { LanguageDto, ProblemDetails, RegionDto } from '@romd/admin-api-client';
import {
  addLanguageAlias,
  addRegionAlias,
  listLanguages,
  listRegions,
  mergeLanguages,
  mergeRegions,
  removeLanguageAlias,
  removeRegionAlias,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

export const taxonomyKeys = {
  all: ['taxonomy'] as const,
  regions: () => [...taxonomyKeys.all, 'regions'] as const,
  languages: () => [...taxonomyKeys.all, 'languages'] as const,
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

export function useRegions() {
  return useQuery({
    queryKey: taxonomyKeys.regions(),
    queryFn: async () => {
      const response = await listRegions();
      if (response.error) {
        throw new ApiClientError('Failed to fetch regions', response.response.status);
      }

      return response.data as RegionDto[];
    },
  });
}

export function useLanguages() {
  return useQuery({
    queryKey: taxonomyKeys.languages(),
    queryFn: async () => {
      const response = await listLanguages();
      if (response.error) {
        throw new ApiClientError('Failed to fetch languages', response.response.status);
      }

      return response.data as LanguageDto[];
    },
  });
}

export function useAddRegionAlias() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ regionId, alias }: { regionId: string; alias: string }) => {
      const response = await addRegionAlias({ path: { regionId }, body: { alias } });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to add alias'),
          response.response.status,
        );
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: taxonomyKeys.regions() }),
  });
}

export function useRemoveRegionAlias() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ regionId, aliasId }: { regionId: string; aliasId: string }) => {
      const response = await removeRegionAlias({ path: { regionId, aliasId } });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to remove alias'),
          response.response.status,
        );
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: taxonomyKeys.regions() }),
  });
}

export function useMergeRegions() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ sourceId, targetId }: { sourceId: string; targetId: string }) => {
      const response = await mergeRegions({ path: { sourceId, targetId } });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to merge regions'),
          response.response.status,
        );
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: taxonomyKeys.regions() }),
  });
}

export function useAddLanguageAlias() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ languageId, alias }: { languageId: string; alias: string }) => {
      const response = await addLanguageAlias({ path: { languageId }, body: { alias } });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to add alias'),
          response.response.status,
        );
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: taxonomyKeys.languages() }),
  });
}

export function useRemoveLanguageAlias() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ languageId, aliasId }: { languageId: string; aliasId: string }) => {
      const response = await removeLanguageAlias({ path: { languageId, aliasId } });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to remove alias'),
          response.response.status,
        );
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: taxonomyKeys.languages() }),
  });
}

export function useMergeLanguages() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ sourceId, targetId }: { sourceId: string; targetId: string }) => {
      const response = await mergeLanguages({ path: { sourceId, targetId } });
      if (response.error) {
        throw new ApiClientError(
          getApiErrorMessage(response.error, 'Failed to merge languages'),
          response.response.status,
        );
      }
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: taxonomyKeys.languages() }),
  });
}
