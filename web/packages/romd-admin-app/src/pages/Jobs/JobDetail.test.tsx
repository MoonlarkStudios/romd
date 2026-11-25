import { MantineProvider } from '@mantine/core';
import type { Job } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import {
  selectionReconciliationJob,
  subjectReconciliationJob,
} from '../../test/fixtures/mediaReconciliationJobs';
import { JobDetail } from './JobDetail';

const { state, read, retry, refetch } = vi.hoisted(() => ({
  state: {
    manager: true,
    admin: true,
    serverFolder: undefined as string | undefined,
    lastServerImport: undefined as string | undefined,
    job: undefined as Job | undefined,
  },
  read: vi.fn(),
  retry: vi.fn(),
  refetch: vi.fn(),
}));
vi.mock('../../hooks/api/useJobs', () => ({
  useJob: (id: string) => {
    read(id);
    return {
      data: state.job,
      isError: !state.job,
      isPending: false,
      refetch,
    };
  },
}));
vi.mock('../../hooks/api/useServerImport', () => ({
  useServerImportStatus: () => ({
    data: {
      directory: state.serverFolder,
      lastJobId: state.lastServerImport,
      canRetry: true,
    },
  }),
}));
vi.mock('../../hooks/api/useJobItems', () => ({
  jobItemsKey: (id: string) => [
    'job-items',
    id,
  ],
  useJobItems: () => ({
    data: {
      items: [],
    },
  }),
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
    canManageTitles: state.manager,
    hasRole: (role: string) => (role === 'Admin' ? state.admin && state.manager : state.manager),
  }),
}));
vi.mock('../../components/Enrichment/RunPanel', () => ({
  RunPanel: ({ job }: { job: Job }) => <section aria-label="Enrichment run">{job.id}</section>,
}));
// jsdom has no layout, so outside the test env Mantine hides an open menu once floating-ui reports its target as hidden.
function show(entry = '/jobs/old-job') {
  const cache = new QueryClient();
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <MemoryRouter
          initialEntries={[
            entry,
          ]}
        >
          <Routes>
            <Route
              path="/jobs/:jobId"
              element={<JobDetail />}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
  return cache;
}
beforeEach(() => {
  state.manager = true;
  state.admin = true;
  state.serverFolder = undefined;
  state.lastServerImport = undefined;
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
const artworkRun: Job = {
  id: 'run-1',
  jobType: 'artwork-completion',
  phase: 'Completed',
  sourceFilename: 'Fill missing artwork',
  total: 12,
  isTerminal: true,
  correlationId: 'run-1',
  progressPercent: 100,
  errors: [],
};

it('shows an artwork run on its job and refreshes the summary with the canonical record', async () => {
  state.job = artworkRun;
  const cache = show('/jobs/run-1');
  const invalidate = vi.spyOn(cache, 'invalidateQueries');
  expect(
    screen.getByRole('region', {
      name: 'Enrichment run',
    }),
  ).toHaveTextContent('run-1');
  expect(
    screen.queryByRole('link', {
      name: 'Open this run',
    }),
  ).not.toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Refresh',
    }),
  );
  expect(refetch).toHaveBeenCalledOnce();
  expect(invalidate).toHaveBeenCalledWith({
    queryKey: [
      'enrichment-workspace',
      'run',
      'run-1',
    ],
  });
});

it('preserves ordinary job details without loading the restricted artwork summary', () => {
  state.job = artworkRun;
  state.manager = false;
  show('/jobs/run-1');
  expect(read).toHaveBeenCalledWith('run-1');
  expect(
    screen.getByRole('heading', {
      name: 'Execution details',
    }),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('region', {
      name: 'Enrichment run',
    }),
  ).not.toBeInTheDocument();
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

it.each([
  null,
  '/srv/inbox',
  '/srv/one-off',
])('shows committed files and source-aware recovery for %s', (source) => {
  state.serverFolder = '/srv/inbox';
  state.lastServerImport = 'another-job';
  state.job = {
    id: 'old-job',
    correlationId: 'old-job',
    jobType: 'upload',
    sourceFilename: 'batch.zip',
    importSourcePath: source,
    importMove: !!source,
    phase: 'Failed',
    isTerminal: true,
    progressPercent: 50,
    romsDiscovered: 2,
    romsProcessed: 1,
    romsIngested: 1,
    errors: [
      {
        item: 'job',
        message: 'The archive could not be read.',
        occurredAt: '2026-10-02T15:00:00Z',
      },
    ],
  };
  show();
  expect(screen.getByText('Import failed')).toBeInTheDocument();
  expect(screen.getByText('1 stored')).toBeInTheDocument();
  expect(screen.getByText('1 file was left unprocessed.')).toBeInTheDocument();
  expect(screen.getByText('No file outcomes were recorded.')).toBeInTheDocument();
  expect(
    screen.queryByRole('textbox', {
      name: 'Search imported files',
    }),
  ).not.toBeInTheDocument();
  if (!source) {
    expect(
      screen.getByRole('link', {
        name: 'Back to Upload',
      }),
    ).toHaveAttribute('href', '/roms/import');
  } else {
    expect(screen.getByText(source)).toBeInTheDocument();
    expect(
      screen.queryByRole('link', {
        name: 'Back to Upload',
      }),
    ).not.toBeInTheDocument();
  }
  const retry = screen.queryByRole('button', {
    name: 'Retry remaining files',
  });
  if (source === '/srv/inbox') expect(retry).toBeInTheDocument();
  else expect(retry).not.toBeInTheDocument();
});

it.each([
  [
    'InputsChanged',
    'Review publication',
    '?review=publication',
  ],
  [
    'InvalidConfiguration',
    'Edit library rules',
    '?tab=games',
  ],
  [
    'CatalogRebuilding',
    'Open library',
    '',
  ],
])('links a deferred publication with outcome %s to its recovery action', (outcome, label, suffix) => {
  state.job = {
    id: 'old-job',
    correlationId: 'old-job',
    jobType: 'materialization',
    sourceFilename: 'Arcade Library',
    libraryId: 'library-1',
    phase: 'Deferred',
    isTerminal: true,
    progressPercent: 0,
    publicationOutcome: outcome,
    errors: [],
  };
  show();
  expect(screen.getByText('Publication deferred')).toBeInTheDocument();
  expect(
    screen.getByRole('link', {
      name: label,
      exact: true,
    }),
  ).toHaveAttribute('href', `/libraries/library-1${suffix}`);
  const publicationSummary = screen.queryByText('This attempt published no changes.');
  if (outcome === 'InvalidConfiguration') {
    expect(publicationSummary).not.toBeInTheDocument();
    expect(screen.getByText(/invalid publication was withdrawn/)).toBeInTheDocument();
  } else expect(publicationSummary).toBeInTheDocument();
});
