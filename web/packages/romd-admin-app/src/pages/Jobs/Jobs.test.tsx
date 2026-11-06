import type { JobDto, ListJobsData } from '@romd/admin-api-client';
import { act, fireEvent, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { selectionReconciliationJob } from '../../test/fixtures/mediaReconciliationJobs';
import { render } from '../../test/utils/render';
import { Jobs } from './index';

const { state, history, archive, retry, cancel, refetch } = vi.hoisted(() => ({
  state: {
    available: true,
    error: false,
    manager: true,
    admin: true,
    nextCursor: 'next-page' as string | null,
    items: null as JobDto[] | null,
  },
  history: vi.fn(),
  archive: vi.fn(),
  retry: vi.fn(),
  cancel: vi.fn(),
  refetch: vi.fn(),
}));
const job = {
  id: 'u1',
  jobType: 'upload',
  correlationId: 'correlation',
  sourceFilename: 'pack.zip',
  phase: 'Completed',
  isTerminal: true,
  hasErrors: false,
  errors: [],
  progressPercent: 100,
} as JobDto;
vi.mock('../../hooks/api/useJobHistory', () => ({
  useJobHistory: (filters: ListJobsData['query']) => {
    history(filters);
    return {
      data: state.available
        ? {
            items: state.items ?? [
              job,
            ],
            nextCursor: state.nextCursor,
            hasNextPage: !!state.nextCursor,
          }
        : undefined,
      error: new Error('Unavailable'),
      isError: state.error,
      isPending: false,
      dataUpdatedAt: 1,
      refetch,
    };
  },
}));
vi.mock('../../hooks/api/useJobActions', () => ({
  useArchiveJob: () => ({
    mutateAsync: archive,
  }),
  useCancelJob: () => ({
    mutateAsync: cancel,
  }),
  useRetryJob: () => ({
    mutateAsync: retry,
  }),
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    hasRole: (role: string) => (role === 'Admin' ? state.admin && state.manager : state.manager),
  }),
}));

beforeEach(() => {
  Object.assign(state, {
    available: true,
    error: false,
    manager: true,
    admin: true,
    nextCursor: 'next-page',
    items: null,
  });
  vi.resetAllMocks();
  archive.mockResolvedValue(undefined);
  cancel.mockResolvedValue(undefined);
  retry.mockResolvedValue({
    kind: 'items',
    requeued: 1,
  });
});

describe('media convergence history', () => {
  it('offers media type filters to admins', async () => {
    render(<Jobs />);
    await userEvent.selectOptions(
      screen.getByRole('combobox', {
        name: 'Job type',
      }),
      'media-reconciliation',
    );
    expect(history).toHaveBeenLastCalledWith(
      expect.objectContaining({
        jobType: 'media-reconciliation',
      }),
    );
  });
  it('hides media filters from managers without Admin', async () => {
    state.admin = false;
    render(<Jobs />);
    await userEvent.click(screen.getByLabelText('Job type'));
    expect(
      screen.queryByRole('option', {
        name: 'Media reconciliation',
        exact: true,
      }),
    ).not.toBeInTheDocument();
  });
  it('passes superseded as its own outcome filter', async () => {
    render(<Jobs />);
    await userEvent.selectOptions(
      screen.getByRole('combobox', {
        name: 'Outcome',
      }),
      'superseded',
    );
    expect(history).toHaveBeenLastCalledWith(
      expect.objectContaining({
        outcome: 'superseded',
      }),
    );
  });
  it('shows active bulk progress with selection and failure counters', () => {
    state.items = [
      selectionReconciliationJob(),
    ];
    render(<Jobs />);
    expect(screen.getAllByText('3 / 6 finished · 1 failed · 2 skipped')).toHaveLength(2);
    expect(
      screen
        .getByRole('table', {
          name: 'Job history',
        })
        .querySelectorAll('[aria-label="Job progress"]'),
    ).toHaveLength(2);
  });
});

describe('job history', () => {
  it('reads URL filters and links directly to a job', () => {
    render(<Jobs />, {
      routerOptions: {
        initialEntries: [
          '/jobs?outcome=failed&archive=archived&type=upload&search=pack&from=2026-09-01',
        ],
      },
    });
    expect(history).toHaveBeenLastCalledWith(
      expect.objectContaining({
        outcome: 'failed',
        archive: 'archived',
        jobType: 'upload',
        query: 'pack',
        from: '2026-09-01T00:00:00Z',
      }),
    );
    expect(
      screen.getByRole('link', {
        name: 'Import pack.zip',
      }),
    ).toHaveAttribute('href', '/jobs/u1');
  });
  it('labels job type filters with the same vocabulary as job rows', async () => {
    render(<Jobs />);
    const types = screen.getByRole('combobox', {
      name: 'Job type',
    });
    expect(
      within(types)
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(
      expect.arrayContaining([
        'Enrichment run',
        'Title enrichment',
        'Bulk enrichment (legacy)',
        'Artwork import',
      ]),
    );
    expect(
      within(types).queryByRole('option', {
        name: 'Artwork completion',
      }),
    ).not.toBeInTheDocument();
    await userEvent.selectOptions(types, 'Enrichment run');
    expect(history).toHaveBeenLastCalledWith(
      expect.objectContaining({
        jobType: 'artwork-completion',
      }),
    );
  });
  it('pages with the server cursor and resets pagination when filters change', async () => {
    render(<Jobs />);
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Older results',
      }),
    );
    expect(history).toHaveBeenLastCalledWith(
      expect.objectContaining({
        cursor: 'next-page',
      }),
    );
    await userEvent.selectOptions(
      screen.getByRole('combobox', {
        name: 'Outcome',
      }),
      'deferred',
    );
    expect(history).toHaveBeenLastCalledWith(
      expect.objectContaining({
        outcome: 'deferred',
        cursor: undefined,
      }),
    );
  });
  it('disables pagination when the server has no next page', () => {
    state.nextCursor = null;
    render(<Jobs />);
    expect(
      screen.getByRole('button', {
        name: 'Older results',
      }),
    ).toBeDisabled();
  });
  it('shows failed reads as errors, not empty history', async () => {
    state.available = false;
    state.error = true;
    render(<Jobs />);
    expect(screen.getByText('Could not load jobs')).toBeInTheDocument();
    expect(screen.queryByText('No matching jobs')).not.toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Refresh',
      }),
    );
    expect(refetch).toHaveBeenCalledOnce();
  });
  it('retains history when refresh fails', () => {
    state.error = true;
    render(<Jobs />);
    expect(screen.getByText('Could not refresh jobs')).toBeInTheDocument();
    expect(
      screen.getByRole('link', {
        name: 'Import pack.zip',
      }),
    ).toBeInTheDocument();
  });
  it('archives only the chosen job and never offers permanent deletion', async () => {
    render(<Jobs />);
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Actions for Import pack.zip',
      }),
    );
    expect(
      screen.queryByRole('menuitem', {
        name: 'Delete',
      }),
    ).not.toBeInTheDocument();
    await userEvent.click(
      await screen.findByRole('menuitem', {
        name: 'Archive',
      }),
    );
    expect(archive).toHaveBeenCalledExactlyOnceWith('u1');
  });
});

it('retains simultaneous filter updates before navigation renders', () => {
  render(<Jobs />, {
    routerOptions: {
      initialEntries: [
        '/jobs?outcome=failed',
      ],
    },
  });
  act(() => {
    fireEvent.change(
      screen.getByRole('combobox', {
        name: 'Outcome',
      }),
      {
        target: {
          value: '',
        },
      },
    );
    fireEvent.change(
      screen.getByRole('combobox', {
        name: 'History',
      }),
      {
        target: {
          value: 'all',
        },
      },
    );
  });
  expect(history).toHaveBeenLastCalledWith(
    expect.objectContaining({
      outcome: undefined,
      archive: 'all',
    }),
  );
});

describe('job table selection', () => {
  const active = {
    ...job,
    id: 'active',
    sourceFilename: 'active.zip',
    phase: 'Pending',
    isTerminal: false,
  } as JobDto;
  const failed = {
    ...job,
    id: 'failed',
    sourceFilename: 'failed.zip',
    phase: 'Failed',
    hasErrors: true,
  } as JobDto;

  it('archives only eligible selected jobs, reports failures, and keeps failures selected', async () => {
    state.items = [
      job,
      active,
      failed,
    ];
    archive.mockImplementation(async (id: string) => {
      if (id === 'failed') throw new Error('Unavailable');
    });
    render(<Jobs />);
    await userEvent.click(
      screen.getByRole('checkbox', {
        name: 'Select this page',
      }),
    );
    expect(
      screen.getByRole('checkbox', {
        name: 'Select this page',
      }),
    ).toBeChecked();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Actions',
        exact: true,
      }),
    );
    await userEvent.click(
      await screen.findByRole('menuitem', {
        name: 'Archive selected (2)',
      }),
    );
    expect(
      await screen.findByText('2 eligible jobs will be updated. 1 selected jobs will be skipped.'),
    ).toBeInTheDocument();
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Confirm archive',
      }),
    );
    expect(await screen.findByText('1 succeeded · 1 skipped · 1 failed.')).toBeInTheDocument();
    expect(archive.mock.calls.map(([id]) => id)).toEqual([
      'u1',
      'failed',
    ]);
    expect(
      screen.getByRole('checkbox', {
        name: 'Select Import failed.zip',
      }),
    ).toBeChecked();
    expect(
      screen.getByRole('checkbox', {
        name: 'Select Import pack.zip',
      }),
    ).not.toBeChecked();
    expect(
      screen.getByRole('checkbox', {
        name: 'Select Import active.zip',
      }),
    ).not.toBeChecked();
    expect(
      screen.getByRole('checkbox', {
        name: 'Select this page',
      }),
    ).toBePartiallyChecked();
  });

  it('clears selection on pagination and filter changes', async () => {
    render(<Jobs />);
    await userEvent.click(
      screen.getByRole('checkbox', {
        name: 'Select this page',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Older results',
      }),
    );
    expect(
      screen.getByRole('button', {
        name: 'Actions',
        exact: true,
      }),
    ).toBeDisabled();
    await userEvent.click(
      screen.getByRole('checkbox', {
        name: 'Select this page',
      }),
    );
    await userEvent.selectOptions(
      screen.getByRole('combobox', {
        name: 'Outcome',
      }),
      'completed',
    );
    expect(
      screen.getByRole('button', {
        name: 'Actions',
        exact: true,
      }),
    ).toBeDisabled();
  });

  it('cancels only selected active jobs after confirmation', async () => {
    state.items = [
      job,
      active,
    ];
    render(<Jobs />);
    await userEvent.click(
      screen.getByRole('checkbox', {
        name: 'Select this page',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Actions',
        exact: true,
      }),
    );
    await userEvent.click(
      await screen.findByRole('menuitem', {
        name: 'Cancel selected (1)',
      }),
    );
    expect(cancel).not.toHaveBeenCalled();
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Confirm cancel',
      }),
    );
    expect(await screen.findByText('1 succeeded · 1 skipped · 0 failed.')).toBeInTheDocument();
    expect(cancel).toHaveBeenCalledExactlyOnceWith('active');
  });

  it('restricts retry to managers and accounts for no remaining retryable failures', async () => {
    state.items = [
      {
        ...job,
        id: 'bulk',
        jobType: 'bulk_enrichment',
        hasErrors: true,
        failedCount: 2,
      } as JobDto,
    ];
    state.manager = false;
    const { unmount } = render(<Jobs />);
    await userEvent.click(
      screen.getByRole('checkbox', {
        name: 'Select this page',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Actions',
        exact: true,
      }),
    );
    expect(
      await screen.findByRole('menuitem', {
        name: 'Archive selected (1)',
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('menuitem', {
        name: /Retry selected/,
      }),
    ).not.toBeInTheDocument();
    unmount();
    state.manager = true;
    render(<Jobs />);
    await userEvent.click(
      screen.getByRole('checkbox', {
        name: 'Select this page',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Actions',
        exact: true,
      }),
    );
    retry.mockResolvedValue({
      kind: 'items',
      requeued: 0,
    });
    await userEvent.click(
      await screen.findByRole('menuitem', {
        name: 'Retry selected (1)',
      }),
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Confirm retry',
      }),
    );
    expect(await screen.findByText('0 succeeded · 1 skipped · 0 failed.')).toBeInTheDocument();
    expect(retry).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({
        id: 'bulk',
      }),
    );
  });

  it('keeps batch results visible when archiving empties the page', async () => {
    const { rerender } = render(<Jobs />);
    await userEvent.click(
      screen.getByRole('checkbox', {
        name: 'Select this page',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Actions',
        exact: true,
      }),
    );
    await userEvent.click(
      await screen.findByRole('menuitem', {
        name: 'Archive selected (1)',
      }),
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Confirm archive',
      }),
    );
    expect(await screen.findByText('1 succeeded · 0 skipped · 0 failed.')).toBeInTheDocument();
    state.items = [];
    rerender(<Jobs />);
    expect(screen.getByText('No matching jobs')).toBeInTheDocument();
    expect(screen.getByText('1 succeeded · 0 skipped · 0 failed.')).toBeInTheDocument();
  });

  it('prevents duplicate submission and locks selection while a batch is pending', async () => {
    let finish!: () => void;
    archive.mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          finish = resolve;
        }),
    );
    render(<Jobs />);
    await userEvent.click(
      screen.getByRole('checkbox', {
        name: 'Select this page',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Actions',
        exact: true,
      }),
    );
    await userEvent.click(
      await screen.findByRole('menuitem', {
        name: 'Archive selected (1)',
      }),
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Confirm archive',
      }),
    );
    expect(
      within(screen.getByRole('dialog')).getByRole('button', {
        name: 'Confirm archive',
      }),
    ).toBeDisabled();
    expect(document.querySelector('input[aria-label="Select this page"]')).toBeDisabled();
    await act(async () => {
      finish();
    });
    expect(await screen.findByText('1 succeeded · 0 skipped · 0 failed.')).toBeInTheDocument();
    expect(archive).toHaveBeenCalledOnce();
  });
});
