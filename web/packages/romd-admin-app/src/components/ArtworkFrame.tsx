import { AspectRatio, Image, Text } from '@mantine/core';
import { romdColors } from '@romd/foundation';
import { type ArtworkBackground, ArtworkSurface } from './ArtworkSurface';
import { type ArtworkRole, artworkPresentation, artworkRoleLabel } from './artworkPresentation';

export interface ArtworkFrameProps {
  role: ArtworkRole;
  background?: ArtworkBackground;
  src?: string | null;
  title: string;
  fit?: 'Contain' | 'Cover';
  loading?: 'eager' | 'lazy';
  focalX?: number;
  focalY?: number;
}

/** Shared title-page and picker presentation: never stretches the source image. */
export function ArtworkFrame({ role, src, title, fit = 'Cover', loading = 'eager', focalX = 50, focalY = 50, background }: ArtworkFrameProps) {
  const { ratio } = artworkPresentation[role];
  return <AspectRatio ratio={ratio}>
    <ArtworkSurface background={background ?? (role === 'Logo' && src ? 'checkerboard' : 'dark')}>
      {src ? <Image loading={loading} src={src} alt={`${title} ${artworkRoleLabel(role).toLowerCase()}`} w="100%" h="100%" fit={role === 'Logo' || fit === 'Contain' ? 'contain' : 'cover'} style={{ objectPosition: `${focalX}% ${focalY}%` }} />
        : <Text c={romdColors.textMuted} ta="center">No {artworkRoleLabel(role).toLowerCase()}</Text>}
    </ArtworkSurface>
  </AspectRatio>;
}
