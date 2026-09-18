import type { HubConnection, JobDto } from '@romd/admin-api-client';
import { createJobHubConnection } from '@romd/admin-api-client';
import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { getAuthToken } from '../../api/client';
import { jobKeys } from '../api/useJobs';

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting' | 'polling-fallback';

/**
 * Connects to the SignalR `/hubs/jobs` hub and pushes `JobUpdated` events
 * directly into React Query cache. Falls back to polling when disconnected.
 *
 * Must be mounted once at the AppLayout level while authenticated.
 */
export function useJobSocket(enabled: boolean) {
  const queryClient = useQueryClient();
  const connectionRef = useRef<HubConnection | null>(null);
  const mountedRef = useRef(true);

  // Reactive status for UI (connection dot). Only changes on connect/disconnect transitions.
  const [status, setStatus] = useState<ConnectionStatus>('disconnected');

  // Enable/disable polling fallback on the jobs list query
  const setPollingFallback = useCallback(
    (active: boolean) => {
      queryClient.setQueryDefaults(jobKeys.list(), {
        refetchInterval: active ? 5000 : false,
        staleTime: active ? 0 : Number.POSITIVE_INFINITY,
      });
    },
    [queryClient],
  );

  const handleJobUpdated = useCallback(
    (dto: JobDto) => {
      // Update individual job cache
      queryClient.setQueryData(jobKeys.detail(dto.id), dto);

      // Update jobs list cache
      queryClient.setQueryData<JobDto[]>(jobKeys.list(), (old) => {
        if (!old) return [dto];
        const index = old.findIndex((j) => j.id === dto.id);
        if (index >= 0) {
          const next = [...old];
          next[index] = dto;
          return next;
        }
        return [dto, ...old];
      });
    },
    [queryClient],
  );

  useEffect(() => {
    mountedRef.current = true;

    if (!enabled) {
      const conn = connectionRef.current;
      if (conn) {
        conn.stop();
        connectionRef.current = null;
      }
      setStatus('disconnected');
      setPollingFallback(false);
      return;
    }

    let stopped = false;

    async function connect() {
      const token = getAuthToken();
      if (!token || stopped) return;

      setStatus('connecting');

      const connection = await createJobHubConnection({
        accessTokenFactory: () => getAuthToken() ?? '',
      });

      if (stopped) return;

      connectionRef.current = connection;

      connection.on('JobUpdated', handleJobUpdated);

      connection.onreconnecting(() => {
        if (!mountedRef.current) return;
        setStatus('reconnecting');
        setPollingFallback(true);
      });

      connection.onreconnected(() => {
        if (!mountedRef.current) return;
        setStatus('connected');
        setPollingFallback(false);
        // Rehydrate missed updates
        queryClient.invalidateQueries({ queryKey: jobKeys.all });
        // Re-subscribe to the all-jobs feed
        connection.invoke('SubscribeToJobFeed').catch(() => {});
      });

      connection.onclose(() => {
        if (!mountedRef.current) return;
        setStatus('polling-fallback');
        setPollingFallback(true);
      });

      try {
        await connection.start();
        if (stopped) {
          connection.stop();
          return;
        }

        setStatus('connected');
        setPollingFallback(false);

        await connection.invoke('SubscribeToJobFeed');
      } catch {
        if (!mountedRef.current) return;
        setStatus('polling-fallback');
        setPollingFallback(true);
      }
    }

    connect();

    return () => {
      stopped = true;
      mountedRef.current = false;
      const conn = connectionRef.current;
      if (conn) {
        conn.stop();
        connectionRef.current = null;
      }
      setPollingFallback(false);
    };
  }, [enabled, handleJobUpdated, queryClient, setPollingFallback]);

  return { status };
}
