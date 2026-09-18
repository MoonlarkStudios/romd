import { getAdminCatalogSnapshot } from '@romd/admin-api-client';
import { ReferenceCatalogProvider } from '@romd/consumer-ui';
import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useAuth } from '../contexts/AuthContext';

export function EffectiveReferenceCatalog({ children }: { children: ReactNode }) {
  const { user, isAuthenticated } = useAuth();
  const { data } = useQuery({
    queryKey: ['reference-catalog', user?.id],
    enabled: isAuthenticated,
    staleTime: 60_000,
    queryFn: async ({ signal }) => {
      const response = await getAdminCatalogSnapshot({ signal, throwOnError: true });
      if (response.data.schemaVersion !== 1) throw new Error('Unsupported reference catalog schema');
      return response.data;
    },
  });
  return <ReferenceCatalogProvider catalog={isAuthenticated ? data : undefined}>{children}</ReferenceCatalogProvider>;
}
