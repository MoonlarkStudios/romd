import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render as renderWithoutProviders, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { enrichmentWorkspaceKeys } from '../../hooks/api/enrichmentWorkspaceCache';
import { render } from '../../test/utils/render';
import { Dashboard } from './index';

const access = vi.hoisted(() => ({
  canManageUsers: true,
  canUploadRoms: true,
  canManageTitles: true,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => access,
}));
vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getServerHealth: vi.fn(),
  listJobs: vi.fn(),
  listTrackedTitles: vi.fn(),
  getEnrichmentStats: vi.fn(),
  getEnrichmentWorkbenchStats: vi.fn(),
  getTrackedCollectionStats: vi.fn(),
  listAuditEvents: vi.fn(),
}));

import {
  getEnrichmentStats,
  getEnrichmentWorkbenchStats,
  getServerHealth,
  getTrackedCollectionStats,
  listAuditEvents,
  listJobs,
  listTrackedTitles,
} from '@romd/admin-api-client';

function returns<T>(fn: T, data: unknown) {
  vi.mocked(fn as ReturnType<typeof vi.fn>).mockResolvedValue({
    data,
  });
}
beforeEach(() => {
  vi.clearAllMocks();
  Object.assign(access, {
    canManageUsers: true,
    canUploadRoms: true,
    canManageTitles: true,
  });
  returns(getServerHealth, {
    totalRoms: 3,
    unidentifiedCount: 2,
    unroutedCount: 0,
  });
  returns(listJobs, {
    items: [],
    nextCursor: null,
    hasNextPage: false,
  });
  returns(getEnrichmentStats, {
    completed: '7',
    failed: '2',
    notFound: '3',
    lowConfidence: '1',
    pending: '0',
  });
  returns(getEnrichmentWorkbenchStats, {
    total: 1204,
    needsReview: 12,
    blocked: 3,
    processing: 1,
    automatic: 40,
  });
  returns(getTrackedCollectionStats, {
    trackedTitleCount: '10',
    satisfiedTitleCount: '8',
    missingTitleCount: '2',
    upgradeTitleCount: '1',
    completionPercent: '80',
    platforms: [],
  });
  returns(listAuditEvents, {
    items: [],
    nextCursor: null,
    hasNextPage: false,
  });
});
afterEach(() => {
  vi.useRealTimers();
});
it('links each attention count to its exact scope and only counts successful enrichment', async () => {
  render(<Dashboard />);
  await screen.findByLabelText('Review unidentified files');
  expect(
    screen.getByRole('link', {
      name: 'Review unidentified files',
    }),
  ).toHaveAttribute('href', '/roms?status=unidentified');
  expect(
    screen.queryByRole('link', {
      name: /Review metadata/,
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole('link', {
      name: /enrichmentStatus=/,
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.getByText('successfully enriched', {
      exact: false,
    }),
  ).toHaveTextContent('7 successfully enriched');
  expect(screen.getByText('8 / 10')).toBeInTheDocument();
  expect(listTrackedTitles).not.toHaveBeenCalled();
});
it('shows a failed section rather than an empty queue and lets the operator retry', async () => {
  vi.mocked(listJobs).mockResolvedValue({
    error: {
      detail: 'Unavailable',
    },
  } as Awaited<ReturnType<typeof listJobs>>);
  render(<Dashboard />);
  expect(await screen.findByText('Running unavailable')).toBeInTheDocument();
  expect(screen.queryByText('No jobs are running.')).not.toBeInTheDocument();
  expect(screen.queryByText('No outstanding items')).not.toBeInTheDocument();
  expect(screen.getByText('8 / 10')).toBeInTheDocument();
  returns(listJobs, {
    items: [],
    nextCursor: null,
    hasNextPage: false,
  });
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Refresh',
      exact: true,
    }),
  );
  expect(await screen.findByText('No jobs are running.')).toBeInTheDocument();
});
it('uses bounded outcome queries and excludes administrator data for other roles', async () => {
  access.canManageUsers = false;
  render(<Dashboard />);
  await screen.findByText('8 / 10');
  expect(listAuditEvents).not.toHaveBeenCalled();
  expect(screen.queryByText('Recent administrative changes')).not.toBeInTheDocument();
  await waitFor(() => expect(listJobs).toHaveBeenCalledTimes(3));
  for (const outcome of [
    'running',
    'queued',
    'failed',
  ])
    expect(listJobs).toHaveBeenCalledWith(
      expect.objectContaining({
        query: {
          outcome,
          archive: 'active',
          limit: 5,
        },
      }),
    );
});
it('shows the four current enrichment counts linked to their workspace views, apart from the tracked total', async () => {
  render(<Dashboard />);
  const views = [
    [
      'Needs review',
      'NeedsReview',
      '12',
    ],
    [
      'Blocked',
      'Blocked',
      '3',
    ],
    [
      'Processing',
      'Processing',
      '1',
    ],
    [
      'Automatic',
      'Automatic',
      '40',
    ],
  ];
  for (const [label, view, count] of views) {
    const link = await screen.findByRole('link', {
      name: label,
    });
    expect(link).toHaveAttribute('href', `/enrichment?view=${view}`);
    expect(link.nextElementSibling).toHaveTextContent(count);
  }
  expect(screen.getByText('1,204 tracked titles')).toBeInTheDocument();
  expect(screen.getByText('A title can appear in more than one count.')).toBeInTheDocument();
  expect(screen.queryByText('Settled')).not.toBeInTheDocument();
  expect(
    screen.queryByRole('link', {
      name: /enrichmentStatus=/,
    }),
  ).not.toBeInTheDocument();
  expect(getEnrichmentWorkbenchStats).toHaveBeenCalledWith(
    expect.objectContaining({
      query: {
        query: undefined,
      },
    }),
  );
});
it('reads enrichment counts once rather than polling them', async () => {
  vi.useFakeTimers({
    shouldAdvanceTime: true,
  });
  render(<Dashboard />);
  expect(await screen.findByText('1,204 tracked titles')).toBeInTheDocument();
  await act(() => vi.advanceTimersByTimeAsync(120_000));
  expect(getEnrichmentWorkbenchStats).toHaveBeenCalledTimes(1);
});
it('reads the enrichment workspace counts cache entry instead of a second copy', async () => {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  cache.setQueryData(enrichmentWorkspaceKeys.counts(''), {
    total: 9,
    needsReview: 2,
    blocked: 0,
    processing: 0,
    automatic: 1,
  });
  renderWithoutProviders(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <MemoryRouter>
          <Dashboard />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
  expect(await screen.findByText('9 tracked titles')).toBeInTheDocument();
  expect(getEnrichmentWorkbenchStats).not.toHaveBeenCalled();
});
it('omits enrichment counts for roles that cannot manage titles', async () => {
  access.canManageTitles = false;
  render(<Dashboard />);
  await screen.findByText('8 / 10');
  expect(
    screen.queryByRole('heading', {
      name: 'Enrichment',
    }),
  ).not.toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Refresh',
      exact: true,
    }),
  );
  await waitFor(() => expect(getServerHealth).toHaveBeenCalledTimes(2));
  expect(getEnrichmentWorkbenchStats).not.toHaveBeenCalled();
});
it('shows an honest all-clear for empty summaries and an invitation to track titles', async () => {
  returns(getServerHealth, {
    unidentifiedCount: 0,
    unroutedCount: 0,
  });
  returns(getEnrichmentStats, {
    completed: '0',
    failed: '0',
    notFound: '0',
    lowConfidence: '0',
  });
  returns(getTrackedCollectionStats, {
    trackedTitleCount: '0',
  });
  render(<Dashboard />);
  expect(await screen.findByText('No outstanding items')).toBeInTheDocument();
  expect(
    screen.getByText('Track titles in Catalog to define your collection goals.'),
  ).toBeInTheDocument();
});
