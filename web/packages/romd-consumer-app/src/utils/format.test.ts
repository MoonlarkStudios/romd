import { describe, expect, it } from 'vitest';
import { formatBytes, formatCount, formatRating } from './format';

describe('consumer numeric formatting', () => {
  it('formats an Int64 byte string without a Number conversion', () => {
    expect(formatBytes('1152921504606846976')).toBe('1 EB');
  });

  it('accepts generated numeric counts and ratings directly', () => {
    expect(formatCount(1234)).toBe(new Intl.NumberFormat().format(1234));
    expect(formatRating(8.5)).toBe('8.5');
  });
});
