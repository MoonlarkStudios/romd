import { MantineProvider } from '@mantine/core';
import type { JobDto } from '@romd/admin-api-client';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { JobDetail } from './JobDetail';

const { state, read, retry } = vi.hoisted(() => ({ state: { manager: true, job: undefined as JobDto | undefined }, read: vi.fn(), retry: vi.fn() }));
vi.mock('../../hooks/api/useJobs', () => ({ useJob: (id: string) => { read(id); return { data: state.job, isError: !state.job, isPending: false, refetch: vi.fn() }; } }));
vi.mock('../../hooks/useJobOperations', () => ({ useJobOperations: () => ({ onCancel: vi.fn(), onArchive: vi.fn(), onRetry: retry, retryingId: null, actionsPending: false }) }));
vi.mock('../../hooks/usePermissions', () => ({ usePermissions: () => ({ hasRole: () => state.manager }) }));
function show() { render(<MantineProvider><MemoryRouter initialEntries={['/jobs/old-job']}><Routes><Route path="/jobs/:jobId" element={<JobDetail />} /></Routes></MemoryRouter></MantineProvider>); }
beforeEach(() => { state.manager = true; state.job = { id: 'old-job', jobType: 'bulk_enrichment', phase: 'Failed', sourceFilename: 'SNES', failedCount: 2, isTerminal: true, hasErrors: true, isArchived: true, correlationId: 'trace-1', systemKey: 'platform-1', errors: [{ item: 'Game', message: 'Provider unavailable', occurredAt: '2026-09-10T00:00:00Z' }] } as JobDto; vi.clearAllMocks(); });
it('loads an archived job outside the recent list and exposes investigation evidence', () => {
  show();
  expect(read).toHaveBeenCalledWith('old-job');
  expect(screen.getByText('Archived job')).toBeInTheDocument();
  expect(screen.getByText('Provider unavailable')).toBeInTheDocument();
  expect(screen.getByText('trace-1')).toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'Open system sources' })).toHaveAttribute('href', '/systems/platform-1?tab=sources');
});
it('gates scoped retries by role', async () => {
  show();
  await userEvent.click(screen.getByRole('button', { name: /^Actions for/ }));
  await userEvent.click(await screen.findByRole('menuitem', { name: 'Retry 2 failed' }));
  expect(retry).toHaveBeenCalledWith(state.job);
});
it('does not offer retries to a non-manager', () => { state.manager = false; show(); expect(screen.queryByRole('button', { name: /^Actions for/ })).not.toBeInTheDocument(); });
it('explains missing or inaccessible jobs', () => { state.job = undefined; show(); expect(screen.getByText('Could not load job')).toBeInTheDocument(); });
