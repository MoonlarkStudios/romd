import type {
  EnrichmentIssueState,
  EnrichmentRequirement,
  EnrichmentRequirementCause,
  EnrichmentRequirementSatisfiedBy,
} from '@romd/admin-api-client';
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
  acceptOmissionConfirmation,
  configureProviderHint,
  continueWithoutProviderConfirmation,
  enrichActionLabel,
  findMatchLabel,
  gapSummary,
  isOutstanding,
  leaveEmptyConfirmation,
  matchedGameCopy,
  noLongerInViewLabel,
  outstandingCount,
  providerLabel,
  requirementCopy,
  requirementProviderId,
  requirementSubjectLabel,
  requirementsAddedLabel,
  returnToAutomaticLabel,
  reviewActionLabel,
  satisfiedActionLabel,
  settledExplanation,
  titleStatuses,
  titleStatusLabels,
  trackingLabel,
} from './enrichmentPresentation';

type Unsatisfied = Exclude<EnrichmentIssueState, 'Satisfied'>;
const causeCases: [
  string,
  Unsatisfied,
  EnrichmentRequirementCause,
  string,
][] = [
  [
    'Identity:igdb',
    'NeedsReview',
    'MatchDecisionRequired',
    'Confirm the IGDB match for this title.',
  ],
  [
    'Identity:steamgriddb',
    'NeedsReview',
    'NoMatchFound',
    'SteamGridDB search found no game for this title.',
  ],
  [
    'Identity:igdb',
    'Blocked',
    'ProviderUnconfigured',
    'IGDB is enabled but has no credentials.',
  ],
  [
    'Identity:steamgriddb',
    'Blocked',
    'ProviderDisabled',
    'No enabled provider can identify this title for the requested work.',
  ],
  [
    'Identity:igdb',
    'Blocked',
    'ProviderUnavailable',
    'IGDB could not be queried successfully. You can retry.',
  ],
  [
    'Identity:steamgriddb',
    'Automatic',
    'Discover',
    'Will search SteamGridDB on the next enrichment.',
  ],
  [
    'Identity:igdb',
    'Processing',
    'Working',
    'Enrichment is running for this title.',
  ],
  [
    'Metadata:Description',
    'NeedsReview',
    'NotProvided',
    'Description is missing after enrichment.',
  ],
  [
    'Metadata:ReleaseDate',
    'NeedsReview',
    'NotProvided',
    'Release date is missing after enrichment.',
  ],
  [
    'Metadata:Genre',
    'NeedsReview',
    'ProviderSuppressed',
    'IGDB is not linked for this title, so this field cannot be fetched.',
  ],
  [
    'Metadata:Description',
    'Blocked',
    'AwaitingIdentity',
    'Waiting for the IGDB match decision.',
  ],
  [
    'Metadata:Description',
    'Blocked',
    'ProviderUnconfigured',
    'IGDB has no credentials.',
  ],
  [
    'Metadata:Genre',
    'Blocked',
    'ProviderDisabled',
    'No enabled provider supplies this field.',
  ],
  [
    'Metadata:ReleaseDate',
    'Blocked',
    'ProviderUnavailable',
    'IGDB could not be queried successfully. You can retry.',
  ],
  [
    'Metadata:Description',
    'Automatic',
    'NotAttempted',
    'Not fetched yet.',
  ],
  [
    'Metadata:Genre',
    'Automatic',
    'Stale',
    'Not fetched from the current match yet.',
  ],
  [
    'Metadata:Refresh',
    'Automatic',
    'Stale',
    'Metadata from the current match has not been fetched yet.',
  ],
  [
    'Metadata:Description',
    'Processing',
    'Working',
    'Enrichment is running.',
  ],
  [
    'Metadata:Refresh',
    'Processing',
    'Working',
    'Enrichment is running.',
  ],
  [
    'Artwork:Backdrop',
    'NeedsReview',
    'ArtworkDecisionRequired',
    'Choose backdrop artwork or leave it empty.',
  ],
  [
    'Artwork:Poster',
    'NeedsReview',
    'NothingUsable',
    'No usable poster was found. Choose a saved image, upload one, or leave it empty.',
  ],
  [
    'Artwork:Hero',
    'NeedsReview',
    'NothingUsable',
    'No usable banner was found. Choose a saved image, upload one, or leave it empty.',
  ],
  [
    'Artwork:Poster',
    'NeedsReview',
    'ProviderSuppressed',
    'No linked provider supplies artwork for this title.',
  ],
  [
    'Artwork:Logo',
    'Blocked',
    'AwaitingIdentity',
    'Waiting for the SteamGridDB match decision.',
  ],
  [
    'Artwork:Poster',
    'Blocked',
    'AwaitingIdentity',
    'Waiting for a provider match decision.',
  ],
  [
    'Artwork:Logo',
    'Blocked',
    'ProviderUnconfigured',
    'The artwork provider has no credentials.',
  ],
  [
    'Artwork:Logo',
    'Blocked',
    'ProviderDisabled',
    'No enabled provider supplies logos.',
  ],
  [
    'Artwork:Hero',
    'Blocked',
    'ProviderDisabled',
    'No enabled provider supplies banners.',
  ],
  [
    'Artwork:Logo',
    'Blocked',
    'ProviderUnavailable',
    'SteamGridDB could not be queried successfully. You can retry.',
  ],
  [
    'Artwork:Backdrop',
    'Blocked',
    'ProviderUnavailable',
    'IGDB could not be queried successfully. You can retry.',
  ],
  [
    'Artwork:Poster',
    'Blocked',
    'ProviderUnavailable',
    'A provider could not be queried successfully. You can retry.',
  ],
  [
    'Artwork:Poster',
    'Blocked',
    'AcquisitionFailed',
    'Artwork acquisition failed. You can retry.',
  ],
  [
    'Artwork:Poster',
    'Automatic',
    'NotAttempted',
    'Will search for a poster on the next enrichment.',
  ],
  [
    'Artwork:Backdrop',
    'Processing',
    'Importing',
    'Saving your selected image.',
  ],
  [
    'Artwork:Logo',
    'Processing',
    'Searching',
    'Searching providers for artwork.',
  ],
  [
    'Operation:Processing',
    'Processing',
    'Working',
    'Enrichment is running for this title. Results appear here when it finishes.',
  ],
];
const satisfiedCases: [
  string,
  EnrichmentRequirementSatisfiedBy,
  string,
][] = [
  [
    'Identity:igdb',
    'Trusted',
    'Matched',
  ],
  [
    'Identity:steamgriddb',
    'ProviderSuppressed',
    'Continuing without SteamGridDB',
  ],
  [
    'Metadata:Description',
    'Present',
    'Present',
  ],
  [
    'Metadata:Genre',
    'AcceptedOmission',
    'Intentionally empty',
  ],
  [
    'Metadata:Refresh',
    'Current',
    'No refresh needed',
  ],
  [
    'Artwork:Poster',
    'Present',
    'Present',
  ],
  [
    'Artwork:Backdrop',
    'LeftEmpty',
    'Left empty by you',
  ],
  [
    'Artwork:Logo',
    'UnavailableFallback',
    'Nothing usable found, text title shown',
  ],
  [
    'Artwork:Backdrop',
    'UnavailableFallback',
    'Nothing usable found, details page shows no backdrop',
  ],
  [
    'Artwork:Hero',
    'NotRequested',
    'Not requested in settings',
  ],
  [
    'Operation:Processing',
    'Idle',
    'No action needed',
  ],
];
// Typed as full records so a newly generated enum value fails typecheck until it has a case here.
const everyCause: Record<EnrichmentRequirementCause, true> = {
  MatchDecisionRequired: true,
  NoMatchFound: true,
  ProviderUnconfigured: true,
  ProviderDisabled: true,
  ProviderUnavailable: true,
  Discover: true,
  Working: true,
  NotProvided: true,
  ProviderSuppressed: true,
  AwaitingIdentity: true,
  NotAttempted: true,
  Stale: true,
  ArtworkDecisionRequired: true,
  NothingUsable: true,
  AcquisitionFailed: true,
  Importing: true,
  Searching: true,
};
const everySatisfiedBy: Record<EnrichmentRequirementSatisfiedBy, true> = {
  Present: true,
  AcceptedOmission: true,
  LeftEmpty: true,
  UnavailableFallback: true,
  Trusted: true,
  ProviderSuppressed: true,
  NotRequested: true,
  Current: true,
  Idle: true,
};
const neutral: Record<EnrichmentIssueState, string> = {
  NeedsReview: 'Needs review',
  Blocked: 'Blocked',
  Processing: 'Processing',
  Automatic: 'Will run on the next enrichment',
  Satisfied: 'No action needed',
};

describe('requirementCopy', () => {
  it.each(causeCases)('%s %s %s', (key, state, cause, copy) => {
    expect(requirementCopy(unsatisfied(key, state, cause))).toBe(copy);
  });

  it.each(satisfiedCases)('%s satisfied by %s', (key, satisfiedBy, copy) => {
    expect(requirementCopy(satisfied(key, satisfiedBy))).toBe(copy);
  });

  it('covers every cause and satisfiedBy value', () => {
    expect(new Set(causeCases.map(([, , cause]) => cause))).toEqual(
      new Set(Object.keys(everyCause)),
    );
    expect(new Set(satisfiedCases.map(([, satisfiedBy]) => satisfiedBy))).toEqual(
      new Set(Object.keys(everySatisfiedBy)),
    );
  });

  it.each([
    [
      'a missing cause',
      {
        ...unsatisfied('Metadata:Description', 'NeedsReview', 'NotProvided'),
        cause: null,
      },
    ],
    [
      'an unknown runtime cause',
      {
        ...unsatisfied('Artwork:Logo', 'Blocked', 'AcquisitionFailed'),
        cause: 'RateLimited' as EnrichmentRequirementCause,
      },
    ],
    [
      'a cause from another state',
      unsatisfied('Artwork:Logo', 'Blocked', 'Searching'),
    ],
    [
      'a cause from another kind',
      unsatisfied('Identity:igdb', 'NeedsReview', 'NotProvided'),
    ],
    [
      'a field cause on Refresh',
      unsatisfied('Metadata:Refresh', 'Automatic', 'NotAttempted'),
    ],
    [
      'Stale outside Automatic',
      unsatisfied('Metadata:Genre', 'Blocked', 'Stale'),
    ],
    [
      'an unknown kind',
      unsatisfied('Collection:Favorites', 'Processing', 'Working'),
    ],
  ])('falls back to neutral state copy for %s', (_, requirement) => {
    expect(requirementCopy(requirement)).toBe(neutral[requirement.state]);
  });

  it.each([
    [
      'a missing satisfiedBy',
      {
        ...satisfied('Identity:igdb', 'Trusted'),
        satisfiedBy: null,
      },
    ],
    [
      'an unknown runtime satisfiedBy',
      {
        ...satisfied('Artwork:Poster', 'Present'),
        satisfiedBy: 'Imagined' as EnrichmentRequirementSatisfiedBy,
      },
    ],
    [
      'a satisfiedBy from another kind',
      satisfied('Identity:igdb', 'Present'),
    ],
    [
      'an unavailable fallback on a role without one',
      satisfied('Artwork:Poster', 'UnavailableFallback'),
    ],
    [
      'a field value on Refresh',
      satisfied('Metadata:Refresh', 'Present'),
    ],
  ])('never claims success for %s', (_, requirement) => {
    expect(requirementCopy(requirement)).toBe('No action needed');
  });

  it('never claims success for an unknown state', () => {
    expect(
      requirementCopy({
        ...satisfied('Artwork:Poster', 'Present'),
        state: 'Paused' as EnrichmentIssueState,
      }),
    ).toBe('Needs review');
  });
});

describe('labels', () => {
  it.each([
    [
      'igdb',
      'IGDB',
    ],
    [
      'SteamGridDB',
      'SteamGridDB',
    ],
    [
      'screenscraper',
      'Screenscraper',
    ],
    [
      'the-games_db',
      'The Games Db',
    ],
  ])('names provider %s as %s', (id, label) => {
    expect(providerLabel(id)).toBe(label);
  });

  it.each([
    [
      'Identity:steamgriddb',
      'SteamGridDB',
    ],
    [
      'Metadata:ReleaseDate',
      'Release date',
    ],
    [
      'Metadata:Refresh',
      'Refresh',
    ],
    [
      'Artwork:Hero',
      'Banner',
    ],
    [
      'Artwork:Logo',
      'Logo',
    ],
  ])('labels %s as %s', (key, label) => {
    expect(requirementSubjectLabel(satisfied(key, 'Present'))).toBe(label);
  });

  it.each([
    [
      'Identity:SteamGridDB',
      'steamgriddb',
    ],
    [
      'Metadata:Genre',
      'igdb',
    ],
    [
      'Artwork:Logo',
      'steamgriddb',
    ],
    [
      'Artwork:Backdrop',
      'igdb',
    ],
    [
      'Artwork:Poster',
      undefined,
    ],
    [
      'Operation:Processing',
      undefined,
    ],
  ])('knows the provider behind %s', (key, providerId) => {
    expect(requirementProviderId(satisfied(key, 'Present'))).toBe(providerId);
  });
});

describe('workbench copy', () => {
  it('names the matched game with its year when known', () => {
    expect(
      matchedGameCopy({
        name: 'Chrono Trigger',
        year: 1995,
      }),
    ).toBe('Matched to Chrono Trigger (1995)');
    expect(
      matchedGameCopy({
        name: 'Chrono Trigger',
        year: null,
      }),
    ).toBe('Matched to Chrono Trigger');
  });

  it('announces added requirements with the right plural', () => {
    expect(requirementsAddedLabel(1)).toBe('1 requirement added');
    expect(requirementsAddedLabel(3)).toBe('3 requirements added');
  });

  it('labels view membership and tracking', () => {
    expect(noLongerInViewLabel).toBe('No longer in this view');
    expect(trackingLabel(true)).toBe('Tracked');
    expect(trackingLabel(false)).toBe('Untracked');
  });
});

describe('title status', () => {
  const flags = {
    needsReview: false,
    hasOperationalBlocker: false,
    hasActiveWork: false,
    canEnrichAutomatically: false,
  };

  it('orders chips NeedsReview, Blocked, Processing, Automatic regardless of input order', () => {
    const statuses = titleStatuses({
      canEnrichAutomatically: true,
      hasActiveWork: true,
      hasOperationalBlocker: true,
      needsReview: true,
    });
    expect(statuses).toEqual([
      'NeedsReview',
      'Blocked',
      'Processing',
      'Automatic',
    ]);
    expect(statuses.map((status) => titleStatusLabels[status])).toEqual([
      'Needs review',
      'Blocked',
      'Processing',
      'Automatic',
    ]);
  });

  it('collapses to Settled only when no flag applies', () => {
    expect(titleStatuses(flags)).toEqual([
      'Settled',
    ]);
    expect(
      titleStatuses({
        ...flags,
        hasActiveWork: true,
      }),
    ).toEqual([
      'Processing',
    ]);
    expect(titleStatusLabels.Settled).toBe('Settled');
    expect(settledExplanation).toBe(
      'Nothing outstanding. Some values may be intentionally empty or unavailable.',
    );
  });
});

describe('outstanding count', () => {
  it('counts unsatisfied identity, metadata including Refresh, and artwork rows, never Operation', () => {
    const rows = withRequirements(
      unsatisfied('Identity:igdb', 'Automatic', 'Discover'),
      unsatisfied('Metadata:Refresh', 'Automatic', 'Stale'),
      unsatisfied('Artwork:Poster', 'Processing', 'Searching'),
      unsatisfied('Operation:Processing', 'Processing', 'Working'),
    );
    expect(outstandingCount(rows)).toBe(3);
    expect(isOutstanding(unsatisfied('Operation:Processing', 'Processing', 'Working'))).toBe(false);
    expect(outstandingCount(settledRequirements)).toBe(0);
  });
});

describe('gap summary', () => {
  it('reports scenario 8.1 as 4 outstanding, then 3 after the description is saved', () => {
    expect(titleStatuses(assessmentFixture(chronoTriggerRequirements))).toEqual([
      'NeedsReview',
      'Blocked',
      'Processing',
    ]);
    expect(gapSummary(assessmentFixture(chronoTriggerRequirements))).toBe('4 outstanding');
    const saved = chronoTriggerRequirements.map((row) =>
      row.key === 'Metadata:Description'
        ? satisfied(row.key, 'Present', [
            'ReviewMetadata',
          ])
        : row,
    );
    expect(titleStatuses(assessmentFixture(saved))).toEqual([
      'Blocked',
      'Processing',
    ]);
    expect(gapSummary(assessmentFixture(saved))).toBe('3 outstanding');
  });

  it('reports scenario 8.2 as settled with two optional assets unavailable', () => {
    const settled = assessmentFixture(obscureGameBoyRequirements);
    expect(titleStatuses(settled)).toEqual([
      'Settled',
    ]);
    expect(gapSummary(settled)).toBe('Nothing outstanding · 2 optional assets unavailable');
  });

  it('lists every non-Present satisfied cause in the plan order', () => {
    const rows = withRequirements(
      satisfied('Identity:steamgriddb', 'ProviderSuppressed'),
      satisfied('Metadata:Description', 'AcceptedOmission'),
      satisfied('Metadata:ReleaseDate', 'AcceptedOmission'),
      satisfied('Artwork:Hero', 'NotRequested'),
      satisfied('Artwork:Backdrop', 'LeftEmpty'),
      satisfied('Artwork:Logo', 'UnavailableFallback'),
      unsatisfied('Artwork:Poster', 'NeedsReview', 'NothingUsable'),
    );
    expect(gapSummary(assessmentFixture(rows))).toBe(
      [
        '1 outstanding',
        'Description intentionally empty',
        'Release date intentionally empty',
        'Backdrop left empty',
        'Logo: nothing usable found, text title shown',
        'Continuing without SteamGridDB',
        'Banner not requested in settings',
      ].join(' · '),
    );
  });

  it('names a single unavailable backdrop by its fallback', () => {
    const rows = withRequirements(satisfied('Artwork:Backdrop', 'UnavailableFallback'));
    expect(gapSummary(assessmentFixture(rows))).toBe(
      'Nothing outstanding · Backdrop: nothing usable found, details page shows no backdrop',
    );
  });

  it('says there are no checklist actions while only the operation is processing', () => {
    const rows = withRequirements(unsatisfied('Operation:Processing', 'Processing', 'Working'));
    expect(gapSummary(assessmentFixture(rows))).toBe('No checklist actions · Processing');
  });
});

describe('actions', () => {
  it.each([
    [
      'ReviewProvider',
      unsatisfied('Identity:steamgriddb', 'NeedsReview', 'MatchDecisionRequired'),
      'Review SteamGridDB match',
    ],
    [
      'ReviewProvider',
      unsatisfied('Metadata:Genre', 'Blocked', 'AwaitingIdentity'),
      'Review IGDB match',
    ],
    [
      'ReviewProvider',
      unsatisfied('Artwork:Poster', 'Blocked', 'AwaitingIdentity'),
      'Review provider match',
    ],
    [
      'ReviewMetadata',
      unsatisfied('Metadata:ReleaseDate', 'NeedsReview', 'NotProvided'),
      'Review release date',
    ],
    [
      'ReviewArtwork',
      unsatisfied('Artwork:Hero', 'NeedsReview', 'NothingUsable'),
      'Review banner',
    ],
    [
      'Enrich',
      unsatisfied('Artwork:Hero', 'Automatic', 'NotAttempted'),
      undefined,
    ],
  ])('labels %s for %s', (action, requirement: EnrichmentRequirement, label) => {
    expect(reviewActionLabel(action, requirement)).toBe(label);
  });

  it.each([
    [
      'ReviewProvider',
      satisfied('Identity:igdb', 'Trusted'),
      'Change IGDB match',
    ],
    [
      'ReviewProvider',
      satisfied('Identity:steamgriddb', 'ProviderSuppressed'),
      'Find SteamGridDB match',
    ],
    [
      'ReviewMetadata',
      satisfied('Metadata:ReleaseDate', 'Present'),
      'Edit release date',
    ],
    [
      'ReviewMetadata',
      satisfied('Metadata:Description', 'AcceptedOmission'),
      'Edit description',
    ],
    [
      'ReviewArtwork',
      satisfied('Artwork:Hero', 'LeftEmpty'),
      'Edit banner',
    ],
    [
      'Enrich',
      satisfied('Metadata:Refresh', 'Current'),
      undefined,
    ],
  ])('labels satisfied %s for %s', (action, requirement: EnrichmentRequirement, label) => {
    expect(satisfiedActionLabel(action, requirement)).toBe(label);
  });

  it('names reversals by their subject', () => {
    expect(findMatchLabel('igdb')).toBe('Find IGDB match');
    expect(returnToAutomaticLabel('Hero')).toBe('Return banner to automatic');
    expect(returnToAutomaticLabel('Backdrop')).toBe('Return backdrop to automatic');
  });

  it('distinguishes retry from enrichment', () => {
    expect(
      enrichActionLabel(
        unsatisfied('Artwork:Poster', 'Blocked', 'AcquisitionFailed', [
          'Retry',
        ]),
      ),
    ).toBe('Retry enrichment');
    expect(
      enrichActionLabel(
        unsatisfied('Artwork:Poster', 'Automatic', 'NotAttempted', [
          'Enrich',
        ]),
      ),
    ).toBe('Enrich this title');
  });

  it('confirms immediate decisions with consequence copy', () => {
    expect(acceptOmissionConfirmation('Description')).toEqual({
      title: 'Keep description empty?',
      message: 'The consumer app shows no description for this title. You can revisit this later.',
      confirmLabel: 'Keep description empty',
    });
    expect(leaveEmptyConfirmation('Hero')).toEqual({
      title: 'Leave banner empty?',
      message:
        'The consumer app shows no banner for this title and automatic runs will not fill it. Use Return to automatic to undo.',
      confirmLabel: 'Leave banner empty',
    });
    expect(continueWithoutProviderConfirmation('steamgriddb')).toEqual({
      title: 'Continue without SteamGridDB?',
      message:
        'Automatic matching with SteamGridDB stops for this title. You can find a match later.',
      confirmLabel: 'Continue without SteamGridDB',
    });
  });

  it.each([
    [
      unsatisfied('Identity:steamgriddb', 'Blocked', 'ProviderUnconfigured'),
      'An administrator can add SteamGridDB credentials',
    ],
    [
      unsatisfied('Identity:igdb', 'Blocked', 'ProviderDisabled'),
      'An administrator can enable IGDB',
    ],
    [
      unsatisfied('Metadata:Genre', 'Blocked', 'ProviderUnconfigured'),
      'An administrator can add IGDB credentials',
    ],
    [
      unsatisfied('Artwork:Logo', 'Blocked', 'ProviderDisabled'),
      'An administrator can enable SteamGridDB',
    ],
    [
      unsatisfied('Artwork:Poster', 'Blocked', 'ProviderUnconfigured'),
      'An administrator can add artwork provider credentials',
    ],
    [
      unsatisfied('Artwork:Hero', 'Blocked', 'ProviderDisabled'),
      'An administrator can enable an artwork provider',
    ],
  ])('tells a Manager who can fix %o', (requirement, hint) => {
    expect(configureProviderHint(requirement)).toBe(hint);
  });
});
