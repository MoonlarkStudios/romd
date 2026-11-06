import { describe, expect, it } from 'vitest';
import {
  assessmentFixture,
  chronoTriggerRequirements,
  obscureGameBoyRequirements,
  satisfied,
  settledRequirements,
  unsatisfied,
  withRequirements,
} from '../../test/msw/fixtures/enrichment';
import {
  addedRequirementCount,
  artworkRole,
  checklistGroups,
  currentRequirement,
  isInView,
  processingOperation,
  reviewEditor,
  workbenchViews,
} from './titleWorkbenchModel';

const descriptionSaved = chronoTriggerRequirements.map((row) =>
  row.key === 'Metadata:Description'
    ? satisfied(row.key, 'Present', [
        'ReviewMetadata',
      ])
    : row,
);

describe('view membership', () => {
  it('follows the flag behind each view, and All always holds the title', () => {
    const chrono = assessmentFixture(chronoTriggerRequirements);
    expect(workbenchViews.filter((view) => isInView(chrono, view))).toEqual([
      'NeedsReview',
      'Processing',
      'Blocked',
      'All',
    ]);
    expect(
      workbenchViews.filter((view) =>
        isInView(assessmentFixture(obscureGameBoyRequirements), view),
      ),
    ).toEqual([
      'All',
    ]);
  });
});

describe('current requirement', () => {
  it('leads scenario 8.1 with the description in Needs review', () => {
    expect(currentRequirement(chronoTriggerRequirements, 'NeedsReview')?.key).toBe(
      'Metadata:Description',
    );
    expect(currentRequirement(chronoTriggerRequirements, 'Blocked')?.key).toBe(
      'Identity:steamgriddb',
    );
    expect(currentRequirement(chronoTriggerRequirements, 'Processing')?.key).toBe(
      'Artwork:Backdrop',
    );
    expect(currentRequirement(chronoTriggerRequirements, 'All')?.key).toBe('Identity:steamgriddb');
  });

  it('moves to the first open row once the view has none left, as after saving the 8.1 description', () => {
    expect(currentRequirement(descriptionSaved, 'NeedsReview')?.key).toBe('Identity:steamgriddb');
  });

  it('honors a linked requirement only while it is outstanding', () => {
    expect(currentRequirement(chronoTriggerRequirements, 'NeedsReview', 'Artwork:Logo')?.key).toBe(
      'Artwork:Logo',
    );
    expect(
      currentRequirement(chronoTriggerRequirements, 'NeedsReview', 'Artwork:Poster')?.key,
    ).toBe('Metadata:Description');
  });

  it('is undefined when nothing is outstanding, even while the operation is processing', () => {
    expect(currentRequirement(obscureGameBoyRequirements, 'All')).toBeUndefined();
    expect(
      currentRequirement(
        withRequirements(unsatisfied('Operation:Processing', 'Processing', 'Working')),
        'Processing',
      ),
    ).toBeUndefined();
  });
});

describe('checklist groups', () => {
  it('keeps every row in backend order, counts outstanding rows and leaves Operation out', () => {
    const groups = checklistGroups(chronoTriggerRequirements);
    expect(
      groups.map((group) => [
        group.kind,
        group.rows.map((row) => row.subject),
        group.outstanding,
      ]),
    ).toEqual([
      [
        'Identity',
        [
          'igdb',
          'steamgriddb',
        ],
        1,
      ],
      [
        'Metadata',
        [
          'Description',
          'Genre',
          'ReleaseDate',
          'Refresh',
        ],
        1,
      ],
      [
        'Artwork',
        [
          'Poster',
          'Backdrop',
          'Hero',
          'Logo',
        ],
        2,
      ],
    ]);
  });

  it('omits kinds with no rows, such as metadata hidden while identity review waits', () => {
    const waiting = settledRequirements.filter((row) => row.kind !== 'Metadata');
    expect(checklistGroups(waiting).map((group) => group.kind)).toEqual([
      'Identity',
      'Artwork',
    ]);
  });
});

describe('population', () => {
  it('counts rows that appeared since the previous list, ignoring the operation', () => {
    const waiting = settledRequirements.filter(
      (row) => row.kind !== 'Metadata' && row.kind !== 'Operation',
    );
    expect(
      addedRequirementCount(
        waiting.map((row) => row.key),
        settledRequirements,
      ),
    ).toBe(4);
    expect(
      addedRequirementCount(
        settledRequirements.map((row) => row.key),
        settledRequirements,
      ),
    ).toBe(0);
  });

  it('finds the processing operation only while it works', () => {
    expect(processingOperation(chronoTriggerRequirements)?.cause).toBe('Working');
    expect(processingOperation(settledRequirements)).toBeUndefined();
  });
});

describe('editors', () => {
  it('opens the editor for the review action, falling back to the row kind', () => {
    const genre = unsatisfied('Metadata:Genre', 'NeedsReview', 'ProviderSuppressed', [
      'ReviewMetadata',
      'AcceptOmission',
      'ReviewProvider',
    ]);
    expect(reviewEditor(genre)).toBe('metadata');
    expect(reviewEditor(genre, 'ReviewProvider')).toBe('match');
    expect(
      reviewEditor(
        unsatisfied('Identity:igdb', 'Automatic', 'Discover', [
          'Enrich',
        ]),
      ),
    ).toBe('match');
    expect(
      reviewEditor(
        unsatisfied('Artwork:Logo', 'Blocked', 'ProviderUnconfigured', [
          'ConfigureProvider',
        ]),
      ),
    ).toBe('artwork');
    expect(reviewEditor(undefined)).toBe('metadata');
  });

  it('names the artwork role only for artwork rows', () => {
    expect(artworkRole(unsatisfied('Artwork:Hero', 'NeedsReview', 'NothingUsable'))).toBe('Hero');
    expect(
      artworkRole(unsatisfied('Metadata:Genre', 'NeedsReview', 'NotProvided')),
    ).toBeUndefined();
  });
});
