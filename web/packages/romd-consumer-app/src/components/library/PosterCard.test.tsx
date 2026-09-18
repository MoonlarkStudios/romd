import type { ConsumerTitleCardDto } from '@romd/consumer-api-client';
import { screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { PosterCard } from './PosterCard';

vi.mock('../../hooks/useBrowserPlaybackCapability', () => ({
  useBrowserPlaybackAvailability: vi.fn(() => ({
    available: true,
    reason: null,
    loading: false,
  })),
}));

describe('PosterCard', () => {
  it('renders server-resolved contained artwork instead of the compatibility cover URL', () => {
    const title = createTitle();
    title.coverUrl = '/media/old-cover';
    title.artwork = [{
      role: 'Poster', assetId: null, contentVersion: 'media-selected', url: '/media/selected',
      width: null, height: null, originalWidth: null, originalHeight: null, fit: 'Contain',
      fallbackReason: 'LegacyCover', variants: [],
    }];
    const { container } = render(<PosterCard title={title} />, { withAuth: false });
    expect(container.querySelector('img')).toHaveAttribute('src', `${window.location.origin}/media/selected`);
    expect(container.querySelector('img')).toHaveStyle({ objectFit: 'contain' });
    expect(container.innerHTML).not.toContain('/media/old-cover');
  });

  it('renders a detail link and a play link as siblings, not nested anchors', () => {
    render(<PosterCard title={createTitle()} />, {
      withAuth: false,
    });

    const detailLinks = screen.getAllByRole('link', { name: 'Chrono Trigger' });
    expect(detailLinks.length).toBeGreaterThanOrEqual(1);
    expect(detailLinks.every((link) => link.getAttribute('href') === '/titles/title-1')).toBe(true);

    const playLink = screen.getByRole('link', { name: 'Play Chrono Trigger' });
    expect(playLink).toHaveAttribute('href', '/titles/title-1/releases/release-1/play');

    // The play link must not be a descendant of any detail link (nested <a> is invalid).
    for (const detailLink of detailLinks) {
      expect(within(detailLink).queryByRole('link', { name: 'Play Chrono Trigger' })).toBeNull();
    }
  });

  it('omits the play link when no default Release is available', () => {
    render(
      <PosterCard
        title={{
          ...createTitle(),
          defaultReleaseId: null,
        }}
      />,
      {
        withAuth: false,
      },
    );

    expect(screen.queryByRole('link', { name: 'Play Chrono Trigger' })).toBeNull();
  });
});

function createTitle(): ConsumerTitleCardDto {
  return {
    id: 'title-1',
    system: { key: 'snes', name: 'Super Nintendo Entertainment System', compactLabel: 'SNES' },
    name: 'Chrono Trigger',
    coverUrl: null,
    genre: 'RPG',
    releaseDate: '1995-03-11',
    rating: '9.6',
    releaseCount: '1',
    defaultReleaseId: 'release-1',
  };
}
