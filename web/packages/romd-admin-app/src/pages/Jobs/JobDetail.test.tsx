import { MantineProvider } from '@mantine/core';
import type { Job } from '@romd/admin-api-client';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import {
  selectionReconciliationJob,
  subjectReconciliationJob,
} from '../../test/fixtures/mediaReconciliationJobs';
import { JobDetail } from './JobDetail';

const { state, read, retry } = vi.hoisted(() => ({
  state: {
    manager: true,
    admin: true,
    job: undefined as Job | undefined,
  },
  read: vi.fn(),
  retry: vi.fn(),
}));
vi.mock('../../hooks/api/useJobs', () => ({
  useJob: (id: string) => {
    read(id);
    return {
      data: state.job,
      isError: !state.job,
      isPending: false,
      refetch: vi.fn(),
    };
  },
}));
vi.mock('../../hooks/useJobOperations', () => ({
  useJobOperations: () => ({
    onCancel: vi.fn(),
    onArchive: vi.fn(),
    onRetry: retry,
    retryingId: null,
    actionsPending: false,
  }),
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    hasRole: (role: string) => (role === 'Admin' ? state.admin && state.manager : state.manager),
  }),
}));
// jsdom has no layout, so outside the test env Mantine hides an open menu once floating-ui reports its target as hidden.
function show() {
  render(
    <MantineProvider env="test">
      <MemoryRouter
        initialEntries={[
          '/jobs/old-job',
        ]}
      >
        <Routes>
          <Route
            path="/jobs/:jobId"
            element={<JobDetail />}
          />
        </Routes>
      </MemoryRouter>
    </MantineProvider>,
  );
}
beforeEach(() => {
  state.manager = true;
  state.admin = true;
  state.job = {
    id: 'old-job',
    jobType: 'bulk_enrichment',
    phase: 'Failed',
    sourceFilename: 'SNES',
    failedCount: 2,
    isTerminal: true,
    hasErrors: true,
    isArchived: true,
    correlationId: 'trace-1',
    systemKey: 'platform-1',
    errors: [
      {
        item: 'Game',
        message: 'Provider unavailable',
        occurredAt: '2026-09-10T00:00:00Z',
      },
    ],
  } as Job;
  vi.clearAllMocks();
});
it('loads an archived job outside the recent list and exposes investigation evidence', () => {
  show();
  expect(read).toHaveBeenCalledWith('old-job');
  expect(screen.getByText('Archived job')).toBeInTheDocument();
  expect(screen.getByText('Provider unavailable')).toBeInTheDocument();
  expect(screen.getByText('trace-1')).toBeInTheDocument();
  expect(
    screen.getByRole('link', {
      name: 'Open system sources',
    }),
  ).toHaveAttribute('href', '/systems/platform-1?tab=sources');
});
it('gates scoped retries by role', async () => {
  show();
  await userEvent.click(
    screen.getByRole('button', {
      name: /^Actions for/,
    }),
  );
  await userEvent.click(
    await screen.findByRole('menuitem', {
      name: 'Retry 2 failed',
    }),
  );
  expect(retry).toHaveBeenCalledWith(state.job);
});
it('does not offer retries to a non-manager', () => {
  state.manager = false;
  show();
  expect(
    screen.queryByRole('button', {
      name: /^Actions for/,
    }),
  ).not.toBeInTheDocument();
});
it('explains missing or inaccessible jobs', () => {
  state.job = undefined;
  show();
  expect(screen.getByText('Could not load job')).toBeInTheDocument();
});
it('opens an enrichment run in its workspace panel', () => {
  state.job = {
    id: 'run-1',
    jobType: 'artwork-completion',
    phase: 'Completed',
    sourceFilename: 'Fill missing artwork',
    total: 12,
    isTerminal: true,
    correlationId: 'run-1',
    errors: [],
  } as Job;
  show();
  expect(
    screen.getByRole('link', {
      name: 'Open this run',
    }),
  ).toHaveAttribute('href', '/enrichment/run-1');
  expect(screen.queryByText('Review artwork results')).not.toBeInTheDocument();
});

it('shows bulk selection, adopted children and honest completion counters', () => {
  state.job = selectionReconciliationJob();
  show();
  expect(screen.getByText('Selection complete · Maximum 100')).toBeInTheDocument();
  expect(screen.getByText('Selected 8 · Created 4 · Adopted 2')).toBeInTheDocument();
  expect(screen.getByText('Finished 3 · Failed 1 · Skipped 2')).toBeInTheDocument();
  expect(screen.getByText('Requested conversion: No')).toBeInTheDocument();
  expect(
    screen.getByRole('link', {
      name: 'Open media storage',
    }),
  ).toHaveAttribute('href', '/storage');
});
it('shows a deferred active disc and its next attempt without claiming it finished', () => {
  state.job = subjectReconciliationJob({
    phase: 'Pending',
    nextAttemptAt: '2026-10-02T15:00:00Z',
  });
  show();
  expect(screen.getByText('Deferred')).toBeInTheDocument();
  expect(screen.getAllByText(/Next attempt/)).toHaveLength(2);
  expect(screen.queryByText(/^Finished /)).not.toBeInTheDocument();
});
it.each([
  [
    'Completed',
    'Completed',
  ],
  [
    'Failed',
    'Failed',
  ],
  [
    'Superseded',
    'Superseded',
  ],
  [
    'Cancelled',
    'Cancelled',
  ],
])('shows the durable %s outcome and exact bounded reason code', (phase, outcome) => {
  state.job = subjectReconciliationJob({
    phase,
    outcome,
    isTerminal: true,
    outcomeCode: 'source-changed',
  });
  show();
  expect(screen.getByText(`Outcome: ${outcome}`)).toBeInTheDocument();
  expect(screen.getByText('source-changed')).toBeInTheDocument();
  expect(screen.queryByText(/^Failure:/)).not.toBeInTheDocument();
});
it.each([
  'OriginalLayout',
  'CatalogCdLayout',
])('shows the %s subject through its own workspace', (kind) => {
  state.job = subjectReconciliationJob({
    subject: {
      kind,
      id: kind === 'OriginalLayout' ? 'layout1' : 'file1',
    },
    sourceFilename: 'Disc.cue',
    phase: 'Failed',
    outcome: 'Failed',
    isTerminal: true,
    outcomeCode: 'measurement-mismatch',
  });
  show();
  expect(
    screen.getByText(`Subject: ${kind} · ${kind === 'OriginalLayout' ? 'layout1' : 'file1'}`),
  ).toBeInTheDocument();
  expect(screen.getByText('measurement-mismatch')).toBeInTheDocument();
  expect(
    screen.getByRole('link', {
      name: 'Open media storage',
    }),
  ).toHaveAttribute('href', '/storage');
  const layoutLink = screen.queryByRole('link', {
    name: 'Open disc layouts',
  });
  if (kind === 'OriginalLayout') expect(layoutLink).toHaveAttribute('href', '/disc-layouts');
  else expect(layoutLink).not.toBeInTheDocument();
});
it('keeps an unrecognized reason code visible without inventing a failure explanation', () => {
  state.job = subjectReconciliationJob({
    phase: 'Failed',
    outcome: 'Failed',
    isTerminal: true,
    outcomeCode: 'future-code',
  });
  show();
  expect(screen.getByText('future-code')).toBeInTheDocument();
  expect(screen.queryByText(/Unrecognized code/)).not.toBeInTheDocument();
});
it('offers current-source retries for cancelled discs', async () => {
  state.job = subjectReconciliationJob({
    phase: 'Cancelled',
    isTerminal: true,
  });
  show();
  await userEvent.click(
    screen.getByRole('button', {
      name: /^Actions for/,
    }),
  );
  await userEvent.click(
    await screen.findByRole('menuitem', {
      name: 'Retry media',
    }),
  );
  expect(retry).toHaveBeenCalledWith(state.job);
});
it('hides media actions from a manager without Admin', () => {
  state.admin = false;
  state.job = subjectReconciliationJob({
    phase: 'Failed',
    isTerminal: true,
  });
  show();
  expect(
    screen.queryByRole('button', {
      name: /^Actions for/,
    }),
  ).not.toBeInTheDocument();
});
it('offers the same exact retry action for a failed finite selection', async () => {
  state.job = selectionReconciliationJob({
    phase: 'Failed',
    isTerminal: true,
    hasErrors: true,
  });
  show();
  await userEvent.click(
    screen.getByRole('button', {
      name: /^Actions for/,
    }),
  );
  await userEvent.click(
    await screen.findByRole('menuitem', {
      name: 'Retry media',
    }),
  );
  expect(retry).toHaveBeenCalledWith(state.job);
});
