import type { QueryClient } from '@tanstack/react-query';

/** What one admin tab tells the others after invalidating caches for a change it made. */
export type CrossTabInvalidation =
  | {
      kind: 'title';
      titleId: string;
    }
  | {
      kind: 'assessments';
    };

const channelName = 'romd-admin-cache';
const channels = new WeakMap<QueryClient, BroadcastChannel>();

function parseInvalidation(data: unknown): CrossTabInvalidation | undefined {
  if (typeof data !== 'object' || data === null) return undefined;
  const { kind, titleId } = data as {
    kind?: unknown;
    titleId?: unknown;
  };
  if (kind === 'assessments')
    return {
      kind,
    };
  return kind === 'title' && typeof titleId === 'string' && titleId !== ''
    ? {
        kind,
        titleId,
      }
    : undefined;
}

/** Tells the other open tabs of this admin origin to invalidate what this tab just invalidated. */
export function announceInvalidation(cache: QueryClient, message: CrossTabInvalidation): void {
  channels.get(cache)?.postMessage(message);
}

/**
 * Joins the cross-tab channel for one query client. `receive` applies another tab's invalidation to this tab only and
 * must not announce it again. Returns the cleanup that leaves the channel; without BroadcastChannel, each tab keeps
 * refreshing on its own.
 */
export function joinCrossTabInvalidation(
  cache: QueryClient,
  receive: (message: CrossTabInvalidation) => void,
): () => void {
  if (typeof BroadcastChannel === 'undefined') return () => undefined;
  const channel = new BroadcastChannel(channelName);
  channel.onmessage = (event: MessageEvent<unknown>) => {
    const message = parseInvalidation(event.data);
    if (message) receive(message);
  };
  channels.set(cache, channel);
  return () => {
    if (channels.get(cache) === channel) channels.delete(cache);
    channel.close();
  };
}
