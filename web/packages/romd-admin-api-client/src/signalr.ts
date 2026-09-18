import type { HubConnection } from '@microsoft/signalr';

export type { HubConnection };

export interface LibraryUpdatedRealtimePayload {
  libraryId: string;
  name: string;
  needsMaterialization: boolean;
  itemCount: number;
  configurationState: string;
}

export interface HubOptions {
  accessTokenFactory: () => string | Promise<string>;
}

/** @deprecated Use {@link HubOptions} instead. */
export type JobHubOptions = HubOptions;

/**
 * Creates a SignalR HubConnection to the `/hubs/jobs` endpoint.
 * The `@microsoft/signalr` module is lazy-loaded to keep the initial bundle small.
 *
 * @example
 * ```ts
 * const connection = await createJobHubConnection({
 *   accessTokenFactory: () => getAuthToken() ?? '',
 * });
 * connection.on('JobUpdated', (dto) => { ... });
 * await connection.start();
 * await connection.invoke('SubscribeToJobFeed');
 * ```
 */
export async function createJobHubConnection(
  options: HubOptions,
): Promise<HubConnection> {
  const signalr = await import('@microsoft/signalr');

  return new signalr.HubConnectionBuilder()
    .withUrl('/hubs/jobs', {
      accessTokenFactory: options.accessTokenFactory,
    })
    .withAutomaticReconnect()
    .build();
}

/**
 * Creates a SignalR HubConnection to the `/hubs/system` endpoint.
 * Receives lightweight invalidation signals for dashboard stats.
 *
 * @example
 * ```ts
 * const connection = await createSystemHubConnection({
 *   accessTokenFactory: () => getAuthToken() ?? '',
 * });
 * connection.on('StorageStatsChanged', () => { ... });
 * await connection.start();
 * await connection.invoke('SubscribeToStats');
 * ```
 */
export async function createSystemHubConnection(
  options: HubOptions,
): Promise<HubConnection> {
  const signalr = await import('@microsoft/signalr');

  return new signalr.HubConnectionBuilder()
    .withUrl('/hubs/system', {
      accessTokenFactory: options.accessTokenFactory,
    })
    .withAutomaticReconnect()
    .build();
}
