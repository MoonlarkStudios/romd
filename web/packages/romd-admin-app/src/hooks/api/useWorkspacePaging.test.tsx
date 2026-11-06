import type {
  CollectionDetail,
  TitleDetail,
  TitleMediaRef,
  TitleRelease,
} from '@romd/admin-api-client';
import {
  listCollectionItems,
  listTitleMedia,
  listTitleReleaseSources,
  listTitleReleases,
} from '@romd/admin-api-client';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { normalizeCollectionDetail } from './useCollectionManagement';
import { normalizeTitleDetail } from './useTitleDetail';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  listCollectionItems: vi.fn(),
  listTitleMedia: vi.fn(),
  listTitleReleases: vi.fn(),
  listTitleReleaseSources: vi.fn(),
}));

const media = (id: string): TitleMediaRef => ({
  id,
  type: 'Screenshot',
  url: `/media/${id}`,
  sourceId: 'user',
  isPrimary: false,
});
const release = (id: string): TitleRelease => ({
  id,
  datId: 'dat-1',
  name: id,
  sources: {
    items: [],
    nextCursor: null,
    hasNextPage: false,
  },
});
const wireTitle = (): TitleDetail => ({
  id: 'title-1',
  revision: 'revision-1',
  systemKey: 'snes',
  name: 'A title',
  enrichmentStatus: 'None',
  media: {
    items: [
      media('media-1'),
    ],
    nextCursor: 'media-page-2',
    hasNextPage: true,
  },
  releases: {
    items: [
      {
        ...release('release-1'),
        sources: {
          items: [
            {
              datGameId: 'game-1',
              datId: 'dat-1',
              datName: 'DAT 1',
              gameName: 'A game',
            },
          ],
          nextCursor: 'source-page-2',
          hasNextPage: true,
        },
      },
    ],
    nextCursor: 'release-page-2',
    hasNextPage: true,
  },
});

describe('workspace complete relationship views', () => {
  beforeEach(() => vi.resetAllMocks());

  it('completes embedded title pages, including empty intermediate media and later release provenance', async () => {
    vi.mocked(listTitleMedia)
      .mockResolvedValueOnce({
        data: {
          items: [],
          nextCursor: 'media-page-3',
          hasNextPage: true,
        },
      })
      .mockResolvedValueOnce({
        data: {
          items: [
            media('media-2'),
          ],
          nextCursor: null,
          hasNextPage: false,
        },
      });
    vi.mocked(listTitleReleases).mockResolvedValue({
      data: {
        items: [
          release('release-2'),
        ],
        nextCursor: null,
        hasNextPage: false,
      },
    });
    vi.mocked(listTitleReleaseSources).mockResolvedValue({
      data: {
        items: [
          {
            datGameId: 'game-2',
            datId: 'dat-2',
            datName: 'DAT 2',
            gameName: 'A second assertion',
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      },
    });

    const result = await normalizeTitleDetail(wireTitle());

    expect(result.media.map((item) => item.id)).toEqual([
      'media-1',
      'media-2',
    ]);
    expect(result.releases.map((item) => item.id)).toEqual([
      'release-1',
      'release-2',
    ]);
    expect(result.releases[0].sources.map((item) => item.datGameId)).toEqual([
      'game-1',
      'game-2',
    ]);
    expect(listTitleMedia).toHaveBeenNthCalledWith(
      2,
      expect.objectContaining({
        path: {
          titleId: 'title-1',
        },
        query: {
          cursor: 'media-page-3',
          limit: 100,
        },
      }),
    );
    expect(listTitleReleaseSources).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({
        path: {
          titleId: 'title-1',
        },
        query: {
          releaseId: 'release-1',
          cursor: 'source-page-2',
          limit: 100,
        },
      }),
    );
  });

  it('rejects a late title feed failure instead of publishing its first page as complete', async () => {
    vi.mocked(listTitleMedia).mockResolvedValue({
      data: {
        items: [],
        nextCursor: null,
        hasNextPage: false,
      },
    });
    vi.mocked(listTitleReleases).mockResolvedValue({
      error: {
        title: 'Unavailable',
        status: 503,
      },
    });

    await expect(normalizeTitleDetail(wireTitle())).rejects.toThrow(
      'Could not load every title release.',
    );
  });

  it('completes collection membership before a caller builds a replacement order', async () => {
    const detail: CollectionDetail = {
      id: 'collection-1',
      name: 'All members',
      items: {
        items: [
          {
            titleId: 'first',
            titleName: 'First',
            systemKey: 'snes',
          },
        ],
        nextCursor: 'members-2',
        hasNextPage: true,
      },
    };
    vi.mocked(listCollectionItems)
      .mockResolvedValueOnce({
        data: {
          items: [],
          nextCursor: 'members-3',
          hasNextPage: true,
        },
      })
      .mockResolvedValueOnce({
        data: {
          items: [
            {
              titleId: 'last',
              titleName: 'Last',
              systemKey: 'snes',
            },
          ],
          nextCursor: null,
          hasNextPage: false,
        },
      });

    const result = await normalizeCollectionDetail(detail);

    expect(result.items.map((item) => item.titleId)).toEqual([
      'first',
      'last',
    ]);
    expect(listCollectionItems).toHaveBeenLastCalledWith({
      path: {
        collectionId: 'collection-1',
      },
      query: {
        cursor: 'members-3',
        limit: 100,
      },
    });
  });

  it('rejects repeated collection cursors instead of returning partial membership', async () => {
    const detail: CollectionDetail = {
      id: 'collection-1',
      name: 'Repeated page',
      items: {
        items: [],
        nextCursor: 'repeat',
        hasNextPage: true,
      },
    };
    vi.mocked(listCollectionItems).mockResolvedValue({
      data: {
        items: [],
        nextCursor: 'repeat',
        hasNextPage: true,
      },
    });

    await expect(normalizeCollectionDetail(detail)).rejects.toThrow('repeated a page cursor');
    expect(listCollectionItems).toHaveBeenCalledTimes(1);
  });
});
