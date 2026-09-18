import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';

/** Status filter */
export type QueueFilter = 'all' | 'Cataloged' | 'Unrouted' | 'Unidentified';

export function isStatusFilter(filter: QueueFilter): filter is 'Cataloged' | 'Unrouted' | 'Unidentified' {
  return filter === 'Cataloged' || filter === 'Unrouted' || filter === 'Unidentified';
}

export interface SortConfig {
  column: string;
  direction: 'asc' | 'desc';
}

export interface LibraryFilters {
  status: QueueFilter;
  search: string;
  sort: SortConfig | null;
}

export interface UseLibraryFiltersReturn {
  filters: LibraryFilters;
  searchInput: string;
  setSearchInput: (value: string) => void;
  setStatus: (status: QueueFilter) => void;
  setSort: (sort: SortConfig | null) => void;
  setFilters: (updates: Partial<LibraryFilters>) => void;
}

export function useLibraryFilters(): UseLibraryFiltersReturn {
  const [searchParams, setSearchParams] = useSearchParams();

  const urlStatus = (searchParams.get('status') as QueueFilter) || 'all';
  const urlSearch = searchParams.get('q') || '';
  const urlSortColumn = searchParams.get('sort');
  const urlSortOrder = searchParams.get('order') as 'asc' | 'desc' | null;

  const [searchInput, setSearchInput] = useState(urlSearch);
  const debouncedSearch = useDebouncedValue(searchInput, 300);
  const hasUserTyped = useRef(false);

  const filters = useMemo<LibraryFilters>(
    () => ({
      status: urlStatus,
      search: urlSearch,
      sort: urlSortColumn
        ? { column: urlSortColumn, direction: urlSortOrder || 'asc' }
        : null,
    }),
    [urlStatus, urlSearch, urlSortColumn, urlSortOrder]
  );

  useEffect(() => {
    if (!hasUserTyped.current) {
      return;
    }

    const currentUrlSearch = searchParams.get('q') || '';
    if (debouncedSearch === currentUrlSearch) {
      return;
    }

    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);

        if (debouncedSearch === '') {
          next.delete('q');
        } else {
          next.set('q', debouncedSearch);
        }

        return next;
      },
      { replace: true }
    );
  }, [debouncedSearch, setSearchParams, searchParams]);

  useEffect(() => {
    if (urlSearch !== searchInput && urlSearch !== debouncedSearch) {
      setSearchInput(urlSearch);
    }
  }, [urlSearch]);

  const setStatus = useCallback(
    (status: QueueFilter) => {
      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          if (status === 'all') {
            next.delete('status');
          } else {
            next.set('status', status);
          }
          return next;
        },
        { replace: true }
      );
    },
    [setSearchParams]
  );

  const setSort = useCallback(
    (sort: SortConfig | null) => {
      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          if (!sort) {
            next.delete('sort');
            next.delete('order');
          } else {
            next.set('sort', sort.column);
            next.set('order', sort.direction);
          }
          return next;
        },
        { replace: true }
      );
    },
    [setSearchParams]
  );

  const setFilters = useCallback(
    (updates: Partial<LibraryFilters>) => {
      if (updates.search !== undefined) {
        hasUserTyped.current = true;
        setSearchInput(updates.search);
      }
      if (updates.status) {
        setStatus(updates.status);
      }
      if (updates.sort !== undefined) {
        setSort(updates.sort);
      }
    },
    [setStatus, setSort]
  );

  return {
    filters,
    searchInput,
    setSearchInput: (value) => {
      hasUserTyped.current = true;
      setSearchInput(value);
    },
    setStatus,
    setSort,
    setFilters,
  };
}
