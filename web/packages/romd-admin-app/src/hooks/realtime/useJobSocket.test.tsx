import type { JobDto } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, expect, it, vi } from 'vitest';
import { getJobTitle } from '../../components/Jobs/jobTypeRegistry';
import { useCreateJob } from '../api/useCreateJob';
import { jobKeys } from '../api/useJobs';
import { useJobSocket } from './useJobSocket';

const hub = vi.hoisted(() => ({
  on: vi.fn(),
  onreconnecting: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
  start: vi.fn(async () => {}),
  stop: vi.fn(async () => {}),
  invoke: vi.fn(async () => {}),
}));
vi.mock('@romd/admin-api-client', async (original) => ({
  ...(await original<typeof import('@romd/admin-api-client')>()),
  createJobHubConnection: vi.fn(async () => hub),
}));
vi.mock('../../api/client', () => ({
  getAuthToken: () => 'token',
}));
beforeEach(() => vi.clearAllMocks());

it.each([
  [
    undefined,
    'Enrichment run · 2 titles',
  ],
  [
    null,
    'Enrichment run · 2 titles',
  ],
  [
    'FutureMode',
    'Enrichment run · 2 titles',
  ],
  [
    'Enrich',
    'Enrich 2 titles',
  ],
  [
    'Artwork',
    'Fill artwork · 2 titles',
  ],
])('caches mode %s as delivered without fabricating mode for historical JobUpdated payloads', async (mode, title) => {
  const cache = new QueryClient();
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  renderHook(() => useJobSocket(true), {
    wrapper,
  });
  await waitFor(() => expect(hub.on).toHaveBeenCalledWith('JobUpdated', expect.any(Function)));
  const onUpdate = hub.on.mock.calls.find(([name]) => name === 'JobUpdated')?.[1];
  expect(onUpdate).toBeTypeOf('function');
  const payload = {
    id: 'run',
    jobType: 'artwork-completion',
    total: 2,
    sourceFilename: 'Fill missing artwork',
    ...(mode === undefined
      ? {}
      : {
          mode,
        }),
  };
  act(() => onUpdate?.(payload));
  const cached = cache.getQueryData<JobDto>(jobKeys.detail('run'));
  expect(cached).toEqual(payload);
  expect(cache.getQueryData(jobKeys.list())).toEqual([
    payload,
  ]);
  expect(cached && getJobTitle(cached)).toBe(title);
  if (mode === undefined) expect(cached).not.toHaveProperty('mode');
});

it('keeps an archived socket update out of the active feed when an older GET resolves last', async () => {
  const cache = new QueryClient();
  const job = {
    id: 'run',
    jobType: 'artwork-completion',
    sourceFilename: 'Run',
    isTerminal: true,
    isArchived: false,
  } as JobDto;
  cache.setQueryData(jobKeys.list(), [
    job,
  ]);
  let finish: (jobs: JobDto[]) => void = () => {};
  const pending = cache
    .fetchQuery({
      queryKey: jobKeys.list(),
      queryFn: () =>
        new Promise<JobDto[]>((resolve) => {
          finish = resolve;
        }),
    })
    .catch(() => undefined);
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  renderHook(() => useJobSocket(true), {
    wrapper,
  });
  await waitFor(() => expect(hub.on).toHaveBeenCalledWith('JobUpdated', expect.any(Function)));
  const onUpdate = hub.on.mock.calls.find(([name]) => name === 'JobUpdated')?.[1];
  const archived = {
    ...job,
    isArchived: true,
  };
  await act(async () => {
    onUpdate?.(archived);
  });
  await act(async () => {
    finish([
      job,
    ]);
    await pending;
  });
  expect(cache.getQueryData(jobKeys.detail(job.id))).toEqual(archived);
  expect(cache.getQueryData(jobKeys.list())).toEqual([]);
  await act(async () => {
    onUpdate?.({
      ...job,
      isArchived: false,
    });
  });
  expect(cache.getQueryData(jobKeys.list())).toEqual([
    job,
  ]);
});

it('preserves an accepted seed when the active GET started before it completes afterward', async () => {
  const cache = new QueryClient();
  cache.setQueryData(jobKeys.list(), []);
  let finish: (jobs: JobDto[]) => void = () => {};
  const pending = cache
    .fetchQuery({
      queryKey: jobKeys.list(),
      queryFn: () =>
        new Promise<JobDto[]>((resolve) => {
          finish = resolve;
        }),
    })
    .catch(() => undefined);
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={cache}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useCreateJob(), {
    wrapper,
  });
  const job = {
    id: 'accepted',
    jobType: 'upload',
    sourceFilename: 'Upload',
    isTerminal: false,
  } as JobDto;
  await act(async () => {
    result.current.seedJob({
      jobId: job.id,
      job,
    });
  });
  await act(async () => {
    finish([]);
    await pending;
  });
  expect(cache.getQueryData(jobKeys.list())).toEqual([
    job,
  ]);
});
