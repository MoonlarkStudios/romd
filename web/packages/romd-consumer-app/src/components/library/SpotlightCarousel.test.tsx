import type { ConsumerTitleCardDto, ResolvedArtworkDto } from '@romd/consumer-api-client';
import { screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { SpotlightCarousel } from './SpotlightCarousel';

const artwork = (role: string): ResolvedArtworkDto => ({
  role,
  assetId: `${role}-asset`,
  contentVersion: null,
  url: `/artwork/chrono/${role.toLowerCase()}/960.webp`,
  width: 960,
  height: 310,
  originalWidth: null,
  originalHeight: null,
  fit: 'Cover',
  fallbackReason: 'None',
  variants: [],
});
const system = {
  key: 'snes',
  name: 'Super Nintendo',
  compactLabel: 'SNES',
};

describe('SpotlightCarousel', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'IntersectionObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );
  });
  afterEach(() => vi.unstubAllGlobals());

  it('renders hero artwork behind a logo title on the shared spotlight surface', () => {
    const title: ConsumerTitleCardDto = {
      id: 'chrono',
      name: 'Chrono Trigger',
      system,
      releaseCount: 1,
      artwork: [
        artwork('Hero'),
        artwork('Logo'),
      ],
    };
    render(
      <SpotlightCarousel
        titles={[
          title,
        ]}
      />,
    );
    const heading = screen.getByRole('heading', {
      level: 1,
      name: 'Chrono Trigger',
    });
    const spotlight = heading.closest('.romd-home-spotlight');
    expect(spotlight).toHaveAttribute('data-has-art', 'true');
    expect(spotlight?.parentElement).toHaveClass('romd-spotlight-surface');
    expect(heading.querySelector('.romd-home-spotlight-logo')).toHaveAttribute(
      'aria-hidden',
      'true',
    );
    expect(spotlight?.querySelector('.romd-home-spotlight-art')).not.toBeNull();
    expect(spotlight?.querySelector('.romd-home-spotlight-poster')).toBeNull();
    expect(
      within(spotlight as HTMLElement).getByRole('link', {
        name: 'View game',
      }),
    ).toHaveAttribute('href', '/titles/chrono');
  });

  it('shows the text title beside the poster when a title has no hero or logo', () => {
    const title: ConsumerTitleCardDto = {
      id: 'earthbound',
      name: 'EarthBound',
      system,
      releaseCount: 1,
    };
    render(
      <SpotlightCarousel
        titles={[
          title,
        ]}
      />,
    );
    const heading = screen.getByRole('heading', {
      level: 1,
      name: 'EarthBound',
    });
    expect(heading).toHaveTextContent('EarthBound');
    const spotlight = heading.closest('.romd-home-spotlight');
    expect(spotlight).not.toHaveAttribute('data-has-art');
    expect(spotlight?.querySelector('.romd-home-spotlight-poster')).not.toBeNull();
    expect(spotlight?.querySelector('.romd-home-spotlight-art')).toBeNull();
    expect(
      screen.getByRole('link', {
        name: 'View game',
      }),
    ).toHaveAttribute('href', '/titles/earthbound');
  });
});
