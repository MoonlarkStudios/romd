import type { ConsumerTitleCardDto } from '@romd/consumer-api-client';
import { screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useBrowserPlaybackAvailability } from '../../hooks/useBrowserPlaybackCapability';
import { render } from '../../test/utils/render';
import { TitleListRow } from './TitleListRow';

vi.mock('../../hooks/useBrowserPlaybackCapability', () => ({
  useBrowserPlaybackAvailability: vi.fn(),
}));

const title: ConsumerTitleCardDto = {
  id: 'title-1',
  system: { key: 'snes', name: 'Super Nintendo Entertainment System', compactLabel: 'SNES' },
  name: 'Super Metroid',
  coverUrl: null,
  genre: 'Adventure',
  releaseDate: '1994-03-19',
  rating: '9.5',
  releaseCount: '1',
  defaultReleaseId: 'release-1',
};

describe('TitleListRow', () => {
  beforeEach(() => {
    vi.mocked(useBrowserPlaybackAvailability).mockReturnValue({
      available: true,
      reason: null,
      loading: false,
    });
  });

  it('shows Play only when the player capability supports the platform', () => {
    const { rerender } = render(<TitleListRow title={title} />, { withAuth: false });
    expect(screen.getByRole('link', { name: 'Play' })).toHaveAttribute(
      'href',
      '/titles/title-1/releases/release-1/play',
    );

    vi.mocked(useBrowserPlaybackAvailability).mockReturnValue({
      available: false,
      reason: 'Browser playback is unavailable.',
      loading: false,
    });
    rerender(<TitleListRow title={title} />);

    expect(screen.queryByRole('link', { name: 'Play' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Details' })).toHaveAttribute('href', '/titles/title-1');
  });

  it('pluralizes the release badge', () => {
    render(<TitleListRow title={title} />, { withAuth: false });
    expect(screen.getByText('1 Release')).toBeInTheDocument();
  });
});
