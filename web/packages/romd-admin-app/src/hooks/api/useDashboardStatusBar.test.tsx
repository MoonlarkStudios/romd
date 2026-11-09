import type { DashboardStatusBar } from '@romd/admin-api-client';
import {
  getCatalogCoverageStats,
  getDashboardStatusBar,
  getServerHealth,
  getServerStorage,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { expect, it, vi } from 'vitest';
import { useDashboardStatusBar } from './useDashboardStatusBar';

vi.mock('@romd/admin-api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@romd/admin-api-client')>()),
  getDashboardStatusBar: vi.fn(),
  getServerStorage: vi.fn(),
  getServerHealth: vi.fn(),
  getCatalogCoverageStats: vi.fn(),
}));

it('loads one coherent status-bar resource and preserves exact byte counts', async () => {
  const data: DashboardStatusBar = {
    storage: {
      totalStorageBytes: '9007199254740993',
      totalStorageBytesOnDisk: '9007199254740992',
      compressedFileCount: 3,
      uncompressedFileCount: 2,
      averageCompressionRatio: 0.5,
      bytesSaved: '1',
      breakdown: [
        {
          category: 'ROMs',
          fileCount: 5,
          sizeBytes: '9007199254740993',
          sizeOnDiskBytes: '9007199254740992',
        },
      ],
    },
    coverage: {
      expectedTitleCount: 10,
      localPayloadTitleCount: 8,
      completeTitleCount: 7,
      partialTitleCount: 1,
      coverageHealthPercent: 70,
    },
    health: {
      totalRoms: 5,
      identifiedCount: 4,
      unidentifiedCount: 1,
      routedCount: 3,
      unroutedCount: 1,
      failedJobCount: 2,
    },
  };
  vi.mocked(getDashboardStatusBar).mockResolvedValue({
    data,
    request: new Request('http://localhost/api/dashboard/status-bar'),
    response: new Response(null, {
      status: 200,
    }),
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
  const { result, unmount } = renderHook(() => useDashboardStatusBar(), {
    wrapper,
  });

  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(result.current.data).toEqual({
    ...data,
    coverage: {
      ...data.coverage,
      lastDatRefreshAt: null,
    },
  });
  expect(getDashboardStatusBar).toHaveBeenCalledTimes(1);
  expect(getServerStorage).not.toHaveBeenCalled();
  expect(getServerHealth).not.toHaveBeenCalled();
  expect(getCatalogCoverageStats).not.toHaveBeenCalled();
  unmount();
  client.clear();
});
