import type { ResolvedArtwork, SavedArtwork, TitleMediaRef } from '@romd/admin-api-client';
import {
  type ArtworkRole,
  artworkPresentation,
  artworkRoles,
  isArtworkRole,
} from '../artworkPresentation';

export type SavedArtworkTarget =
  | {
      assetId: string;
      mediaId?: never;
    }
  | {
      mediaId: string;
      assetId?: never;
    };

export interface ArtworkLibraryEntry {
  key: string;
  name: string;
  source: string;
  url: string;
  target: SavedArtworkTarget;
  media?: TitleMediaRef;
  asset?: SavedArtwork;
  retained?: SavedArtwork[];
  roles: ArtworkRole[];
}

export function artworkLibraryEntries(
  media: TitleMediaRef[],
  saved: SavedArtwork[],
  artwork: ResolvedArtwork[],
): ArtworkLibraryEntry[] {
  const mediaIds = new Set(media.map((item) => item.id));
  const retainedByMedia = new Map<string, SavedArtwork[]>();
  for (const asset of saved) {
    for (const id of asset.mediaIds) {
      const retained = retainedByMedia.get(id) ?? [];
      retained.push(asset);
      retainedByMedia.set(id, retained);
    }
  }

  return [
    ...media.map((item): ArtworkLibraryEntry => {
      const retained = retainedByMedia.get(item.id) ?? [];
      return {
        key: `media-${item.id}`,
        name: item.type,
        source: item.sourceId.replace(/^gallery:/, ''),
        url: item.url,
        target: {
          mediaId: item.id,
        },
        media: item,
        retained,
        roles: artworkRoles.filter((role) =>
          artwork.some(
            (image) =>
              image.role === role &&
              image.fallbackReason !== 'LegacyCover' &&
              (image.url === item.url || retained.some((asset) => asset.id === image.assetId)),
          ),
        ),
      };
    }),
    ...saved
      .filter((asset) => !asset.mediaIds.some((id) => mediaIds.has(id)))
      .map(
        (asset): ArtworkLibraryEntry => ({
          key: `asset-${asset.id}`,
          name: isArtworkRole(asset.role) ? artworkPresentation[asset.role].mediaType : asset.role,
          source: asset.sourceId.replace(/^gallery:/, ''),
          url: asset.artwork.url ?? '',
          target: {
            assetId: asset.id,
          },
          asset,
          roles: artworkRoles.filter((role) =>
            artwork.some((image) => image.role === role && image.assetId === asset.id),
          ),
        }),
      ),
  ];
}

export function artworkTargetForRole(
  entry: ArtworkLibraryEntry,
  role: ArtworkRole,
): SavedArtworkTarget {
  const retained = entry.retained?.find((asset) => asset.role === role);
  return retained
    ? {
        assetId: retained.id,
      }
    : entry.target;
}
