import type { JobDto } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { downloadExportFile } from '../../hooks/api/useExport';
import { render } from '../../test/utils/render';
import { JobActions } from './JobActions';

vi.mock('../../hooks/api/useExport', () => ({
  downloadExportFile: vi.fn(),
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    hasRole: () => true,
  }),
}));
const completed = {
  id: 'export',
  jobType: 'export',
  phase: 'Completed',
  isTerminal: true,
  hasDownload: true,
  hasErrors: false,
  totalFiles: '1',
  format: 'rvz',
} as JobDto;
const callbacks = {
  onCancel: vi.fn(),
  onArchive: vi.fn(),
  onRetry: vi.fn(),
  retryingId: null,
  actionsPending: false,
};
beforeEach(() => vi.resetAllMocks());

it('downloads the existing completed ZIP once and disables actions during authorization', async () => {
  let finish: () => void = () => {};
  vi.mocked(downloadExportFile).mockImplementationOnce(
    () =>
      new Promise<void>((resolve) => {
        finish = resolve;
      }),
  );
  render(
    <JobActions
      {...callbacks}
      job={completed}
    />,
  );
  const user = userEvent.setup();
  const actions = screen.getByRole('button', {
    name: 'Actions for Export 1 files',
  });
  await user.click(actions);
  await user.click(
    screen.getByRole('menuitem', {
      name: 'Download',
    }),
  );
  expect(downloadExportFile).toHaveBeenCalledExactlyOnceWith('export');
  expect(actions).toBeDisabled();
  finish();
  await waitFor(() => expect(actions).toBeEnabled());
});

it.each([
  {
    ...completed,
    isTerminal: false,
  },
  {
    ...completed,
    hasDownload: false,
  },
])('does not offer an unfinished or missing export', async (job) => {
  render(
    <JobActions
      {...callbacks}
      job={job}
    />,
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: /Actions for/,
    }),
  );
  expect(
    screen.queryByRole('menuitem', {
      name: 'Download',
    }),
  ).not.toBeInTheDocument();
  expect(downloadExportFile).not.toHaveBeenCalled();
});

it('offers no unsupported cancel or retry for an active metadata update', () => {
  render(
    <JobActions
      {...callbacks}
      job={
        {
          ...completed,
          jobType: 'metadata-rematerialization',
          isTerminal: false,
          hasDownload: false,
          phase: 'Rematerializing',
        } as JobDto
      }
    />,
  );
  expect(
    screen.queryByRole('button', {
      name: /Actions for/,
    }),
  ).not.toBeInTheDocument();
});

it('offers archive but no unsupported retry after a metadata update defers titles, even for managers', async () => {
  render(
    <JobActions
      {...callbacks}
      job={
        {
          ...completed,
          jobType: 'metadata-rematerialization',
          hasDownload: false,
          phase: 'CompletedWithErrors',
          hasErrors: true,
          deferredCount: 2,
          failedCount: '2',
        } as JobDto
      }
    />,
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: /Actions for/,
    }),
  );
  expect(
    screen.getByRole('menuitem', {
      name: 'Archive',
    }),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('menuitem', {
      name: /Cancel|Retry/,
    }),
  ).not.toBeInTheDocument();
});
