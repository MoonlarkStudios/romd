import type { JobDto } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { JobsTable } from './JobsTable';

const { rowRender } = vi.hoisted(() => ({ rowRender: vi.fn() }));
vi.mock('../../components/Jobs/JobActions', () => ({ JobActions: ({ job }: { job: JobDto }) => { rowRender(job.id); return null; } }));
vi.mock('../../hooks/usePermissions', () => ({ usePermissions: () => ({ hasRole: () => true }) }));

it('renders only the changed row on selection and retains every selection on refresh', async () => {
  const jobs = Array.from({ length: 50 }, (_, id) => ({ id: `${id}`, jobType: 'upload', sourceFilename: `file-${id}`, phase: 'Completed', isTerminal: true, hasErrors: false, errors: [] } as JobDto));
  const operations = { onCancel: vi.fn(), onArchive: vi.fn(), onRetry: vi.fn(), retryingId: null, actionsPending: false, runBatch: vi.fn().mockResolvedValue(null) };
  const { rerender } = render(<JobsTable jobs={jobs} operations={operations} />);
  expect(rowRender).toHaveBeenCalledTimes(50);
  rowRender.mockClear();
  await userEvent.click(screen.getByRole('checkbox', { name: 'Select Import file-0', exact: true }));
  expect(rowRender.mock.calls).toEqual([['0']]);
  rowRender.mockClear();
  await userEvent.click(screen.getByRole('checkbox', { name: 'Select Import file-1', exact: true }));
  expect(rowRender.mock.calls).toEqual([['1']]);
  rowRender.mockClear();
  // A new server snapshot must still update changed rows, even when selection is stable.
  rerender(<JobsTable jobs={jobs.map((job) => job.id === '2' ? { ...job, isArchived: true } : job)} operations={operations} />);
  expect(rowRender.mock.calls).toEqual([['2']]);
  expect(screen.getByRole('checkbox', { name: 'Select Import file-0', exact: true })).toBeChecked();
  expect(screen.getByRole('checkbox', { name: 'Select Import file-1', exact: true })).toBeChecked();
});
