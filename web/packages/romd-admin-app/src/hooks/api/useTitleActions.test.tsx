import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { catalogSearchKeys } from './useCatalogSearch';
import { useSetTitlesTracking, useSetTitleTracking } from './useTitleActions';
import { trackedCollectionKeys } from './useTrackedCollection';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    setTitleTracking: vi.fn(),
    setTitlesTracking: vi.fn(),
  };
});

import { setTitlesTracking, setTitleTracking } from '@romd/admin-api-client';

const mockSetTitleTracking = setTitleTracking as ReturnType<typeof vi.fn>;
const mockSetTitlesTracking = setTitlesTracking as ReturnType<typeof vi.fn>;

function createHarness() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  queryClient.setQueryData(catalogSearchKeys.all, 'catalog');
  queryClient.setQueryData(trackedCollectionKeys.all, 'tracked');
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return { queryClient, wrapper };
}

describe('tracking query invalidation', () => {
  beforeEach(() => {
    mockSetTitleTracking.mockResolvedValue({ data: undefined, error: undefined });
    mockSetTitlesTracking.mockResolvedValue({ data: undefined, error: undefined });
  });

  it('invalidates catalog and tracked collection queries after a single tracking mutation', async () => {
    const { queryClient, wrapper } = createHarness();
    const { result } = renderHook(() => useSetTitleTracking(), { wrapper });

    await act(() => result.current.mutateAsync({ titleId: 'title-1', tracked: true }));

    expect(queryClient.getQueryState(catalogSearchKeys.all)?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(trackedCollectionKeys.all)?.isInvalidated).toBe(true);
  });

  it('invalidates catalog and tracked collection queries after a bulk tracking mutation', async () => {
    const { queryClient, wrapper } = createHarness();
    const { result } = renderHook(() => useSetTitlesTracking(), { wrapper });

    await act(() => result.current.mutateAsync({ titleIds: ['title-1', 'title-2'], tracked: false }));

    expect(queryClient.getQueryState(catalogSearchKeys.all)?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(trackedCollectionKeys.all)?.isInvalidated).toBe(true);
  });
});
