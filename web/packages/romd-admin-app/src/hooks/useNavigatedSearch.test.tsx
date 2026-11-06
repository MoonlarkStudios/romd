import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, useLocation, useNavigate } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useCatalogFilters } from '../pages/Catalog/useCatalogFilters';
import { useLibraryFilters } from '../pages/Library/useLibraryFilters';
import { useSearchFilters } from '../pages/Search/useSearchFilters';

const hooks = [
  [
    'catalog',
    useCatalogFilters,
  ],
  [
    'library',
    useLibraryFilters,
  ],
  [
    'search',
    useSearchFilters,
  ],
] as const;

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <MemoryRouter
      initialEntries={[
        '/?q=initial',
      ]}
    >
      {children}
    </MemoryRouter>
  );
}

describe.each(hooks)('%s search navigation', (_name, useFilters) => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('preserves immediate typing while debouncing URL updates', async () => {
    const { result } = renderHook(
      () => ({
        filters: useFilters(),
        location: useLocation(),
      }),
      {
        wrapper: Wrapper,
      },
    );
    act(() => result.current.filters.setSearchInput('new search'));
    expect(result.current.filters.searchInput).toBe('new search');
    expect(new URLSearchParams(result.current.location.search).get('q')).toBe('initial');
    await act(() => vi.advanceTimersByTimeAsync(300));
    expect(result.current.filters.searchInput).toBe('new search');
    expect(new URLSearchParams(result.current.location.search).get('q')).toBe('new search');
  });

  it('restores input when navigating to a different search', async () => {
    const { result } = renderHook(
      () => ({
        filters: useFilters(),
        navigate: useNavigate(),
      }),
      {
        wrapper: Wrapper,
      },
    );
    await act(() => result.current.navigate('/?q=from-history'));
    expect(result.current.filters.searchInput).toBe('from-history');
    await act(() => vi.advanceTimersByTimeAsync(300));
    expect(result.current.filters.searchInput).toBe('from-history');
  });
});
