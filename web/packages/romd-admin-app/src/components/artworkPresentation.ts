export const artworkRoles = ['Poster', 'Backdrop', 'Hero', 'Logo'] as const;
export type ArtworkRole = (typeof artworkRoles)[number];

export const artworkPresentation = {
  Poster: { mediaType: 'Cover', ratio: 2 / 3, description: 'The cover shown on library cards.' },
  Hero: { mediaType: 'Background', ratio: 96 / 31, description: 'A wide banner for compact headers. Existing hero selections are preserved.' },
  Backdrop: { mediaType: 'Background', ratio: 16 / 9, description: 'Cinematic background for game details. Prefer clean artwork without a game logo.' },
  Logo: { mediaType: 'Logo', ratio: 2, description: 'The game logo, shown without cropping.' },
} satisfies Record<ArtworkRole, { mediaType: string; ratio: number; description: string }>;

export function isArtworkRole(value: string): value is ArtworkRole {
  return artworkRoles.some(role => role === value);
}

export function artworkSourceLabel(source: string): string {
  const id = source.replace(/^gallery:/, '');
  return ({ user: 'Your uploads', igdb: 'IGDB', steamgriddb: 'SteamGridDB' })[id] ?? id;
}

export function artworkRoleLabel(role: string): string {
  return role === 'Hero' ? 'Banner' : role;
}

export function backdropSuitable(width: number, height: number): boolean {
  return width >= 1200 && height >= 720 && width / height >= 1.3 && width / height <= 2.1;
}
