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
  const reset = vi.spyOn(client, 'resetQueries');
  const versionKey = [
    'library-publication',
    'account',
  ];
  const catalogKey = [
    'consumer',
    'catalog',
    {},
  ];
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
  await waitFor(() => expect(reset).toHaveBeenCalledTimes(1));
  client.setQueryData(catalogKey, {
    pages: [
      'previous page',
    ],
    pageParams: [
      'previous cursor',
    ],
  });
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

it('reconciles Browse on the first observation while preserving a mounted player', async () => {
  let finishObservation: (() => void) | undefined;
  vi.mocked(getConsumerLibraryPublication).mockImplementationOnce(async () => {
    await new Promise<void>((resolve) => {
      finishObservation = resolve;
    });
    return {
      data: {
        version: 'current',
      },
    } as never;
  });
  const client = new QueryClient();
  const titleKey = [
    'consumer',
    'title',
    'game',
  ];
  const playerKey = [
    'consumer',
    'playbackTitle',
    'account',
    'game',
  ];
  const playing = {
    id: 'game',
  };
  render(
    <QueryClientProvider client={client}>
      <LibraryPublicationObserver />
    </QueryClientProvider>,
  );
  await waitFor(() => expect(finishObservation).toBeDefined());
  client.setQueryData(titleKey, {
    id: 'game',
  });
  client.setQueryData(playerKey, playing);

  await act(async () => finishObservation?.());

  await waitFor(() => expect(client.getQueryData(titleKey)).toBeUndefined());
  expect(client.getQueryData(playerKey)).toBe(playing);
});
