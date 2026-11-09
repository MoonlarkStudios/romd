import type { SystemResource } from '@romd/admin-api-client';
import { getAdminSystem, listAdminSystems, listSystemAliases } from '@romd/admin-api-client';
import { useQuery } from '@tanstack/react-query';
import { collectPages } from '../../utils/collectPages';

export const platformKeys = {
  all: [
    'platforms',
  ] as const,
  lists: () =>
    [
      ...platformKeys.all,
      'list',
    ] as const,
  list: () =>
    [
      ...platformKeys.lists(),
    ] as const,
  details: () =>
    [
      ...platformKeys.all,
      'detail',
    ] as const,
  detail: (id: string) =>
    [
      ...platformKeys.details(),
      id,
    ] as const,
  aliases: (id: string) =>
    [
      ...platformKeys.all,
      'aliases',
      id,
    ] as const,
};

export function usePlatforms() {
  return useQuery({
    queryKey: platformKeys.list(),
    queryFn: async ({ signal }) =>
      collectPages(async (cursor) => {
        signal.throwIfAborted();
        const response = await listAdminSystems({
          signal,
          query: {
            cursor,
            limit: 100,
          },
        });
        signal.throwIfAborted();
        if (response.error || !response.data) throw new Error('Failed to fetch platforms');
        return response.data;
      }),
  });
}

export function usePlatformAliases(systemKey: string | undefined) {
  return useQuery({
    queryKey: platformKeys.aliases(systemKey ?? ''),
    queryFn: async ({ signal }) => {
      if (!systemKey) throw new Error('System key is required');
      return collectPages(async (cursor) => {
        const response = await listSystemAliases({
          signal,
          path: {
            systemKey,
          },
          query: {
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data) throw new Error('Failed to fetch system aliases');
        return response.data;
      });
    },
    enabled: !!systemKey,
  });
}

export function usePlatform(systemKey: string | undefined) {
  return useQuery({
    queryKey: platformKeys.detail(systemKey ?? ''),
    queryFn: async () => {
      if (!systemKey) throw new Error('Platform ID is required');
      const response = await getAdminSystem({
        path: {
          systemKey,
        },
      });
      if (response.error) {
        throw new Error('Failed to fetch platform');
      }
      return response.data as SystemResource;
    },
    enabled: !!systemKey,
  });
}
