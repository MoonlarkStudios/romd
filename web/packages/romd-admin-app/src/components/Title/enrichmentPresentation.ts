import type {
  EnrichmentAssessment,
  EnrichmentIssueState,
  EnrichmentRequirement,
  EnrichmentRequirementCause,
  EnrichmentRequirementSatisfiedBy,
  ProviderGame,
} from '@romd/admin-api-client';
import { artworkRoleLabel } from '../artworkPresentation';

// The only home of enrichment requirement copy. Every string is keyed by structured contract values.

const requirementKinds = [
  'Identity',
  'Metadata',
  'Artwork',
  'Operation',
] as const;
type RequirementKind = (typeof requirementKinds)[number];
type Copy = (requirement: EnrichmentRequirement) => string | undefined;
type CopyByKind = Partial<Record<RequirementKind, Copy>>;

export type TitleStatus = 'NeedsReview' | 'Blocked' | 'Processing' | 'Automatic' | 'Settled';
type StatusFlags = Pick<
  EnrichmentAssessment,
  'needsReview' | 'hasOperationalBlocker' | 'hasActiveWork' | 'canEnrichAutomatically'
>;
type AssessmentSummary = StatusFlags & Pick<EnrichmentAssessment, 'requirements'>;

export interface DecisionConfirmationCopy {
  title: string;
  message: string;
  confirmLabel: string;
}

export const titleStatusLabels: Record<TitleStatus, string> = {
  NeedsReview: 'Needs review',
  Blocked: 'Blocked',
  Processing: 'Processing',
  Automatic: 'Automatic',
  Settled: 'Settled',
};

export const settledExplanation =
  'Nothing outstanding. Some values may be intentionally empty or unavailable.';

/** Title status chips in their fixed scanning order; Settled only when no flag applies. */
export function titleStatuses(flags: StatusFlags): TitleStatus[] {
  const statuses: TitleStatus[] = [];
  if (flags.needsReview) statuses.push('NeedsReview');
  if (flags.hasOperationalBlocker) statuses.push('Blocked');
  if (flags.hasActiveWork) statuses.push('Processing');
  if (flags.canEnrichAutomatically) statuses.push('Automatic');
  return statuses.length
    ? statuses
    : [
        'Settled',
      ];
}

const providerNames: Partial<Record<string, string>> = {
  igdb: 'IGDB',
  steamgriddb: 'SteamGridDB',
};
const fieldNames: Partial<Record<string, string>> = {
  ReleaseDate: 'Release date',
};
/** Roles only one provider can serve; other roles never name a provider. */
const roleProviders: Partial<Record<string, string>> = {
  Logo: 'steamgriddb',
  Backdrop: 'igdb',
};
const unavailableFallbacks: Partial<Record<string, string>> = {
  Logo: 'Nothing usable found, text title shown',
  Backdrop: 'Nothing usable found, details page shows no backdrop',
};

export function providerLabel(providerId: string): string {
  return (
    providerNames[providerId.toLowerCase()] ??
    providerId
      .split(/[-_\s]+/)
      .filter(Boolean)
      .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
      .join(' ')
  );
}

export function fieldLabel(field: string): string {
  return fieldNames[field] ?? field;
}

function isRequirementKind(kind: string): kind is RequirementKind {
  return requirementKinds.some((value) => value === kind);
}

/** The provider a requirement concerns, when one is known: its own for identity, IGDB for core metadata. */
export function requirementProviderId(
  requirement: Pick<EnrichmentRequirement, 'kind' | 'subject'>,
): string | undefined {
  if (requirement.kind === 'Identity') return requirement.subject.toLowerCase();
  if (requirement.kind === 'Metadata') return 'igdb';
  if (requirement.kind === 'Artwork') return roleProviders[requirement.subject];
  return undefined;
}

export function requirementSubjectLabel(requirement: EnrichmentRequirement): string {
  if (requirement.kind === 'Identity') return providerLabel(requirement.subject);
  if (requirement.kind === 'Metadata') return fieldLabel(requirement.subject);
  if (requirement.kind === 'Artwork') return artworkRoleLabel(requirement.subject);
  return requirement.subject;
}

const lower = (requirement: EnrichmentRequirement) =>
  requirementSubjectLabel(requirement).toLowerCase();
const provider = (requirement: EnrichmentRequirement) => providerLabel(requirement.subject);
const igdb = providerLabel('igdb');
const roleProvider = (requirement: EnrichmentRequirement) => {
  const providerId = roleProviders[requirement.subject];
  return providerId && providerLabel(providerId);
};
const forField =
  (copy: Copy): Copy =>
  (requirement) =>
    requirement.subject === 'Refresh' ? undefined : copy(requirement);
const forRefresh =
  (copy: Copy): Copy =>
  (requirement) =>
    requirement.subject === 'Refresh' ? copy(requirement) : undefined;

const causeCopy: Record<
  EnrichmentRequirementCause,
  {
    state: EnrichmentIssueState;
    copy: CopyByKind;
  }
> = {
  MatchDecisionRequired: {
    state: 'NeedsReview',
    copy: {
      Identity: (r) => `Confirm the ${provider(r)} match for this title.`,
    },
  },
  NoMatchFound: {
    state: 'NeedsReview',
    copy: {
      Identity: (r) => `${provider(r)} search found no game for this title.`,
    },
  },
  ProviderUnconfigured: {
    state: 'Blocked',
    copy: {
      Identity: (r) => `${provider(r)} is enabled but has no credentials.`,
      Metadata: forField(() => `${igdb} has no credentials.`),
      Artwork: () => 'The artwork provider has no credentials.',
    },
  },
  ProviderDisabled: {
    state: 'Blocked',
    copy: {
      Identity: () => 'No enabled provider can identify this title for the requested work.',
      Metadata: forField(() => 'No enabled provider supplies this field.'),
      Artwork: (r) => `No enabled provider supplies ${lower(r)}s.`,
    },
  },
  ProviderUnavailable: {
    state: 'Blocked',
    copy: {
      Identity: (r) => `${provider(r)} could not be queried successfully. You can retry.`,
      Metadata: forField(() => `${igdb} could not be queried successfully. You can retry.`),
      Artwork: (r) =>
        `${roleProvider(r) ?? 'A provider'} could not be queried successfully. You can retry.`,
    },
  },
  Discover: {
    state: 'Automatic',
    copy: {
      Identity: (r) => `Will search ${provider(r)} on the next enrichment.`,
    },
  },
  Working: {
    state: 'Processing',
    copy: {
      Identity: () => 'Enrichment is running for this title.',
      Metadata: () => 'Enrichment is running.',
      Operation: () =>
        'Enrichment is running for this title. Results appear here when it finishes.',
    },
  },
  NotProvided: {
    state: 'NeedsReview',
    copy: {
      Metadata: forField((r) => `${fieldLabel(r.subject)} is missing after enrichment.`),
    },
  },
  ProviderSuppressed: {
    state: 'NeedsReview',
    copy: {
      Metadata: forField(
        () => `${igdb} is not linked for this title, so this field cannot be fetched.`,
      ),
      Artwork: () => 'No linked provider supplies artwork for this title.',
    },
  },
  AwaitingIdentity: {
    state: 'Blocked',
    copy: {
      Metadata: forField(() => `Waiting for the ${igdb} match decision.`),
      Artwork: (r) =>
        `Waiting for ${roleProvider(r) ? `the ${roleProvider(r)}` : 'a provider'} match decision.`,
    },
  },
  NotAttempted: {
    state: 'Automatic',
    copy: {
      Metadata: forField(() => 'Not fetched yet.'),
      Artwork: (r) => `Will search for a ${lower(r)} on the next enrichment.`,
    },
  },
  Stale: {
    state: 'Automatic',
    copy: {
      Metadata: (r) =>
        r.subject === 'Refresh'
          ? 'Metadata from the current match has not been fetched yet.'
          : 'Not fetched from the current match yet.',
    },
  },
  ArtworkDecisionRequired: {
    state: 'NeedsReview',
    copy: {
      Artwork: (r) => `Choose ${lower(r)} artwork or leave it empty.`,
    },
  },
  NothingUsable: {
    state: 'NeedsReview',
    copy: {
      Artwork: (r) =>
        `No usable ${lower(r)} was found. Choose a saved image, upload one, or leave it empty.`,
    },
  },
  AcquisitionFailed: {
    state: 'Blocked',
    copy: {
      Artwork: () => 'Artwork acquisition failed. You can retry.',
    },
  },
  Importing: {
    state: 'Processing',
    copy: {
      Artwork: () => 'Saving your selected image.',
    },
  },
  Searching: {
    state: 'Processing',
    copy: {
      Artwork: () => 'Searching providers for artwork.',
    },
  },
};

const satisfiedCopy: Record<EnrichmentRequirementSatisfiedBy, CopyByKind> = {
  Present: {
    Metadata: forField(() => 'Present'),
    Artwork: () => 'Present',
  },
  AcceptedOmission: {
    Metadata: forField(() => 'Intentionally empty'),
  },
  LeftEmpty: {
    Artwork: () => 'Left empty by you',
  },
  UnavailableFallback: {
    Artwork: (r) => unavailableFallbacks[r.subject],
  },
  Trusted: {
    Identity: () => 'Matched',
  },
  ProviderSuppressed: {
    Identity: (r) => `Continuing without ${provider(r)}`,
  },
  NotRequested: {
    Artwork: () => 'Not requested in settings',
  },
  Current: {
    Metadata: forRefresh(() => 'No refresh needed'),
  },
  Idle: {
    Operation: () => 'No action needed',
  },
};

/** Used whenever structured data is missing, unknown or inconsistent: never claims success. */
const neutralStateCopy: Record<EnrichmentIssueState, string> = {
  NeedsReview: 'Needs review',
  Blocked: 'Blocked',
  Processing: 'Processing',
  Automatic: 'Will run on the next enrichment',
  Satisfied: 'No action needed',
};

export function requirementCopy(requirement: EnrichmentRequirement): string {
  const neutral: string | undefined = neutralStateCopy[requirement.state];
  const fallback = neutral ?? neutralStateCopy.NeedsReview;
  if (!isRequirementKind(requirement.kind)) return fallback;
  if (requirement.state === 'Satisfied') {
    const byKind: CopyByKind | undefined = requirement.satisfiedBy
      ? satisfiedCopy[requirement.satisfiedBy]
      : undefined;
    return byKind?.[requirement.kind]?.(requirement) ?? fallback;
  }
  const entry:
    | {
        state: EnrichmentIssueState;
        copy: CopyByKind;
      }
    | undefined = requirement.cause ? causeCopy[requirement.cause] : undefined;
  return (
    (entry?.state === requirement.state
      ? entry.copy[requirement.kind]?.(requirement)
      : undefined) ?? fallback
  );
}

/** Checklist work: unsatisfied identity, metadata (including Refresh) and artwork rows. Operation rows drive the Processing chip instead. */
export function isOutstanding(requirement: EnrichmentRequirement): boolean {
  return (
    requirement.state !== 'Satisfied' &&
    isRequirementKind(requirement.kind) &&
    requirement.kind !== 'Operation'
  );
}

export function outstandingRequirements(
  requirements: EnrichmentRequirement[],
): EnrichmentRequirement[] {
  return requirements.filter(isOutstanding);
}

export function outstandingCount(requirements: EnrichmentRequirement[]): number {
  return outstandingRequirements(requirements).length;
}

export function outstandingLabel(count: number): string {
  return `${count} outstanding`;
}

function acceptedOmissionLabel(field: string): string {
  return `${fieldLabel(field)} intentionally empty`;
}

export function revisitOmissionLabel(field: string): string {
  return `Revisit ${fieldLabel(field).toLowerCase()}`;
}

type GapPhrases = (rows: EnrichmentRequirement[]) => string[];
const fallbackPhrase = (row: EnrichmentRequirement) => {
  const copy = unavailableFallbacks[row.subject] ?? 'Nothing usable found';
  return `${artworkRoleLabel(row.subject)}: ${copy.charAt(0).toLowerCase()}${copy.slice(1)}`;
};
/** Phrases for satisfied rows that do not establish content exists, in the plan's order. */
const gapPhrases: Record<
  EnrichmentRequirementSatisfiedBy,
  {
    kind: RequirementKind;
    phrases: GapPhrases;
  } | null
> = {
  AcceptedOmission: {
    kind: 'Metadata',
    phrases: (rows) => rows.map((row) => acceptedOmissionLabel(row.subject)),
  },
  LeftEmpty: {
    kind: 'Artwork',
    phrases: (rows) => rows.map((row) => `${artworkRoleLabel(row.subject)} left empty`),
  },
  UnavailableFallback: {
    kind: 'Artwork',
    phrases: (rows) =>
      rows.length > 1
        ? [
            `${rows.length} optional assets unavailable`,
          ]
        : rows.map(fallbackPhrase),
  },
  ProviderSuppressed: {
    kind: 'Identity',
    phrases: (rows) => rows.map((row) => `Continuing without ${provider(row)}`),
  },
  NotRequested: {
    kind: 'Artwork',
    phrases: (rows) =>
      rows.map((row) => `${artworkRoleLabel(row.subject)} not requested in settings`),
  },
  Present: null,
  Trusted: null,
  Current: null,
  Idle: null,
};

/** Outstanding count first, then what the satisfied rows really mean. Never a total, never "Complete". */
export function gapSummary(assessment: AssessmentSummary): string {
  const outstanding = outstandingCount(assessment.requirements);
  const statuses = titleStatuses(assessment);
  const lead =
    outstanding > 0
      ? outstandingLabel(outstanding)
      : statuses[0] === 'Settled'
        ? 'Nothing outstanding'
        : [
            'No checklist actions',
            ...statuses.map((status) => titleStatusLabels[status]),
          ].join(' · ');
  const phrases = Object.entries(gapPhrases).flatMap(([satisfiedBy, gap]) => {
    const rows = assessment.requirements.filter(
      (row) =>
        row.state === 'Satisfied' && row.satisfiedBy === satisfiedBy && row.kind === gap?.kind,
    );
    return gap && rows.length ? gap.phrases(rows) : [];
  });
  return [
    lead,
    ...phrases,
  ].join(' · ');
}

const reviewActionLabels: Partial<Record<string, Copy>> = {
  ReviewProvider: (r) => {
    const providerId = requirementProviderId(r);
    return providerId ? `Review ${providerLabel(providerId)} match` : 'Review provider match';
  },
  ReviewMetadata: (r) => `Review ${lower(r)}`,
  ReviewArtwork: (r) => `Review ${lower(r)}`,
};

export function reviewActionLabel(
  action: string,
  requirement: EnrichmentRequirement,
): string | undefined {
  return reviewActionLabels[action]?.(requirement);
}

const satisfiedActionLabels: Partial<Record<string, Copy>> = {
  ReviewProvider: (r) =>
    r.satisfiedBy === 'ProviderSuppressed'
      ? findMatchLabel(r.subject)
      : `Change ${provider(r)} match`,
  ReviewMetadata: (r) => `Edit ${lower(r)}`,
  ReviewArtwork: (r) => `Edit ${lower(r)}`,
};

/** A satisfied row's review action: changing what is there, or finding a match the curator went without. */
export function satisfiedActionLabel(
  action: string,
  requirement: EnrichmentRequirement,
): string | undefined {
  return satisfiedActionLabels[action]?.(requirement);
}

export function findMatchLabel(providerId: string): string {
  return `Find ${providerLabel(providerId)} match`;
}

export function returnToAutomaticLabel(role: string): string {
  return `Return ${artworkRoleLabel(role).toLowerCase()} to automatic`;
}

/** Identity copy naming the matched game when provider-match data is already loaded. */
export function matchedGameCopy(game: Pick<ProviderGame, 'name' | 'year'>): string {
  return `Matched to ${game.name}${game.year ? ` (${game.year})` : ''}`;
}

export function requirementsAddedLabel(count: number): string {
  return `${count} ${count === 1 ? 'requirement' : 'requirements'} added`;
}

export const noLongerInViewLabel = 'No longer in this view';

export function trackingLabel(isTracked: boolean): string {
  return isTracked ? 'Tracked' : 'Untracked';
}

export function enrichActionLabel(requirement: EnrichmentRequirement): string {
  return requirement.actions.includes('Retry') ? 'Retry enrichment' : 'Enrich this title';
}

export function acceptOmissionConfirmation(field: string): DecisionConfirmationCopy {
  const label = fieldLabel(field).toLowerCase();
  return {
    title: `Keep ${label} empty?`,
    message: `The consumer app shows no ${label} for this title. You can revisit this later.`,
    confirmLabel: `Keep ${label} empty`,
  };
}

export function leaveEmptyConfirmation(role: string): DecisionConfirmationCopy {
  const label = artworkRoleLabel(role).toLowerCase();
  return {
    title: `Leave ${label} empty?`,
    message: `The consumer app shows no ${label} for this title and automatic runs will not fill it. Use Return to automatic to undo.`,
    confirmLabel: `Leave ${label} empty`,
  };
}

export function continueWithoutProviderConfirmation(providerId: string): DecisionConfirmationCopy {
  const label = providerLabel(providerId);
  return {
    title: `Continue without ${label}?`,
    message: `Automatic matching with ${label} stops for this title. You can find a match later.`,
    confirmLabel: `Continue without ${label}`,
  };
}

/** What a Manager sees instead of provider settings, which only Admins can open. */
export function configureProviderHint(requirement: EnrichmentRequirement): string {
  const providerId = requirementProviderId(requirement);
  if (requirement.cause === 'ProviderDisabled') {
    return `An administrator can enable ${providerId ? providerLabel(providerId) : 'an artwork provider'}`;
  }
  return `An administrator can add ${providerId ? providerLabel(providerId) : 'artwork provider'} credentials`;
}
