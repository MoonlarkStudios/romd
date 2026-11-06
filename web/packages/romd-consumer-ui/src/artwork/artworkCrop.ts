import type { CSSProperties } from 'react';

/** Frame proportions consumer surfaces reserve for each artwork role. */
export const artworkAspectRatios = {
  Poster: '2 / 3',
  Hero: '96 / 31',
  Logo: '3 / 1',
  Backdrop: '16 / 9',
} as const;

export type ArtworkRole = keyof typeof artworkAspectRatios;

/** The presentation fields of resolved artwork; hosts pass their API data structurally. */
export interface ArtworkCropSource {
  fit: string;
  focalX?: number;
  focalY?: number;
}

export interface ArtworkCrop {
  fit: 'cover' | 'contain';
  focalX: number;
  focalY: number;
}

export function artworkCrop({ fit, focalX, focalY }: ArtworkCropSource): ArtworkCrop {
  return {
    fit: fit === 'Cover' ? 'cover' : 'contain',
    focalX: focalX ?? 50,
    focalY: focalY ?? 50,
  };
}

/** How consumer surfaces place an image: logos stay whole and align by `--romd-logo-position`; others honor fit and focal point. */
export function artworkObjectStyle(
  role: ArtworkRole,
  crop: ArtworkCrop | null | undefined,
): Pick<CSSProperties, 'objectFit' | 'objectPosition'> {
  return role === 'Logo'
    ? {
        objectFit: 'contain',
        objectPosition: 'var(--romd-logo-position, left center)',
      }
    : {
        objectFit: crop?.fit ?? 'contain',
        objectPosition: `${crop?.focalX ?? 50}% ${crop?.focalY ?? 50}%`,
      };
}
