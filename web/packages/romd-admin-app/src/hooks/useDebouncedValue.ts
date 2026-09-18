import { useEffect, useState } from 'react';

/**
 * Debounces a value, returning the debounced version after the delay.
 *
 * Useful for preventing excessive URL updates or API calls during typing.
 *
 * @example
 * ```tsx
 * const [searchInput, setSearchInput] = useState('');
 * const debouncedSearch = useDebouncedValue(searchInput, 300);
 *
 * // debouncedSearch updates 300ms after searchInput stops changing
 * ```
 */
export function useDebouncedValue<T>(value: T, delay: number): T {
  const [debouncedValue, setDebouncedValue] = useState(value);

  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedValue(value);
    }, delay);

    return () => {
      clearTimeout(timer);
    };
  }, [value, delay]);

  return debouncedValue;
}
