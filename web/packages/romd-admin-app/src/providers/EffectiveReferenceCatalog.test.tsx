import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import type { ReactNode } from 'react';
import { expect, it } from 'vitest';
import { AuthContext, type AuthContextValue } from '../contexts/AuthContext';
import { installReferenceCatalog } from '../test/fixtures/referenceCatalog';
import { authFixtures } from '../test/msw/fixtures/auth';
import { server } from '../test/msw/server';
import { useAdminCatalogSnapshot } from './EffectiveReferenceCatalog';

const auth: AuthContextValue = {
  user: authFixtures.currentUser,
  isAuthenticated: true,
  isLoading: false,
  error: null,
  retry: async () => {},
  login: async () => {},
  logout: async () => {},
};
it('retains the previous complete catalog when a refresh fails after earlier families succeeded', async () => {
  const initial = installReferenceCatalog();
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>
      <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>
    </QueryClientProvider>
  );
  const { result, unmount } = renderHook(
    () => {
      const query = useAdminCatalogSnapshot();
      // Read both fields during render so React Query tracks the same subscription a UI uses.
      return {
        data: query.data,
        isError: query.isError,
        refetch: query.refetch,
      };
    },
    {
      wrapper,
    },
  );
  await waitFor(() => expect(result.current.data).toEqual(initial.catalog));
  const replacement = installReferenceCatalog({
    revision: 'c'.repeat(64),
  });
  let failedReads = 0;
  server.use(
    http.get('/api/catalog-snapshot/ratings', () => {
      failedReads += 1;
      return HttpResponse.json(
        {
          errorCode: 'Server.Unavailable',
        },
        {
          status: 503,
        },
      );
    }),
  );
  await act(async () => {
    const refreshed = await result.current.refetch();
    expect(refreshed.isError).toBe(true);
    expect(refreshed.error?.message).toBe('The reference catalog could not be loaded.');
  });
  await waitFor(() => expect(result.current.isError).toBe(true));
  expect(result.current.data).toEqual(initial.catalog);
  expect(failedReads).toBe(1);
  expect(replacement.requests.map((url) => url.pathname)).toEqual([
    '/api/catalog-snapshot',
    '/api/catalog-snapshot/systems',
    '/api/catalog-snapshot/companies',
    '/api/catalog-snapshot/regions',
    '/api/catalog-snapshot/languages',
    '/api/catalog-snapshot/rating-boards',
  ]);
  expect(
    replacement.requests
      .slice(1)
      .every((url) => url.searchParams.get('revision') === 'c'.repeat(64)),
  ).toBe(true);
  expect(
    client.getQueryState([
      'reference-catalog',
      auth.user?.id,
    ])?.status,
  ).toBe('error');
  expect(
    client.getQueryData([
      'reference-catalog',
      auth.user?.id,
    ]),
  ).toEqual(initial.catalog);
  unmount();
  client.clear();
});
