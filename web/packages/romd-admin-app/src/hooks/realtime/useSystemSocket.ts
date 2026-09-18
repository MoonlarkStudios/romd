import type { HubConnection, LibraryUpdatedRealtimePayload } from '@romd/admin-api-client';
import { createSystemHubConnection } from '@romd/admin-api-client';
import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { getAuthToken } from '../../api/client';
import { coverageStatsKeys } from '../api/useCoverageStats';
import { healthStatsKeys } from '../api/useHealthStats';
import { storageStatsKeys } from '../api/useStorageStats';
import { libraryKeys } from '../api/useUsers';

type SystemSocketStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

const POLLING_INTERVAL = 30_000;

/**
 * Connects to the SignalR `/hubs/system` endpoint and invalidates
 * dashboard stat queries on invalidation signals. Falls back to
 * 30-second polling when disconnected.
 */
export function useSystemSocket(enabled: boolean) {
  const queryClient = useQueryClient();
  const connectionRef = useRef<HubConnection | null>(null);
  const mountedRef = useRef(true);
  const [status, setStatus] = useState<SystemSocketStatus>('disconnected');

  const setPollingFallback = useCallback(
    (active: boolean) => {
      const defaults = {
        refetchInterval: active ? POLLING_INTERVAL : (false as const),
        staleTime: active ? 0 : Number.POSITIVE_INFINITY,
      };
      queryClient.setQueryDefaults(storageStatsKeys.all, defaults);
      queryClient.setQueryDefaults(coverageStatsKeys.all, defaults);
      queryClient.setQueryDefaults(healthStatsKeys.all, defaults);
      queryClient.setQueryDefaults(libraryKeys.all, defaults);
    },
    [queryClient],
  );

  const invalidateAll = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: storageStatsKeys.all });
    queryClient.invalidateQueries({ queryKey: coverageStatsKeys.all });
    queryClient.invalidateQueries({ queryKey: healthStatsKeys.all });
    queryClient.invalidateQueries({ queryKey: libraryKeys.all });
  }, [queryClient]);

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

      const connection = await createSystemHubConnection({
        accessTokenFactory: () => getAuthToken() ?? '',
      });

      if (stopped) return;

      connectionRef.current = connection;

      connection.on('StorageStatsChanged', () => {
        queryClient.invalidateQueries({ queryKey: storageStatsKeys.all });
      });
      connection.on('CoverageStatsChanged', () => {
        queryClient.invalidateQueries({ queryKey: coverageStatsKeys.all });
      });
      connection.on('HealthStatsChanged', () => {
        queryClient.invalidateQueries({ queryKey: healthStatsKeys.all });
      });
      connection.on(
        'LibraryUpdated',
        (_payload: LibraryUpdatedRealtimePayload, _schemaVersion: number) => {
          queryClient.invalidateQueries({ queryKey: libraryKeys.all });
        },
      );

      connection.onreconnecting(() => {
        if (!mountedRef.current) return;
        setStatus('reconnecting');
        setPollingFallback(true);
      });

      connection.onreconnected(() => {
        if (!mountedRef.current) return;
        setStatus('connected');
        setPollingFallback(false);
        invalidateAll();
        connection.invoke('SubscribeToStats').catch(() => {});
      });

      connection.onclose(() => {
        if (!mountedRef.current) return;
        setStatus('disconnected');
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
        await connection.invoke('SubscribeToStats');
      } catch {
        if (!mountedRef.current) return;
        setStatus('disconnected');
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
  }, [enabled, queryClient, setPollingFallback, invalidateAll]);

  return { status };
}
