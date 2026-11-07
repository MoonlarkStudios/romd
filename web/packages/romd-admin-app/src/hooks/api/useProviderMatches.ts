import {
  confirmTitleProviderMatch,
  deleteTitleProviderMatch,
  listTitleProviderMatches,
  type TitleProviderMatch,
} from '@romd/admin-api-client';
import {
  type QueryClient,
  queryOptions,
  useMutation,
  useQuery,
  useQueryClient,
} from '@tanstack/react-query';
import { invalidateTitle, providerMatchKeys } from './titleCache';
import { ifMatch } from './titleConflict';

export const providerMatchesQuery = (titleId: string) =>
  queryOptions({
    queryKey: providerMatchKeys.title(titleId),
    queryFn: async ({ signal }) => {
      const result = await listTitleProviderMatches({
        path: {
          titleId,
        },
        signal,
      });
      if (result.error || !result.data) throw new Error('Provider matches could not be loaded.');
      return result.data;
    },
    staleTime: 30_000,
  });

export function useProviderMatches(titleId: string) {
  return useQuery(providerMatchesQuery(titleId));
}

/** A provider-match write carried a stale match revision (412); the server wrote nothing. */
export class ProviderMatchConflictError extends Error {
  constructor() {
    super('The provider match changed. Review the reloaded match, then decide again.');
    this.name = 'ProviderMatchConflictError';
  }
}

/**
 * Records a match a write returned, so the next write to it sends its new revision before the refetch lands.
 */
export function storeProviderMatch(cache: QueryClient, titleId: string, match: TitleProviderMatch) {
  cache.setQueryData<TitleProviderMatch[]>(providerMatchKeys.title(titleId), (rows) =>
    rows?.map((row) => (row.providerId === match.providerId ? match : row)),
  );
}

/**
 * Confirms a match, or unlinks it to continue without the provider, under the revision of the row the curator
 * decided on, then refreshes every surface of the title. A stale row reloads the matches and rejects with
 * {@link ProviderMatchConflictError}, so the curator decides again on what changed.
 */
export function useProviderMatchDecision(titleId: string) {
  const cache = useQueryClient();
  return useMutation({
    mutationFn: async ({ row, unlink }: { row: TitleProviderMatch; unlink: boolean }) => {
      const input = {
        path: {
          titleId,
          providerId: row.providerId,
        },
        headers: ifMatch(row.revision),
      };
      const settle = async (result: { error?: unknown; response?: Response }) => {
        if (result.response?.status === 412) {
          await cache.invalidateQueries({
            queryKey: providerMatchKeys.title(titleId),
          });
          throw new ProviderMatchConflictError();
        }
        if (result.error)
          throw new Error('The match could not be updated. Reload matches and try again.');
      };
      // Both writes answer the match with its new revision, so a later write needs no reload first.
      const written = unlink
        ? await deleteTitleProviderMatch(input)
        : await confirmTitleProviderMatch(input);
      await settle(written);
      if (written.data) storeProviderMatch(cache, titleId, written.data);
    },
    onSuccess: () => invalidateTitle(cache, titleId),
  });
}
