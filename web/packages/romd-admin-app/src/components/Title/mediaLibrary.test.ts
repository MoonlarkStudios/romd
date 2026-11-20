import type { TitleMediaRef } from '@romd/admin-api-client';
import { describe, expect, it } from 'vitest';
import { compatibleMediaTypes, isPackagingType } from '../mediaTypes';
import type { ArtworkLibraryEntry } from './artworkLibrary';
import { spreadPages, stepPage } from './manualDocument';
import { matchesLibraryFilter, placementLabel, releaseOptions } from './mediaLibrary';
import { rolePresentationState } from './PresentationRoleCard';

const media = (name: string, catalogReleaseId: string | null = null): TitleMediaRef => ({
  id: name,
  type: name,
  url: '',
  sourceId: 'user',
  isPrimary: false,
  contentType: 'image/png',
  catalogReleaseId,
});
const entry = (name: string, catalogReleaseId: string | null = null): ArtworkLibraryEntry => ({
  key: `media-${name}`,
  name,
  source: 'user',
  url: '',
  target: {
    mediaId: name,
  },
  roles: [],
  media: media(name, catalogReleaseId),
});

describe('manual pages', () => {
  it('opens like a booklet: the cover alone, then facing pairs', () => {
    expect(spreadPages(1, 8)).toEqual([
      1,
    ]);
    expect(spreadPages(3, 8)).toEqual([
      2,
      3,
    ]);
    expect(spreadPages(8, 8)).toEqual([
      8,
    ]);
    expect(spreadPages(7, 7)).toEqual([
      6,
      7,
    ]);
  });

  it('steps by view and stays within the document', () => {
    expect(stepPage(1, 8, true, 1)).toBe(2);
    expect(stepPage(2, 8, true, 1)).toBe(4);
    expect(stepPage(3, 8, true, -1)).toBe(1);
    expect(stepPage(8, 8, true, 1)).toBe(8);
    expect(stepPage(1, 8, false, -1)).toBe(1);
    expect(stepPage(4, 8, false, 1)).toBe(5);
  });
});

describe('library filters', () => {
  it('filters by group, type and release, with title-level media as its own choice', () => {
    const front = entry('BoxFront', 'cr-usa');
    const screenshot = entry('Screenshot');
    expect(matchesLibraryFilter(front, 'packaging', 'all')).toBe(true);
    expect(matchesLibraryFilter(front, 'BoxBack', 'all')).toBe(false);
    expect(matchesLibraryFilter(front, 'all', 'cr-usa')).toBe(true);
    expect(matchesLibraryFilter(front, 'all', 'title')).toBe(false);
    expect(matchesLibraryFilter(screenshot, 'gameplay', 'title')).toBe(true);
  });

  it('offers each canonical release once and labels placement in reading order', () => {
    const releases = releaseOptions([
      {
        id: 'a',
        datId: 'd',
        name: 'Game (USA)',
        catalogReleaseId: 'cr-usa',
        region: 'USA',
        sources: [],
      },
      {
        id: 'b',
        datId: 'd2',
        name: 'Game (USA) [alt dat]',
        catalogReleaseId: 'cr-usa',
        sources: [],
      },
      {
        id: 'c',
        datId: 'd',
        name: 'Unmatched',
        catalogReleaseId: null,
        sources: [],
      },
    ]);
    expect(releases.map((release) => release.value)).toEqual([
      'cr-usa',
    ]);
    expect(
      placementLabel(
        {
          ...media('BoxFront', 'cr-usa'),
          region: 'USA',
          language: 'English',
        },
        releases,
      ),
    ).toBe('Game (USA) · USA · English');
    expect(placementLabel(media('Cover'), releases)).toBe('Title-level');
  });

  it('keeps images and manuals in their own kinds', () => {
    expect(compatibleMediaTypes('Manual')).toEqual([
      'Manual',
    ]);
    expect(compatibleMediaTypes('Cover')).toContain('DiscLabel');
    expect(compatibleMediaTypes('Cover')).not.toContain('Manual');
    expect(isPackagingType('BoxFull')).toBe(true);
    expect(isPackagingType('Cover')).toBe(false);
  });
});

describe('role states', () => {
  const selection = (mode: string, pendingJobId: string | null = null) => ({
    role: 'Poster',
    mode,
    revision: '1',
    pinnedAssetId: null,
    pendingJobId,
  });
  const image = (fallbackReason = 'None', url: string | null = '/artwork/poster') => ({
    role: 'Poster',
    url,
    fit: 'Cover',
    fallbackReason,
    variants: [],
  });

  it('names who chose the image or why there is none', () => {
    expect(rolePresentationState(selection('Pinned'), image())).toBe('pinned');
    expect(rolePresentationState(selection('Automatic'), image())).toBe('automatic');
    expect(rolePresentationState(selection('Automatic'), image('LegacyCover'))).toBe('fallback');
    expect(rolePresentationState(selection('Empty'), image('None', null))).toBe('empty');
    expect(rolePresentationState(selection('Automatic'), image('NoArtwork', null))).toBe('missing');
    expect(rolePresentationState(selection('Pinned', 'job'), image())).toBe('importing');
    expect(
      rolePresentationState(undefined, undefined, {
        failedToLoad: true,
      }),
    ).toBe('unavailable');
  });
});
