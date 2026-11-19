import {
  getManagedSystemDetails,
  listDatCatalogs,
  listDatSubscriptions,
  listManagedSystems,
  type ManagedSystem,
  previewSystemNameAliases,
  type SystemResource,
  setSystemEnabled,
} from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { collectPages } from '../../utils/collectPages';
import { platformKeys } from './usePlatforms';

export const managedSystemKeys = {
  all: [
    'managed-systems',
  ] as const,
  catalogs: [
    'setup-catalogs',
  ] as const,
  subscriptions: [
    'dat-subscriptions',
  ] as const,
  nameAliasPreview: [
    'system-setup-name-alias-preview',
  ] as const,
};
const bySystemName = new Intl.Collator(undefined, {
  sensitivity: 'base',
  numeric: true,
});

const orderSystemsByName = (systems: ManagedSystem[]) =>
  [
    ...systems,
  ].sort((a, b) => bySystemName.compare(a.name, b.name));

export function useManagedSystems(query = '', { enabled = true, orderByName = !!query } = {}) {
  return useQuery({
    queryKey: query
      ? [
          ...managedSystemKeys.all,
          'search',
          query,
        ]
      : managedSystemKeys.all,
    enabled,
    select: orderByName ? orderSystemsByName : undefined,
    queryFn: async ({ signal }) => {
      return collectPages(async (cursor) => {
        signal.throwIfAborted();
        const response = await listManagedSystems({
          signal,
          query: {
            query: query || undefined,
            cursor,
            limit: 100,
          },
        });
        signal.throwIfAborted();
        if (!response.data || response.error)
          throw new Error('Your systems could not be loaded. Try again.');
        return response.data;
      });
    },
    refetchInterval: (query) =>
      query.state.data?.some((x) => x.enabled && x.state === 'Processing') ? 3000 : false,
  });
}
export function useManagedSystemDetails(systemKey: string | undefined) {
  return useQuery({
    queryKey: [
      ...managedSystemKeys.all,
      'details',
      systemKey,
    ],
    enabled: !!systemKey,
    queryFn: async ({ signal }) => {
      if (!systemKey) throw new Error('System key is required');
      const response = await getManagedSystemDetails({
        path: {
          systemKey,
        },
        signal,
      });
      if (response.error || !response.data) throw new Error('System details could not be loaded.');
      return response.data;
    },
  });
}

export function useSystemNameAliasValidation(nameAliases: string[]) {
  const preview = useQuery({
    queryKey: [
      ...managedSystemKeys.nameAliasPreview,
      nameAliases,
    ],
    enabled: nameAliases.length > 0,
    retry: false,
    queryFn: async ({ signal }) => {
      const response = await previewSystemNameAliases({
        body: {
          nameAliases,
        },
        signal,
      });
      if (response.response?.status === 400 && response.error?.detail)
        throw new Error(response.error.detail);
      if (!response.data || response.error) throw new Error('Name aliases could not be checked.');
      return response.data;
    },
  });
  return {
    aliasesValid: nameAliases.length === 0 || (preview.isSuccess && preview.data.length === 0),
    aliasError: preview.isError
      ? preview.error.message
      : preview.data
          ?.map(
            (conflict) =>
              `“${conflict.alias}” is already ${conflict.builtIn ? 'a built-in alias' : 'an alias'} for ${conflict.systemName}.`,
          )
          .join(' '),
    checkingAliases: preview.isFetching && !preview.isSuccess,
    retryAliasCheck: preview.isError ? () => void preview.refetch() : undefined,
  };
}

export function useRefreshSystemPresentation() {
  const client = useQueryClient();
  return async (resource: SystemResource) => {
    const detail = platformKeys.detail(resource.key);
    await client.cancelQueries({
      queryKey: detail,
    });
    client.setQueryData(detail, resource);
    void client.invalidateQueries({
      queryKey: platformKeys.aliases(resource.key),
    });
    void client.invalidateQueries({
      queryKey: platformKeys.overrides(resource.key),
    });
    void client.invalidateQueries({
      queryKey: platformKeys.lists(),
    });
    void client.invalidateQueries({
      queryKey: [
        'reference-catalog',
      ],
    });
    void client.invalidateQueries({
      queryKey: managedSystemKeys.all,
    });
    void client.invalidateQueries({
      queryKey: managedSystemKeys.nameAliasPreview,
      refetchType: 'none',
    });
  };
}

/** The publisher's catalog directory; its message explains an empty or unverified directory. */
export function useDatCatalogs() {
  return useQuery({
    queryKey: managedSystemKeys.catalogs,
    queryFn: async ({ signal }) => {
      const response = await listDatCatalogs({
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
/** Local subscription state only; it never contacts the publisher. */
export function useDatSubscriptions({ pollApplying = false }: { pollApplying?: boolean } = {}) {
  return useQuery({
    queryKey: managedSystemKeys.subscriptions,
    queryFn: async ({ signal }) => {
      const response = await listDatSubscriptions({
        signal,
      });
      if (!response.data || response.error)
        throw new Error(
          'Subscription status could not be loaded. Installed sources are unchanged.',
        );
      return response.data;
    },
    refetchInterval: (query) =>
      pollApplying && query.state.data?.some((subscription) => subscription.state === 'Applying')
        ? 3000
        : false,
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
