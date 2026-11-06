import { describe, expect, it } from 'vitest';
import { artworkAspectRatios, artworkCrop, artworkObjectStyle } from './artworkCrop';

describe('artworkCrop', () => {
  it('maps the resolved fit and defaults the focal point to the center', () => {
    expect(
      artworkCrop({
        fit: 'Cover',
        focalX: 0,
        focalY: 100,
      }),
    ).toEqual({
      fit: 'cover',
      focalX: 0,
      focalY: 100,
    });
    expect(
      artworkCrop({
        fit: 'Contain',
      }),
    ).toEqual({
      fit: 'contain',
      focalX: 50,
      focalY: 50,
    });
  });
});

describe('artworkObjectStyle', () => {
  it('places images by fit and focal point, and keeps logos whole on the logo alignment', () => {
    expect(
      artworkObjectStyle('Backdrop', {
        fit: 'cover',
        focalX: 20,
        focalY: 80,
      }),
    ).toEqual({
      objectFit: 'cover',
      objectPosition: '20% 80%',
    });
    expect(artworkObjectStyle('Poster', null)).toEqual({
      objectFit: 'contain',
      objectPosition: '50% 50%',
    });
    expect(
      artworkObjectStyle('Logo', {
        fit: 'cover',
        focalX: 0,
        focalY: 0,
      }),
    ).toEqual({
      objectFit: 'contain',
      objectPosition: 'var(--romd-logo-position, left center)',
    });
  });

  it('reserves the consumer frame proportions for each role', () => {
    expect(artworkAspectRatios).toEqual({
      Poster: '2 / 3',
      Hero: '96 / 31',
      Logo: '3 / 1',
      Backdrop: '16 / 9',
    });
  });
});
