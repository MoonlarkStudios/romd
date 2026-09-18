import type { JobDto } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useJobNotifications } from './useJobNotifications';

const { showSpy, updateSpy, hideSpy, jobsState } = vi.hoisted(() => ({
  showSpy: vi.fn(),
  updateSpy: vi.fn(),
  hideSpy: vi.fn(),
  jobsState: { jobs: [] as unknown[] },
}));

vi.mock('@mantine/notifications', () => ({
  notifications: { show: showSpy, update: updateSpy, hide: hideSpy },
}));

vi.mock('../api/useJobs', () => ({
  useJobs: () => ({ data: jobsState.jobs }),
}));

interface NotificationProps {
  title?: string;
  message?: string;
  color?: string;
}

function makeJob(overrides: Record<string, unknown>): JobDto {
  return {
    id: 'm1',
    correlationId: 'c1',
    jobType: 'materialization',
    sourceFilename: 'Family Room',
    phase: 'Materializing',
    progressPercent: '50',
    errors: [],
    isTerminal: false,
    hasErrors: false,
    ...overrides,
  } as JobDto;
}

function renderNotificationsHook() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>{children}</MemoryRouter>
    </QueryClientProvider>
  );
  return renderHook(() => useJobNotifications(), { wrapper });
}

function allTerminalCalls(): NotificationProps[] {
  return [...showSpy.mock.calls, ...updateSpy.mock.calls].map(
    (call) => call[0] as NotificationProps,
  );
}

describe('useJobNotifications', () => {
  beforeEach(() => {
    showSpy.mockClear();
    updateSpy.mockClear();
    hideSpy.mockClear();
    jobsState.jobs = [];
  });

  it('shows a neutral deferred notification, not the green success toast, on a deferred transition', () => {
    jobsState.jobs = [makeJob({})];
    const { rerender } = renderNotificationsHook();

    jobsState.jobs = [makeJob({ isTerminal: true, phase: 'Deferred', progressPercent: '100' })];
    rerender();

    expect(updateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Materialization: Deferred',
        message: 'Family Room: catalog rebuild pending — will retry automatically',
        color: 'gray',
      }),
    );
    const calls = allTerminalCalls();
    expect(calls.some((props) => props.color === 'green')).toBe(false);
    expect(calls.some((props) => props.message?.includes('processed successfully'))).toBe(false);
  });

  it('shows the deferred notification for a job that appears already terminal', () => {
    const { rerender } = renderNotificationsHook();

    jobsState.jobs = [makeJob({ id: 'm2', isTerminal: true, phase: 'Deferred' })];
    rerender();

    expect(showSpy).toHaveBeenCalledWith(
      expect.objectContaining({ title: 'Materialization: Deferred', color: 'gray' }),
    );
    expect(allTerminalCalls().some((props) => props.color === 'green')).toBe(false);
  });

  it('still shows the green success toast for a completed materialization', () => {
    jobsState.jobs = [makeJob({})];
    const { rerender } = renderNotificationsHook();

    jobsState.jobs = [makeJob({ isTerminal: true, phase: 'Completed', progressPercent: '100' })];
    rerender();

    expect(updateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Materialization: Complete',
        message: 'Family Room processed successfully',
        color: 'green',
      }),
    );
  });
});


it.each([
  ['Cancelled', 'Cancelled', 'gray'],
  ['Failed', 'Failed', 'red'],
  ['CompletedWithErrors', 'Completed with errors', 'yellow'],
])('reports %s without a success notification', (phase, label, color) => {
  updateSpy.mockClear();
  jobsState.jobs = [makeJob({})];
  const { rerender } = renderNotificationsHook();
  jobsState.jobs = [makeJob({ isTerminal: true, phase })];
  rerender();
  expect(updateSpy).toHaveBeenCalledWith(expect.objectContaining({ title: `Materialization: ${label}`, color }));
});
