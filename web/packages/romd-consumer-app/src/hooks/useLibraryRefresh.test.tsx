import { getConsumerLibraryPublication } from '@romd/consumer-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, waitFor } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { LibraryPublicationObserver } from './useLibraryRefresh';

vi.mock('../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: {
      id: 'account',
    },
    isAuthenticated: true,
  }),
}));
vi.mock('@romd/consumer-api-client', () => ({
  getConsumerLibraryPublication: vi.fn(),
}));

it('discards warmed library pages when the same account receives a new publication', async () => {
  vi.mocked(getConsumerLibraryPublication)
    .mockResolvedValueOnce({
      data: {
        version: 'previous',
      },
    } as never)
    .mockResolvedValueOnce({
      data: {
        version: 'current',
      },
    } as never);
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const versionKey = [
    'library-publication',
    'account',
  ];
  const catalogKey = [
    'consumer',
    'catalog',
    {},
  ];
  client.setQueryData(catalogKey, {
    pages: [
      'previous page',
    ],
    pageParams: [
      'previous cursor',
    ],
  });
  client.setQueryData(
    [
      'consumer',
      'currentUser',
    ],
    {
      id: 'account',
    },
  );
  render(
    <QueryClientProvider client={client}>
      <LibraryPublicationObserver />
    </QueryClientProvider>,
  );

  await waitFor(() => expect(client.getQueryData(versionKey)).toBe('previous'));
  expect(client.getQueryData(catalogKey)).toBeDefined();
  await act(async () => {
    await client.refetchQueries({
      queryKey: versionKey,
    });
  });

  await waitFor(() => expect(client.getQueryData(catalogKey)).toBeUndefined());
  expect(
    client.getQueryData([
      'consumer',
      'currentUser',
    ]),
  ).toEqual({
    id: 'account',
  });
});
