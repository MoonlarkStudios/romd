import { screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../test/utils/render';
import { Shelves } from './Shelves';

vi.mock('../hooks/useBrowserPlaybackCapability', () => ({
  useBrowserPlaybackAvailability: vi.fn(() => ({
    available: true,
    reason: null,
    loading: false,
  })),
}));

vi.mock('../hooks/usePlayActivity', () => ({ useRecentlyPlayed: vi.fn(() => ({ data: [], isError: false })) }));

vi.mock('../hooks/useConsumerLibrary', () => ({
  useCurrentLibrary: vi.fn(),
  useCollections: vi.fn(),
  useCatalogSearch: vi.fn(),
  useCollectionTitles: vi.fn(),
}));

import { useCatalogSearch, useCollections, useCollectionTitles, useCurrentLibrary } from '../hooks/useConsumerLibrary';
import { useRecentlyPlayed } from '../hooks/usePlayActivity';

describe('Shelves', () => {
  beforeEach(() => {
    vi.stubGlobal('IntersectionObserver', class {
      observe() {}
      unobserve() {}
      disconnect() {}
    });
    vi.mocked(useRecentlyPlayed).mockReturnValue({ data: [], isError: false } as unknown as ReturnType<typeof useRecentlyPlayed>);
    vi.mocked(useCurrentLibrary).mockReturnValue({
      data: {
        name: 'Player Library',
        counts: { ownedTitleCount: '42' },
        platforms: [
          {
            id: 'platform-1',
            name: 'Super Nintendo',
            count: '8',
          },
        ],
        genres: [
          {
            id: 'genre-1',
            name: 'Adventure',
            count: '12',
          },
        ],
        featuredCollections: [],
      },
      isLoading: false,
      isError: false,
    } as ReturnType<typeof useCurrentLibrary>);

    vi.mocked(useCollections).mockReturnValue({
      data: [
        {
          id: 'collection-1',
          name: 'SNES Favorites',
          description: 'Top shelf',
          itemCount: '12',
        },
      ],
    } as ReturnType<typeof useCollections>);

    vi.mocked(useCatalogSearch).mockImplementation((params) => {
      const itemName = params.completeness === 'complete' ? 'Super Metroid' : 'Chrono Trigger';

      return {
        data: {
          pages: [
            {
              items: [
                {
                  id: params.completeness === 'complete' ? 'title-ready' : 'title-rated',
                  system: { key: 'snes', name: 'SNES', compactLabel: 'SNES' },
                  name: itemName,
                  coverUrl: null,
                  genre: 'RPG',
                  releaseDate: '1995-01-01',
                  rating: '9.6',
                  releaseCount: '1',
                  defaultReleaseId: 'release-1',
                },
              ],
              nextCursor: null,
              hasNextPage: false,
            },
          ],
        },
        isLoading: false,
      } as ReturnType<typeof useCatalogSearch>;
    });

    vi.mocked(useCollectionTitles).mockReturnValue({
      data: {
        pages: [
          {
            items: [
              {
                id: 'title-collection',
                system: { key: 'snes', name: 'SNES', compactLabel: 'SNES' },
                name: 'EarthBound',
                coverUrl: null,
                genre: 'RPG',
                releaseDate: '1995-06-05',
                rating: '9.1',
                releaseCount: '1',
                defaultReleaseId: 'release-2',
              },
            ],
            nextCursor: null,
            hasNextPage: false,
          },
        ],
      },
    } as ReturnType<typeof useCollectionTitles>);
  });

  it('renders discovery, collections, and distinct game choices', () => {
    render(<Shelves />);

    expect(screen.getByRole('heading', { level: 1, name: 'Chrono Trigger' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'SNES Favorites' })).toHaveAttribute('href', '/collections/collection-1');
    expect(screen.queryByRole('heading', { name: 'Shelves' })).not.toBeInTheDocument();
    expect(screen.queryByText('See all')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: /explore your games/i })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: /top rated/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: /browse by genre/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: /browse by system/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /play/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/your next great game/i)).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'View game' })).toHaveAttribute('href', '/titles/title-rated');
    expect(screen.getByRole('link', { name: 'EarthBound' })).toBeInTheDocument();
  });

  it('puts recently played first and avoids repeating those games in discovery', () => {
    const title = { id: 'title-ready', name: 'Super Metroid', system: { key: 'snes', name: 'SNES', compactLabel: 'SNES' }, defaultReleaseId: 'release-1' };
    vi.mocked(useRecentlyPlayed).mockReturnValue({ data: [title], isError: false } as unknown as ReturnType<typeof useRecentlyPlayed>);
    render(<Shelves />);
    expect(screen.getAllByRole('heading', { level: 2 })[0]).toHaveTextContent('Recently Played');
    expect(screen.queryByRole('heading', { name: 'Explore your games' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Play Super Metroid' })).not.toBeInTheDocument();
    for (const link of screen.getAllByRole('link', { name: 'Super Metroid' })) {
      expect(link).toHaveAttribute('href', '/titles/title-ready');
    }
  });

  it('keeps a featured game when all top rated games have been played', () => {
    vi.mocked(useRecentlyPlayed).mockReturnValue({
      data: [{ id: 'title-rated', name: 'Chrono Trigger', system: { key: 'snes', name: 'SNES', compactLabel: 'SNES' } }],
      isError: false,
    } as unknown as ReturnType<typeof useRecentlyPlayed>);
    render(<Shelves />);
    expect(screen.getByRole('heading', { level: 1, name: 'Chrono Trigger' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'View game' })).toHaveAttribute('href', '/titles/title-rated');
  });

  it('features the three highest reviewed games even when recently played', () => {
    vi.mocked(useCatalogSearch).mockImplementation((params) => ({
      data: { pages: [{ items: params.sortBy === 'rating' ? [
        { id: 'first', name: 'First', system: { key: 'snes', name: 'SNES', compactLabel: 'SNES' }, rating: 99 },
        { id: 'second', name: 'Second', system: { key: 'snes', name: 'SNES', compactLabel: 'SNES' }, rating: 98 },
        { id: 'third', name: 'Third', system: { key: 'snes', name: 'SNES', compactLabel: 'SNES' }, rating: 97 },
        { id: 'fourth', name: 'Fourth', system: { key: 'snes', name: 'SNES', compactLabel: 'SNES' }, rating: 96 },
      ] : [] }] }, isLoading: false,
    } as unknown as ReturnType<typeof useCatalogSearch>));
    vi.mocked(useRecentlyPlayed).mockReturnValue({ data: [{ id: 'first', name: 'First', system: { key: 'snes', name: 'SNES', compactLabel: 'SNES' } }], isError: false } as unknown as ReturnType<typeof useRecentlyPlayed>);
    render(<Shelves />);
    expect(screen.getByRole('button', { name: 'Show First' })).toHaveAttribute('aria-current', 'true');
    expect(screen.getByRole('button', { name: 'Show Second' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show Third' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Show Fourth' })).toBeNull();
    expect(screen.getAllByRole('link', { name: 'View game' })).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Next featured game' })).toBeInTheDocument();
  });

  it('does not render placeholder shelves or future-only badges', () => {
    render(<Shelves />);

    expect(screen.queryByText(/nothing in progress yet/i)).not.toBeInTheDocument();
    expect(screen.queryByText('Soon')).not.toBeInTheDocument();
    expect(screen.queryByText(/unplayed/i)).not.toBeInTheDocument();
  });
});
