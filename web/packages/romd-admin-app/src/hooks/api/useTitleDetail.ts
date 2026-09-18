import type { TitleDetail, TitleSourceReference } from '@romd/admin-api-client';
import { getTitleDetails, getTitleSourceReferences } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export const titleDetailKeys = {
  all: ['titleDetail'] as const,
  detail: (titleId: string) => [...titleDetailKeys.all, titleId] as const,
  // A child of detail(id) so title-level invalidations refresh the backing sources too,
  // while the detail cache entry itself stays untouched.
  sourceReferences: (titleId: string) =>
    [...titleDetailKeys.detail(titleId), 'sourceReferences'] as const,
};

/**
 * Fetches detailed title information including releases and files.
 *
 * @example
 * ```tsx
 * const { data: title, isLoading, isError } = useTitleDetail(titleId);
 * ```
 */
export function useTitleDetail(titleId: string | undefined) {
  return useQuery({
    queryKey: titleDetailKeys.detail(titleId ?? ''),
    queryFn: async () => {
      if (!titleId) throw new Error('Title ID is required');

      const response = await getTitleDetails({ path: { titleId } });

      if (response.error) {
        throw new Error('Failed to fetch title details');
      }

      return response.data as TitleDetail;
    },
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
    queryFn: async () => {
      if (!titleId) throw new Error('Title ID is required');

      const response = await getTitleSourceReferences({ path: { titleId } });

      if (response.error) {
        throw new Error('Failed to fetch title source references');
      }

      return response.data as TitleSourceReference[];
    },
    enabled: !!titleId,
  });
}
