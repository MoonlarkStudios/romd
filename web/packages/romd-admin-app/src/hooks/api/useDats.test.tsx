import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { datKeys, useSetDatSourceStatus } from './useDats';
import { titleDetailKeys } from './useTitleDetail';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    setDatSourceStatus: vi.fn(),
  };
});

import { setDatSourceStatus } from '@romd/admin-api-client';

const mockSetDatSourceStatus = setDatSourceStatus as ReturnType<typeof vi.fn>;

function createHarness() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  queryClient.setQueryData(datKeys.all, 'dats');
  queryClient.setQueryData(titleDetailKeys.all, 'titleDetail');
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return { queryClient, wrapper };
}

describe('useSetDatSourceStatus', () => {
  beforeEach(() => {
    mockSetDatSourceStatus.mockResolvedValue({
      data: { status: 'Disabled', changed: true },
      error: undefined,
    });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('sends the requested status for the DAT', async () => {
    const { wrapper } = createHarness();
    const { result } = renderHook(() => useSetDatSourceStatus(), { wrapper });

    await act(() =>
      result.current.mutateAsync({ datId: 'dat-1', datName: 'No-Intro SNES', status: 'Disabled' }),
    );

    expect(mockSetDatSourceStatus).toHaveBeenCalledWith({
      path: { datId: 'dat-1' },
      body: { status: 'Disabled' },
    });
  });

  it('invalidates DAT and title detail queries after a status change', async () => {
    const { queryClient, wrapper } = createHarness();
    const { result } = renderHook(() => useSetDatSourceStatus(), { wrapper });

    await act(() =>
      result.current.mutateAsync({ datId: 'dat-1', datName: 'No-Intro SNES', status: 'Disabled' }),
    );

    expect(queryClient.getQueryState(datKeys.all)?.isInvalidated).toBe(true);
    expect(queryClient.getQueryState(titleDetailKeys.all)?.isInvalidated).toBe(true);
  });

  it('leaves caches intact when the status change fails', async () => {
    mockSetDatSourceStatus.mockResolvedValue({ data: undefined, error: { status: 400 } });
    const { queryClient, wrapper } = createHarness();
    const { result } = renderHook(() => useSetDatSourceStatus(), { wrapper });

    await expect(
      act(() =>
        result.current.mutateAsync({
          datId: 'dat-1',
          datName: 'No-Intro SNES',
          status: 'Disabled',
        }),
      ),
    ).rejects.toThrow('Failed to update source status');

    expect(queryClient.getQueryState(datKeys.all)?.isInvalidated).toBe(false);
    expect(queryClient.getQueryState(titleDetailKeys.all)?.isInvalidated).toBe(false);
  });
});
