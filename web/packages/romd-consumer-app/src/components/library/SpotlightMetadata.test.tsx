import type { ConsumerTitleCardDto } from '@romd/consumer-api-client';
import { render as baseRender, fireEvent, screen } from '@testing-library/react';
import type { ReactElement } from 'react';
import { describe, expect, it } from 'vitest';
import { referenceCatalog } from '../../../../../test/referenceCatalog';
import { SpotlightMetadata } from './SpotlightMetadata';

const title: ConsumerTitleCardDto = { id: 'game', system: { key: 'snes', name: 'Super Nintendo Entertainment System', compactLabel: 'SNES' }, name: 'Game', releaseCount: 1 };

const render = (ui: ReactElement) => baseRender(ui);
const rating = (code: string) => ({ ...referenceCatalog.ratings.find(item => item.board === 'Esrb' && item.code === code)!, boardName: 'ESRB' });

describe('SpotlightMetadata', () => {
  it('shows the recorded rating, release year, players and an explicit review scale', () => {
    render(<SpotlightMetadata title={{ ...title, contentRatings: [rating('E')], releaseDate: '1994-03-19', genre: 'Shooter', players: 2, rating: 96.2 }} />);
    expect(screen.getByRole('img', { name: 'ESRB Everyone' })).toBeVisible();
    expect(screen.getByText('1994')).toBeVisible();
    expect(screen.getByText('2 players')).toBeVisible();
    expect(screen.getByText('96')).toBeVisible();
    expect(screen.getByText('/100')).toBeVisible();
    expect(screen.getByText('SNES')).toBeVisible();
  });
  it('omits unknown metadata and never invents a classification or score', () => {
    render(<SpotlightMetadata title={title} />);
    expect(screen.queryByRole('img')).toBeNull();
    expect(screen.queryByText('/100')).toBeNull();
    expect(screen.queryByText(/players?/)).toBeNull();
  });
  it('keeps a readable rating when its image fails and uses singular player wording', () => {
    render(<SpotlightMetadata title={{ ...title, contentRatings: [rating('T')], players: 1 }} />);
    fireEvent.error(screen.getByRole('img'));
    expect(screen.getByText('ESRB T')).toBeVisible();
    expect(screen.getByText('1 player')).toBeVisible();
  });
});
