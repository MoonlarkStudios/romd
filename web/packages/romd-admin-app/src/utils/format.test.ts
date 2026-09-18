import { describe, expect, it } from 'vitest';
import { compareIntegerValues, formatBytes, formatDuration, formatRate, integerPercentage } from './format';

describe('exact integer formatting', () => {
  it('keeps values above JavaScript safe integer precision distinct for sorting', () => {
    expect(compareIntegerValues('9007199254740992', '9007199254740993')).toBe(-1);
    expect(compareIntegerValues('9007199254740993', '9007199254740992')).toBe(1);
  });

  it('formats canonical integer strings without converting the source to Number', () => {
    expect(formatBytes('1152921504606846976')).toBe('1 EB');
    expect(integerPercentage('9007199254740993', '18014398509481986')).toBe(50);
  });
});

describe('formatDuration', () => {
  it('formats seconds, minutes, and hours', () => {
    expect(formatDuration(45)).toBe('45s');
    expect(formatDuration(133)).toBe('2m 13s');
    expect(formatDuration(3840)).toBe('1h 4m');
  });

  it('returns a dash for unknown durations', () => {
    expect(formatDuration(Number.POSITIVE_INFINITY)).toBe('—');
    expect(formatDuration(-1)).toBe('—');
  });
});

describe('formatRate', () => {
  it('appends /s to a formatted byte size', () => {
    expect(formatRate(1024)).toBe('1 KB/s');
  });

  it('returns a dash for non-positive or unknown rates', () => {
    expect(formatRate(0)).toBe('—');
    expect(formatRate(Number.POSITIVE_INFINITY)).toBe('—');
  });
});
