import type { TitleMediaRef } from '@romd/admin-api-client';
import type { TitleReleaseView } from '../../hooks/api/useTitleDetail';
import {
  isMediaType,
  type MediaGroup,
  mediaGroupOf,
  mediaGroups,
  mediaTypeInfo,
  mediaTypeLabel,
} from '../mediaTypes';
import type { ArtworkLibraryEntry } from './artworkLibrary';

export interface ReleaseOption {
  value: string;
  label: string;
  region?: string | null;
  language?: string | null;
}

/** The title's canonical releases; media can be placed only on these. Releases without one are not offered. */
export function releaseOptions(releases: TitleReleaseView[]): ReleaseOption[] {
  const seen = new Map<string, ReleaseOption>();
  for (const release of releases) {
    if (!release.catalogReleaseId || seen.has(release.catalogReleaseId)) continue;
    seen.set(release.catalogReleaseId, {
      value: release.catalogReleaseId,
      label: release.name,
      region: release.region,
      language: release.language,
    });
  }
  return [
    ...seen.values(),
  ];
}

/** The release, region and language an item records, in reading order; title-level media says so. */
export function placementLabel(media: TitleMediaRef | undefined, releases: ReleaseOption[]) {
  if (!media) return '';
  const release = releases.find((item) => item.value === media.catalogReleaseId);
  return (
    [
      media.catalogReleaseId ? (release?.label ?? 'Unknown release') : 'Title-level',
      media.region,
      media.language,
    ]
      .filter(Boolean)
      .join(' · ') || 'Title-level'
  );
}

/** `all`, a media group, or one media type. */
export type LibraryTypeFilter = 'all' | MediaGroup | string;
/** `all`, `title` for title-level media, or a canonical release ID. */
export type LibraryReleaseFilter = 'all' | 'title' | string;

/** Select data: every group present, as a whole and by each of its types when it holds more than one. */
export function libraryTypeOptions(entries: ArtworkLibraryEntry[]) {
  const present = new Set(entries.map((entry) => entry.name));
  return [
    {
      value: 'all',
      label: 'All types',
    },
    ...mediaGroups.flatMap((group) => {
      const types = Object.keys(mediaTypeInfo).filter(
        (type) => present.has(type) && mediaGroupOf(type) === group.value,
      );
      if (!types.length) return [];
      return [
        {
          group: group.label,
          items: [
            {
              value: group.value,
              label: `All ${group.label.toLowerCase()}`,
            },
            ...(types.length > 1
              ? types.map((type) => ({
                  value: type,
                  label: mediaTypeLabel(type),
                }))
              : []),
          ],
        },
      ];
    }),
  ];
}

export function matchesLibraryFilter(
  entry: ArtworkLibraryEntry,
  type: LibraryTypeFilter,
  release: LibraryReleaseFilter,
) {
  const typeMatches =
    type === 'all' || (isMediaType(type) ? entry.name === type : mediaGroupOf(entry.name) === type);
  const releaseId = entry.media?.catalogReleaseId ?? null;
  const releaseMatches =
    release === 'all' || (release === 'title' ? releaseId === null : releaseId === release);
  return typeMatches && releaseMatches;
}
