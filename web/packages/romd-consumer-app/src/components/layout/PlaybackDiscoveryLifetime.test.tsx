import type { ConsumerTitleCard, RecentlyPlayedTitle } from '@romd/consumer-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { screen, waitFor } from '@testing-library/react';
import { Route, Routes } from 'react-router';
import { expect, it, vi } from 'vitest';
import { Shelves } from '../../pages/Shelves';
import { fetchPlayerCapability } from '../../services/playerBridge';
import { render } from '../../test/utils/render';
import { AppLayout } from './AppLayout';

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: {
      email: 'player@example.test',
    },
    logout: vi.fn(),
  }),
}));
vi.mock('../../services/playerBridge', () => ({
  fetchPlayerCapability: vi.fn(),
}));
vi.mock('../../hooks/usePlayActivity', () => ({
  useRecentlyPlayed: () => ({
    data: recent,
    isError: false,
  }),
}));
vi.mock('../../hooks/useConsumerLibrary', () => ({
  useCurrentLibrary: () => ({
    data: {
      counts: {
        ownedTitleCount: 1,
      },
    },
    isLoading: false,
  }),
  useCollections: () => ({
    data: [],
  }),
  useCatalogSearch: () => ({
    data: {
      pages: [
        {
          items: [
            title,
          ],
        },
      ],
    },
    isLoading: false,
  }),
  useCollectionTitles: () => ({
    data: {
      pages: [],
    },
  }),
}));

const title: ConsumerTitleCard = {
  id: 'persisted-title',
  name: 'Persisted game',
  system: {
    key: 'nes',
    name: 'NES',
    compactLabel: 'NES',
  },
  defaultReleaseId: 'persisted-release',
  releaseCount: 1,
};
let recent: RecentlyPlayedTitle[] = [];

it('keeps discovery alive while persisted activity moves the only card between shelves', async () => {
  vi.stubGlobal(
    'IntersectionObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  recent = [];
  // Keep the real query and its consumed AbortSignal pending. This is the
  // transfer that a late recently-played response used to cancel on restore.
  vi.mocked(fetchPlayerCapability).mockImplementation(() => new Promise<never>(() => {}));
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
      },
    },
  });
  const ui = () => (
    <QueryClientProvider client={client}>
      <Routes>
        <Route element={<AppLayout />}>
          <Route
            path="/"
            element={<Shelves />}
          />
        </Route>
      </Routes>
    </QueryClientProvider>
  );
  const view = render(ui(), {
    withAuth: false,
  });
  await screen.findByRole('heading', {
    name: 'Explore your games',
  });
  await waitFor(() => expect(fetchPlayerCapability).toHaveBeenCalledTimes(1));
  const signal = vi.mocked(fetchPlayerCapability).mock.calls[0]?.[0];
  expect(signal).toBeInstanceOf(AbortSignal);
  if (!signal) throw new Error('Discovery must consume its cancellation signal');
  expect(signal.aborted).toBe(false);

  recent = [
    {
      ...title,
      lastPlayedAt: '2026-11-13T00:00:00Z',
      playCount: 1,
    },
  ];
  view.rerender(ui());
  await screen.findByRole('heading', {
    name: 'Recently Played',
  });
  expect(
    screen.queryByRole('heading', {
      name: 'Explore your games',
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.getAllByRole('link', {
      name: title.name,
    }),
  ).toHaveLength(1);
  expect(signal.aborted).toBe(false);
  expect(fetchPlayerCapability).toHaveBeenCalledTimes(1);

  // Leaving the authenticated layout still cancels pending work normally.
  view.rerender(
    <QueryClientProvider client={client}>
      <div>Signed out</div>
    </QueryClientProvider>,
  );
  expect(signal.aborted).toBe(true);
  view.unmount();
  client.clear();
});
