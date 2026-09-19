import type { QueryClient, QueryKey } from '@tanstack/react-query';

export const STATS_REFRESH_INTERVAL = 5_000;

/** Fixed windows keep long imports fresh without postponing the final refresh. */
export function createStatsInvalidationScheduler(queryClient: QueryClient) {
  const pending = new Map<string, QueryKey>();
  let timer: ReturnType<typeof setTimeout> | undefined;
  let disposed = false;

  function schedule() {
    if (!disposed && timer === undefined && pending.size > 0) {
      timer = setTimeout(flush, STATS_REFRESH_INTERVAL);
    }
  }

  function flush() {
    timer = undefined;
    for (const [id, queryKey] of pending) {
      // Keep the dirty bit until the previous request finishes. Cancelling and
      // restarting expensive aggregates can otherwise starve them during imports.
      if (queryClient.isFetching({ queryKey }) > 0) continue;
      pending.delete(id);
      void queryClient.invalidateQueries({ queryKey }, { cancelRefetch: false });
    }
    schedule();
  }

  return {
    invalidate(queryKey: QueryKey) {
      if (disposed) return;
      pending.set(JSON.stringify(queryKey), queryKey);
      schedule();
    },
    dispose() {
      disposed = true;
      clearTimeout(timer);
      // Preserve staleness for the next mount without issuing background work.
      for (const queryKey of pending.values()) {
        void queryClient.invalidateQueries({ queryKey, refetchType: 'none' });
      }
      pending.clear();
    },
  };
}
