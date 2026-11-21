import { getConsumerLibraryPublication } from '@romd/consumer-api-client';
import { type QueryClient, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef } from 'react';
import { useAuth } from '../contexts/AuthContext';

export function refreshLibraryQueries(cache: QueryClient) {
  // Reset infinite queries as well as single pages so cursors from an older publication
  // cannot keep old games in a later page or be sent with the new library.
  return cache.resetQueries({
    queryKey: [
      'consumer',
    ],
    predicate: (query) =>
      query.queryKey[1] !== 'currentUser' && query.queryKey[1] !== 'playbackTitle',
  });
}

export function useRefreshLibrary() {
  const cache = useQueryClient();
  return useCallback(
    () => refreshLibraryQueries(cache),
    [
      cache,
    ],
  );
}

export function LibraryPublicationObserver() {
  const { user, isAuthenticated } = useAuth();
  const cache = useQueryClient();
  const userId = isAuthenticated ? user?.id : undefined;
  const previous = useRef<{
    userId?: string;
    version?: string | null;
  }>({});
  const observed = useQuery({
    queryKey: [
      'library-publication',
      userId,
    ],
    enabled: !!userId,
    staleTime: 0,
    refetchInterval: 15_000,
    queryFn: async ({ signal }) => {
      const result = await getConsumerLibraryPublication({
        signal,
      });
      if (result.error || !result.data) throw new Error('Could not check for library updates.');
      return result.data.version;
    },
  });
  useEffect(() => {
    const old = previous.current;
    if (old.userId !== userId || observed.data !== undefined) {
      previous.current = {
        userId,
        version: observed.data,
      };
    }
    if (old.userId && old.userId !== userId) {
      void cache.resetQueries({
        queryKey: [
          'consumer',
        ],
      });
    } else if (observed.data !== undefined && old.version !== observed.data) {
      // Browse can already be reading an older snapshot before the first observation resolves.
      void refreshLibraryQueries(cache);
    }
  }, [
    userId,
    observed.data,
    cache,
  ]);
  return null;
}
