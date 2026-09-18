import { act, renderHook } from '@testing-library/react';
import { MemoryRouter, useSearchParams } from 'react-router';
import { describe, expect, it } from 'vitest';
import { useUrlState } from './useUrlState';

function createWrapper(initialEntries: string[] = ['/']) {
  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <MemoryRouter initialEntries={initialEntries}>{children}</MemoryRouter>;
  };
}

describe('useUrlState', () => {
  it('returns default value when param is not present', () => {
    const { result } = renderHook(() => useUrlState({ key: 'status', defaultValue: 'all' }), {
      wrapper: createWrapper(['/roms']),
    });

    expect(result.current[0]).toBe('all');
  });

  it('returns URL param value when present', () => {
    const { result } = renderHook(() => useUrlState({ key: 'status', defaultValue: 'all' }), {
      wrapper: createWrapper(['/roms?status=Cataloged']),
    });

    expect(result.current[0]).toBe('Cataloged');
  });

  it('updates URL when value changes', () => {
    const { result } = renderHook(
      () => {
        const urlState = useUrlState({ key: 'status', defaultValue: 'all' });
        const [searchParams] = useSearchParams();
        return { urlState, searchParams };
      },
      { wrapper: createWrapper(['/roms']) }
    );

    act(() => {
      result.current.urlState[1]('Cataloged');
    });

    expect(result.current.urlState[0]).toBe('Cataloged');
    expect(result.current.searchParams.get('status')).toBe('Cataloged');
  });

  it('removes param from URL when setting to default value', () => {
    const { result } = renderHook(
      () => {
        const urlState = useUrlState({ key: 'status', defaultValue: 'all' });
        const [searchParams] = useSearchParams();
        return { urlState, searchParams };
      },
      { wrapper: createWrapper(['/roms?status=Cataloged']) }
    );

    expect(result.current.urlState[0]).toBe('Cataloged');

    act(() => {
      result.current.urlState[1]('all');
    });

    expect(result.current.urlState[0]).toBe('all');
    expect(result.current.searchParams.has('status')).toBe(false);
  });

  it('preserves other URL params when updating', () => {
    const { result } = renderHook(
      () => {
        const urlState = useUrlState({ key: 'status', defaultValue: 'all' });
        const [searchParams] = useSearchParams();
        return { urlState, searchParams };
      },
      { wrapper: createWrapper(['/roms?q=mario&sort=size']) }
    );

    act(() => {
      result.current.urlState[1]('Cataloged');
    });

    expect(result.current.searchParams.get('status')).toBe('Cataloged');
    expect(result.current.searchParams.get('q')).toBe('mario');
    expect(result.current.searchParams.get('sort')).toBe('size');
  });

  it('works with different keys', () => {
    const { result } = renderHook(() => useUrlState({ key: 'q', defaultValue: '' }), {
      wrapper: createWrapper(['/roms?q=sonic']),
    });

    expect(result.current[0]).toBe('sonic');
  });
});
