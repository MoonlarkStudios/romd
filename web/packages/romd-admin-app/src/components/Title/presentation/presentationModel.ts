import type {
  ArtworkCandidate,
  ArtworkSelectionState,
  ResolvedArtwork,
} from '@romd/admin-api-client';
import type { PreviewDevice } from '@romd/consumer-ui';
import { type ArtworkRole, artworkRoles } from '../../artworkPresentation';
import type { SavedArtworkTarget } from '../artworkLibrary';
import type { FocalPoint } from '../FocalPointEditor';

/** The consumer surfaces the presentation canvas lays out, and the roles each one shows. */
export type PresentationSurface = 'library' | 'spotlight' | 'details';

export const presentationSurfaces: readonly {
  value: PresentationSurface;
  label: string;
  roles: readonly ArtworkRole[];
}[] = [
  {
    value: 'library',
    label: 'Library card',
    roles: [
      'Poster',
    ],
  },
  {
    value: 'spotlight',
    label: 'Home spotlight',
    roles: [
      'Hero',
      'Logo',
      'Poster',
    ],
  },
  {
    value: 'details',
    label: 'Game details',
    roles: [
      'Backdrop',
      'Logo',
      'Poster',
    ],
  },
];

export const isPresentationSurface = (value: string): value is PresentationSurface =>
  presentationSurfaces.some((surface) => surface.value === value);

/** The surface that shows a role: the current one when it does, otherwise the role's home surface. */
export function surfaceForRole(
  role: ArtworkRole,
  current: PresentationSurface,
): PresentationSurface {
  if (presentationSurfaces.find((surface) => surface.value === current)?.roles.includes(role))
    return current;
  return role === 'Poster' ? 'library' : role === 'Hero' ? 'spotlight' : 'details';
}

/**
 * Where each role sits in the rendered consumer surface, as selectors tried in order. A missing image still has a
 * place to click: the backdrop falls back to the hero section, the banner to the spotlight, the logo to the title.
 */
export const roleTargets: Readonly<
  Record<PresentationSurface, Partial<Record<ArtworkRole, readonly string[]>>>
> = {
  library: {
    Poster: [
      '.romd-game-poster-surface',
    ],
  },
  spotlight: {
    Hero: [
      '.romd-home-spotlight-art',
      '.romd-home-spotlight',
    ],
    Poster: [
      '.romd-home-spotlight-poster',
    ],
    Logo: [
      '.romd-home-spotlight-info .romd-hero',
    ],
  },
  details: {
    Backdrop: [
      '.romd-detail-backdrop',
      '.romd-detail-hero',
    ],
    Poster: [
      '.romd-detail-poster',
    ],
    Logo: [
      '.romd-detail-copy .romd-hero',
    ],
  },
};

/** Roles whose cover-fit crop a focal point moves; logos always show whole. */
export const focalRoles: readonly ArtworkRole[] = [
  'Poster',
  'Hero',
  'Backdrop',
];

export interface CropProbe {
  label: string;
  surface: PresentationSurface;
  device: PreviewDevice;
}

/** The consumer frames a role is cropped to, outlined while the curator places its focal point. */
export function cropProbes(role: ArtworkRole): readonly CropProbe[] {
  if (role === 'Poster')
    return [
      {
        label: 'Library card',
        surface: 'library',
        device: 'desktop',
      },
      {
        label: 'Home spotlight',
        surface: 'spotlight',
        device: 'desktop',
      },
      {
        label: 'Game details',
        surface: 'details',
        device: 'mobile',
      },
    ];
  if (role === 'Logo') return [];
  const surface = role === 'Hero' ? 'spotlight' : 'details';
  return (
    [
      'desktop',
      'tablet',
      'mobile',
    ] as const
  ).map((device) => ({
    label: device === 'desktop' ? 'Desktop' : device === 'tablet' ? 'Tablet' : 'Mobile',
    surface,
    device,
  }));
}

export interface ArtworkDraft extends FocalPoint {
  role: ArtworkRole;
  target?: SavedArtworkTarget;
  candidate?: ArtworkCandidate;
  file?: File;
  url: string;
  /** The role revision the change started from; a stale one is refused rather than overwriting. */
  revision: string;
}

/** A role change the curator has made in the canvas but not saved. */
export type StagedChange =
  | {
      kind: 'image';
      draft: ArtworkDraft;
    }
  | {
      kind: 'automatic';
      revision: string;
    }
  | {
      kind: 'empty';
      revision: string;
    };

export type StagedChanges = Partial<Record<ArtworkRole, StagedChange>>;

export const stagedRoles = (staged: StagedChanges) =>
  artworkRoles.filter((role) => staged[role] !== undefined);

/** How a staged change reads in the save bar and the role strip. */
export function stagedLabel(change: StagedChange): string {
  if (change.kind === 'automatic') return 'Returns to automatic';
  if (change.kind === 'empty') return 'Left empty';
  if (change.draft.candidate) return 'New image';
  if (change.draft.file) return 'Upload';
  return 'Saved image';
}

/**
 * The artwork players would see once the staged changes are saved. A staged image replaces the role with its crop; a
 * role left empty shows its fallback; a role returning to automatic keeps its current image, which is all the canvas
 * can know until the server resolves it. `live` previews a focal point while it is still being dragged.
 */
export function proposedArtwork(
  artwork: readonly ResolvedArtwork[],
  staged: StagedChanges,
  live?: {
    role: ArtworkRole;
    point: FocalPoint;
  },
): ResolvedArtwork[] {
  const next = artwork.filter((item) => {
    const change = staged[item.role as ArtworkRole];
    return !change || change.kind === 'automatic';
  });
  for (const role of artworkRoles) {
    const change = staged[role];
    if (change?.kind === 'image') {
      const point = live?.role === role ? live.point : change.draft;
      next.push({
        role,
        assetId: null,
        contentVersion: null,
        width: null,
        height: null,
        originalWidth: null,
        originalHeight: null,
        url: change.draft.url,
        focalX: point.focalX,
        focalY: point.focalY,
        fit: role === 'Logo' ? 'Contain' : 'Cover',
        fallbackReason: 'None',
        variants: [],
      });
    } else if (change?.kind === 'empty') {
      next.push({
        role,
        assetId: null,
        contentVersion: null,
        width: null,
        height: null,
        originalWidth: null,
        originalHeight: null,
        url: null,
        focalX: 50,
        focalY: 50,
        fit: role === 'Logo' ? 'Contain' : 'Cover',
        fallbackReason: 'None',
        variants: [],
      });
    } else if (live?.role === role) {
      const index = next.findIndex((item) => item.role === role);
      if (index >= 0)
        next[index] = {
          ...next[index],
          focalX: live.point.focalX,
          focalY: live.point.focalY,
        };
    }
  }
  return next;
}

export type RolePresentationState =
  | 'loading'
  | 'unavailable'
  | 'importing'
  | 'failed'
  | 'pinned'
  | 'automatic'
  | 'fallback'
  | 'empty'
  | 'missing';

/** Which of the curator-facing states a role is in: who chose its image, or why it has none. */
export function rolePresentationState(
  selection: ArtworkSelectionState | undefined,
  artwork: ResolvedArtwork | undefined,
  { failedToLoad = false, importing = false, failed = false } = {},
): RolePresentationState {
  if (!selection) return failedToLoad ? 'unavailable' : 'loading';
  if (failed) return 'failed';
  if (selection.pendingJobId || importing) return 'importing';
  if (selection.mode === 'Empty') return 'empty';
  if (artwork?.fallbackReason === 'LegacyCover') return 'fallback';
  if (!artwork?.url) return 'missing';
  return selection.mode === 'Pinned' ? 'pinned' : 'automatic';
}

const stateCopy: Readonly<
  Record<
    RolePresentationState,
    {
      label: string;
      color: string;
    }
  >
> = {
  loading: {
    label: 'Loading',
    color: 'gray',
  },
  unavailable: {
    label: 'Unavailable',
    color: 'red',
  },
  importing: {
    label: 'Importing',
    color: 'blue',
  },
  failed: {
    label: 'Needs attention',
    color: 'red',
  },
  pinned: {
    label: 'Pinned',
    color: 'grape',
  },
  automatic: {
    label: 'Automatic',
    color: 'teal',
  },
  fallback: {
    label: 'Fallback',
    color: 'yellow',
  },
  empty: {
    label: 'Intentionally empty',
    color: 'gray',
  },
  missing: {
    label: 'Missing',
    color: 'orange',
  },
};

export const roleStateLabel = (state: RolePresentationState) => stateCopy[state].label;
export const roleStateColor = (state: RolePresentationState) => stateCopy[state].color;
