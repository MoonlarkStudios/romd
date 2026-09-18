import type { TitleContentRating } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { render } from '../../test/utils/render';
import { ContentRatingRow } from './ContentRatingRow';

function rating(overrides: Partial<TitleContentRating>): TitleContentRating {
  return {
    board: 'Esrb',
    code: 'E',
    designation: 'Rated',
    minimumAge: '0',
    sourceId: 'igdb',
    externalRatingId: null,
    descriptors: [],
    synopsis: null,
    ...overrides,
  };
}

describe('ContentRatingRow', () => {
  it('renders official marks for known board codes', () => {
    render(
      <ContentRatingRow
        ratings={[rating({ board: 'Esrb', code: 'E' }), rating({ board: 'Pegi', code: 'PEGI 7' })]}
      />,
    );

    expect(screen.getByAltText('ESRB Everyone')).toBeInTheDocument();
    expect(screen.getByAltText('PEGI 7')).toBeInTheDocument();
  });

  it('falls back to a text badge when no mark exists for the code', () => {
    render(<ContentRatingRow ratings={[rating({ board: 'Esrb', code: 'ZZZ' })]} />);

    expect(screen.queryByAltText('ESRB ZZZ')).not.toBeInTheDocument();
    expect(screen.getByText('ESRB ZZZ')).toBeInTheDocument();
  });

  it('caps marks at max and collapses the remainder into a +N indicator', () => {
    render(
      <ContentRatingRow
        max={1}
        ratings={[rating({ board: 'Pegi', code: 'PEGI 7' }), rating({ board: 'Esrb', code: 'E' })]}
      />,
    );

    expect(screen.getByAltText('ESRB Everyone')).toBeInTheDocument();
    expect(screen.queryByAltText('PEGI 7')).not.toBeInTheDocument();
    expect(screen.getByText('+1')).toBeInTheDocument();
  });

  it('renders nothing when there are no ratings', () => {
    render(<ContentRatingRow ratings={[]} />);

    expect(screen.queryByRole('img')).not.toBeInTheDocument();
  });
});
