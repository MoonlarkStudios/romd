import type { DatReplacementPreview, DatReviewImpact } from '@romd/admin-api-client';
import { describe, expect, it } from 'vitest';
import {
  decisionConsequence,
  defaultReviewFilter,
  reviewOutcome,
  reviewRail,
  reviewVerdict,
  reviewViewFromParams,
  withReviewView,
} from './datReviewSummary';

const quiet: DatReviewImpact = {
  needsReviewEntries: 0,
  removedWithStoredRoms: 0,
  removedWithTitleLinks: 0,
  lostRomMatches: 0,
  newRomMatches: 0,
  sha1ChangedEntries: 0,
  sha1ChangedFiles: 0,
  metadataOnlyEntries: 0,
  otherChecksumOnlyEntries: 0,
  diskChangedEntries: 0,
};

function preview(
  impact: Partial<DatReviewImpact> | null,
  changes: Partial<DatReplacementPreview> = {},
): DatReplacementPreview {
  return {
    activeSha256: 'a'.repeat(64),
    candidateSha256: 'b'.repeat(64),
    activeVersion: '1',
    candidateVersion: '2',
    unchanged: false,
    activeEntries: 7712,
    candidateEntries: 7718,
    entriesAdded: 7,
    entriesRemoved: 1,
    entriesChanged: 1,
    filesAdded: 7,
    filesRemoved: 1,
    filesChanged: 1,
    hashesChanged: 1,
    activeBiosEntries: 0,
    candidateBiosEntries: 0,
    changes: [],
    truncated: false,
    impact: impact
      ? {
          ...quiet,
          ...impact,
        }
      : null,
    ...changes,
  };
}

describe('review verdict', () => {
  it('names a title unlink and says plainly that no stored ROM loses its match', () => {
    const reviewed = preview({
      needsReviewEntries: 1,
      removedWithTitleLinks: 1,
      sha1ChangedEntries: 1,
    });
    expect(reviewVerdict(reviewed)).toEqual({
      tone: 'review',
      title: '1 change needs review',
      detail: 'Unlinks 1 title. No stored ROM loses its match.',
    });
    expect(decisionConsequence(reviewed)).toBe(
      'Applying unlinks 1 title. No stored ROM loses its match. Libraries refresh after the job.',
    );
    expect(defaultReviewFilter(reviewed)).toBe('NeedsReview');
  });

  it('reports lost and gained ROM matches together', () => {
    const reviewed = preview({
      needsReviewEntries: 2,
      lostRomMatches: 2,
      newRomMatches: 1,
    });
    expect(reviewVerdict(reviewed).detail).toBe(
      '2 stored ROMs lose their match. No title links are lost. 1 stored ROM gains a match.',
    );
    expect(decisionConsequence(reviewed)).toBe(
      'Applying removes the catalog match for 2 stored ROMs. Libraries refresh after the job.',
    );
  });

  it('does not alarm when stored ROMs are involved but keep a match', () => {
    expect(
      reviewVerdict(
        preview({
          needsReviewEntries: 1,
          removedWithStoredRoms: 1,
        }),
      ).detail,
    ).toBe('These entries involve stored ROMs, but every stored ROM keeps a match.');
  });

  it('clears an update that touches no stored ROM or title link', () => {
    const reviewed = preview({
      sha1ChangedEntries: 1,
    });
    expect(reviewVerdict(reviewed)).toMatchObject({
      tone: 'clear',
      title: 'Nothing needs review',
    });
    expect(decisionConsequence(reviewed)).toBe(
      'Applying changes no stored ROM matches or title links. Libraries refresh after the job.',
    );
    expect(defaultReviewFilter(reviewed)).toBe('All');
  });

  it('is explicit when impact was not calculated', () => {
    expect(reviewVerdict(preview(null)).tone).toBe('unknown');
    expect(decisionConsequence(preview(null))).toBe(
      'Applying runs in a background job. Stored ROM files are never deleted.',
    );
  });

  it('describes a first import against an empty baseline', () => {
    const reviewed = preview(
      {
        newRomMatches: 3,
      },
      {
        activeSha256: '',
        activeEntries: 0,
        entriesRemoved: 0,
        entriesChanged: 0,
      },
    );
    expect(reviewVerdict(reviewed)).toEqual({
      tone: 'clear',
      title: 'Imports 7,718 catalog entries',
      detail: 'Nothing is installed for this source yet. 3 stored ROMs gain a match.',
    });
    expect(reviewRail(reviewed).flatMap((group) => group.rows.map((row) => row.filter))).toEqual([
      'All',
      'NewMatches',
    ]);
  });
});

it('keeps zero rows in the rail so the admin can see they were checked', () => {
  const [impact, changes] = reviewRail(
    preview({
      needsReviewEntries: 1,
      removedWithTitleLinks: 1,
    }),
  );
  expect(
    impact.rows.map(({ filter, count }) => [
      filter,
      count,
    ]),
  ).toEqual([
    [
      'NeedsReview',
      1,
    ],
    [
      'RemovedLinked',
      1,
    ],
    [
      'LostMatches',
      0,
    ],
    [
      'RemovedStored',
      0,
    ],
  ]);
  expect(changes.rows.map(({ filter }) => filter)).toEqual([
    'All',
    'Removed',
    'Changed',
    'Sha1Changed',
    'OtherChecksumOnly',
    'MetadataOnly',
    'Added',
    'NewMatches',
  ]);
});

it('records what was approved for the job notice', () => {
  expect(
    reviewOutcome(
      preview({
        removedWithTitleLinks: 1,
      }),
    ),
  ).toBe('1 title unlinked · 1 removed · 1 changed · 7 added');
});

it('keeps the review position in a readable URL and omits defaults', () => {
  const params = withReviewView(new URLSearchParams('tab=updates&q=old'), {
    filter: 'Sha1Changed',
    entry: 'RGlzYyBB',
  });
  expect(params.toString()).toBe('tab=updates&filter=sha1-changed&entry=RGlzYyBB');
  expect(reviewViewFromParams(params)).toEqual({
    filter: 'Sha1Changed',
    entry: 'RGlzYyBB',
    query: undefined,
  });
  expect(withReviewView(params, {}).toString()).toBe('tab=updates');
  expect(
    reviewViewFromParams(new URLSearchParams('filter=unknown&q=%20%20')).filter,
  ).toBeUndefined();
});
