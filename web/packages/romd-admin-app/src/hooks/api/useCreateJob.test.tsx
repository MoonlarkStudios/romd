import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import { useCreateJob } from './useCreateJob';
import { jobKeys } from './useJobs';

describe('useCreateJob', () => {
  it('invalidates job detail and list immediately for a durable job reference', () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    queryClient.setQueryData(jobKeys.detail('materialization-job'), null);
    queryClient.setQueryData(jobKeys.list(), []);
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useCreateJob(), { wrapper });

    act(() => {
      result.current.seedJob(
        { jobId: 'materialization-job' },
        { invalidateDelayMs: 0 },
      );
    });

    expect(queryClient.getQueryState(jobKeys.detail('materialization-job'))?.isInvalidated).toBe(
      true,
    );
    expect(queryClient.getQueryState(jobKeys.list())?.isInvalidated).toBe(true);
  });
});
