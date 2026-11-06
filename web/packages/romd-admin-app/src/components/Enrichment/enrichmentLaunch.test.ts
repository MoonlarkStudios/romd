import { describe, expect, it } from 'vitest';
import { effectiveScope, launchRequest, launchScopes } from './enrichmentLaunch';

const values = (...args: Parameters<typeof launchScopes>) =>
  launchScopes(...args).map((option) => option.value);

describe('launchScopes', () => {
  it('offers the selection, the filters, automatic eligibility and every tracked title when all apply', () => {
    expect(
      launchScopes({
        mode: 'Enrich',
        selectedCount: 2,
        hasFilters: true,
      }),
    ).toEqual([
      {
        value: 'Selected',
        label: 'Selected titles (2)',
      },
      {
        value: 'Filtered',
        label: 'Titles matching the current filters',
      },
      {
        value: 'Automatic',
        label: 'Titles matching the filters that can be enriched automatically',
      },
      {
        value: 'AllTracked',
        label: 'All tracked titles',
      },
    ]);
  });

  it('offers Selected only with a selection and Filtered only with filters', () => {
    expect(
      values({
        mode: 'Enrich',
        selectedCount: 0,
        hasFilters: false,
      }),
    ).toEqual([
      'Automatic',
      'AllTracked',
    ]);
    expect(
      launchScopes({
        mode: 'Enrich',
        selectedCount: 0,
        hasFilters: false,
      })[0].label,
    ).toBe('Titles that can be enriched automatically');
  });

  it('offers Automatic only when enriching metadata', () => {
    expect(
      values({
        mode: 'Artwork',
        selectedCount: 3,
        hasFilters: true,
      }),
    ).toEqual([
      'Selected',
      'Filtered',
      'AllTracked',
    ]);
  });
});

describe('effectiveScope', () => {
  it('keeps an offered scope and falls back to the filters, then every tracked title', () => {
    const artwork = launchScopes({
      mode: 'Artwork',
      selectedCount: 0,
      hasFilters: true,
    });
    expect(effectiveScope('Filtered', artwork)).toBe('Filtered');
    expect(effectiveScope('Automatic', artwork)).toBe('Filtered');
    expect(
      effectiveScope(
        'Selected',
        launchScopes({
          mode: 'Enrich',
          selectedCount: 0,
          hasFilters: false,
        }),
      ),
    ).toBe('AllTracked');
  });
});

describe('launchRequest', () => {
  const filters = {
    query: 'mario',
    systemKey: 'snes',
    tracked: 'tracked',
    enrichment: [
      'NeedsReview',
      'Blocked',
    ],
  };
  const base = {
    requestId: 'request',
    mode: 'Enrich' as const,
    titleIds: [
      'one',
    ],
    filters,
  };

  it('sends ids only for Selected and filters only for Filtered and Automatic', () => {
    expect(
      launchRequest({
        ...base,
        scope: 'Selected',
      }),
    ).toEqual({
      requestId: 'request',
      scope: 'Selected',
      mode: 'Enrich',
      titleIds: [
        'one',
      ],
    });
    expect(
      launchRequest({
        ...base,
        scope: 'Filtered',
      }),
    ).toEqual({
      requestId: 'request',
      scope: 'Filtered',
      mode: 'Enrich',
      filters,
    });
    expect(
      launchRequest({
        ...base,
        scope: 'Automatic',
      }),
    ).toEqual({
      requestId: 'request',
      scope: 'Automatic',
      mode: 'Enrich',
      filters,
    });
    expect(
      launchRequest({
        ...base,
        scope: 'AllTracked',
      }),
    ).toEqual({
      requestId: 'request',
      scope: 'AllTracked',
      mode: 'Enrich',
    });
  });

  it('omits enrichment when no flag is set and sends Automatic without filters when there are none', () => {
    expect(
      launchRequest({
        ...base,
        scope: 'Filtered',
        filters: {
          ...filters,
          enrichment: [],
        },
      }).filters,
    ).not.toHaveProperty('enrichment');
    expect(
      launchRequest({
        ...base,
        scope: 'Filtered',
        filters: {
          ...filters,
          enrichment: [
            ' ',
          ],
        },
      }).filters,
    ).not.toHaveProperty('enrichment');
    expect(
      launchRequest({
        ...base,
        scope: 'Automatic',
        filters: undefined,
      }),
    ).not.toHaveProperty('filters');
  });
});
