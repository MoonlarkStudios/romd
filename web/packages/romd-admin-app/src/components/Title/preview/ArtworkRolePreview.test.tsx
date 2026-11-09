import { MantineProvider } from '@mantine/core';
import type { ResolvedArtwork } from '@romd/admin-api-client';
import { render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import {
  type ArtworkPreviewTitle,
  ArtworkRolePreview,
  type ArtworkRolePreviewProps,
} from './ArtworkRolePreview';

vi.mock('../../../hooks/api/usePlatforms', () => ({
  usePlatform: () => ({
    data: {
      name: 'Super Nintendo',
    },
  }),
}));

const artwork = (role: string, overrides: Partial<ResolvedArtwork> = {}): ResolvedArtwork => ({
  role,
  assetId: null,
  contentVersion: null,
  url: `/draft/${role.toLowerCase()}.png`,
  width: null,
  height: null,
  originalWidth: null,
  originalHeight: null,
  fit: role === 'Logo' ? 'Contain' : 'Cover',
  fallbackReason: 'None',
  variants: [],
  ...overrides,
});
const title = (items: ResolvedArtwork[]): ArtworkPreviewTitle => ({
  name: 'Chrono Trigger',
  systemKey: 'snes',
  releaseDate: '1995-03-11',
  genre: 'RPG',
  artwork: items,
});
const show = (props: ArtworkRolePreviewProps) =>
  render(
    <MantineProvider env="test">
      <ArtworkRolePreview {...props} />
    </MantineProvider>,
  );
const imageOf = (container: Element | null, source: string) =>
  container?.querySelector(`img[src="${source}"]`) as HTMLImageElement;

describe('ArtworkRolePreview', () => {
  it('previews a banner on the consumer spotlight at the device width with its focal point, logo, and an inert call to action', () => {
    show({
      artworkRole: 'Hero',
      device: 'desktop',
      title: title([
        artwork('Hero', {
          focalX: 20,
          focalY: 80,
        }),
        artwork('Logo'),
        artwork('Poster'),
      ]),
    });
    const frame = screen.getByRole('figure', {
      name: 'Desktop preview, 1440 pixels wide',
    });
    expect(frame.firstElementChild).toHaveAttribute('inert');
    const heading = within(frame).getByRole('heading', {
      hidden: true,
      level: 1,
      name: 'Chrono Trigger',
    });
    expect(heading.querySelector('.romd-home-spotlight-logo img')).toHaveAttribute(
      'src',
      '/draft/logo.png',
    );
    const hero = imageOf(frame.querySelector('.romd-home-spotlight-art'), '/draft/hero.png');
    expect(hero.style.objectPosition).toBe('20% 80%');
    expect(hero.style.objectFit).toBe('cover');
    expect(frame.querySelector('.romd-home-spotlight-poster')).toBeNull();
    expect(
      within(frame).getByText('View game').closest('.romd-spotlight-details'),
    ).toBeInTheDocument();
    expect(
      within(frame).queryByRole('link', {
        hidden: true,
      }),
    ).toBeNull();
    expect(
      within(frame).queryByRole('button', {
        hidden: true,
      }),
    ).toBeNull();
    const platform = within(frame).getByTitle('Super Nintendo');
    expect(platform).toHaveTextContent('Super Nintendo');
    expect(platform.querySelector('[aria-hidden="true"]')).toHaveTextContent('Super Nintendo');
    expect(platform.querySelector('span:not([aria-hidden])')).toHaveTextContent('Super Nintendo');
  });

  it('centers the name in the logo box like the consumer when a logo record has no image', () => {
    show({
      artworkRole: 'Hero',
      device: 'desktop',
      title: title([
        artwork('Logo', {
          url: null,
        }),
      ]),
    });
    const fallback = screen
      .getByRole('heading', {
        hidden: true,
        level: 1,
        name: 'Chrono Trigger',
      })
      .querySelector('.romd-home-spotlight-logo span')?.parentElement;
    expect(fallback).toHaveTextContent('Chrono Trigger');
    expect(fallback).toHaveStyle({
      position: 'relative',
      minHeight: 'inherit',
      display: 'grid',
    });
    expect(fallback?.style.placeItems).toBe('var(--romd-logo-fallback-alignment, center start)');
  });

  it('shows the text title beside the poster when the spotlight has no banner', () => {
    show({
      artworkRole: 'Hero',
      device: 'mobile',
      title: title([
        artwork('Poster'),
      ]),
    });
    const frame = screen.getByRole('figure', {
      name: 'Mobile preview, 390 pixels wide',
    });
    expect(
      within(frame).getByRole('heading', {
        hidden: true,
        level: 1,
        name: 'Chrono Trigger',
      }),
    ).toHaveTextContent('Chrono Trigger');
    expect(
      imageOf(frame.querySelector('.romd-home-spotlight-poster'), '/draft/poster.png'),
    ).toBeInTheDocument();
  });

  it('previews a backdrop on consumer game details sized to the device viewport, honoring fit and focal point', () => {
    show({
      artworkRole: 'Backdrop',
      device: 'mobile',
      title: title([
        artwork('Backdrop', {
          fit: 'Contain',
          focalX: 10,
          focalY: 30,
        }),
        artwork('Logo'),
      ]),
    });
    const frame = screen.getByRole('figure', {
      name: 'Mobile preview, 390 pixels wide',
    });
    expect(
      (frame.firstElementChild as HTMLElement).style.getPropertyValue('--romd-viewport-height'),
    ).toBe('844px');
    const hero = within(frame).getByRole('region', {
      hidden: true,
      name: 'Chrono Trigger overview',
    });
    const backdrop = imageOf(hero.querySelector('.romd-detail-backdrop'), '/draft/backdrop.png');
    expect(backdrop.style.objectFit).toBe('contain');
    expect(backdrop.style.objectPosition).toBe('10% 30%');
    expect(hero.querySelector('img.romd-detail-logo')).toHaveAttribute('src', '/draft/logo.png');
  });

  it('previews a poster on the library card at the consumer card width without a device frame', () => {
    show({
      artworkRole: 'Poster',
      device: 'desktop',
      title: title([
        artwork('Poster', {
          focalX: 40,
          focalY: 60,
        }),
      ]),
    });
    expect(screen.queryByRole('figure')).toBeNull();
    const surface = screen
      .getByTestId('artwork-composition-preview')
      .querySelector('.romd-game-poster-surface');
    const poster = imageOf(surface, '/draft/poster.png');
    expect(poster.style.objectPosition).toBe('40% 60%');
    expect(
      (surface?.closest('.romd-consumer-presentation')?.parentElement as HTMLElement).style.width,
    ).toBe('259.2px');
  });

  it('uses the consumer card width for each device', () => {
    const { rerender } = show({
      artworkRole: 'Poster',
      device: 'tablet',
      title: title([]),
    });
    const card = () =>
      screen.getByTestId('artwork-composition-preview').firstElementChild as HTMLElement;
    expect(card().style.width).toBe('190px');
    expect(within(card()).getAllByText('Chrono Trigger')).toHaveLength(2);
    const mobile: ArtworkRolePreviewProps = {
      artworkRole: 'Poster',
      device: 'mobile',
      title: title([]),
    };
    rerender(
      <MantineProvider env="test">
        <ArtworkRolePreview {...mobile} />
      </MantineProvider>,
    );
    expect(card().style.width).toBe('150px');
  });

  it('previews a poster on game details when asked', () => {
    show({
      artworkRole: 'Poster',
      posterContext: 'details',
      device: 'tablet',
      title: title([
        artwork('Poster'),
      ]),
    });
    const frame = screen.getByRole('figure', {
      name: 'Tablet preview, 834 pixels wide',
    });
    expect(
      imageOf(
        within(frame)
          .getByRole('region', {
            hidden: true,
            name: 'Chrono Trigger overview',
          })
          .querySelector('.romd-detail-poster'),
        '/draft/poster.png',
      ),
    ).toBeInTheDocument();
  });

  it('previews a logo on game details and offers light and dark inspection', () => {
    show({
      artworkRole: 'Logo',
      device: 'desktop',
      title: title([
        artwork('Logo'),
      ]),
    });
    expect(
      screen
        .getByRole('figure', {
          name: 'Desktop preview, 1440 pixels wide',
        })
        .querySelector('img.romd-detail-logo'),
    ).toHaveAttribute('src', '/draft/logo.png');
    expect(screen.getByText('Inspect logo on light and dark backgrounds')).toBeInTheDocument();
    expect(
      screen.getAllByRole('img', {
        name: 'Chrono Trigger logo',
      }),
    ).toHaveLength(2);
  });
});
