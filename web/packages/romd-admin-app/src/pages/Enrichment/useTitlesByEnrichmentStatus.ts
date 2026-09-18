import type { CatalogTitle, PageOfCatalogTitle } from '@romd/admin-api-client';
import { searchCatalog } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export const enrichmentTitlesKeys = {
  all: ['enrichmentTitles'] as const,
  byStatus: (status: string) => [...enrichmentTitlesKeys.all, status] as const,
};

interface UseTitlesByEnrichmentStatusOptions {
  enabled?: boolean;
  pollingInterval?: number;
}

/**
 * Fetches titles filtered by enrichment status.
 * Used for the enrichment management page.
 */
export function useTitlesByEnrichmentStatus(
  status: string,
  options?: UseTitlesByEnrichmentStatusOptions
) {
  const { enabled = true, pollingInterval } = options ?? {};

  return useQuery({
    queryKey: enrichmentTitlesKeys.byStatus(status),
    queryFn: async () => {
      const response = await searchCatalog({
        query: {
          enrichmentStatus: status,
          limit: 100,
        },
      });

      if (response.error) {
        throw new Error('Failed to fetch titles');
      }

      return (response.data as PageOfCatalogTitle).items;
    },
    enabled,
    refetchInterval: pollingInterval,
  });
}
