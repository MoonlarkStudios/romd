import { notifications } from '@mantine/notifications';
import type { Bios } from '@romd/admin-api-client';
import { backfillBiosCatalog, listBiosByPlatform } from '@romd/admin-api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

export const biosKeys = {
  all: ['bios'] as const,
  byPlatform: (systemKey: string) => [...biosKeys.all, 'platform', systemKey] as const,
};

export function useBiosByPlatform(systemKey: string | undefined) {
  return useQuery({
    queryKey: biosKeys.byPlatform(systemKey ?? ''),
    queryFn: async () => {
      if (!systemKey) throw new Error('Platform ID is required');
      const response = await listBiosByPlatform({ path: { systemKey } });
      if (response.error) {
        throw new Error('Failed to fetch BIOS entries');
      }
      return response.data as Bios[];
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
      queryClient.invalidateQueries({ queryKey: biosKeys.all });
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
