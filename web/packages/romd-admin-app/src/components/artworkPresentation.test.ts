import { describe, expect, it } from 'vitest';
import { isMediaType } from './artworkPresentation';

describe('isMediaType', () => {
  it('accepts every contract media type by its exact name', () => {
    for (const type of [
      'Cover',
      'Screenshot',
      'Banner',
      'Logo',
      'Background',
      'Video',
      'Box3d',
      'TitleScreen',
    ]) {
      expect(isMediaType(type)).toBe(true);
    }
  });

  it('refuses another casing and an object property name', () => {
    expect(isMediaType('cover')).toBe(false);
    expect(isMediaType('toString')).toBe(false);
  });
});
