import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, useLocation } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  selectionReconciliationJob,
  subjectReconciliationJob,
} from '../test/fixtures/mediaReconciliationJobs';
import { useJobOperations } from './useJobOperations';

const { cancel, archive, retry, notify, role } = vi.hoisted(() => ({
  cancel: vi.fn(),
  archive: vi.fn(),
  retry: vi.fn(),
  notify: vi.fn(),
  role: {
    admin: true,
  },
}));
vi.mock('@mantine/notifications', () => ({
  notifications: {
    show: notify,
  },
}));
vi.mock('./api/useJobActions', () => ({
  useCancelJob: () => ({
    mutateAsync: cancel,
    isPending: false,
  }),
  useArchiveJob: () => ({
    mutateAsync: archive,
    isPending: false,
  }),
  useRetryJob: () => ({
    mutateAsync: retry,
    isPending: false,
  }),
}));
vi.mock('./usePermissions', () => ({
  usePermissions: () => ({
    hasRole: (name: string) => (name === 'Admin' ? role.admin : true),
  }),
}));
function setup() {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <MemoryRouter
      initialEntries={[
        '/jobs/old',
      ]}
    >
      {children}
    </MemoryRouter>
  );
  return renderHook(
    () => ({
      operations: useJobOperations(),
      location: useLocation(),
    }),
    {
      wrapper,
    },
  );
}
beforeEach(() => {
  vi.clearAllMocks();
  role.admin = true;
  cancel.mockResolvedValue(undefined);
  archive.mockResolvedValue(undefined);
  retry.mockResolvedValue({
    kind: 'items',
    requeued: 1,
  });
});
describe('media job operations', () => {
  it.each([
    subjectReconciliationJob(),
    selectionReconciliationJob(),
  ])('acknowledges $jobType cancellation without marking it terminal', async (job) => {
    const { result } = setup();
    await act(async () => {
      await result.current.operations.onCancel(job);
    });
    expect(cancel).toHaveBeenCalledWith(job.id);
    expect(notify).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Cancellation requested',
        message: expect.stringContaining('remains active'),
      }),
    );
    expect(job.isTerminal).toBe(false);
  });
  it('follows an adopted current-source retry even when no new job was created', async () => {
    const target = '11111111-2222-3333-4444-555555555555';
    retry.mockResolvedValue({
      kind: 'media',
      jobId: target,
    });
    const { result } = setup();
    await act(async () => {
      await result.current.operations.onRetry(
        subjectReconciliationJob({
          phase: 'Cancelled',
          isTerminal: true,
        }),
      );
    });
    expect(result.current.location.pathname).toBe(`/jobs/${target}`);
    expect(notify).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Media retry accepted',
      }),
    );
    expect(result.current.operations.retryingId).toBeNull();
  });
  it('keeps the current page and surfaces a failed retry', async () => {
    retry.mockRejectedValue(new Error('The source is unavailable.'));
    const { result } = setup();
    await act(async () => {
      await result.current.operations.onRetry(
        subjectReconciliationJob({
          phase: 'Failed',
          isTerminal: true,
        }),
      );
    });
    expect(result.current.location.pathname).toBe('/jobs/old');
    expect(notify).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Retry failed',
        message: 'The source is unavailable.',
      }),
    );
  });
  it('denies manager media actions and accepts an administrator retry of a finite selection', async () => {
    role.admin = false;
    const { result } = setup();
    await act(async () => {
      const response = await result.current.operations.runBatch(
        [
          subjectReconciliationJob(),
          selectionReconciliationJob(),
        ],
        'cancel',
        true,
      );
      expect(response).toEqual({
        action: 'cancel',
        succeeded: 0,
        skipped: 2,
        failed: [],
      });
    });
    expect(cancel).not.toHaveBeenCalled();
    role.admin = true;
    retry.mockResolvedValue({
      kind: 'media',
      jobId: '11111111-2222-4333-8444-555555555555',
    });
    await act(async () => {
      await result.current.operations.runBatch(
        [
          selectionReconciliationJob({
            phase: 'Failed',
            isTerminal: true,
          }),
        ],
        'retry',
        true,
      );
    });
    expect(retry).toHaveBeenCalledWith(
      expect.objectContaining({
        requestMode: 'Selection',
      }),
    );
  });
  it('counts an adopted disc retry as an accepted batch action', async () => {
    retry.mockResolvedValue({
      kind: 'media',
      jobId: '11111111-2222-3333-4444-555555555555',
    });
    const { result } = setup();
    await act(async () => {
      const response = await result.current.operations.runBatch(
        [
          subjectReconciliationJob({
            phase: 'Superseded',
            isTerminal: true,
          }),
        ],
        'retry',
        true,
      );
      expect(response?.succeeded).toBe(1);
      expect(response?.skipped).toBe(0);
    });
    expect(result.current.location.pathname).toBe('/jobs/old');
  });
});
