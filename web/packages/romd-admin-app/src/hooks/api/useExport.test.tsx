import { createDownloadTicket, exportLibrary } from '@romd/admin-api-client';
import { startBrowserDownload } from '@romd/delivery';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, expect, it, vi } from 'vitest';
import { downloadExportFile, useExportLibrary } from './useExport';

vi.mock('@romd/admin-api-client', async (original) => ({
  ...(await original<typeof import('@romd/admin-api-client')>()),
  exportLibrary: vi.fn(),
  createDownloadTicket: vi.fn(),
}));
vi.mock('@romd/delivery', () => ({
  startBrowserDownload: vi.fn(),
}));

beforeEach(() => vi.resetAllMocks());

it('binds a library export to its explicit library and selected format', async () => {
  vi.mocked(exportLibrary).mockResolvedValue({
    data: {
      jobId: 'job',
    },
  } as never);
  const client = new QueryClient();
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useExportLibrary(), {
    wrapper,
  });
  // The new body contract is exercised before implementation adds the parameter.
  const params = {
    libraryId: 'kids',
    format: 'rvz' as const,
  };
  await act(() => result.current.mutateAsync(params));
  expect(exportLibrary).toHaveBeenCalledWith({
    body: {
      libraryId: 'kids',
      format: 'rvz',
    },
  });
});

it('hands off fresh completed-export tickets on every attempt without fetching payload bytes', async () => {
  vi.mocked(createDownloadTicket)
    .mockResolvedValueOnce({
      data: {
        downloadUrl: '/api/download-tickets/first',
      },
    } as never)
    .mockResolvedValueOnce({
      data: {
        downloadUrl: '/api/download-tickets/second',
      },
    } as never);
  const fetch = vi.spyOn(globalThis, 'fetch');
  await downloadExportFile('job');
  await downloadExportFile('job');
  expect(createDownloadTicket).toHaveBeenNthCalledWith(1, {
    body: {
      target: 'ExportJob',
      id: 'job',
    },
  });
  expect(createDownloadTicket).toHaveBeenNthCalledWith(2, {
    body: {
      target: 'ExportJob',
      id: 'job',
    },
  });
  expect(startBrowserDownload).toHaveBeenNthCalledWith(1, '/api/download-tickets/first');
  expect(startBrowserDownload).toHaveBeenNthCalledWith(2, '/api/download-tickets/second');
  expect(fetch).not.toHaveBeenCalled();
});

it('does not hand off a completed export when authorization fails', async () => {
  vi.mocked(createDownloadTicket).mockResolvedValue({
    error: {
      status: 403,
    },
  } as never);
  await expect(downloadExportFile('job')).rejects.toThrow();
  expect(createDownloadTicket).toHaveBeenCalledWith({
    body: {
      target: 'ExportJob',
      id: 'job',
    },
  });
  expect(startBrowserDownload).not.toHaveBeenCalled();
});

it.each([
  {},
  {
    jobId: '',
  },
  {
    jobId: 7,
  },
  null,
  undefined,
])('rejects a malformed queue result instead of creating a progress URL (%j)', async (data) => {
  vi.mocked(exportLibrary).mockResolvedValue({
    data,
  } as never);
  const client = new QueryClient();
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useExportLibrary(), {
    wrapper,
  });
  await act(async () => {
    await expect(
      result.current.mutateAsync({
        libraryId: 'kids',
      }),
    ).rejects.toThrow('Failed to start export');
  });
  expect(exportLibrary).toHaveBeenCalledWith({
    body: {
      libraryId: 'kids',
      format: 'original',
    },
  });
});
