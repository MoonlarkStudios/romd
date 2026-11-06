import { QueryClient, type QueryKey, useQueryClient } from '@tanstack/react-query';
import { render, waitFor } from '@testing-library/react';
import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { QueryProvider } from '../../providers/QueryProvider';
import { joinCrossTabInvalidation } from './crossTabCache';
import {
  enrichmentWorkspaceKeys,
  invalidateEnrichmentAssessments,
} from './enrichmentWorkspaceCache';
import { invalidateTitle, shareInvalidationsAcrossTabs, titleSurfaceKeys } from './titleCache';
import { catalogSearchKeys } from './useCatalogSearch';
import { titleDetailKeys } from './useTitleDetail';

const titleUnion = (titleId: string): QueryKey[] => [
  [
    ...enrichmentWorkspaceKeys.all,
    'list',
    'NeedsReview',
    '',
  ],
  [
    ...enrichmentWorkspaceKeys.all,
    'counts',
    '',
  ],
  enrichmentWorkspaceKeys.title(titleId),
  ...titleSurfaceKeys(titleId),
];
const unrelated: QueryKey = titleDetailKeys.detail('other');

/** A tab's cache holding every key the title union covers, plus another title's detail. */
function tab(): QueryClient {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  for (const key of [
    ...titleUnion('turok'),
    unrelated,
  ])
    cache.setQueryData(key, {
      seeded: true,
    });
  return cache;
}
const invalidated = (cache: QueryClient, key: QueryKey) =>
  cache.getQueryState(key)?.isInvalidated ?? false;
const settle = () => new Promise((resolve) => setTimeout(resolve, 50));
/** Another tab posting on the shared channel. */
function otherTab(message: unknown) {
  const channel = new BroadcastChannel('romd-admin-cache');
  channel.postMessage(message);
  channel.close();
}

const leaves: (() => void)[] = [];
afterEach(() => {
  for (const leave of leaves.splice(0)) leave();
  vi.unstubAllGlobals();
});

describe('cross-tab invalidation', () => {
  it('invalidates the same title union in another tab, once, without echoing back', async () => {
    const first = tab();
    const second = tab();
    leaves.push(shareInvalidationsAcrossTabs(first), shareInvalidationsAcrossTabs(second));
    const firstCalls = vi.spyOn(first, 'invalidateQueries');
    const secondCalls = vi.spyOn(second, 'invalidateQueries');
    await invalidateTitle(first, 'turok');
    const direct = firstCalls.mock.calls.length;
    await waitFor(() => {
      for (const key of titleUnion('turok')) expect(invalidated(second, key)).toBe(true);
    });
    await settle();
    expect(firstCalls).toHaveBeenCalledTimes(direct);
    expect(secondCalls).toHaveBeenCalledTimes(direct);
    expect(invalidated(second, unrelated)).toBe(false);
  });

  it('follows another tab that invalidated every assessment', async () => {
    const first = tab();
    const second = tab();
    leaves.push(shareInvalidationsAcrossTabs(first), shareInvalidationsAcrossTabs(second));
    await invalidateEnrichmentAssessments(first);
    await waitFor(() => {
      expect(invalidated(second, enrichmentWorkspaceKeys.title('turok'))).toBe(true);
      expect(invalidated(second, catalogSearchKeys.all)).toBe(true);
    });
    expect(invalidated(second, unrelated)).toBe(false);
  });

  it('ignores messages that are not invalidations', async () => {
    const cache = tab();
    leaves.push(shareInvalidationsAcrossTabs(cache));
    for (const message of [
      'title',
      {
        kind: 'title',
      },
      {
        kind: 'title',
        titleId: '',
      },
      {
        kind: 'everything',
      },
      null,
    ])
      otherTab(message);
    await settle();
    for (const key of titleUnion('turok')) expect(invalidated(cache, key)).toBe(false);
  });

  it('keeps invalidating this tab when BroadcastChannel is unavailable', async () => {
    vi.stubGlobal('BroadcastChannel', undefined);
    const cache = tab();
    const leave = joinCrossTabInvalidation(cache, () => {
      throw new Error('nothing can arrive');
    });
    leaves.push(leave);
    await invalidateTitle(cache, 'turok');
    expect(invalidated(cache, titleDetailKeys.detail('turok'))).toBe(true);
    expect(leave).not.toThrow();
  });

  it('is shared by the app query provider for as long as it is mounted', async () => {
    let client: QueryClient | undefined;
    function Probe() {
      client = useQueryClient();
      return null;
    }
    const view = render(createElement(QueryProvider, null, createElement(Probe)));
    const key = titleDetailKeys.detail('turok');
    client?.setQueryData(key, {
      seeded: true,
    });
    otherTab({
      kind: 'title',
      titleId: 'turok',
    });
    await waitFor(() => expect(client && invalidated(client, key)).toBe(true));
    view.unmount();
    client?.setQueryData(key, {
      seeded: true,
    });
    otherTab({
      kind: 'title',
      titleId: 'turok',
    });
    await settle();
    expect(client && invalidated(client, key)).toBe(false);
  });
});
