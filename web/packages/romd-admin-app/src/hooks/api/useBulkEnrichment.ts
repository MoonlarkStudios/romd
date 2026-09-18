import type { EnrichmentScope } from '@romd/admin-api-client';
import { triggerBulkEnrichment } from '@romd/admin-api-client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { jobKeys } from './useJobs';

/** Enrichment scope values (mirrors the backend EnrichmentScope enum, named strings). */
export const EnrichmentScopeValue = {
  Tracked: 'Tracked',
  All: 'All',
} as const satisfies Record<string, EnrichmentScope>;

interface BulkEnrichmentParams {
  systemKey: string;
  /** Defaults to Tracked (only curated titles). Pass All to enrich the entire catalog. */
  scope?: EnrichmentScope;
}

/**
 * Hook to trigger bulk enrichment for a platform.
 * Defaults to the tracked (curated) scope; pass `scope: EnrichmentScopeValue.All` to
 * enrich every title. Returns 202; progress is tracked via SignalR / Jobs page.
 */
export function useBulkEnrichment() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ systemKey, scope }: BulkEnrichmentParams) => {
      const response = await triggerBulkEnrichment({
        path: { systemKey },
        query: scope === undefined ? undefined : { scope },
      });
      if (response.error) {
        throw new Error('Failed to trigger bulk enrichment');
      }
      return response.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: jobKeys.all });
    },
  });
}
