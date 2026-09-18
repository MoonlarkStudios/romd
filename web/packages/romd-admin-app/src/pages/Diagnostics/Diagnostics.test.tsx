import { client, type OperationalDiagnosticsDto } from '@romd/admin-api-client';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { delay, HttpResponse, http } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  emptyOperationalDiagnosticsFixture,
  operationalDiagnosticsFixture,
} from '../../test/msw/fixtures/diagnostics';
import { server } from '../../test/msw/server';
import { render } from '../../test/utils/render';
import { Diagnostics } from './index';

function respondWith(diagnostics: OperationalDiagnosticsDto) {
  server.use(
    http.get('*/api/diagnostics/operational', () => HttpResponse.json(diagnostics)),
  );
}

describe('Diagnostics', () => {
  beforeEach(() => {
    client.setConfig({ baseUrl: 'http://localhost' });
  });

  afterEach(() => {
    client.setConfig({ baseUrl: '' });
  });

  it('shows an accessible loading state while the bounded snapshot is pending', async () => {
    server.use(
      http.get('*/api/diagnostics/operational', async () => {
        await delay(100);
        return HttpResponse.json(operationalDiagnosticsFixture);
      }),
    );

    render(<Diagnostics />);

    expect(screen.getByRole('heading', { name: 'Diagnostics' })).toBeInTheDocument();
    expect(screen.getByRole('status', { name: 'Loading operational diagnostics' })).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'Catalog projections' })).toBeInTheDocument();
  });

  it('renders all five sections and their actionable facts from a populated snapshot', async () => {
    render(<Diagnostics />);

    expect(await screen.findByRole('heading', { name: 'Catalog projections' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Job recovery' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Realtime outbox' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Workers and queues' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Storage dependencies' })).toBeInTheDocument();

    expect(screen.getByText('Projection rebuild stopped at release 42.')).toBeInTheDocument();
    expect(screen.getByText('1h 40m')).toBeInTheDocument();
    expect(screen.getByText('12')).toBeInTheDocument();
    expect(screen.getByText('Heartbeat 15s old')).toBeInTheDocument();
    expect(screen.getByText('50 GB free')).toBeInTheDocument();
  });

  it('keeps clean projections compact until an admin expands their evidence', async () => {
    const projection = operationalDiagnosticsFixture.catalogProjections.items[0];
    respondWith({
      ...emptyOperationalDiagnosticsFixture,
      catalogProjections: { availability: 'Available', isTruncated: false, items: [{ ...projection, state: 'Clean', lastError: null }] },
    });
    const user = userEvent.setup();
    render(<Diagnostics />);
    expect(await screen.findByText('1 clean projections in this snapshot.')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: projection.platformName })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Show clean projections' }));
    expect(screen.getByRole('link', { name: projection.platformName })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Hide clean projections' }));
    expect(screen.queryByRole('link', { name: projection.platformName })).not.toBeInTheDocument();
  });

  it('renders explicit empty facts instead of omitting healthy sections', async () => {
    respondWith(emptyOperationalDiagnosticsFixture);

    render(<Diagnostics />);

    expect(await screen.findByText('No catalog projections were reported.')).toBeInTheDocument();
    expect(screen.getByText('No wedged DAT replacement jobs.')).toBeInTheDocument();
    expect(screen.getByText('No stranded bulk enrichment jobs.')).toBeInTheDocument();
    expect(screen.getByText('No pending outbox messages.')).toBeInTheDocument();
    expect(screen.getByText('No Hangfire servers were reported.')).toBeInTheDocument();
    expect(screen.getByText('No recurring jobs were reported.')).toBeInTheDocument();
    expect(screen.getByText('No queued or fetched work was reported.')).toBeInTheDocument();
  });

  it('shows a request error with a manual retry and no misleading section data', async () => {
    server.use(
      http.get('*/api/diagnostics/operational', () =>
        HttpResponse.json({ title: 'Unavailable' }, { status: 503 }),
      ),
    );

    render(<Diagnostics />);

    expect(
      await screen.findByText('Failed to load operational diagnostics'),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Catalog projections' })).not.toBeInTheDocument();
  });

  it('isolates unavailable sections while preserving completed section evidence', async () => {
    respondWith({
      ...operationalDiagnosticsFixture,
      catalogProjections: {
        availability: 'Unavailable',
        error: 'Catalog query reached its time budget.',
        items: [],
        isTruncated: false,
      },
      storage: {
        dataVolumeAvailability: 'Available',
        dataVolumeFreeBytes: '53687091200',
        casAvailability: 'Unavailable',
        error: 'CAS root is not reachable.',
      },
    });

    render(<Diagnostics />);

    expect(await screen.findByText('Catalog projections unavailable')).toBeInTheDocument();
    expect(screen.getByText('Catalog query reached its time budget.')).toBeInTheDocument();
    expect(screen.queryByText('No catalog projections were reported.')).not.toBeInTheDocument();
    expect(screen.getByText('CAS root is not reachable.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Inspect job replace-job-1' })).toBeInTheDocument();
    expect(screen.getByText('dat-replacement-convergence-sweep')).toBeInTheDocument();
    expect(screen.getByText('50 GB free')).toBeInTheDocument();
  });

  it('does not present fallback outbox metrics as observed facts when its probe is unavailable', async () => {
    respondWith({
      ...operationalDiagnosticsFixture,
      outbox: {
        availability: 'Unavailable',
        error: 'Outbox query reached its time budget.',
        pendingCount: 0,
        oldestPendingAgeSeconds: null,
        failingCount: 0,
        lastProcessedAt: null,
      },
    });

    render(<Diagnostics />);

    const outboxSection = await screen.findByRole('region', { name: 'Realtime outbox' });
    expect(within(outboxSection).getByText('Realtime outbox unavailable')).toBeInTheDocument();
    expect(within(outboxSection).getByText('Outbox query reached its time budget.')).toBeInTheDocument();
    expect(within(outboxSection).queryByText('Pending')).not.toBeInTheDocument();
    expect(within(outboxSection).queryByText('Retrying')).not.toBeInTheDocument();
    expect(within(outboxSection).queryByText('Oldest pending')).not.toBeInTheDocument();
    expect(within(outboxSection).queryByText('Last processed')).not.toBeInTheDocument();
  });

  it('labels bounded error text and list truncation explicitly', async () => {
    respondWith({
      ...operationalDiagnosticsFixture,
      jobs: {
        ...operationalDiagnosticsFixture.jobs,
        wedgedReplaceDatJobs: [
          {
            ...operationalDiagnosticsFixture.jobs.wedgedReplaceDatJobs[0],
            attemptError: 'An intentionally bounded provider failure…',
            attemptErrorTruncated: true,
          },
        ],
        wedgedReplaceDatJobsTruncated: true,
      },
    });

    render(<Diagnostics />);

    expect(
      await screen.findByText('Error text was truncated by the bounded diagnostics query.'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Showing a bounded subset; additional wedged DAT replacement jobs were omitted.'),
    ).toBeInTheDocument();
  });

  it('preserves the named Unknown recurring-job state', async () => {
    respondWith({
      ...operationalDiagnosticsFixture,
      hangfire: {
        ...operationalDiagnosticsFixture.hangfire,
        recurringJobs: [
          {
            ...operationalDiagnosticsFixture.hangfire.recurringJobs[0],
            lastJobState: 'Unknown',
          },
        ],
      },
    });

    render(<Diagnostics />);

    expect(await screen.findByText('Unknown')).toBeInTheDocument();
    expect(screen.queryByText('No history')).not.toBeInTheDocument();
  });
});


it('keeps the previous snapshot visible when manual refresh fails', async () => {
  client.setConfig({ baseUrl: 'http://localhost' });
  try {
    render(<Diagnostics />);
    await screen.findByText('Projection rebuild stopped at release 42.');
    server.use(http.get('*/api/diagnostics/operational', () => HttpResponse.json({}, { status: 503 })));
    await userEvent.click(screen.getByRole('button', { name: 'Refresh snapshot' }));
    expect(await screen.findByText('Could not refresh diagnostics')).toBeInTheDocument();
    expect(screen.getByText('Projection rebuild stopped at release 42.')).toBeInTheDocument();
  } finally {
    client.setConfig({ baseUrl: '' });
  }
});


it('connects operational findings to the affected job and system', async () => {
  client.setConfig({ baseUrl: 'http://localhost' });
  try {
    render(<Diagnostics />);
    const replacement = await screen.findByRole('link', { name: 'Inspect replacement' });
    expect(replacement).toHaveAttribute('href', '/jobs/replace-job-1');
    expect(screen.getAllByRole('link', { name: 'Review sources' }).length).toBeGreaterThan(0);
  } finally { client.setConfig({ baseUrl: '' }); }
});
