import { describe, expect, it } from 'vitest';
import {
  executionLine,
  globalReviewLabel,
  isRunActive,
  metadataOutcomeLabel,
  recordedLine,
  roleOutcomeLabel,
  runCountsLine,
  runHeading,
} from './runPresentation';

const run = {
  processed: 120,
  total: 240,
  nextAttemptAt: null,
};

describe('executionLine', () => {
  it.each([
    [
      'Pending',
      'Queued',
    ],
    [
      'Enriching',
      'Running · 120 of 240 processed',
    ],
    [
      'Completed',
      'Finished · 120 of 240 processed',
    ],
    [
      'CompletedWithErrors',
      'Finished with errors · 120 of 240 processed',
    ],
    [
      'Failed',
      'Stopped with an error · 120 of 240 processed',
    ],
    [
      'Cancelled',
      'Cancelled · 120 of 240 processed',
    ],
  ])('reports %s as "%s"', (phase, line) => {
    expect(
      executionLine({
        ...run,
        phase,
      }),
    ).toBe(line);
  });

  it('reports a deferred active run as paused until its next attempt, in local time', () => {
    const now = new Date(2026, 8, 22, 14, 0).getTime();
    const nextAttemptAt = new Date(2026, 8, 22, 14, 32).toISOString();
    expect(
      executionLine(
        {
          ...run,
          phase: 'Enriching',
          nextAttemptAt,
        },
        now,
      ),
    ).toBe('Paused · next attempt at 14:32');
    expect(
      executionLine(
        {
          ...run,
          phase: 'Pending',
          nextAttemptAt,
        },
        now,
      ),
    ).toBe('Paused · next attempt at 14:32');
  });

  it('stops saying paused once the attempt time has passed or the run is history', () => {
    const now = new Date(2026, 8, 22, 15, 0).getTime();
    const nextAttemptAt = new Date(2026, 8, 22, 14, 32).toISOString();
    expect(
      executionLine(
        {
          ...run,
          phase: 'Enriching',
          nextAttemptAt,
        },
        now,
      ),
    ).toBe('Running · 120 of 240 processed');
    expect(
      executionLine(
        {
          ...run,
          phase: 'Cancelled',
          nextAttemptAt: new Date(2026, 8, 22, 16, 0).toISOString(),
        },
        now,
      ),
    ).toBe('Cancelled · 120 of 240 processed');
  });

  it('never claims resolution for any phase', () => {
    for (const phase of [
      'Pending',
      'Enriching',
      'Completed',
      'CompletedWithErrors',
      'Failed',
      'Cancelled',
    ]) {
      expect(
        executionLine({
          ...run,
          phase,
        }),
      ).not.toMatch(/resolved|nothing changed/i);
    }
  });
});

describe('run lines', () => {
  it('names the run by mode and size', () => {
    expect(
      runHeading({
        mode: 'Enrich',
        total: 240,
      }),
    ).toBe('This run · Enrich 240 titles');
    expect(
      runHeading({
        mode: 'Artwork',
        total: 1,
      }),
    ).toBe('This run · Fill artwork for 1 title');
  });

  it('lists current review and recorded outcomes separately, never as a total', () => {
    expect(
      runCountsLine({
        review: 28,
        failed: 12,
        unfinished: 0,
      }),
    ).toBe('28 need review now · 12 failed · 0 unfinished');
    expect(
      runCountsLine({
        review: 1,
        failed: 0,
        unfinished: 7,
      }),
    ).toBe('1 needs review now · 0 failed · 7 unfinished');
  });

  it('labels the review queue as global', () => {
    expect(globalReviewLabel(61)).toBe('Global queue: 61 need review →');
    expect(globalReviewLabel(undefined)).toBe('Global queue →');
  });

  it('treats every phase except the four terminal ones as active', () => {
    expect(
      [
        'Pending',
        'Enriching',
        'SomethingNew',
        'Completed',
        'CompletedWithErrors',
        'Failed',
        'Cancelled',
      ].map(isRunActive),
    ).toEqual([
      true,
      true,
      true,
      false,
      false,
      false,
      false,
    ]);
  });
});

describe('recorded outcomes', () => {
  it('describes the metadata step without claiming complete metadata, and legacy history as unknown', () => {
    expect(metadataOutcomeLabel('Succeeded')).toBe('Metadata step completed');
    expect(metadataOutcomeLabel('Failed')).toBe('Metadata failed');
    expect(metadataOutcomeLabel('NotAttempted')).toBe('Metadata not attempted');
    expect(metadataOutcomeLabel(null)).toBe('Metadata: not recorded');
  });

  it('describes each recorded role outcome in plain words', () => {
    expect(roleOutcomeLabel('Hero', 'Unavailable')).toBe('Banner: nothing usable found');
    expect(roleOutcomeLabel('Logo', 'SomethingNew')).toBe('Logo: recorded as SomethingNew');
    expect(
      recordedLine({
        metadataOutcome: 'Succeeded',
        roles: [
          {
            role: 'Backdrop',
            status: 'NeedsReview',
            count: 1,
          },
          {
            role: 'Logo',
            status: 'Unavailable',
            count: 1,
          },
        ],
      }),
    ).toBe(
      'Recorded: Metadata step completed · Backdrop: candidates ready · Logo: nothing usable found',
    );
  });
});
