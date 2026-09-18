import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router';

interface UseUrlStateOptions<T extends string> {
  /** The URL search param key */
  key: string;
  /** Default value when param is not present */
  defaultValue: T;
}

/**
 * Syncs a single URL search param with React state.
 *
 * - Removes the param when value equals the default (cleaner URLs)
 * - Uses replace: true to avoid polluting browser history
 *
 * @example
 * ```tsx
 * const [status, setStatus] = useUrlState({ key: 'status', defaultValue: 'all' });
 * // URL: /roms → status = 'all'
 * // URL: /roms?status=Cataloged → status = 'Cataloged'
 * // setStatus('all') → removes param from URL
 * ```
 */
export function useUrlState<T extends string>({
  key,
  defaultValue,
}: UseUrlStateOptions<T>): [T, (value: T) => void] {
  const [searchParams, setSearchParams] = useSearchParams();

  const value = useMemo(() => {
    const param = searchParams.get(key);
    return (param ?? defaultValue) as T;
  }, [searchParams, key, defaultValue]);

  const setValue = useCallback(
    (newValue: T) => {
      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          if (newValue === defaultValue) {
            next.delete(key);
          } else {
            next.set(key, newValue);
          }
          return next;
        },
        { replace: true }
      );
    },
    [setSearchParams, key, defaultValue]
  );

  return [value, setValue];
}
