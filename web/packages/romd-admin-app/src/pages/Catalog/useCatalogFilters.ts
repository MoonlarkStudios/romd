import { useCallback, useEffect, useEffectEvent, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import type { TitleStatus } from '../../components/Title/enrichmentPresentation';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import {
  canonicalEnrichment,
  type LastAttemptStatus,
  legacyEnrichmentFlag,
  parseLastAttempt,
} from './catalogEnrichmentFilters';

export type ReleaseCompletenessFilter = 'all' | 'complete' | 'partial' | 'none';
export type ReleaseReviewFilter = 'all' | 'missing' | 'upgrades';
export type TrackedFilter = 'all' | 'tracked' | 'untracked';

export interface CatalogFilters {
  query: string;
  systemKey?: string;
  genre?: string;
  /** Title status flags that must all apply, or only Settled. */
  enrichment: TitleStatus[];
  /** Last recorded enrichment attempt, independent of the current status. */
  enrichmentStatus?: LastAttemptStatus;
  artworkStatus?: string;
  releaseCompleteness: ReleaseCompletenessFilter;
  releaseReview: ReleaseReviewFilter;
  tracked: TrackedFilter;
  sortBy: string;
}

export type LockedCatalogFilters = Partial<Pick<CatalogFilters, 'systemKey' | 'tracked'>>;

export interface UseCatalogFiltersOptions {
  lockedFilters?: LockedCatalogFilters;
}

export interface UseCatalogFiltersReturn {
  filters: CatalogFilters;
  lockedFilters: LockedCatalogFilters;
  searchInput: string;
  setSearchInput: (value: string) => void;
  setFilter: <K extends keyof CatalogFilters>(key: K, value: CatalogFilters[K] | null) => void;
  clearFilters: () => void;
  hasActiveFilters: boolean;
}

const RELEASE_COMPLETENESS_VALUES: ReleaseCompletenessFilter[] = [
  'all',
  'complete',
  'partial',
  'none',
];
const TRACKED_VALUES: TrackedFilter[] = [
  'all',
  'tracked',
  'untracked',
];
const FILTER_PARAM_KEYS = [
  'q',
  'systemKey',
  'genre',
  'enrichment',
  'enrichmentStatus',
  'artworkStatus',
  'releaseCompleteness',
  'releaseReview',
  'tracked',
  'sortBy',
] as const;

function isValidReleaseCompleteness(value: string | null): value is ReleaseCompletenessFilter {
  return value !== null && RELEASE_COMPLETENESS_VALUES.includes(value as ReleaseCompletenessFilter);
}

function isValidTracked(value: string | null): value is TrackedFilter {
  return value !== null && TRACKED_VALUES.includes(value as TrackedFilter);
}

function getFilterParamName(key: keyof CatalogFilters): string {
  return key === 'query' ? 'q' : key;
}

/**
 * Manages Catalog page filters synchronized with URL search params.
 *
 * @example
 * ```tsx
 * const { filters, searchInput, setSearchInput, setFilter } = useCatalogFilters();
 *
 * // URL: /catalog → filters = { query: '', releaseCompleteness: 'all', sortBy: 'name' }
 * // URL: /catalog?q=mario&releaseCompleteness=complete
 * // → filters = { query: 'mario', releaseCompleteness: 'complete', sortBy: 'relevance' }
 * ```
 */
export function useCatalogFilters(options: UseCatalogFiltersOptions = {}): UseCatalogFiltersReturn {
  const { lockedFilters } = options;
  const lockedPlatformId = lockedFilters?.systemKey;
  const lockedTracked = lockedFilters?.tracked;
  const [searchParams, setSearchParams] = useSearchParams();

  // Parse current URL state
  const urlQuery = searchParams.get('q') || '';
  const urlPlatformId = searchParams.get('systemKey') || undefined;
  const urlGenre = searchParams.get('genre') || undefined;
  const urlArtworkStatus = searchParams.get('artworkStatus') || undefined;
  const urlEnrichmentStatusRaw = searchParams.get('enrichmentStatus');
  const urlEnrichmentStatus = parseLastAttempt(urlEnrichmentStatusRaw);
  // Normalized while parsing, so no request carries a legacy flag-valued enrichmentStatus.
  const urlEnrichment = useMemo(
    () =>
      canonicalEnrichment([
        ...searchParams.getAll('enrichment'),
        ...legacyEnrichmentFlag(urlEnrichmentStatusRaw),
      ]),
    [
      searchParams,
      urlEnrichmentStatusRaw,
    ],
  );
  const urlReleaseCompletenessRaw = searchParams.get('releaseCompleteness');
  const urlReleaseCompleteness: ReleaseCompletenessFilter = isValidReleaseCompleteness(
    urlReleaseCompletenessRaw,
  )
    ? urlReleaseCompletenessRaw
    : 'all';
  const legacyView = searchParams.get('view');
  const reviewValue = searchParams.get('releaseReview') ?? legacyView;
  const urlReleaseReview: ReleaseReviewFilter =
    reviewValue === 'missing' || reviewValue === 'upgrades' ? reviewValue : 'all';
  const urlTrackedRaw = searchParams.get('tracked');
  const urlTracked: TrackedFilter = isValidTracked(urlTrackedRaw) ? urlTrackedRaw : 'all';
  const urlSortBy = searchParams.get('sortBy') || undefined;

  // Local state for immediate search input feedback
  const [searchInput, setSearchInput] = useState(urlQuery);

  // Debounce search input before syncing to URL
  const debouncedSearch = useDebouncedValue(searchInput, 300);

  // Auto-switch to relevance sort when query present
  const effectiveSortBy = urlSortBy ?? (urlQuery ? 'relevance' : 'name');

  // Build filters object from URL
  const filters = useMemo<CatalogFilters>(
    () => ({
      query: urlQuery,
      systemKey: lockedPlatformId ?? urlPlatformId,
      genre: urlGenre,
      enrichment: urlEnrichment,
      enrichmentStatus: urlEnrichmentStatus,
      artworkStatus: urlArtworkStatus,
      releaseCompleteness: urlReleaseCompleteness,
      releaseReview: urlReleaseReview,
      tracked: lockedTracked ?? urlTracked,
      sortBy: effectiveSortBy,
    }),
    [
      urlQuery,
      urlPlatformId,
      urlGenre,
      urlEnrichment,
      urlEnrichmentStatus,
      urlArtworkStatus,
      urlReleaseCompleteness,
      urlReleaseReview,
      urlTracked,
      effectiveSortBy,
      lockedPlatformId,
      lockedTracked,
    ],
  );

  // Sync debounced search to URL
  useEffect(() => {
    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);

        if (debouncedSearch === '') {
          next.delete('q');
          if (next.get('sortBy') === 'relevance') {
            next.delete('sortBy');
          }
        } else {
          next.set('q', debouncedSearch);
          if (!prev.get('sortBy')) {
            next.set('sortBy', 'relevance');
          }
        }

        return next;
      },
      {
        replace: true,
      },
    );
  }, [
    debouncedSearch,
    setSearchParams,
  ]);

  // Sync URL search to local state when navigating
  const syncNavigatedSearch = useEffectEvent((query: string) => {
    if (query !== searchInput && query !== debouncedSearch) setSearchInput(query);
  });
  useEffect(() => {
    syncNavigatedSearch(urlQuery);
  }, [
    urlQuery,
  ]);

  useEffect(() => {
    if (!lockedPlatformId || !searchParams.has('systemKey')) {
      return;
    }

    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.delete('systemKey');
        return next;
      },
      {
        replace: true,
      },
    );
  }, [
    lockedPlatformId,
    searchParams,
    setSearchParams,
  ]);

  useEffect(() => {
    if (!lockedTracked || !searchParams.has('tracked')) {
      return;
    }

    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.delete('tracked');
        return next;
      },
      {
        replace: true,
      },
    );
  }, [
    lockedTracked,
    searchParams,
    setSearchParams,
  ]);

  useEffect(() => {
    if (legacyView !== 'missing' && legacyView !== 'upgrades') return;
    setSearchParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        next.set('view', 'tracked');
        if (!next.has('releaseReview')) next.set('releaseReview', legacyView);
        return next;
      },
      {
        replace: true,
      },
    );
  }, [
    legacyView,
    setSearchParams,
  ]);

  // Rewrite old bookmarks and non-canonical values in place, keeping every other parameter.
  useEffect(() => {
    const urlValues = searchParams.getAll('enrichment');
    const canonical =
      urlValues.length === urlEnrichment.length &&
      urlValues.every((value, index) => value === urlEnrichment[index]) &&
      (urlEnrichmentStatusRaw ?? undefined) === urlEnrichmentStatus;
    if (canonical) {
      return;
    }

    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.delete('enrichment');
        for (const value of urlEnrichment) next.append('enrichment', value);
        if (urlEnrichmentStatus) next.set('enrichmentStatus', urlEnrichmentStatus);
        else next.delete('enrichmentStatus');
        return next;
      },
      {
        replace: true,
      },
    );
  }, [
    searchParams,
    setSearchParams,
    urlEnrichment,
    urlEnrichmentStatus,
    urlEnrichmentStatusRaw,
  ]);

  const setFilter = useCallback(
    <K extends keyof CatalogFilters>(key: K, value: CatalogFilters[K] | null) => {
      if ((key === 'systemKey' && lockedPlatformId) || (key === 'tracked' && lockedTracked)) {
        return;
      }

      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          const paramName = getFilterParamName(key);

          if (value === null || value === '' || value === undefined) {
            next.delete(paramName);
          } else if (Array.isArray(value)) {
            next.delete(paramName);
            for (const item of canonicalEnrichment(value)) next.append(paramName, item);
          } else if (
            key === 'releaseCompleteness' ||
            key === 'tracked' ||
            key === 'releaseReview'
          ) {
            // Only set the param when not 'all' (all is the default)
            if (value !== 'all') {
              next.set(paramName, String(value));
            } else {
              next.delete(paramName);
            }
          } else {
            next.set(paramName, String(value));
          }

          // If switching to relevance sort without a query, switch to name instead
          if (key === 'sortBy' && value === 'relevance' && !next.get('q')) {
            next.set('sortBy', 'name');
          }

          return next;
        },
        {
          replace: true,
        },
      );
    },
    [
      lockedPlatformId,
      lockedTracked,
      setSearchParams,
    ],
  );

  const clearFilters = useCallback(() => {
    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);

        for (const key of FILTER_PARAM_KEYS) {
          next.delete(key);
        }

        return next;
      },
      {
        replace: true,
      },
    );
    setSearchInput('');
  }, [
    setSearchParams,
  ]);

  const hasActiveFilters = useMemo(
    () =>
      !!filters.query ||
      !!filters.systemKey ||
      !!filters.genre ||
      filters.enrichment.length > 0 ||
      !!filters.enrichmentStatus ||
      !!filters.artworkStatus ||
      filters.releaseCompleteness !== 'all' ||
      filters.releaseReview !== 'all' ||
      filters.tracked !== 'all',
    [
      filters,
    ],
  );

  return {
    filters,
    lockedFilters: lockedFilters ?? {},
    searchInput,
    setSearchInput,
    setFilter,
    clearFilters,
    hasActiveFilters,
  };
}
