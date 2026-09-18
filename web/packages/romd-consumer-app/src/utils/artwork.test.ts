import type { ResolvedArtworkDto } from '@romd/consumer-api-client';
import { describe, expect, it } from 'vitest';
import { artworkDeliveryUrl, selectArtwork } from './artwork';

const poster: ResolvedArtworkDto = {
  role: 'Poster', assetId: 'asset', contentVersion: 'large-v1', url: '/artwork/asset/large/large-v1',
  width: 600, height: 900, originalWidth: 1200, originalHeight: 1800, fit: 'Cover', fallbackReason: 'None',
  variants: [
    { name: 'large', url: '/artwork/asset/large/large-v1', contentVersion: 'large-v1', contentType: 'image/webp', width: 600, height: 900 },
    { name: 'small', url: '/artwork/asset/small/small-v1', contentVersion: 'small-v1', contentType: 'image/webp', width: 200, height: 300 },
  ],
};

describe('selectArtwork', () => {
  it('preserves edge focal points and centers older assignments', () => {
    expect(selectArtwork([{ ...poster, focalX: 0, focalY: 100 }], 'Poster', 200)).toMatchObject({ focalX: 0, focalY: 100 });
    expect(selectArtwork([poster], 'Poster', 200)).toMatchObject({ focalX: 50, focalY: 50 });
  });
  it('chooses the smallest sufficient delivery image and caps device pixel ratio', () => {
    expect(selectArtwork([poster], 'Poster', 100, 4)?.contentVersion).toBe('small-v1');
    expect(selectArtwork([poster], 'Poster', 240, 2)?.contentVersion).toBe('large-v1');
    expect(selectArtwork([poster], 'Poster', 1400, 2)?.contentVersion).toBe('large-v1');
    expect(poster.variants[0].name).toBe('large');
  });
  it('honors contained legacy cover and never uses a poster for a missing hero', () => {
    const fallback = { ...poster, fit: 'Contain', url: '/media/box', contentVersion: 'media-box', variants: [] };
    expect(selectArtwork([fallback], 'Poster', 200)).toMatchObject({ fit: 'contain', url: '/media/box' });
    expect(selectArtwork([fallback], 'Hero', 1200)).toBeNull();
  });
  it('excludes originals even if a malformed contract includes them', () => {
    const withOriginal = { ...poster, variants: [...poster.variants, { ...poster.variants[0], name: 'original', width: 3000, height: 4500, contentVersion: 'original' }] };
    expect(selectArtwork([withOriginal], 'Poster', 3000)?.contentVersion).toBe('large-v1');
  });
});

describe('artworkDeliveryUrl', () => {
  it('resolves only local ROMD delivery paths', () => {
    expect(artworkDeliveryUrl('/artwork/a/card/v1', 'https://romd.test')).toBe('https://romd.test/artwork/a/card/v1');
    expect(artworkDeliveryUrl('/media/box', 'https://romd.test')).toBe('https://romd.test/media/box');
    for (const url of ['https://provider.test/artwork/a', '//other.test/media/a', '/api/secrets', 'javascript:alert(1)', '/media/a?token=x']) {
      expect(artworkDeliveryUrl(url, 'https://romd.test')).toBeNull();
    }
  });
});
