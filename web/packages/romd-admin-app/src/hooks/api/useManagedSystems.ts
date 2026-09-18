import { discoverDatCatalogs, listManagedSystems, setSystemEnabled } from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

export const managedSystemKeys = {
  all: [
    'managed-systems',
  ] as const,
  catalogs: [
    'setup-catalogs',
  ] as const,
};
export function useManagedSystems() {
  return useQuery({
    queryKey: managedSystemKeys.all,
    queryFn: async ({ signal }) => {
      const response = await listManagedSystems({
        signal,
      });
      if (!response.data || response.error)
        throw new Error('Your systems could not be loaded. Try again.');
      return response.data;
    },
    refetchInterval: (query) =>
      query.state.data?.some((x) => x.enabled && x.state === 'Processing') ? 3000 : false,
  });
}
export function useSetupCatalogs() {
  return useQuery({
    queryKey: managedSystemKeys.catalogs,
    queryFn: async ({ signal }) => {
      const response = await discoverDatCatalogs({
        signal,
      });
      if (!response.data || response.error)
        throw new Error(
          'Catalog availability could not be checked. You can still upload a DAT or set up later.',
        );
      return response.data;
    },
    retry: false,
    staleTime: 60000,
  });
}
export function useSetSystemEnabled() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ systemKey, enabled }: { systemKey: string; enabled: boolean }) => {
      const response = await setSystemEnabled({
        path: {
          systemKey,
        },
        body: {
          enabled,
        },
      });
      if (response.error) throw new Error('Your system preference could not be saved. Try again.');
    },
    onSuccess: () =>
      client.invalidateQueries({
        queryKey: managedSystemKeys.all,
      }),
  });
}
