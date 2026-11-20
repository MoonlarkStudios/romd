import type { EnrichmentRequirement } from '@romd/admin-api-client';
import { artworkRole, reviewEditor } from './titleWorkbenchModel';

export const titleSections = [
  'metadata',
  'artwork',
  'releases',
  'ratings',
] as const;
export type TitleSection = (typeof titleSections)[number];

export function titleSectionPath(titleId: string, section: TitleSection = 'metadata') {
  return `/titles/${encodeURIComponent(titleId)}/${section}`;
}

export function titleRequirementPath(titleId: string, requirement: EnrichmentRequirement) {
  const editor = reviewEditor(requirement);
  const params = new URLSearchParams({
    requirement: requirement.key,
  });
  const role = artworkRole(requirement);
  if (role) params.set('role', role);
  if (editor === 'match') params.set('match', '1');
  return `${titleSectionPath(titleId, editor === 'match' ? 'metadata' : editor)}?${params}`;
}

export const titleSectionLabels: Record<TitleSection, string> = {
  metadata: 'Metadata',
  artwork: 'Artwork',
  releases: 'Releases',
  ratings: 'Ratings',
};

export function titleRoute(
  titleId: string,
  segment: string | undefined,
  search: string,
  hash = '',
) {
  const params = new URLSearchParams(search);
  const legacyEditor = params.get('editor') ?? params.get('tab');
  const requested = legacyEditor ?? segment;
  const section = titleSections.find((item) => item === requested) ?? 'metadata';
  const isDiscs = requested === 'discs';
  const canonical = isDiscs ? 'releases' : section;
  if (requested === 'match') params.set('match', '1');
  // Legacy enrichment links now lead to the status rail.
  const destination = isDiscs ? '#disc-sets' : requested === 'enrichment' ? '#title-status' : hash;
  params.delete('editor');
  params.delete('tab');
  const query = params.toString();
  return {
    section: canonical,
    replace: segment !== canonical || !!legacyEditor,
    to: `${titleSectionPath(titleId, canonical)}${query ? `?${query}` : ''}${destination}`,
  };
}
