import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';

export type BiosFilter = 'exclude' | 'include' | 'only';

export interface SearchFilters {
  query: string;
  systemKey?: string;
  year?: string;
  manufacturer?: string;
  region?: string;
  bios: BiosFilter;
  sortBy: string;
}

export interface UseSearchFiltersReturn {
  filters: SearchFilters;
  /** Raw search input value (for controlled input) */
  searchInput: string;
  /** Set search input (debounced before syncing to URL) */
  setSearchInput: (value: string) => void;
  /** Set a single filter value */
  setFilter: (key: string, value: string | null) => void;
  /** Clear all filters */
  clearFilters: () => void;
  /** Whether any filters are active */
  hasActiveFilters: boolean;
}

/**
 * Manages Search page filters synchronized with URL search params.
 *
 * Features:
 * - Debounced search input (300ms) to prevent URL spam
 * - Auto-switches to "relevance" sort when query present
 * - URL persistence for all filter values
 * - Uses replace: true to avoid polluting browser history
 *
 * @example
 * ```tsx
 * const { filters, searchInput, setSearchInput, setFilter, clearFilters } = useSearchFilters();
 *
 * // URL: /search → filters = { query: '', bios: 'exclude', sortBy: 'name' }
 * // URL: /search?q=mario&systemKey=abc123&sortBy=relevance
 * // → filters = { query: 'mario', systemKey: 'abc123', bios: 'exclude', sortBy: 'relevance' }
 * ```
 */
export function useSearchFilters(): UseSearchFiltersReturn {
  const [searchParams, setSearchParams] = useSearchParams();

  // Parse current URL state
  const urlQuery = searchParams.get('q') || '';
  const urlPlatformId = searchParams.get('systemKey') || undefined;
  const urlYear = searchParams.get('year') || undefined;
  const urlManufacturer = searchParams.get('manufacturer') || undefined;
  const urlRegion = searchParams.get('region') || undefined;
  const urlBios = (searchParams.get('bios') as BiosFilter) || 'exclude';
  const urlSortBy = searchParams.get('sortBy') || undefined;

  // Local state for immediate search input feedback
  const [searchInput, setSearchInput] = useState(urlQuery);

  // Debounce search input before syncing to URL
  const debouncedSearch = useDebouncedValue(searchInput, 300);

  // Auto-switch to relevance sort when query present, otherwise default to name
  const effectiveSortBy = urlSortBy ?? (debouncedSearch ? 'relevance' : 'name');

  // Build filters object from URL
  const filters = useMemo<SearchFilters>(
    () => ({
      query: urlQuery,
      systemKey: urlPlatformId,
      year: urlYear,
      manufacturer: urlManufacturer,
      region: urlRegion,
      bios: urlBios,
      sortBy: effectiveSortBy,
    }),
    [urlQuery, urlPlatformId, urlYear, urlManufacturer, urlRegion, urlBios, effectiveSortBy]
  );

  // Sync debounced search to URL
  useEffect(() => {
    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);

        if (debouncedSearch === '') {
          next.delete('q');
          // When clearing query, also reset sortBy if it was 'relevance'
          if (next.get('sortBy') === 'relevance') {
            next.delete('sortBy');
          }
        } else {
          next.set('q', debouncedSearch);
          // Auto-switch to relevance when typing (if not explicitly set)
          if (!prev.get('sortBy')) {
            next.set('sortBy', 'relevance');
          }
        }

        return next;
      },
      { replace: true }
    );
  }, [debouncedSearch, setSearchParams]);

  // Sync URL search to local state when navigating (e.g., browser back/forward)
  useEffect(() => {
    if (urlQuery !== searchInput && urlQuery !== debouncedSearch) {
      setSearchInput(urlQuery);
    }
  }, [urlQuery]); // eslint-disable-line react-hooks/exhaustive-deps

  const setFilter = useCallback(
    (key: string, value: string | null) => {
      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev);

          if (value === null || value === '') {
            next.delete(key);
          } else {
            next.set(key, value);
          }

          // If switching to relevance sort without a query, switch to name instead
          if (key === 'sortBy' && value === 'relevance' && !next.get('q')) {
            next.set('sortBy', 'name');
          }

          return next;
        },
        { replace: true }
      );
    },
    [setSearchParams]
  );

  const clearFilters = useCallback(() => {
    setSearchParams({}, { replace: true });
    setSearchInput('');
  }, [setSearchParams]);

  const hasActiveFilters = useMemo(
    () =>
      !!urlQuery ||
      !!urlPlatformId ||
      !!urlYear ||
      !!urlManufacturer ||
      !!urlRegion ||
      urlBios !== 'exclude',
    [urlQuery, urlPlatformId, urlYear, urlManufacturer, urlRegion, urlBios]
  );

  return {
    filters,
    searchInput,
    setSearchInput,
    setFilter,
    clearFilters,
    hasActiveFilters,
  };
}
