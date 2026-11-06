import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { expect, it, vi } from 'vitest';
import { jobFixtures } from '../../test/msw/fixtures/jobs';
import { useJobSocket } from '../realtime/useJobSocket';
import { useJobs } from './useJobs';

const { list, hub } = vi.hoisted(() => ({
  list: vi.fn(),
  hub: {
    on: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    onclose: vi.fn(),
    start: async () => {},
    stop: async () => {},
    invoke: async () => {},
  },
}));
vi.mock('@romd/admin-api-client', async (original) => ({
  ...(await original<typeof import('@romd/admin-api-client')>()),
  listJobs: list,
  createJobHubConnection: async () => hub,
}));
vi.mock('../../api/client', () => ({
  getAuthToken: () => 'token',
}));

it('recovers a missed completion event while the job connection stays connected', async () => {
  vi.useFakeTimers();
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  let finished = false;
  list.mockImplementation(async () => ({
    data: {
      items: [
        {
          ...jobFixtures.list[0],
          isTerminal: finished,
        },
      ],
      hasNextPage: false,
      nextCursor: null,
    },
  }));
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  const hook = renderHook(
    () => ({
      status: useJobSocket(true).status,
      jobs: useJobs(),
    }),
    {
      wrapper,
    },
  );
  try {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1);
    });
    expect(hook.result.current.status).toBe('connected');
    expect(hook.result.current.jobs.data?.[0].isTerminal).toBe(false);
    finished = true;
    // No JobUpdated callback is delivered.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3001);
    });
    expect(hook.result.current.status).toBe('connected');
    expect(hook.result.current.jobs.data?.[0].isTerminal).toBe(true);
  } finally {
    hook.unmount();
    cache.clear();
    vi.useRealTimers();
  }
});
