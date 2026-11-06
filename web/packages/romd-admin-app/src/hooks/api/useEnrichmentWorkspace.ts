import {
  getEnrichmentWorkbenchStats,
  getTitleEnrichmentAssessment,
  type ListEnrichmentWorkbenchData,
  listEnrichmentWorkbench,
  replaceTitleEnrichmentOmission,
} from '@romd/admin-api-client';
import {
  queryOptions,
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
} from '@tanstack/react-query';

import { enrichmentWorkspaceKeys } from './enrichmentWorkspaceCache';
import { cacheAcceptedTitle, invalidateTitle } from './titleCache';
import { ifMatch } from './titleConflict';

export { enrichmentWorkspaceKeys } from './enrichmentWorkspaceCache';

export function useEnrichmentWorkspace(
  view: NonNullable<ListEnrichmentWorkbenchData['query']>['view'],
  search: string,
) {
  const cache = useQueryClient();
  const queryKey = [
    ...enrichmentWorkspaceKeys.all,
    'list',
    view,
    search,
  ];
  const query = useInfiniteQuery({
    queryKey,
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam, signal }) => {
      const result = await listEnrichmentWorkbench({
        query: {
          view,
          query: search || undefined,
          cursor: pageParam,
          limit: 50,
        },
        signal,
      });
      if (result.error || !result.data)
        throw new Error('Could not load the enrichment workspace. Your choices are still saved.');
      if (
        !Object.hasOwn(result.data, 'nextCursor') ||
        result.data.hasNextPage !== (typeof result.data.nextCursor === 'string') ||
        (result.data.hasNextPage && !result.data.nextCursor?.trim()) ||
        (pageParam !== undefined && result.data.nextCursor === pageParam)
      )
        throw new Error(
          'The server returned an invalid workspace continuation. Refresh the queue.',
        );
      return result.data;
    },
    getNextPageParam: (page) => (page.hasNextPage ? page.nextCursor : undefined),
    // Keep loaded pages together: refreshing only page one can leave gaps when
    // titles leave a queue. Counts load independently of every cursor page.
    // Poll idle queues too, so missed realtime events cannot hide newly eligible work.
    refetchInterval: 30_000,
    staleTime: 15_000,
  });
  return {
    ...query,
    fetchNextPage: () => {
      // A refresh may start after the last render. Consult the live query before
      // paginating so neither entry point can cancel its cursor-chain refresh.
      if (
        cache.isFetching({
          queryKey,
          exact: true,
        })
      )
        return Promise.resolve(undefined);
      return query.fetchNextPage({
        cancelRefetch: false,
      });
    },
  };
}

/** One cache entry per search; each surface chooses its own freshness. */
export const enrichmentWorkspaceCountsQuery = (search: string) =>
  queryOptions({
    queryKey: enrichmentWorkspaceKeys.counts(search),
    queryFn: async ({ signal }) => {
      const result = await getEnrichmentWorkbenchStats({
        query: {
          query: search || undefined,
        },
        signal,
      });
      if (result.error || !result.data)
        throw new Error('Could not update queue counts. You can keep reviewing titles.');
      return result.data;
    },
  });

export function useEnrichmentWorkspaceCounts(search: string) {
  return useQuery({
    ...enrichmentWorkspaceCountsQuery(search),
    staleTime: 15_000,
    refetchInterval: 30_000,
  });
}

/** One title's assessment. Its `revision` is the title's validator as of this read. */
export const enrichmentAssessmentQuery = (titleId: string) =>
  queryOptions({
    queryKey: enrichmentWorkspaceKeys.title(titleId),
    queryFn: async ({ signal }) => {
      const result = await getTitleEnrichmentAssessment({
        path: {
          titleId,
        },
        signal,
      });
      if (result.error || !result.data)
        throw new Error(
          result.response?.status === 404
            ? 'This title is no longer available.'
            : 'Could not load this title’s enrichment decisions.',
        );
      return result.data;
    },
  });

export function useEnrichmentAssessment(titleId: string | undefined) {
  return useQuery({
    ...enrichmentAssessmentQuery(titleId ?? ''),
    enabled: !!titleId,
    refetchInterval: (query) => (query.state.data?.hasActiveWork ? 3000 : 30_000),
    staleTime: 0,
  });
}

export function useEnrichmentOmission() {
  const cache = useQueryClient();
  return useMutation({
    mutationFn: async ({
      titleId,
      field,
      accepted,
      revision,
    }: {
      titleId: string;
      field: string;
      accepted: boolean;
      /** The title revision of the assessment the curator decided on. */
      revision: string;
    }) => {
      const result = await replaceTitleEnrichmentOmission({
        path: {
          titleId,
          field,
        },
        headers: ifMatch(revision),
        body: {
          accepted,
        },
      });
      if (result.error || !result.data) {
        // A stale decision reloads the title so the curator can decide again on what is current.
        if (result.response?.status === 412) void invalidateTitle(cache, titleId);
        throw new Error(
          result.response?.status === 412
            ? 'This title changed while you were reviewing it. Refresh the decision before trying again.'
            : 'Could not save this decision. Your current choices are unchanged; try again.',
        );
      }
      return result.data;
    },
    onSuccess: async (data, { titleId }) => {
      await cacheAcceptedTitle(cache, data);
      await invalidateTitle(cache, titleId);
    },
  });
}
