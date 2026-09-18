import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useDebouncedValue } from './useDebouncedValue';

describe('useDebouncedValue', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('returns initial value immediately', () => {
    const { result } = renderHook(() => useDebouncedValue('initial', 300));

    expect(result.current).toBe('initial');
  });

  it('does not update value before delay', () => {
    const { result, rerender } = renderHook(({ value }) => useDebouncedValue(value, 300), {
      initialProps: { value: 'initial' },
    });

    rerender({ value: 'updated' });

    // Value should still be initial before delay
    expect(result.current).toBe('initial');

    // Advance time but not past delay
    act(() => {
      vi.advanceTimersByTime(200);
    });

    expect(result.current).toBe('initial');
  });

  it('updates value after delay', () => {
    const { result, rerender } = renderHook(({ value }) => useDebouncedValue(value, 300), {
      initialProps: { value: 'initial' },
    });

    rerender({ value: 'updated' });

    act(() => {
      vi.advanceTimersByTime(300);
    });

    expect(result.current).toBe('updated');
  });

  it('resets timer on subsequent changes', () => {
    const { result, rerender } = renderHook(({ value }) => useDebouncedValue(value, 300), {
      initialProps: { value: 'initial' },
    });

    // First change
    rerender({ value: 'first' });

    act(() => {
      vi.advanceTimersByTime(200);
    });

    // Second change before delay completes
    rerender({ value: 'second' });

    // Advance past original delay time
    act(() => {
      vi.advanceTimersByTime(200);
    });

    // Should still be initial because timer was reset
    expect(result.current).toBe('initial');

    // Complete the new delay
    act(() => {
      vi.advanceTimersByTime(100);
    });

    expect(result.current).toBe('second');
  });

  it('works with different types', () => {
    const { result, rerender } = renderHook(({ value }) => useDebouncedValue(value, 100), {
      initialProps: { value: 42 },
    });

    expect(result.current).toBe(42);

    rerender({ value: 100 });

    act(() => {
      vi.advanceTimersByTime(100);
    });

    expect(result.current).toBe(100);
  });

  it('works with objects', () => {
    const initial = { name: 'test' };
    const updated = { name: 'updated' };

    const { result, rerender } = renderHook(({ value }) => useDebouncedValue(value, 100), {
      initialProps: { value: initial },
    });

    expect(result.current).toBe(initial);

    rerender({ value: updated });

    act(() => {
      vi.advanceTimersByTime(100);
    });

    expect(result.current).toBe(updated);
  });

  it('cleans up timer on unmount', () => {
    const clearTimeoutSpy = vi.spyOn(global, 'clearTimeout');

    const { unmount } = renderHook(() => useDebouncedValue('test', 300));

    unmount();

    expect(clearTimeoutSpy).toHaveBeenCalled();

    clearTimeoutSpy.mockRestore();
  });
});
