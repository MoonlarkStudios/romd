import {
  getCatalogCoverageStats,
  getDashboardStatusBar,
  getServerHealth,
  getServerStorage,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { useCoverageStats } from './useCoverageStats';
import { useDashboardStatusBar } from './useDashboardStatusBar';
import { useHealthStats } from './useHealthStats';
import { useStorageStats } from './useStorageStats';

vi.mock('@romd/admin-api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@romd/admin-api-client')>()),
  getCatalogCoverageStats: vi.fn(),
  getServerStorage: vi.fn(),
  getServerHealth: vi.fn(),
  getDashboardStatusBar: vi.fn(),
}));

describe('dashboard request cancellation', () => {
  it.each([
    [
      'status bar',
      useDashboardStatusBar,
      getDashboardStatusBar,
    ],
    [
      'coverage',
      useCoverageStats,
      getCatalogCoverageStats,
    ],
    [
      'storage',
      useStorageStats,
      getServerStorage,
    ],
    [
      'health',
      useHealthStats,
      getServerHealth,
    ],
  ] as const)('aborts the %s HTTP request when its last observer unmounts', async (_name, useStats, getStats) => {
    let requestSignal: AbortSignal | undefined;
    vi.mocked(getStats).mockImplementation((options) => {
      requestSignal = options?.signal ?? undefined;
      return new Promise(() => {});
    });
    const client = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    const { unmount } = renderHook(
      () => {
        useStats();
      },
      {
        wrapper,
      },
    );
    await waitFor(() => expect(requestSignal).toBeInstanceOf(AbortSignal));
    expect(requestSignal?.aborted).toBe(false);
    unmount();
    expect(requestSignal?.aborted).toBe(true);
    client.clear();
  });
});
