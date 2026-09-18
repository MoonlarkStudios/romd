import type { TitleDetail, TitleEnrichmentStateResponse } from '@romd/admin-api-client';
import {
  confirmExternalId,
  getTitleEnrichmentState,
  setFieldOverrides,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { catalogSearchKeys } from './useCatalogSearch';
import { titleDetailKeys } from './useTitleDetail';

export const titleEnrichmentKeys = {
  all: ['titleEnrichment'] as const,
  detail: (titleId: string) => [...titleEnrichmentKeys.all, titleId] as const,
};

/**
 * Fetches full enrichment state for a title including provider layers,
 * external IDs with confidence, and per-field source overrides.
 */
export function useTitleEnrichment(titleId: string | undefined) {
  return useQuery({
    queryKey: titleEnrichmentKeys.detail(titleId ?? ''),
    queryFn: async () => {
      if (!titleId) throw new Error('Title ID is required');
      const response = await getTitleEnrichmentState({ path: { titleId } });
      if (response.error) {
        throw new Error('Failed to fetch enrichment state');
      }
      return response.data as TitleEnrichmentStateResponse;
    },
    enabled: !!titleId,
  });
}

interface SetFieldOverridesParams {
  titleId: string;
  overrides: Record<string, string>;
  optimisticTitle?: Partial<TitleDetail>;
  optimisticFieldProvenance?: Record<string, string | null>;
  optimisticFieldSourceOverrides?: Record<string, string | null>;
}

interface SetFieldOverridesContext {
  titleId: string;
  previousTitle?: TitleDetail;
  previousEnrichment?: TitleEnrichmentStateResponse;
}

function applyNullableRecordPatch(
  current: Record<string, string> | null | undefined,
  patch: Record<string, string | null>,
): Record<string, string> {
  const next = { ...(current ?? {}) };

  for (const [key, value] of Object.entries(patch)) {
    if (value === null || value === '') {
      delete next[key];
    } else {
      next[key] = value;
    }
  }

  return next;
}

/**
 * Hook to set or clear per-field source overrides for a title.
 * Invalidates both enrichment state and title detail caches.
 */
export function useSetFieldOverrides() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, overrides }: SetFieldOverridesParams) => {
      const response = await setFieldOverrides({
        path: { titleId },
        body: { overrides },
      });
      if (response.error) {
        throw new Error('Failed to set field overrides');
      }
      return response.data as unknown as TitleDetail | undefined;
    },
    onMutate: async ({
      titleId,
      optimisticTitle,
      optimisticFieldProvenance,
      optimisticFieldSourceOverrides,
    }) => {
      await Promise.all([
        queryClient.cancelQueries({ queryKey: titleDetailKeys.detail(titleId) }),
        queryClient.cancelQueries({ queryKey: titleEnrichmentKeys.detail(titleId) }),
      ]);

      const previousTitle = queryClient.getQueryData<TitleDetail>(
        titleDetailKeys.detail(titleId),
      );
      const previousEnrichment = queryClient.getQueryData<TitleEnrichmentStateResponse>(
        titleEnrichmentKeys.detail(titleId),
      );

      if (optimisticTitle || optimisticFieldProvenance) {
        queryClient.setQueryData<TitleDetail>(
          titleDetailKeys.detail(titleId),
          (current) =>
            current
              ? {
                  ...current,
                  ...optimisticTitle,
                  fieldProvenance: optimisticFieldProvenance
                    ? applyNullableRecordPatch(current.fieldProvenance, optimisticFieldProvenance)
                    : current.fieldProvenance,
                }
              : current,
        );
      }

      if (optimisticFieldProvenance || optimisticFieldSourceOverrides) {
        queryClient.setQueryData<TitleEnrichmentStateResponse>(
          titleEnrichmentKeys.detail(titleId),
          (current) =>
            current
              ? {
                  ...current,
                  fieldProvenance: optimisticFieldProvenance
                    ? applyNullableRecordPatch(current.fieldProvenance, optimisticFieldProvenance)
                    : current.fieldProvenance,
                  fieldSourceOverrides: optimisticFieldSourceOverrides
                    ? applyNullableRecordPatch(
                        current.fieldSourceOverrides,
                        optimisticFieldSourceOverrides,
                      )
                    : current.fieldSourceOverrides,
                }
              : current,
        );
      }

      return { titleId, previousTitle, previousEnrichment };
    },
    onError: (_error, _variables, context: SetFieldOverridesContext | undefined) => {
      if (context?.previousTitle) {
        queryClient.setQueryData(titleDetailKeys.detail(context.titleId), context.previousTitle);
      }

      if (context?.previousEnrichment) {
        queryClient.setQueryData(
          titleEnrichmentKeys.detail(context.titleId),
          context.previousEnrichment,
        );
      }
    },
    onSuccess: async (
      data,
      { titleId, optimisticTitle, optimisticFieldProvenance, optimisticFieldSourceOverrides },
    ) => {
      if (data) {
        queryClient.setQueryData(titleDetailKeys.detail(titleId), data);
      } else if (optimisticTitle || optimisticFieldProvenance) {
        queryClient.setQueryData<TitleDetail>(titleDetailKeys.detail(titleId), (current) =>
          current
            ? {
                ...current,
                ...optimisticTitle,
                fieldProvenance: optimisticFieldProvenance
                  ? applyNullableRecordPatch(current.fieldProvenance, optimisticFieldProvenance)
                  : current.fieldProvenance,
              }
            : current,
        );
      }

      if (optimisticFieldProvenance || optimisticFieldSourceOverrides) {
        queryClient.setQueryData<TitleEnrichmentStateResponse>(
          titleEnrichmentKeys.detail(titleId),
          (current) =>
            current
              ? {
                  ...current,
                  fieldProvenance: optimisticFieldProvenance
                    ? applyNullableRecordPatch(current.fieldProvenance, optimisticFieldProvenance)
                    : current.fieldProvenance,
                  fieldSourceOverrides: optimisticFieldSourceOverrides
                    ? applyNullableRecordPatch(
                        current.fieldSourceOverrides,
                        optimisticFieldSourceOverrides,
                      )
                    : current.fieldSourceOverrides,
                }
              : current,
        );
      }

      await Promise.all([
        queryClient.invalidateQueries({ queryKey: titleEnrichmentKeys.detail(titleId) }),
        queryClient.invalidateQueries({ queryKey: catalogSearchKeys.all }),
      ]);
    },
  });
}

interface ConfirmExternalIdParams {
  titleId: string;
  provider: string;
}

/**
 * Hook to confirm an auto-matched external ID, making it immutable to auto-enrichment.
 */
export function useConfirmExternalId() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ titleId, provider }: ConfirmExternalIdParams) => {
      const response = await confirmExternalId({
        path: { titleId, provider },
      });
      if (response.error) {
        throw new Error('Failed to confirm external ID');
      }
      return response.data;
    },
    onSuccess: async (_data, { titleId }) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: titleEnrichmentKeys.detail(titleId) }),
        queryClient.invalidateQueries({ queryKey: titleDetailKeys.detail(titleId) }),
      ]);
    },
  });
}
