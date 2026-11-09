import type { OperationalDiagnostics } from '@romd/admin-api-client';
import { getServerDiagnostics } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export const operationalDiagnosticsKeys = {
  all: [
    'operational-diagnostics',
  ] as const,
  snapshot: () =>
    [
      ...operationalDiagnosticsKeys.all,
      'snapshot',
    ] as const,
};

/**
 * Fetches one bounded server-side snapshot. Refresh is operator-driven so the
 * diagnostics page cannot amplify a dependency outage through polling.
 */
export function useOperationalDiagnostics(enabled = true) {
  return useQuery({
    enabled,
    queryKey: operationalDiagnosticsKeys.snapshot(),
    queryFn: async ({ signal }): Promise<OperationalDiagnostics> => {
      const response = await getServerDiagnostics({
        signal,
      });
      if (response.error || !response.data) {
        throw new Error('Failed to fetch operational diagnostics');
      }

      return response.data as OperationalDiagnostics;
    },
    staleTime: 60_000,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
}
