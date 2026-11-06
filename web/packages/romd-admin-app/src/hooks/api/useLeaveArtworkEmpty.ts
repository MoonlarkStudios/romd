import { type ReplaceTitleArtworkRoleData, replaceTitleArtworkRole } from '@romd/admin-api-client';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { invalidateTitle } from './titleCache';
import { ifMatch } from './titleConflict';
import { storeArtwork } from './useArtwork';

/**
 * Leaves an artwork role empty so automatic runs never fill it, under the role revision the curator saw, then
 * refreshes every surface of the title. A stale revision reloads the title first, so a retry decides on what
 * changed rather than sending the same revision again.
 */
export function useLeaveArtworkEmpty(titleId: string) {
  const cache = useQueryClient();
  return useMutation({
    mutationFn: async ({
      role,
      revision,
    }: {
      role: ReplaceTitleArtworkRoleData['path']['role'];
      revision: string;
    }) => {
      const response = await replaceTitleArtworkRole({
        path: {
          titleId,
          role,
        },
        headers: ifMatch(revision),
        body: {
          mode: 'Empty',
        },
      }).catch(() => null);
      if (!response) throw new Error('Could not update artwork. Try again.');
      if (response.response?.status === 412) {
        await invalidateTitle(cache, titleId);
        throw new Error('The artwork changed since you saw it. Review it, then decide again.');
      }
      if (response.error || !response.data)
        throw new Error(
          response.error?.detail ?? 'Could not leave this role empty. Reload and try again.',
        );
      await storeArtwork(cache, titleId, response.data);
    },
    onSuccess: () => invalidateTitle(cache, titleId),
  });
}
