import type { JobDto } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { ActivityCenter } from './ActivityCenter';

const { retryMutate, cancelMutate, archiveMutate } = vi.hoisted(() => ({
  retryMutate: vi.fn(),
  cancelMutate: vi.fn(),
  archiveMutate: vi.fn(),
}));

vi.mock('../../hooks/api/useJobs', () => {
  const base = (over: Record<string, unknown>): JobDto =>
    ({
      id: 'x',
      correlationId: 'c',
      sourceFilename: '',
      phase: '',
      progressPercent: '100',
      errors: [],
      isTerminal: true,
      hasErrors: false,
      ...over,
    }) as JobDto;

  const jobs = [
    base({
      id: 'r1',
      jobType: 'upload',
      sourceFilename: 'pack.zip',
      isTerminal: false,
      progressPercent: '40',
      phase: 'Hashing',
      nextAttemptAt: '2026-09-29T23:00:00Z',
      errors: [
        {
          item: 'deferral',
          message: 'Media inspection capacity is occupied',
          occurredAt: '',
          reason: 'Unknown',
        },
      ],
    }),
    base({
      id: 'n1',
      jobType: 'bulk_enrichment',
      sourceFilename: 'SNES',
      totalTitles: '10',
      failedCount: '3',
      hasErrors: true,
      errors: [
        {
          item: 'X',
          message: 'boom',
          occurredAt: '',
          reason: 'ProviderError',
        },
      ],
    }),
    base({
      id: 'e1',
      jobType: 'materialization',
      sourceFilename: 'Library Materialization',
    }),
    base({
      id: 'e2',
      jobType: 'materialization',
      sourceFilename: 'Family Room',
      phase: 'Deferred',
    }),
    base({
      id: 'e3',
      jobType: 'artwork-completion',
      sourceFilename: 'Fill missing artwork',
      total: 12,
      phase: 'Completed',
    }),
  ];

  return {
    useJobs: () => ({
      data: jobs,
      isLoading: false,
    }),
  };
});

vi.mock('../../hooks/api/useJobActions', () => ({
  useCancelJob: () => ({
    mutate: cancelMutate,
  }),
  useRetryJob: () => ({
    mutateAsync: retryMutate,
  }),
  useArchiveJob: () => ({
    mutateAsync: archiveMutate,
  }),
}));

vi.mock('../../hooks/realtime/JobSocketContext', () => ({
  useConnectionStatus: () => 'connected',
}));

describe('ActivityCenter', () => {
  it('badges running + needs-attention counts', () => {
    render(<ActivityCenter />);
    // 1 running + 1 failed = 2
    expect(screen.getByText('2')).toBeInTheDocument();
  });

  it('partitions jobs into running, needs-attention, and earlier zones', async () => {
    const user = userEvent.setup();
    render(<ActivityCenter />);

    await user.click(
      screen.getByRole('button', {
        name: 'Activity',
      }),
    );

    expect(await screen.findByText('Running now')).toBeInTheDocument();
    expect(screen.getByText('Import pack.zip')).toBeInTheDocument();
    expect(screen.getByText('Needs attention')).toBeInTheDocument();
    expect(screen.getByText('Enrich 10 titles · SNES')).toBeInTheDocument();
    expect(screen.getByText('Earlier')).toBeInTheDocument();
    expect(screen.getByText('Materialize library')).toBeInTheDocument();
  });

  it('shows an upload retry reason without counting a failed file', async () => {
    const user = userEvent.setup();
    render(<ActivityCenter />);
    await user.click(
      screen.getByRole('button', {
        name: 'Activity',
      }),
    );
    expect(await screen.findByText('Retry scheduled')).toBeInTheDocument();
    expect(screen.getByText('Media inspection capacity is occupied')).toBeInTheDocument();
    expect(screen.queryByText(/failed so far/)).not.toBeInTheDocument();
  });

  it('marks a deferred materialization distinctly in the earlier zone', async () => {
    const user = userEvent.setup();
    render(<ActivityCenter />);

    await user.click(
      screen.getByRole('button', {
        name: 'Activity',
      }),
    );

    expect(await screen.findByText('Earlier')).toBeInTheDocument();
    expect(screen.getByText('Materialize Family Room')).toBeInTheDocument();
    expect(screen.getByText('Deferred')).toBeInTheDocument();
  });

  it('names an enrichment run with the shared job title', async () => {
    const user = userEvent.setup();
    render(<ActivityCenter />);

    await user.click(
      screen.getByRole('button', {
        name: 'Activity',
      }),
    );

    expect(await screen.findByText('Enrichment run · 12 titles')).toBeInTheDocument();
    expect(screen.queryByText('Fill missing artwork')).not.toBeInTheDocument();
  });

  it('retries a failed job from the needs-attention zone', async () => {
    const user = userEvent.setup();
    retryMutate.mockResolvedValue({
      kind: 'items',
      requeued: 3,
    });
    render(<ActivityCenter />);

    await user.click(
      screen.getByRole('button', {
        name: 'Activity',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: /retry 3 failed/i,
      }),
    );

    expect(retryMutate).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 'n1',
      }),
    );
  });
});
