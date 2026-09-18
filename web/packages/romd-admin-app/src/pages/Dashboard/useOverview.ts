import { getAdminAudit } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { useEnrichmentStats } from '../../hooks/api/useEnrichmentStats';
import { useHealthStats } from '../../hooks/api/useHealthStats';
import { useJobHistory } from '../../hooks/api/useJobHistory';
import { useTrackedCollectionStats } from '../../hooks/api/useTrackedCollection';
import { usePermissions } from '../../hooks/usePermissions';

/** Composes existing domain summaries without downloading title lists or probing dependencies. */
export function useOverview() {
  const permissions = usePermissions();
  const health = useHealthStats();
  const enrichment = useEnrichmentStats();
  const collection = useTrackedCollectionStats();
  const running = useJobHistory({ outcome: 'running', archive: 'active', limit: 5 });
  const queued = useJobHistory({ outcome: 'queued', archive: 'active', limit: 5 });
  const failed = useJobHistory({ outcome: 'failed', archive: 'active', limit: 5 });
  const audit = useQuery({
    queryKey: ['admin-audit', 'overview'], enabled: permissions.canManageUsers,
    queryFn: async ({ signal }) => {
      const response = await getAdminAudit({ signal });
      if (response.error || !response.data) throw new Error('Could not load administrative changes.');
      return response.data;
    }, staleTime: 60_000,
  });
  const queries = [health, enrichment, collection, running, queued, failed, ...(permissions.canManageUsers ? [audit] : [])];
  return { permissions, health, enrichment, collection, running, queued, failed, audit,
    refreshing: queries.some(query => query.isFetching),
    refresh: () => Promise.allSettled(queries.map(query => query.refetch())),
  };
}
