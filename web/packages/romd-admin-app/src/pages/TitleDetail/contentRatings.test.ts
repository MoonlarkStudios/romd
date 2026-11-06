import { describe, expect, it } from 'vitest';
import { boardSortIndex, describeDesignation, formatContentRating } from './contentRatings';

describe('formatContentRating', () => {
  it('does not duplicate the board name when the code already embeds it', () => {
    expect(formatContentRating('Pegi', 'PEGI 7')).toBe('PEGI 7');
    expect(formatContentRating('Cero', 'CERO B')).toBe('CERO B');
  });

  it('prepends the board name when the code does not embed it', () => {
    expect(formatContentRating('Esrb', 'E10+')).toBe('ESRB E10+');
    expect(formatContentRating('Acb', 'G')).toBe('ACB G');
  });
});

describe('describeDesignation', () => {
  it('summarizes rated, pending, and refused designations', () => {
    expect(describeDesignation('Rated', 0)).toBe('All ages');
    expect(describeDesignation('Rated', 12)).toBe('Ages 12+');
    expect(describeDesignation('RatingPending', null)).toBe('Pending classification');
    expect(describeDesignation('RefusedClassification', null)).toBe('Refused classification');
  });
});

describe('boardSortIndex', () => {
  it('orders boards by the canonical catalog order', () => {
    expect(boardSortIndex('Esrb')).toBeLessThan(boardSortIndex('Pegi'));
    expect(boardSortIndex('ClassInd')).toBeLessThan(boardSortIndex('Acb'));
  });
});
