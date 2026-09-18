import type { TitleSourceReference } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { titleDetailKeys, useTitleSourceReferences } from './useTitleDetail';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    getTitleSourceReferences: vi.fn(),
  };
});

import { getTitleSourceReferences } from '@romd/admin-api-client';

const mockGetTitleSourceReferences = getTitleSourceReferences as ReturnType<typeof vi.fn>;

const references: TitleSourceReference[] = [
  {
    catalogSourceId: 'catsrc-1',
    kind: 'Dat',
    name: 'No-Intro SNES',
    status: 'Active',
    entryCount: '2',
  },
];

function createHarness() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return { queryClient, wrapper };
}

describe('useTitleSourceReferences', () => {
  beforeEach(() => {
    mockGetTitleSourceReferences.mockResolvedValue({ data: references, error: undefined });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('caches references under a dedicated branch of the title detail key', async () => {
    const { queryClient, wrapper } = createHarness();
    const { result } = renderHook(() => useTitleSourceReferences('title-1'), { wrapper });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGetTitleSourceReferences).toHaveBeenCalledWith({ path: { titleId: 'title-1' } });
    expect(queryClient.getQueryData(titleDetailKeys.sourceReferences('title-1'))).toEqual(
      references,
    );
    // The detail cache entry itself must stay untouched — no key collision.
    expect(queryClient.getQueryData(titleDetailKeys.detail('title-1'))).toBeUndefined();
  });

  it('does not fetch without a title id', () => {
    const { wrapper } = createHarness();
    const { result } = renderHook(() => useTitleSourceReferences(undefined), { wrapper });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGetTitleSourceReferences).not.toHaveBeenCalled();
  });
});
