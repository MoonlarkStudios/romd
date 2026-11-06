import {
  listTitleMedia,
  mergeTitles,
  replaceTitleContentRating,
  updateTitle,
  updateTitleEnrichment,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { writtenTitleFixture, writtenTitleViewFixture } from '../../test/msw/fixtures/enrichment';
import { finalPage } from '../../test/workspacePages';
import { catalogSearchKeys } from './useCatalogSearch';
import { useSetTitleContentRating } from './useContentRating';
import { useMergeTitles, useUpdateTitleMetadata } from './useTitleActions';
import { titleDetailKeys } from './useTitleDetail';
import { useSetFieldOverrides } from './useTitleEnrichment';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  listTitleMedia: vi.fn(),
  mergeTitles: vi.fn(),
  replaceTitleContentRating: vi.fn(),
  updateTitle: vi.fn(),
  updateTitleEnrichment: vi.fn(),
}));

function response<T>(data: T) {
  return {
    data,
    error: undefined,
    request: new Request('http://localhost'),
    response: new Response(),
  };
}

function harness() {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
      mutations: {
        retry: false,
      },
    },
  });
  const old = writtenTitleViewFixture({
    id: 'title-1',
    revision: 'rev-1',
  });
  cache.setQueryData(titleDetailKeys.detail('title-1'), old);
  cache.setQueryData(
    titleDetailKeys.detail('source'),
    writtenTitleViewFixture({
      id: 'source',
    }),
  );
  cache.setQueryData(catalogSearchKeys.all, 'catalog');
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  return {
    cache,
    old,
    wrapper,
  };
}

const media = {
  id: 'media-1',
  type: 'Cover',
  url: '/media/media-1',
  sourceId: 'user',
  isPrimary: true,
};
const accepted = () =>
  writtenTitleFixture({
    id: 'title-1',
    revision: 'rev-2',
    publisher: 'Accepted publisher',
    media: {
      items: [
        media,
      ],
      nextCursor: 'media-2',
      hasNextPage: true,
    },
  });

describe('accepted title writes and complete view caches', () => {
  beforeEach(() => vi.resetAllMocks());

  it.each([
    'rating',
    'override',
  ] as const)('normalizes Page-shaped %s results before caching the title view', async (kind) => {
    const { cache, wrapper } = harness();
    const wire = accepted();
    vi.mocked(replaceTitleContentRating).mockResolvedValue(response(wire));
    vi.mocked(updateTitleEnrichment).mockResolvedValue(response(wire));
    vi.mocked(listTitleMedia).mockResolvedValue(
      response(
        finalPage([
          {
            ...media,
            id: 'media-2',
          },
        ]),
      ),
    );
    const { result } = renderHook(
      () => {
        const rating = useSetTitleContentRating();
        const override = useSetFieldOverrides();
        return {
          rating,
          override,
        };
      },
      {
        wrapper,
      },
    );

    await act(() =>
      kind === 'rating'
        ? result.current.rating.mutateAsync({
            titleId: 'title-1',
            revision: 'rev-1',
            board: 'Esrb',
            code: 'E',
          })
        : result.current.override.mutateAsync({
            titleId: 'title-1',
            revision: 'rev-1',
            overrides: {
              Publisher: 'igdb',
            },
          }),
    );

    expect(cache.getQueryData(titleDetailKeys.detail('title-1'))).toMatchObject({
      revision: 'rev-2',
      media: [
        media,
        {
          ...media,
          id: 'media-2',
        },
      ],
      releases: [],
    });
    expect(listTitleMedia).toHaveBeenCalledExactlyOnceWith({
      path: {
        titleId: 'title-1',
      },
      query: {
        cursor: 'media-2',
        limit: 100,
      },
      signal: undefined,
    });
  });

  it.each([
    'merge',
    'update',
  ] as const)('preserves committed %s success when a later relationship page fails', async (kind) => {
    const { cache, old, wrapper } = harness();
    const wire = accepted();
    vi.mocked(mergeTitles).mockResolvedValue(response(wire));
    vi.mocked(updateTitle).mockResolvedValue(response(wire));
    vi.mocked(listTitleMedia).mockResolvedValue({
      error: {
        status: 503,
        title: 'Unavailable',
      },
      request: new Request('http://localhost'),
      response: new Response(null, {
        status: 503,
      }),
    });
    const { result } = renderHook(
      () => ({
        merge: useMergeTitles(),
        update: useUpdateTitleMetadata(),
      }),
      {
        wrapper,
      },
    );

    let written: unknown;
    await act(async () => {
      written =
        kind === 'merge'
          ? await result.current.merge.mutateAsync({
              sourceTitleId: 'source',
              sourceRevision: 'source-rev',
              targetTitleId: 'title-1',
              targetRevision: 'rev-1',
            })
          : await result.current.update.mutateAsync({
              titleId: 'title-1',
              revision: 'rev-1',
              metadata: {
                publisher: 'Accepted publisher',
              },
            });
    });

    expect(written).toEqual(wire);
    await waitFor(() =>
      expect(
        kind === 'merge' ? result.current.merge.isSuccess : result.current.update.isSuccess,
      ).toBe(true),
    );
    expect(cache.getQueryData(titleDetailKeys.detail('title-1'))).toEqual(old);
    expect(cache.getQueryState(titleDetailKeys.detail('title-1'))?.isInvalidated).toBe(true);
    expect(cache.getQueryState(catalogSearchKeys.all)?.isInvalidated).toBe(true);
    if (kind === 'merge')
      expect(cache.getQueryData(titleDetailKeys.detail('source'))).toBeUndefined();
  });
});
