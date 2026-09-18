import type { OperationalDiagnosticsDto } from '@romd/admin-api-client';
import { getOperationalDiagnostics } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export const operationalDiagnosticsKeys = {
  all: ['operational-diagnostics'] as const,
  snapshot: () => [...operationalDiagnosticsKeys.all, 'snapshot'] as const,
};

/**
 * Fetches one bounded server-side snapshot. Refresh is operator-driven so the
 * diagnostics page cannot amplify a dependency outage through polling.
 */
export function useOperationalDiagnostics(enabled = true) {
  return useQuery({
    enabled,
    queryKey: operationalDiagnosticsKeys.snapshot(),
    queryFn: async ({ signal }): Promise<OperationalDiagnosticsDto> => {
      const response = await getOperationalDiagnostics({ signal });
      if (response.error || !response.data) {
        throw new Error('Failed to fetch operational diagnostics');
      }

      return response.data as OperationalDiagnosticsDto;
    },
    staleTime: 60_000,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
}
