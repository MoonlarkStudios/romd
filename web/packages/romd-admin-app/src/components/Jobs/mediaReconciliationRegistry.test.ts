import { describe, expect, it } from 'vitest';
import { mediaReconciliationKeys } from '../../hooks/api/useMediaReconciliation';
import {
  selectionReconciliationJob,
  subjectReconciliationJob,
} from '../../test/fixtures/mediaReconciliationJobs';
import { canPerformJobAction } from './jobActionEligibility';
import {
  getJobOutcome,
  getJobTitle,
  getJobTypeConfig,
  mediaReconciliationSummary,
  visibleJobTypeOptions,
} from './jobTypeRegistry';

describe('media reconciliation resources', () => {
  it('keeps one administrative family for subjects and selections', () => {
    expect(
      visibleJobTypeOptions(true).filter((option) => option.value === 'media-reconciliation'),
    ).toEqual([
      {
        value: 'media-reconciliation',
        label: 'Media reconciliation',
      },
    ]);
    expect(
      visibleJobTypeOptions(false).some((option) => option.value === 'media-reconciliation'),
    ).toBe(false);
    for (const job of [
      subjectReconciliationJob(),
      selectionReconciliationJob(),
    ]) {
      expect(canPerformJobAction(job, 'cancel', true, false)).toBe(false);
      expect(canPerformJobAction(job, 'cancel', true, true)).toBe(true);
      expect(getJobTypeConfig(job.jobType).invalidates(job)).toContainEqual(
        mediaReconciliationKeys.policies,
      );
    }
  });
  it('distinguishes a deferred active subject from a failed or completed job', () => {
    expect(
      getJobOutcome(
        subjectReconciliationJob({
          nextAttemptAt: '2030-01-01T00:00:00Z',
        }),
      ),
    ).toBe('deferred');
    expect(
      getJobOutcome(
        subjectReconciliationJob({
          isTerminal: true,
          phase: 'Failed',
          outcome: 'Failed',
          hasErrors: true,
        }),
      ),
    ).toBe('failed');
    expect(
      getJobOutcome(
        selectionReconciliationJob({
          isTerminal: true,
          phase: 'CompletedWithErrors',
          outcome: 'CompletedWithErrors',
          hasErrors: true,
        }),
      ),
    ).toBe('partial');
  });
  it('reports finished and failed subsets without counting skipped subjects as children', () => {
    expect(mediaReconciliationSummary(selectionReconciliationJob())).toBe(
      '3 / 6 finished · 1 failed · 2 skipped',
    );
    expect(getJobTitle(subjectReconciliationJob())).toBe('Reconcile Luigi.iso');
    expect(getJobTitle(selectionReconciliationJob())).toBe('Reconcile media');
  });
  it.each([
    'Failed',
    'Cancelled',
    'Superseded',
    'CompletedWithErrors',
  ])('offers exact retries for terminal %s work in either mode', (phase) => {
    for (const job of [
      subjectReconciliationJob({
        phase,
        isTerminal: true,
      }),
      selectionReconciliationJob({
        phase,
        isTerminal: true,
      }),
    ]) {
      expect(canPerformJobAction(job, 'retry', true, true)).toBe(true);
      expect(canPerformJobAction(job, 'retry', true, false)).toBe(false);
      expect(canPerformJobAction(job, 'cancel', true, true)).toBe(false);
    }
  });
});
