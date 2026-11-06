import { describe, expect, it } from 'vitest';
import { titleStatuses } from '../../components/Title/enrichmentPresentation';
import {
  canonicalEnrichment,
  enrichmentFilters,
  lastAttemptLabel,
  legacyEnrichmentFlag,
  parseLastAttempt,
} from './catalogEnrichmentFilters';

describe('canonicalEnrichment', () => {
  it('keeps known flags once each in the chip order', () => {
    expect(
      canonicalEnrichment([
        'Automatic',
        'Unknown',
        'NeedsReview',
        'Automatic',
        'Blocked',
      ]),
    ).toEqual([
      'NeedsReview',
      'Blocked',
      'Automatic',
    ]);
  });

  it('lets Settled exclude every flag', () => {
    expect(
      canonicalEnrichment([
        'NeedsReview',
        'Settled',
        'Processing',
      ]),
    ).toEqual([
      'Settled',
    ]);
    expect(canonicalEnrichment([])).toEqual([]);
  });

  it('offers the flags in the same order as the status chips', () => {
    const allFlags = titleStatuses({
      needsReview: true,
      hasOperationalBlocker: true,
      hasActiveWork: true,
      canEnrichAutomatically: true,
    });
    expect(enrichmentFilters).toEqual([
      ...allFlags,
      'Settled',
    ]);
  });
});

describe('last attempt vocabulary', () => {
  it('maps only the four legacy flags out of enrichmentStatus', () => {
    expect(legacyEnrichmentFlag('Blocked')).toEqual([
      'Blocked',
    ]);
    expect(legacyEnrichmentFlag('Settled')).toEqual([]);
    expect(legacyEnrichmentFlag('Failed')).toEqual([]);
    expect(legacyEnrichmentFlag(null)).toEqual([]);
  });

  it('accepts only the six last-attempt values', () => {
    expect(parseLastAttempt('NotFound')).toBe('NotFound');
    expect(parseLastAttempt('NeedsReview')).toBeUndefined();
    expect(parseLastAttempt(null)).toBeUndefined();
  });

  it('labels an absent attempt as never attempted and shows unknown values as recorded', () => {
    expect(lastAttemptLabel(undefined)).toBe('Never attempted');
    expect(lastAttemptLabel('LowConfidence')).toBe('Low confidence');
    expect(lastAttemptLabel('Enriched')).toBe('Enriched');
  });
});
