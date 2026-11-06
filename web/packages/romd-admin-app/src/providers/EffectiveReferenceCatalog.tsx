import { ReferenceCatalogProvider } from '@romd/consumer-ui';
import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useAuth } from '../contexts/AuthContext';
import {
  type AssembledReferenceCatalog,
  loadAdminReferenceCatalog,
} from '../utils/referenceCatalog';

/**
 * The caller's published reference catalog snapshot. Every reader shares this query, so the snapshot is
 * fetched once; `select` derives a reader's view of it.
 */
export function useAdminCatalogSnapshot<T = AssembledReferenceCatalog>(
  select?: (catalog: AssembledReferenceCatalog) => T,
) {
  const { user, isAuthenticated } = useAuth();
  return useQuery({
    queryKey: [
      'reference-catalog',
      user?.id,
    ],
    enabled: isAuthenticated,
    staleTime: 60_000,
    queryFn: ({ signal }) => loadAdminReferenceCatalog(signal),
    // The transport owns one bounded SnapshotChanged restart. React Query must not multiply it.
    retry: false,
    select,
  });
}

export function EffectiveReferenceCatalog({ children }: { children: ReactNode }) {
  const { isAuthenticated } = useAuth();
  const { data } = useAdminCatalogSnapshot();
  return (
    <ReferenceCatalogProvider catalog={isAuthenticated ? data : undefined}>
      {children}
    </ReferenceCatalogProvider>
  );
}
