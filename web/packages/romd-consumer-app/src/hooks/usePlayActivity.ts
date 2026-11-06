import type { PageOfPlaySessionDto, UpsertPlaySessionRequest } from '@romd/consumer-api-client';
import {
  deleteConsumerPlaySession,
  deleteConsumerPlaySessions,
  getConsumerPlaySession,
  listConsumerPlaySessions,
  listConsumerRecentlyPlayed,
  replaceConsumerPlaySession,
} from '@romd/consumer-api-client';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

export const playActivityQueryKeys = {
  sessions: [
    'consumer',
    'playActivity',
    'sessions',
  ] as const,
  session: (sessionId: string) =>
    [
      'consumer',
      'playActivity',
      'session',
      sessionId,
    ] as const,
  recentlyPlayed: [
    'consumer',
    'playActivity',
    'recentlyPlayed',
  ] as const,
};

export async function putPlaySessionSnapshot(
  sessionId: string,
  body: UpsertPlaySessionRequest,
): Promise<void> {
  const response = await replaceConsumerPlaySession({
    path: {
      sessionId,
    },
    body,
  });
  if (response.error || !response.data) {
    throw new Error('Unable to store play activity');
  }
}

export function useRecentlyPlayed(limit = 18) {
  return useQuery({
    queryKey: playActivityQueryKeys.recentlyPlayed,
    queryFn: async () => {
      const response = await listConsumerRecentlyPlayed({
        query: {
          limit,
        },
      });
      if (response.error || !response.data) {
        throw new Error('Unable to load recently played titles');
      }
      return response.data.items;
    },
  });
}

export function usePlaySessions(limit = 50) {
  return useInfiniteQuery({
    queryKey: playActivityQueryKeys.sessions,
    queryFn: async ({ pageParam }) => {
      const response = await listConsumerPlaySessions({
        query: {
          limit,
          cursor: pageParam,
        },
      });
      if (response.error || !response.data) {
        throw new Error('Unable to load play sessions');
      }
      return response.data as PageOfPlaySessionDto;
    },
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => (page.hasNextPage ? (page.nextCursor ?? undefined) : undefined),
  });
}

export function usePlaySession(sessionId: string | undefined) {
  return useQuery({
    queryKey: playActivityQueryKeys.session(sessionId ?? ''),
    queryFn: async () => {
      if (!sessionId) {
        throw new Error('Session ID is required');
      }
      const response = await getConsumerPlaySession({
        path: {
          sessionId,
        },
      });
      if (response.error || !response.data) {
        throw new Error('Unable to load play session');
      }
      return response.data;
    },
    enabled: Boolean(sessionId),
  });
}

export function useDeletePlaySession() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (sessionId: string) => {
      const response = await deleteConsumerPlaySession({
        path: {
          sessionId,
        },
      });
      if (response.error) {
        throw new Error('Unable to delete play session');
      }
    },
    onSuccess: async (_, sessionId) => {
      queryClient.removeQueries({
        queryKey: playActivityQueryKeys.session(sessionId),
      });
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: playActivityQueryKeys.sessions,
        }),
        queryClient.invalidateQueries({
          queryKey: playActivityQueryKeys.recentlyPlayed,
        }),
      ]);
    },
  });
}

export function useClearPlaySessions() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => {
      const response = await deleteConsumerPlaySessions();
      if (response.error) {
        throw new Error('Unable to clear play history');
      }
    },
    onSuccess: async () => {
      queryClient.removeQueries({
        queryKey: [
          'consumer',
          'playActivity',
          'session',
        ],
      });
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: playActivityQueryKeys.sessions,
        }),
        queryClient.invalidateQueries({
          queryKey: playActivityQueryKeys.recentlyPlayed,
        }),
      ]);
    },
  });
}
