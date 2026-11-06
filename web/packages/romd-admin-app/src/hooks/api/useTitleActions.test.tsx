import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { writtenTitleFixture, writtenTitleViewFixture } from '../../test/msw/fixtures/enrichment';
import { TitleConflictError } from './titleConflict';
import { catalogSearchKeys } from './useCatalogSearch';
import {
  useDeleteTitleMedia,
  useMergeTitles,
  useMoveGame,
  useSetTitlesTracking,
  useSetTitleTracking,
  useUploadTitleMedia,
} from './useTitleActions';
import { titleDetailKeys, titleDetailQuery } from './useTitleDetail';
import { trackedCollectionKeys } from './useTrackedCollection';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    replaceTitleTracking: vi.fn(),
    batchSetTitleTracking: vi.fn(),
    deleteTitleMedia: vi.fn(),
    getTitle: vi.fn(),
    mergeTitles: vi.fn(),
    moveGame: vi.fn(),
    uploadTitleMedia: vi.fn(),
  };
});

import {
  batchSetTitleTracking,
  deleteTitleMedia,
  getTitle,
  mergeTitles,
  moveGame,
  replaceTitleTracking,
  uploadTitleMedia,
} from '@romd/admin-api-client';

const mockSetTitleTracking = replaceTitleTracking as ReturnType<typeof vi.fn>;
const mockBatchSetTitleTracking = batchSetTitleTracking as ReturnType<typeof vi.fn>;
const mockDeleteTitleMedia = deleteTitleMedia as ReturnType<typeof vi.fn>;
const mockGetTitle = getTitle as ReturnType<typeof vi.fn>;
const mockMergeTitles = mergeTitles as ReturnType<typeof vi.fn>;
const mockMoveGame = moveGame as ReturnType<typeof vi.fn>;
const mockUploadTitleMedia = uploadTitleMedia as ReturnType<typeof vi.fn>;

function createHarness() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  queryClient.setQueryData(catalogSearchKeys.all, 'catalog');
  queryClient.setQueryData(trackedCollectionKeys.all, 'tracked');
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return {
    queryClient,
    wrapper,
  };
}

describe('tracking query invalidation', () => {
  beforeEach(() => {
    mockSetTitleTracking.mockResolvedValue({
      data: undefined,
      error: undefined,
    });
    mockBatchSetTitleTracking.mockResolvedValue({
      data: undefined,
      error: undefined,
    });
  });

  it('invalidates catalog and tracked collection queries after a single tracking mutation', async () => {
    const { queryClient, wrapper } = createHarness();
    const { result } = renderHook(() => useSetTitleTracking(), {
      wrapper,
    });

    await act(() =>
      result.current.mutateAsync({
        titleId: 'title-1',
        tracked: true,
      }),
    );

    expect(queryClient.getQueryState(catalogSearchKeys.all)?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(trackedCollectionKeys.all)?.isInvalidated).toBe(true);
  });

  it('invalidates catalog and tracked collection queries after a bulk tracking mutation', async () => {
    const { queryClient, wrapper } = createHarness();
    const { result } = renderHook(() => useSetTitlesTracking(), {
      wrapper,
    });

    await act(() =>
      result.current.mutateAsync({
        titleIds: [
          'title-1',
          'title-2',
        ],
        tracked: false,
      }),
    );

    expect(queryClient.getQueryState(catalogSearchKeys.all)?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(trackedCollectionKeys.all)?.isInvalidated).toBe(true);
  });
  it('sends canonical pins and explicit reset without changing tracking', async () => {
    const { wrapper } = createHarness();
    const { result } = renderHook(() => useSetTitleTracking(), {
      wrapper,
    });
    await act(() =>
      result.current.mutateAsync({
        titleId: 'title-1',
        tracked: true,
        pinnedCatalogReleaseId: 'release-1',
      }),
    );
    expect(mockSetTitleTracking).toHaveBeenLastCalledWith({
      path: {
        titleId: 'title-1',
      },
      body: {
        tracked: true,
        pinnedCatalogReleaseId: 'release-1',
      },
    });
    await act(() =>
      result.current.mutateAsync({
        titleId: 'title-1',
        tracked: true,
        pinnedCatalogReleaseId: null,
      }),
    );
    expect(mockSetTitleTracking).toHaveBeenLastCalledWith({
      path: {
        titleId: 'title-1',
      },
      body: {
        tracked: true,
        pinnedCatalogReleaseId: null,
      },
    });
  });
});

describe('title preconditions', () => {
  const shown = (titleId: string, revision: string) => ({
    id: titleId,
    revision,
    systemKey: 'snes',
    name: titleId,
    enrichmentStatus: 'Completed',
  });
  const media = {
    id: 'media-1',
    type: 'Cover',
    url: '/media/media-1',
    sourceId: 'user',
    isPrimary: false,
  };

  it('chains each upload from the ETag the previous upload returned', async () => {
    const { queryClient, wrapper } = createHarness();
    queryClient.setQueryData(titleDetailKeys.detail('title-1'), shown('title-1', 'rev-1'));
    mockUploadTitleMedia
      .mockResolvedValueOnce({
        data: media,
        response: new Response(null, {
          headers: {
            ETag: '"rev-2"',
          },
        }),
      })
      .mockResolvedValueOnce({
        data: media,
        response: new Response(null, {
          headers: {
            ETag: '"rev-3"',
          },
        }),
      });
    const { result } = renderHook(() => useUploadTitleMedia(), {
      wrapper,
    });
    const file = new File(
      [
        'png',
      ],
      'cover.png',
    );

    await act(() =>
      result.current.mutateAsync({
        titleId: 'title-1',
        file,
        type: 'cover',
      }),
    );
    await act(() =>
      result.current.mutateAsync({
        titleId: 'title-1',
        file,
        type: 'cover',
      }),
    );

    expect(mockUploadTitleMedia.mock.calls.map(([options]) => options.headers)).toEqual([
      {
        'If-Match': '"rev-1"',
      },
      {
        'If-Match': '"rev-2"',
      },
    ]);
  });

  it('rejects a stale one-click write with a title conflict and reloads the title', async () => {
    const { queryClient, wrapper } = createHarness();
    queryClient.setQueryData(titleDetailKeys.detail('title-1'), shown('title-1', 'rev-1'));
    mockDeleteTitleMedia.mockResolvedValue({
      error: {
        status: 412,
      },
      response: new Response(null, {
        status: 412,
      }),
    });
    const { result } = renderHook(() => useDeleteTitleMedia(), {
      wrapper,
    });

    await expect(
      act(() =>
        result.current.mutateAsync({
          titleId: 'title-1',
          mediaId: 'media-1',
        }),
      ),
    ).rejects.toBeInstanceOf(TitleConflictError);

    expect(mockDeleteTitleMedia.mock.lastCall?.[0].headers).toEqual({
      'If-Match': '"rev-1"',
    });
    expect(queryClient.getQueryState(titleDetailKeys.detail('title-1'))?.isInvalidated).toBe(true);
  });

  it('moves a game under its title without a precondition', async () => {
    const { wrapper } = createHarness();
    mockMoveGame.mockResolvedValue({
      data: {
        gameId: 'game-1',
        titleCreated: false,
        oldTitleDeleted: false,
      },
      response: new Response(),
    });
    const { result } = renderHook(() => useMoveGame(), {
      wrapper,
    });

    await act(() =>
      result.current.mutateAsync({
        titleId: 'title-1',
        gameId: 'game-1',
        targetTitleId: 'title-2',
      }),
    );

    expect(mockMoveGame).toHaveBeenCalledWith({
      path: {
        titleId: 'title-1',
        gameId: 'game-1',
      },
      body: {
        targetTitleId: 'title-2',
      },
    });
    expect(mockGetTitle).not.toHaveBeenCalled();
  });
});

describe('merge preconditions', () => {
  const shown = (titleId: string, revision: string) =>
    writtenTitleFixture({
      id: titleId,
      revision,
      systemKey: 'snes',
      name: titleId,
      enrichmentStatus: 'Completed',
    });
  const problem = (status: number, errorCode: string) => ({
    error: {
      status,
      errorCode,
      traceId: 'trace',
    },
    response: new Response(null, {
      status,
    }),
  });
  const merge = {
    sourceTitleId: 'title-1',
    sourceRevision: 'source-1',
    targetTitleId: 'title-2',
  };

  beforeEach(() => {
    mockGetTitle.mockReset();
    mockMergeTitles.mockReset();
  });

  it('sends the target revision the curator saw and never reads the target at click time', async () => {
    const { wrapper } = createHarness();
    mockMergeTitles.mockResolvedValue({
      data: shown('title-2', 'rev-3'),
      response: new Response(),
    });
    const { result } = renderHook(() => useMergeTitles(), {
      wrapper,
    });

    await act(() =>
      result.current.mutateAsync({
        ...merge,
        targetRevision: 'rev-2',
      }),
    );

    expect(mockMergeTitles).toHaveBeenCalledWith({
      path: {
        titleId: 'title-2',
      },
      headers: {
        'If-Match': '"rev-2"',
      },
      body: {
        sourceTitleId: 'title-1',
        sourceTitleRevision: 'source-1',
      },
    });
    expect(mockGetTitle).not.toHaveBeenCalled();
  });

  it.each([
    [
      'Titles.MergeSourceChanged',
      true,
    ],
    [
      'Catalog.PlatformMismatch',
      false,
    ],
  ])('treats a 409 %s as a title conflict only for a changed source', async (errorCode, conflict) => {
    const { wrapper } = createHarness();
    mockMergeTitles.mockResolvedValue(problem(409, errorCode));
    mockGetTitle.mockResolvedValue({
      data: shown('title-2', 'rev-2'),
    });
    const { result } = renderHook(() => useMergeTitles(), {
      wrapper,
    });

    const failure = await act(() =>
      result.current
        .mutateAsync({
          ...merge,
          targetRevision: 'rev-2',
        })
        .then(
          () => undefined,
          (reason: unknown) => reason,
        ),
    );

    expect(failure).toBeInstanceOf(Error);
    expect(failure instanceof TitleConflictError).toBe(conflict);
  });

  it('refetches a stale target nothing observes, so the retry sends its new revision and succeeds', async () => {
    const { queryClient, wrapper } = createHarness();
    mockGetTitle
      .mockResolvedValueOnce({
        data: shown('title-2', 'rev-2'),
      })
      .mockResolvedValueOnce({
        data: shown('title-2', 'rev-3'),
      });
    // The curator saw rev-2 through the merge form's target query, which nothing observes any more.
    await queryClient.fetchQuery(titleDetailQuery('title-2'));
    mockMergeTitles
      .mockResolvedValueOnce(problem(412, 'Titles.PreconditionFailed'))
      .mockResolvedValueOnce({
        data: shown('title-2', 'rev-4'),
        response: new Response(),
      });
    const { result } = renderHook(() => useMergeTitles(), {
      wrapper,
    });

    await expect(
      act(() =>
        result.current.mutateAsync({
          ...merge,
          targetRevision: 'rev-2',
        }),
      ),
    ).rejects.toBeInstanceOf(TitleConflictError);
    const refreshed = queryClient.getQueryData<{
      revision: string;
    }>(titleDetailKeys.detail('title-2'));
    expect(refreshed?.revision).toBe('rev-3');

    await act(() =>
      result.current.mutateAsync({
        ...merge,
        targetRevision: refreshed?.revision ?? '',
      }),
    );

    expect(mockMergeTitles.mock.calls.map(([options]) => options.headers)).toEqual([
      {
        'If-Match': '"rev-2"',
      },
      {
        'If-Match': '"rev-3"',
      },
    ]);
    expect(queryClient.getQueryData(titleDetailKeys.detail('title-2'))).toEqual(
      writtenTitleViewFixture({
        id: 'title-2',
        name: 'title-2',
        revision: 'rev-4',
      }),
    );
  });
});
