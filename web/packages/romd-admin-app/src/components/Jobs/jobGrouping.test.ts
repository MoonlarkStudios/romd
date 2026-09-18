import type { JobDto } from '@romd/admin-api-client';
import { describe, expect, it } from 'vitest';
import { formatGroupRange, groupConsecutiveJobs } from './jobGrouping';

function makeJob(overrides: Record<string, unknown>): JobDto {
  return {
    id: '1',
    correlationId: 'c',
    sourceFilename: '',
    phase: 'Completed',
    progressPercent: '0',
    errors: [],
    isTerminal: true,
    ...overrides,
  } as JobDto;
}

describe('groupConsecutiveJobs', () => {
  it('merges consecutive completed jobs sharing title and outcome', () => {
    const jobs = ['a', 'b', 'c'].map((id) =>
      makeJob({ id, jobType: 'materialization', sourceFilename: 'All Games', hasErrors: false }),
    );
    const groups = groupConsecutiveJobs(jobs);
    expect(groups).toHaveLength(1);
    expect(groups[0].jobs).toHaveLength(3);
  });

  it('never merges running jobs', () => {
    const jobs = ['a', 'b'].map((id) =>
      makeJob({ id, jobType: 'materialization', sourceFilename: 'All Games', isTerminal: false }),
    );
    expect(groupConsecutiveJobs(jobs)).toHaveLength(2);
  });

  it('breaks a group when the outcome differs', () => {
    const jobs = [
      makeJob({ id: 'a', jobType: 'materialization', sourceFilename: 'All Games', hasErrors: false }),
      makeJob({ id: 'b', jobType: 'materialization', sourceFilename: 'All Games', phase: 'Failed', hasErrors: true }),
    ];
    expect(groupConsecutiveJobs(jobs)).toHaveLength(2);
  });

  it('does not merge a deferred materialization into a completed group of the same library', () => {
    const jobs = [
      makeJob({ id: 'a', jobType: 'materialization', sourceFilename: 'All Games', hasErrors: false }),
      makeJob({
        id: 'b',
        jobType: 'materialization',
        sourceFilename: 'All Games',
        hasErrors: false,
        phase: 'Deferred',
      }),
    ];
    expect(groupConsecutiveJobs(jobs)).toHaveLength(2);
  });

  it('does not merge materializations of different libraries', () => {
    const jobs = [
      makeJob({ id: 'a', jobType: 'materialization', sourceFilename: 'Family Room', hasErrors: false }),
      makeJob({ id: 'b', jobType: 'materialization', sourceFilename: 'Arcade', hasErrors: false }),
    ];
    expect(groupConsecutiveJobs(jobs)).toHaveLength(2);
  });
});

describe('formatGroupRange', () => {
  it('reads earliest-to-latest regardless of job array order', () => {
    const earlier = makeJob({ startedAt: '2026-06-05T11:23:00Z', completedAt: '2026-06-05T11:30:00Z' });
    const later = makeJob({ startedAt: '2026-06-05T13:38:00Z', completedAt: '2026-06-05T13:40:00Z' });

    const ascending = formatGroupRange([earlier, later]);
    const descending = formatGroupRange([later, earlier]);

    expect(ascending).toBe(descending);
    expect(ascending).toContain('–');
  });

  it('returns a single time when the group has no spread', () => {
    const job = makeJob({ startedAt: '2026-06-05T11:23:00Z', completedAt: '2026-06-05T11:23:00Z' });
    expect(formatGroupRange([job])).not.toContain('–');
  });
});

