import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { expect, it, vi } from 'vitest';
import { artworkKeys } from './useArtwork';
import { useLeaveArtworkEmpty } from './useLeaveArtworkEmpty';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    replaceTitleArtworkRole: vi.fn(),
  };
});

import { replaceTitleArtworkRole } from '@romd/admin-api-client';

it('reloads the artwork after a stale role revision, so a retry sends the current one', async () => {
  vi.mocked(replaceTitleArtworkRole).mockResolvedValue({
    data: undefined,
    error: {
      status: 412,
    },
    request: new Request('http://localhost/api/titles/turok/artwork/Hero'),
    response: new Response(null, {
      status: 412,
    }),
  });
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  cache.setQueryData(artworkKeys.selections('turok'), {
    revision: 'family-rev',
    roles: [],
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useLeaveArtworkEmpty('turok'), {
    wrapper,
  });

  await act(async () => {
    await expect(
      result.current.mutateAsync({
        role: 'Hero',
        revision: 'stale-rev',
      }),
    ).rejects.toThrow(/The artwork changed since you saw it/);
  });

  expect(cache.getQueryState(artworkKeys.selections('turok'))?.isInvalidated).toBe(true);
});
