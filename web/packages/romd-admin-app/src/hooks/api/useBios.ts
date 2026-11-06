import { notifications } from '@mantine/notifications';
import { backfillBiosCatalog, listSystemBios } from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { collectPages } from '../../utils/collectPages';

export const biosKeys = {
  all: [
    'bios',
  ] as const,
  byPlatform: (systemKey: string) =>
    [
      ...biosKeys.all,
      'platform',
      systemKey,
    ] as const,
};

export function useBiosByPlatform(systemKey: string | undefined) {
  return useQuery({
    queryKey: biosKeys.byPlatform(systemKey ?? ''),
    queryFn: async ({ signal }) => {
      if (!systemKey) throw new Error('System key is required');
      return collectPages(async (cursor) => {
        const response = await listSystemBios({
          signal,
          path: {
            systemKey,
          },
          query: {
            cursor,
            limit: 100,
          },
        });
        if (response.error || !response.data) throw new Error('Failed to fetch BIOS entries');
        return response.data;
      });
    },
    enabled: !!systemKey,
  });
}

/**
 * Groups BIOS games of already platform-assigned DATs into BIOS catalog entries.
 * Idempotent on the server; refreshes all BIOS lists on success.
 */
export function useBackfillBiosCatalog() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async () => {
      const response = await backfillBiosCatalog();
      if (response.error || !response.data) {
        throw new Error('Failed to sync BIOS catalog');
      }
      return response.data;
    },
    onSuccess: (result) => {
      const created = Number(result.biosEntriesCreated ?? 0);
      const grouped = Number(result.gamesGrouped ?? 0);
      notifications.show({
        title: 'BIOS catalog synced',
        message:
          grouped === 0
            ? 'No new BIOS games to group.'
            : `Grouped ${grouped.toLocaleString()} BIOS game${grouped === 1 ? '' : 's'} into ${created.toLocaleString()} entr${created === 1 ? 'y' : 'ies'}.`,
        color: 'green',
      });
      queryClient.invalidateQueries({
        queryKey: biosKeys.all,
      });
    },
    onError: () => {
      notifications.show({
        title: 'Sync failed',
        message: 'Could not sync the BIOS catalog. Please try again.',
        color: 'red',
      });
    },
  });
}
