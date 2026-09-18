import type { JobDto } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { render } from '../../test/utils/render';
import { OverviewJobs } from './OverviewJobs';

function makeJob(overrides: Record<string, unknown>): JobDto {
  return {
    id: '1',
    correlationId: 'c1',
    sourceFilename: 'job',
    phase: 'Completed',
    progressPercent: '100',
    errors: [],
    isTerminal: true,
    hasErrors: false,
    ...overrides,
  } as JobDto;
}

describe('OverviewJobs', () => {
  it('renders a distinct Deferred badge for a deferred materialization instead of Completed', () => {
    const jobs = [
      makeJob({
        id: 'd1',
        jobType: 'materialization',
        sourceFilename: 'Family Room',
        phase: 'Deferred',
      }),
      makeJob({ id: 'u1', jobType: 'upload', sourceFilename: 'pack.zip', phase: 'Completed' }),
    ];

    render(<OverviewJobs jobs={jobs} />);

    expect(screen.getByText('Materialize Family Room')).toBeInTheDocument();
    expect(screen.getByText('Deferred')).toBeInTheDocument();
    expect(screen.getAllByText('Completed')).toHaveLength(1);
  });

  it('keeps running and failed badges unchanged', () => {
    const jobs = [
      makeJob({
        id: 'r1',
        jobType: 'upload',
        sourceFilename: 'running.zip',
        isTerminal: false,
        progressPercent: '40',
      }),
      makeJob({ id: 'f1', jobType: 'upload', sourceFilename: 'failed.zip', phase: 'Failed', hasErrors: true }),
    ];

    render(<OverviewJobs jobs={jobs} />);

    expect(screen.getByText('Running')).toBeInTheDocument();
    expect(screen.getByText('Failed')).toBeInTheDocument();
    expect(screen.queryByText('Deferred')).not.toBeInTheDocument();
  });
});
