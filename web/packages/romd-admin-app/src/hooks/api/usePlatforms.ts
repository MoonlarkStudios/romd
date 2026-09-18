import type { PlatformAlias, SystemResourceDto } from '@romd/admin-api-client';
import { getAdminSystems, listAdminSystems, listPlatformAliases } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';

export const platformKeys = {
  all: ['platforms'] as const,
  lists: () => [...platformKeys.all, 'list'] as const,
  list: () => [...platformKeys.lists()] as const,
  details: () => [...platformKeys.all, 'detail'] as const,
  detail: (id: string) => [...platformKeys.details(), id] as const,
  aliases: (id: string) => [...platformKeys.all, 'aliases', id] as const,
};

export function usePlatforms() {
  return useQuery({
    queryKey: platformKeys.list(),
    queryFn: async () => {
      const response = await listAdminSystems();
      if (response.error) {
        throw new Error('Failed to fetch platforms');
      }
      return response.data as SystemResourceDto[];
    },
  });
}

export function usePlatformAliases(systemKey: string | undefined) {
  return useQuery({
    queryKey: platformKeys.aliases(systemKey ?? ''),
    queryFn: async () => {
      if (!systemKey) throw new Error('Platform ID is required');
      const response = await listPlatformAliases({ path: { systemKey } });
      if (response.error) {
        throw new Error('Failed to fetch platform aliases');
      }
      return response.data as PlatformAlias[];
    },
    enabled: !!systemKey,
  });
}

export function usePlatform(systemKey: string | undefined) {
  return useQuery({
    queryKey: platformKeys.detail(systemKey ?? ''),
    queryFn: async () => {
      if (!systemKey) throw new Error('Platform ID is required');
      const response = await getAdminSystems({ path: { key: systemKey } });
      if (response.error) {
        throw new Error('Failed to fetch platform');
      }
      return response.data as SystemResourceDto;
    },
    enabled: !!systemKey,
  });
}
