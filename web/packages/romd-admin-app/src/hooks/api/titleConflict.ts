import type { QueryClient } from '@tanstack/react-query';
import { type TitleDetailView, titleDetailKeys, titleDetailQuery } from './useTitleDetail';

/** A title write carried a stale revision (412); the server wrote nothing. */
export class TitleConflictError extends Error {
  constructor() {
    super('The title changed since this draft started.');
    this.name = 'TitleConflictError';
  }
}

/**
 * The `If-Match` header for a title revision. A title's ETag is its `revision` in exactly one pair of double
 * quotes (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency").
 */
export function ifMatch(revision: string) {
  return {
    'If-Match': `"${revision}"`,
  };
}

/** The revision a strong title ETag carries: the inverse of {@link ifMatch}'s quoting. */
export function revisionFromETag(etag: string | null | undefined): string | undefined {
  return etag && etag.length > 1 && etag.startsWith('"') && etag.endsWith('"')
    ? etag.slice(1, -1)
    : undefined;
}

/** Unwraps a title write's response: a stale revision rejects with {@link TitleConflictError}. */
export function titleWriteResult<T>(
  result: {
    data?: T;
    error?: unknown;
    response?: Response;
  },
  failure: string,
): T {
  if (result.response?.status === 412) throw new TitleConflictError();
  if (result.error || result.data === undefined) throw new Error(failure);
  return result.data;
}

/**
 * The revision of the title the page shows, for one-click writes. It is the cached representation; only when the
 * cache is empty may it fetch, since a precondition compared with a just-fetched revision would hide changes the
 * curator never saw.
 */
export async function shownTitleRevision(cache: QueryClient, titleId: string): Promise<string> {
  return (await cache.ensureQueryData(titleDetailQuery(titleId))).revision;
}

/**
 * Records the title revision a write returned so the next write sends it, first cancelling reads that started
 * before the write and would otherwise restore the older revision.
 */
export async function storeTitleRevision(
  cache: QueryClient,
  titleId: string,
  revision: string | undefined,
): Promise<void> {
  if (!revision) return;
  await cache.cancelQueries({
    queryKey: titleDetailKeys.detail(titleId),
  });
  cache.setQueryData<TitleDetailView>(titleDetailKeys.detail(titleId), (current) =>
    current
      ? {
          ...current,
          revision,
        }
      : current,
  );
}
