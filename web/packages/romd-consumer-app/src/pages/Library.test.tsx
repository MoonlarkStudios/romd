import { act, fireEvent, screen } from '@testing-library/react';
import { useLocation, useNavigate } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../test/utils/render';
import { Library } from './Library';

vi.mock('../hooks/useBrowserPlaybackCapability', () => ({
  useBrowserPlaybackAvailability: vi.fn(() => ({
    available: true,
    reason: null,
    loading: false,
  })),
}));

vi.mock('../hooks/useConsumerLibrary', () => ({
  useCurrentLibrary: vi.fn(),
  useCatalogSearch: vi.fn(),
}));

import { useCatalogSearch, useCurrentLibrary } from '../hooks/useConsumerLibrary';

function NavigationProbe() {
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <>
      <output aria-label="Current location">{location.search}</output>
      <button
        type="button"
        onClick={() => navigate(-1)}
      >
        Previous search
      </button>
    </>
  );
}

describe('Library', () => {
  afterEach(() => vi.useRealTimers());
  beforeEach(() => {
    vi.mocked(useCurrentLibrary).mockReturnValue({
      data: {
        name: 'Player Library',
        counts: {
          ownedTitleCount: '12',
          availableTitleCount: '10',
          collectionCount: '1',
        },
        platforms: [
          {
            id: 'platform-1',
            name: 'Nintendo Entertainment System',
            count: '4',
          },
        ],
        genres: [
          {
            id: 'genre-1',
            name: 'Platformer',
            count: '7',
          },
        ],
        featuredCollections: [
          {
            id: 'collection-1',
            name: 'Favorites',
            description: 'Top shelf',
            system: null,
            coverUrl: null,
            heroUrl: null,
            itemCount: '3',
          },
        ],
      },
      isError: false,
      isLoading: false,
    } as ReturnType<typeof useCurrentLibrary>);
    vi.mocked(useCatalogSearch).mockReturnValue({
      data: {
        pages: [
          {
            items: [
              {
                id: 'title-1',
                system: {
                  key: 'nes',
                  name: 'NES',
                  compactLabel: 'NES',
                },
                name: 'Super Mario Bros.',
                coverUrl: null,
                genre: 'Platformer',
                releaseDate: null,
                rating: null,
                releaseCount: '1',
                defaultReleaseId: 'release-1',
              },
            ],
            nextCursor: null,
            hasNextPage: false,
          },
        ],
      },
      isFetching: false,
      isError: false,
      isLoading: false,
      hasNextPage: false,
      isFetchingNextPage: false,
      fetchNextPage: vi.fn(),
    } as ReturnType<typeof useCatalogSearch>);
  });

  it('renders the library context, filters, and title links without play overlays', () => {
    render(<Library />, {
      withAuth: false,
    });

    expect(
      screen.getByRole('heading', {
        name: /browse games/i,
      }),
    ).toBeInTheDocument();
    expect(screen.queryByText('Owned')).not.toBeInTheDocument();
    expect(screen.getByLabelText(/search/i)).toBeInTheDocument();
    expect(screen.getByPlaceholderText('All systems')).toBeInTheDocument();
    expect(
      screen.getByRole('link', {
        name: 'Super Mario Bros.',
      }),
    ).toHaveAttribute('href', '/titles/title-1');
    expect(
      screen.queryByRole('link', {
        name: 'Play Super Mario Bros.',
      }),
    ).not.toBeInTheDocument();
  });

  it('passes URL filters to the backend-backed catalog search hook', () => {
    render(<Library />, {
      withAuth: false,
      routerOptions: {
        initialEntries: [
          '/library?q=mario&systemKey=platform-1&genre=Platformer&completeness=complete&sortBy=rating',
        ],
      },
    });

    expect(useCatalogSearch).toHaveBeenCalledWith(
      {
        query: 'mario',
        systemKey: 'platform-1',
        genre: 'Platformer',
        completeness: 'complete',
        sortBy: 'rating',
      },
      {
        limit: 36,
      },
    );
    expect(screen.getByText('Top rated first')).toBeInTheDocument();
    expect(screen.getByText('"mario"')).toBeInTheDocument();
  });

  it.each([
    false,
    true,
  ])('clears the URL and input without restoring a search (pending edit: %s)', async (pending) => {
    vi.useFakeTimers();
    render(
      <>
        <Library />
        <NavigationProbe />
      </>,
      {
        withAuth: false,
        routerOptions: {
          initialEntries: [
            '/library?q=mario&systemKey=platform-1',
          ],
        },
      },
    );
    if (pending)
      fireEvent.change(screen.getByLabelText('Search'), {
        target: {
          value: 'samus',
        },
      });
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Clear all',
      }),
    );
    await act(() => vi.advanceTimersByTimeAsync(350));
    expect(screen.getByLabelText('Search')).toHaveValue('');
    expect(screen.getByLabelText('Current location')).toHaveTextContent(/^$/);
    expect(vi.mocked(useCatalogSearch).mock.calls.at(-1)?.[0]).toMatchObject({
      query: undefined,
      systemKey: undefined,
    });
  });

  it.each([
    'zelda',
    'mario',
  ])('cancels a pending draft when browser history restores %s', async (previousQuery) => {
    vi.useFakeTimers();
    render(
      <>
        <Library />
        <NavigationProbe />
      </>,
      {
        withAuth: false,
        routerOptions: {
          initialEntries: [
            `/library?q=${previousQuery}`,
            '/library?q=mario',
          ],
          initialIndex: 1,
        },
      },
    );
    fireEvent.change(screen.getByLabelText('Search'), {
      target: {
        value: 'samus',
      },
    });
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Previous search',
      }),
    );
    await act(() => vi.advanceTimersByTimeAsync(350));
    expect(screen.getByLabelText('Search')).toHaveValue(previousQuery);
    expect(screen.getByLabelText('Current location')).toHaveTextContent(`?q=${previousQuery}`);
    expect(vi.mocked(useCatalogSearch).mock.calls.at(-1)?.[0]).toMatchObject({
      query: previousQuery,
    });
  });

  it('commits typed search after debounce while preserving URL filters', async () => {
    vi.useFakeTimers();
    render(
      <>
        <Library />
        <NavigationProbe />
      </>,
      {
        withAuth: false,
        routerOptions: {
          initialEntries: [
            '/library?systemKey=platform-1',
          ],
        },
      },
    );
    fireEvent.change(screen.getByLabelText('Search'), {
      target: {
        value: '  mario  ',
      },
    });
    expect(screen.getByLabelText('Current location')).not.toHaveTextContent('q=');
    await act(() => vi.advanceTimersByTimeAsync(300));
    expect(screen.getByLabelText('Current location')).toHaveTextContent(
      'systemKey=platform-1&q=mario',
    );
    expect(vi.mocked(useCatalogSearch).mock.calls.at(-1)?.[0]).toMatchObject({
      query: 'mario',
      systemKey: 'platform-1',
    });
  });
});
