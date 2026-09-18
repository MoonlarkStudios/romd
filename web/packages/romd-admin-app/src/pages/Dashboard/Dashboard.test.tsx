import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { Dashboard } from './index';

const access = vi.hoisted(() => ({ canManageUsers: true, canUploadRoms: true, canManageTitles: true }));
vi.mock('../../hooks/usePermissions', () => ({ usePermissions: () => access }));
vi.mock('@romd/admin-api-client', async () => ({ ...await vi.importActual('@romd/admin-api-client'),
  getSystemHealth: vi.fn(), getJobHistory: vi.fn(), getEnrichmentStats: vi.fn(), getTrackedCollectionStats: vi.fn(), getAdminAudit: vi.fn(),
  listMissingTrackedTitles: vi.fn(), listSatisfiedTrackedTitles: vi.fn(), listTrackedTitleUpgrades: vi.fn(),
}));

import { getAdminAudit, getEnrichmentStats, getJobHistory, getSystemHealth, getTrackedCollectionStats, listMissingTrackedTitles, listSatisfiedTrackedTitles, listTrackedTitleUpgrades } from '@romd/admin-api-client';

function returns<T>(fn: T, data: unknown) { vi.mocked(fn as ReturnType<typeof vi.fn>).mockResolvedValue({ data }); }
beforeEach(() => {
  vi.clearAllMocks(); Object.assign(access, { canManageUsers: true, canUploadRoms: true, canManageTitles: true });
  returns(getSystemHealth, { totalRoms: 3, unidentifiedCount: 2, unroutedCount: 0 });
  returns(getJobHistory, { items: [], nextCursor: null });
  returns(getEnrichmentStats, { completed: '7', failed: '2', notFound: '3', lowConfidence: '1', pending: '0' });
  returns(getTrackedCollectionStats, { trackedTitleCount: '10', satisfiedTitleCount: '8', missingTitleCount: '2', upgradeTitleCount: '1', completionPercent: '80', platforms: [] });
  returns(getAdminAudit, { items: [], nextCursor: null });
});
it('links each attention count to its exact scope and only counts successful enrichment', async () => {
  render(<Dashboard />);
  expect(await screen.findByRole('link', { name: 'Review metadata match not found' })).toHaveAttribute('href', '/catalog?view=tracked&enrichmentStatus=NotFound');
  expect(screen.getByRole('link', { name: 'Review metadata match needs review' })).toHaveAttribute('href', '/catalog?view=tracked&enrichmentStatus=LowConfidence');
  expect(screen.getByRole('link', { name: 'Review unidentified files' })).toHaveAttribute('href', '/roms?status=unidentified');
  expect(screen.getByText('successfully enriched', { exact: false })).toHaveTextContent('7 successfully enriched');
  expect(screen.getByText('8 / 10')).toBeInTheDocument();
  expect(listMissingTrackedTitles).not.toHaveBeenCalled(); expect(listSatisfiedTrackedTitles).not.toHaveBeenCalled(); expect(listTrackedTitleUpgrades).not.toHaveBeenCalled();
});
it('shows a failed section rather than an empty queue and lets the operator retry', async () => {
  vi.mocked(getJobHistory).mockResolvedValue({ error: { detail: 'Unavailable' } } as Awaited<ReturnType<typeof getJobHistory>>);
  render(<Dashboard />);
  expect(await screen.findByText('Running unavailable')).toBeInTheDocument();
  expect(screen.queryByText('No jobs are running.')).not.toBeInTheDocument();
  expect(screen.queryByText('No outstanding items')).not.toBeInTheDocument();
  expect(screen.getByText('8 / 10')).toBeInTheDocument();
  returns(getJobHistory, { items: [], nextCursor: null });
  await userEvent.click(screen.getByRole('button', { name: 'Refresh', exact: true }));
  expect(await screen.findByText('No jobs are running.')).toBeInTheDocument();
});
it('uses bounded outcome queries and excludes administrator data for other roles', async () => {
  access.canManageUsers = false;
  render(<Dashboard />);
  await screen.findByText('8 / 10');
  expect(getAdminAudit).not.toHaveBeenCalled();
  expect(screen.queryByText('Recent administrative changes')).not.toBeInTheDocument();
  await waitFor(() => expect(getJobHistory).toHaveBeenCalledTimes(3));
  for (const outcome of ['running', 'queued', 'failed']) expect(getJobHistory).toHaveBeenCalledWith(expect.objectContaining({ query: { outcome, archive: 'active', limit: 5 } }));
});
it('shows an honest all-clear for empty summaries and an invitation to track titles', async () => {
  returns(getSystemHealth, { unidentifiedCount: 0, unroutedCount: 0 });
  returns(getEnrichmentStats, { completed: '0', failed: '0', notFound: '0', lowConfidence: '0' });
  returns(getTrackedCollectionStats, { trackedTitleCount: '0' });
  render(<Dashboard />);
  expect(await screen.findByText('No outstanding items')).toBeInTheDocument();
  expect(screen.getByText('Track titles in Catalog to define your collection goals.')).toBeInTheDocument();
});
