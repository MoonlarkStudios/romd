import type { HubConnection, LibraryUpdatedRealtimePayload } from '@romd/admin-api-client';
import { createSystemHubConnection } from '@romd/admin-api-client';
import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { getAuthToken } from '../../api/client';
import { coverageStatsKeys } from '../api/useCoverageStats';
import { healthStatsKeys } from '../api/useHealthStats';
import { storageStatsKeys } from '../api/useStorageStats';
import { libraryKeys } from '../api/useUsers';
import { createStatsInvalidationScheduler } from './statsInvalidation';

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
    const stats = createStatsInvalidationScheduler(queryClient);

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
        stats.invalidate(storageStatsKeys.all);
      });
      connection.on('CoverageStatsChanged', () => {
        stats.invalidate(coverageStatsKeys.all);
      });
      connection.on('HealthStatsChanged', () => {
        stats.invalidate(healthStatsKeys.all);
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
        stats.invalidate(storageStatsKeys.all);
        stats.invalidate(coverageStatsKeys.all);
        stats.invalidate(healthStatsKeys.all);
        queryClient.invalidateQueries({ queryKey: libraryKeys.all });
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
      stats.dispose();
      mountedRef.current = false;
      const conn = connectionRef.current;
      if (conn) {
        conn.stop();
        connectionRef.current = null;
      }
      setPollingFallback(false);
    };
  }, [enabled, queryClient, setPollingFallback]);

  return { status };
}
