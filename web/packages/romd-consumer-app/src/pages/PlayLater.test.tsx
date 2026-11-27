import { type ConsumerTitleCard, client } from '@romd/consumer-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { User } from 'oidc-client-ts';
import { Link, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../api/client';
import { clearCachedSession, userManager } from '../auth/userManager';
import { AuthContext } from '../contexts/AuthContext';
import { playLaterAccountKey } from '../hooks/playLaterQueryKeys';
import { LibraryPublicationObserver } from '../hooks/useLibraryRefresh';
import { render } from '../test/utils/render';
import { Library } from './Library';
import { PlayLater } from './PlayLater';
import { Shelves } from './Shelves';
import { TitleDetail } from './TitleDetail';

const mario: ConsumerTitleCard = {
  id: 'mario',
  name: 'Super Mario Bros.',
  system: {
    key: 'nes',
    name: 'NES',
    compactLabel: 'NES',
  },
  releaseCount: 1,
};
const metroid: ConsumerTitleCard = {
  id: 'metroid',
  name: 'Super Metroid',
  system: {
    key: 'snes',
    name: 'Super Nintendo',
    compactLabel: 'SNES',
  },
  releaseCount: 1,
};
const celeste: ConsumerTitleCard = {
  id: 'celeste',
  name: 'Celeste',
  system: {
    key: 'pico8',
    name: 'PICO-8',
    compactLabel: 'PICO-8',
  },
  releaseCount: 1,
};

vi.mock('../hooks/useBrowserPlaybackCapability', () => ({
  useBrowserPlaybackAvailability: () => ({
    available: false,
    loading: false,
  }),
  useBrowserPlaybackCapability: () => ({
    isLoading: false,
  }),
}));
vi.mock('../hooks/usePlayActivity', () => ({
  useRecentlyPlayed: () => ({
    data: [],
  }),
}));
vi.mock('../hooks/useConsumerLibrary', () => ({
  useCurrentLibrary: () => ({
    data: {
      counts: {
        ownedTitleCount: 3,
      },
      platforms: [],
      genres: [],
    },
  }),
  useCatalogSearch: () => ({
    data: {
      pages: [
        {
          items: [
            mario,
            metroid,
          ],
        },
      ],
    },
  }),
  useCollections: () => ({
    data: [],
  }),
  useCollectionTitles: () => ({
    data: {
      pages: [],
    },
  }),
  useTitleDetail: (id: string) => ({
    data: {
      ...(id === mario.id ? mario : metroid),
      media: [],
      releases: [],
    },
  }),
}));
vi.mock('../components/library/DiscSetDownloads', () => ({
  DiscSetDownloads: () => null,
}));

function account(id: string) {
  return {
    user: {
      id,
      username: id,
      email: `${id}@example.test`,
      roles: [],
    },
    isAuthenticated: true,
    isLoading: false,
    error: null,
    retry: async () => {},
    login: async () => {},
    logout: async () => {},
  };
}

async function authenticate(id: string) {
  await userManager.events.load(
    new User({
      access_token: id,
      token_type: 'Bearer',
      profile: {
        sub: id,
      },
    }),
  );
}

function Portal({ id, cache }: { id: string; cache: QueryClient }) {
  return (
    <QueryClientProvider client={cache}>
      <AuthContext.Provider value={account(id)}>
        <LibraryPublicationObserver />
        <nav>
          <Link to="/">Home</Link>
          <Link to="/library">Browse</Link>
          <Link to="/play-later">Saved list</Link>
        </nav>
        <Routes>
          <Route
            path="/"
            element={<Shelves />}
          />
          <Route
            path="/library"
            element={<Library />}
          />
          <Route
            path="/play-later"
            element={<PlayLater />}
          />
          <Route
            path="/titles/:titleId"
            element={<TitleDetail />}
          />
        </Routes>
      </AuthContext.Provider>
    </QueryClientProvider>
  );
}

function cacheForTest() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
}

function page(items: ConsumerTitleCard[], nextCursor: string | null = null) {
  return Response.json({
    items,
    nextCursor,
    hasNextPage: nextCursor !== null,
  });
}

function fakeServer() {
  const saved: Record<string, ConsumerTitleCard[]> = {
    alice: [],
    bob: [],
  };
  const requests: Request[] = [];
  let publication = 'initial';
  let visible = true;
  let pendingWrite: Promise<Response> | undefined;
  let pendingList: Promise<Response> | undefined;
  const respond = vi.fn(async (request: Request) => {
    requests.push(request);
    const url = new URL(request.url);
    const caller = request.headers.get('authorization')?.replace('Bearer ', '') ?? '';
    const bookmarks = saved[caller];
    if (!bookmarks)
      return Response.json(
        {},
        {
          status: 401,
        },
      );

    switch (url.pathname) {
      case '/api/me/library/publication':
        return Response.json({
          version: publication,
        });
      case '/api/me/play-later':
        return pendingList ?? page(visible ? bookmarks : []);
      case '/api/me/play-later/membership':
        return Response.json({
          titleIds: visible
            ? url.searchParams
                .getAll('titleIds')
                .filter((id) => bookmarks.some((title) => title.id === id))
            : [],
        });
      case '/api/me/play-later/mario':
        if (pendingWrite) return pendingWrite;
        if (request.method === 'PUT') {
          saved[caller] = [
            mario,
            ...bookmarks.filter(({ id }) => id !== mario.id),
          ];
        } else if (request.method === 'DELETE') {
          saved[caller] = bookmarks.filter(({ id }) => id !== mario.id);
        }
        return new Response(null, {
          status: 204,
        });
      default:
        throw new Error(`Unexpected request at ${url.pathname}`);
    }
  });
  vi.stubGlobal('fetch', respond);
  return {
    saved,
    requests,
    respond,
    setPublication(value: string, access: boolean) {
      publication = value;
      visible = access;
    },
    deferList(result: Promise<Response>) {
      pendingList = result;
    },
    deferWrite(result: Promise<Response>) {
      pendingWrite = result;
    },
  };
}

beforeEach(async () => {
  client.setConfig({
    baseUrl: window.location.origin,
  });
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  await authenticate('alice');
});

afterEach(() => clearCachedSession());

describe('Play Later', () => {
  it('toggles with the keyboard across Browse views and title detail, with a Home shelf', async () => {
    const server = fakeServer();
    const cache = cacheForTest();
    const user = userEvent.setup();
    render(
      <Portal
        id="alice"
        cache={cache}
      />,
      {
        routerOptions: {
          initialEntries: [
            '/library',
          ],
        },
      },
    );
    const save = await screen.findByRole('button', {
      name: 'Play Later for Super Mario Bros.',
      pressed: false,
    });
    const title = screen.getByRole('link', {
      name: mario.name,
    });
    expect(title).toHaveAttribute('href', '/titles/mario');
    expect(title).not.toContainElement(save);
    const membershipRequests = server.requests.filter((request) =>
      new URL(request.url).pathname.endsWith('/membership'),
    );
    expect(membershipRequests.length).toBeGreaterThan(0);
    for (const request of membershipRequests) {
      expect(new URL(request.url).searchParams.getAll('titleIds')).toEqual([
        'mario',
        'metroid',
      ]);
    }

    title.focus();
    await user.tab();
    expect(save).toHaveFocus();
    await user.keyboard('{Enter}');
    const remove = await screen.findByRole('button', {
      name: 'Play Later for Super Mario Bros.',
      pressed: true,
    });
    expect(remove).toBe(save);
    expect(remove).toHaveFocus();
    expect(
      screen.getByRole('heading', {
        name: 'Browse games',
      }),
    ).toBeInTheDocument();
    expect(server.requests.filter(({ method }) => method === 'PUT')).toHaveLength(1);

    await user.click(screen.getByText('Filters & view'));
    await user.click(
      screen.getByRole('button', {
        name: 'List view',
      }),
    );
    expect(
      screen.getByRole('button', {
        name: 'Play Later for Super Mario Bros.',
        pressed: true,
      }),
    ).toBeInTheDocument();

    await user.click(
      within(screen.getByRole('navigation')).getByRole('link', {
        name: 'Home',
      }),
    );
    const shelf = await screen.findByRole('heading', {
      name: 'Play Later',
      level: 2,
    });
    const discovery = screen.getByRole('heading', {
      name: 'Explore your games',
      level: 2,
    });
    expect(
      shelf.compareDocumentPosition(discovery) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    const seeAll = await screen.findByRole('link', {
      name: 'See all in Play Later',
    });
    await user.click(seeAll);
    expect(
      await screen.findByRole('heading', {
        name: 'Play Later',
        level: 1,
      }),
    ).toBeInTheDocument();
    const savedTitle = await screen.findByRole('link', {
      name: mario.name,
    });
    expect(within(savedTitle).getByText(mario.name)).toBeVisible();
    await user.click(savedTitle);
    expect(
      await screen.findByRole('link', {
        name: 'Play Later',
      }),
    ).toHaveAttribute('href', '/play-later');

    const savedButton = await screen.findByRole('button', {
      name: 'Saved, remove from Play Later',
    });
    expect(savedButton).toHaveTextContent('Saved');
    await user.click(savedButton);
    expect(
      await screen.findByRole('button', {
        name: 'Play Later',
        exact: true,
      }),
    ).toHaveFocus();
    await user.click(
      screen.getByRole('link', {
        name: 'Play Later',
      }),
    );
    expect(await screen.findByText('No games saved for later')).toBeInTheDocument();
    expect(server.requests.filter(({ method }) => method === 'DELETE')).toHaveLength(1);
  });

  it('keeps server order across systems and follows the next cursor', async () => {
    const server = fakeServer();
    const following = Promise.withResolvers<Response>();
    const send = server.respond.getMockImplementation();
    if (!send) throw new Error('Missing fake server');
    server.respond.mockImplementation((request) => {
      const url = new URL(request.url);
      if (url.pathname !== '/api/me/play-later') return send(request);
      return url.searchParams.get('cursor') === 'second'
        ? following.promise
        : Promise.resolve(
            page(
              [
                metroid,
                mario,
              ],
              'second',
            ),
          );
    });
    const user = userEvent.setup();
    render(
      <Portal
        id="alice"
        cache={cacheForTest()}
      />,
      {
        routerOptions: {
          initialEntries: [
            '/play-later',
          ],
        },
      },
    );

    await user.click(
      await screen.findByRole('button', {
        name: 'Load more',
      }),
    );
    const first = screen.getByRole('link', {
      name: 'Super Metroid',
    });
    const middle = screen.getByRole('link', {
      name: 'Super Mario Bros.',
    });
    await act(async () =>
      following.resolve(
        page([
          celeste,
        ]),
      ),
    );
    const last = await screen.findByRole('link', {
      name: celeste.name,
    });
    expect(first.compareDocumentPosition(middle) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(middle.compareDocumentPosition(last) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(
      screen.queryByRole('button', {
        name: 'Load more',
      }),
    ).not.toBeInTheDocument();
  });

  it('purges the previous account and ignores its late unauthorized mutation response', async () => {
    const server = fakeServer();
    server.saved.alice = [
      mario,
    ];
    server.saved.bob = [
      metroid,
    ];
    const removeUser = vi.spyOn(userManager, 'removeUser');
    const write = Promise.withResolvers<Response>();
    server.deferWrite(write.promise);
    const cache = cacheForTest();
    const user = userEvent.setup();
    const view = render(
      <Portal
        id="alice"
        cache={cache}
      />,
      {
        routerOptions: {
          initialEntries: [
            '/play-later',
          ],
        },
      },
    );
    await user.click(
      await screen.findByRole('link', {
        name: mario.name,
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Saved, remove from Play Later',
      }),
    );
    await waitFor(() =>
      expect(server.requests.some(({ method }) => method === 'DELETE')).toBe(true),
    );

    const aliceKey = playLaterAccountKey('alice');
    const formerWrite = cache.getMutationCache().findAll({
      mutationKey: aliceKey,
    })[0];
    expect(formerWrite?.state.status).toBe('pending');

    await authenticate('bob');
    view.rerender(
      <Portal
        id="bob"
        cache={cache}
      />,
    );
    expect(
      screen.queryByRole('button', {
        name: 'Saved, remove from Play Later',
      }),
    ).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('link', {
        name: 'Play Later',
      }),
    );
    expect(
      await screen.findByRole('link', {
        name: metroid.name,
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('link', {
        name: mario.name,
      }),
    ).not.toBeInTheDocument();
    expect(
      cache.getQueriesData({
        queryKey: aliceKey,
      }),
    ).toEqual([]);

    expect(
      cache.getMutationCache().findAll({
        mutationKey: aliceKey,
      }),
    ).toEqual([]);

    await act(async () =>
      write.resolve(
        Response.json(
          {},
          {
            status: 401,
          },
        ),
      ),
    );
    await waitFor(() => expect(formerWrite?.state.status).toBe('error'));
    expect(removeUser).not.toHaveBeenCalled();
    expect(
      cache.getQueriesData({
        queryKey: aliceKey,
      }),
    ).toEqual([]);
    expect(
      screen.getByRole('link', {
        name: metroid.name,
      }),
    ).toBeInTheDocument();
  });

  it.each([
    'Home',
    'Saved list',
  ])('withholds a cached card while rechecking publication on %s', async (destination) => {
    const server = fakeServer();
    server.saved.alice = [
      celeste,
    ];
    const cache = cacheForTest();
    const user = userEvent.setup();
    render(
      <Portal
        id="alice"
        cache={cache}
      />,
      {
        routerOptions: {
          initialEntries: [
            '/play-later',
          ],
        },
      },
    );
    expect(
      await screen.findByRole('link', {
        name: celeste.name,
      }),
    ).toBeInTheDocument();
    const nav = within(screen.getByRole('navigation'));
    await user.click(
      nav.getByRole('link', {
        name: 'Browse',
      }),
    );

    server.setPublication('hidden', false);
    const response = Promise.withResolvers<Response>();
    server.deferList(response.promise);
    const queryKey = [
      ...playLaterAccountKey('alice'),
      'titles',
    ];
    const warmed = cache.getQueryData(queryKey);
    expect(warmed).toBeDefined();

    await user.click(
      nav.getByRole('link', {
        name: destination,
      }),
    );
    expect(await screen.findByText('Loading Play Later')).toBeInTheDocument();
    expect(cache.getQueryData(queryKey)).toBe(warmed);
    expect(
      screen.queryByRole('link', {
        name: celeste.name,
      }),
    ).not.toBeInTheDocument();

    await act(async () => response.resolve(page([])));
    expect(await screen.findByText('No games saved for later')).toBeInTheDocument();
  });

  it('drops cached titles when publication changes and reloads them after access returns', async () => {
    const server = fakeServer();
    server.saved.alice = [
      mario,
    ];
    const cache = cacheForTest();
    render(
      <Portal
        id="alice"
        cache={cache}
      />,
      {
        routerOptions: {
          initialEntries: [
            '/play-later',
          ],
        },
      },
    );
    expect(
      await screen.findByRole('link', {
        name: mario.name,
      }),
    ).toBeInTheDocument();

    const key = [
      'library-publication',
      'alice',
    ];
    server.setPublication('hidden', false);
    await act(async () =>
      cache.refetchQueries({
        queryKey: key,
      }),
    );
    expect(await screen.findByText('No games saved for later')).toBeInTheDocument();
    expect(
      screen.queryByRole('link', {
        name: mario.name,
      }),
    ).not.toBeInTheDocument();

    server.setPublication('restored', true);
    await act(async () =>
      cache.refetchQueries({
        queryKey: key,
      }),
    );
    expect(
      await screen.findByRole('link', {
        name: mario.name,
      }),
    ).toBeInTheDocument();
  });
});
