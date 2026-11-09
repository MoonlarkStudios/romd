import type {
  EnrichmentAssessment,
  EnrichmentIssueState,
  EnrichmentRequirement,
  EnrichmentRequirementCause,
  EnrichmentRequirementSatisfiedBy,
  TitleDetail,
  TitleMediaRef,
} from '@romd/admin-api-client';
import type { TitleDetailView, TitleReleaseView } from '../../../hooks/api/useTitleDetail';

export const artworkPage = <T>(items: T[], nextCursor: string | null = null) => ({
  items,
  nextCursor,
  hasNextPage: nextCursor !== null,
});

type TitleFixtureOverrides = Omit<Partial<TitleDetail>, 'media' | 'releases'> & {
  media?: TitleDetail['media'] | TitleMediaRef[];
  releases?: TitleDetail['releases'] | TitleReleaseView[];
};

function parts(key: string) {
  const [kind, subject] = key.split(':');
  return {
    key,
    kind,
    subject,
  };
}

export function satisfied(
  key: string,
  satisfiedBy: EnrichmentRequirementSatisfiedBy,
  actions: string[] = [],
): EnrichmentRequirement {
  return {
    ...parts(key),
    state: 'Satisfied',
    cause: null,
    satisfiedBy,
    actions,
  };
}

export function unsatisfied(
  key: string,
  state: Exclude<EnrichmentIssueState, 'Satisfied'>,
  cause: EnrichmentRequirementCause,
  actions: string[] = [],
): EnrichmentRequirement {
  return {
    ...parts(key),
    state,
    cause,
    satisfiedBy: null,
    actions,
  };
}

/** Every row the facts function emits for a title with nothing outstanding, in canonical order. */
export const settledRequirements: EnrichmentRequirement[] = [
  satisfied('Identity:igdb', 'Trusted', [
    'ReviewProvider',
  ]),
  satisfied('Identity:steamgriddb', 'Trusted', [
    'ReviewProvider',
  ]),
  satisfied('Metadata:Description', 'Present', [
    'ReviewMetadata',
  ]),
  satisfied('Metadata:Genre', 'Present', [
    'ReviewMetadata',
  ]),
  satisfied('Metadata:ReleaseDate', 'Present', [
    'ReviewMetadata',
  ]),
  satisfied('Metadata:Refresh', 'Current'),
  satisfied('Artwork:Poster', 'Present', [
    'ReviewArtwork',
  ]),
  satisfied('Artwork:Backdrop', 'Present', [
    'ReviewArtwork',
  ]),
  satisfied('Artwork:Hero', 'Present', [
    'ReviewArtwork',
  ]),
  satisfied('Artwork:Logo', 'Present', [
    'ReviewArtwork',
  ]),
  satisfied('Operation:Processing', 'Idle'),
];

/** The settled rows with the given rows replacing (or joining) them by key. */
export function withRequirements(...changes: EnrichmentRequirement[]): EnrichmentRequirement[] {
  const replaced = settledRequirements.map(
    (row) => changes.find((change) => change.key === row.key) ?? row,
  );
  return [
    ...replaced,
    ...changes.filter((change) => !settledRequirements.some((row) => row.key === change.key)),
  ];
}

/** The title representation a core title write, such as an omission decision, returns with its new revision. */
export function writtenTitleFixture(overrides: TitleFixtureOverrides = {}): TitleDetail {
  return {
    id: 'chrono-trigger',
    revision: 'rev-2',
    systemKey: 'snes',
    name: 'Chrono Trigger',
    enrichmentStatus: 'Completed',
    artwork: [],
    contentRatings: [],
    releaseRegionPriority: [],
    hasLocalPayload: false,
    isTracked: true,
    completionPercent: 0,
    createdAt: '2024-01-01T00:00:00Z',
    ...overrides,
    media: Array.isArray(overrides.media)
      ? artworkPage(overrides.media)
      : (overrides.media ?? artworkPage([])),
    releases: Array.isArray(overrides.releases)
      ? artworkPage(
          overrides.releases.map((release) => ({
            ...release,
            sources: artworkPage(release.sources ?? []),
          })),
        )
      : (overrides.releases ?? artworkPage([])),
  };
}

/** The complete local view used by components, distinct from the wire's embedded pages. */
export function writtenTitleViewFixture(overrides: Partial<TitleDetailView> = {}): TitleDetailView {
  return {
    ...writtenTitleFixture(),
    media: [],
    releases: [],
    ...overrides,
  };
}

/** An assessment whose four flags follow its rows, as the backend computes them. */
export function assessmentFixture(
  requirements: EnrichmentRequirement[],
  overrides: Partial<EnrichmentAssessment> = {},
): EnrichmentAssessment {
  const any = (state: EnrichmentIssueState) => requirements.some((row) => row.state === state);
  return {
    titleId: 'chrono-trigger',
    titleName: 'Chrono Trigger',
    platformName: 'Super Nintendo',
    revision: 'rev-1',
    needsReview: any('NeedsReview'),
    canEnrichAutomatically: any('Automatic'),
    hasActiveWork: any('Processing'),
    hasOperationalBlocker: any('Blocked'),
    requirements,
    acceptedOmissions: [],
    ...overrides,
  };
}

/** Plan scenario 8.1: mixed states on Chrono Trigger. A pending import always keeps the operation working. */
export const chronoTriggerRequirements = withRequirements(
  unsatisfied('Identity:steamgriddb', 'Blocked', 'ProviderUnconfigured', [
    'ConfigureProvider',
  ]),
  unsatisfied('Metadata:Description', 'NeedsReview', 'NotProvided', [
    'ReviewMetadata',
    'AcceptOmission',
  ]),
  unsatisfied('Artwork:Backdrop', 'Processing', 'Importing'),
  unsatisfied('Artwork:Logo', 'Blocked', 'ProviderUnconfigured', [
    'ConfigureProvider',
  ]),
  unsatisfied('Operation:Processing', 'Processing', 'Working'),
);

/** Plan scenario 8.2: only optional assets missing on an obscure Game Boy title. */
export const obscureGameBoyRequirements = withRequirements(
  satisfied('Artwork:Backdrop', 'UnavailableFallback', [
    'ReviewArtwork',
  ]),
  satisfied('Artwork:Logo', 'UnavailableFallback', [
    'ReviewArtwork',
  ]),
);
