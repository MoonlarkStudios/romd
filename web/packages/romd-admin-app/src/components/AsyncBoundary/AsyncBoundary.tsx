import type { UseQueryResult } from '@tanstack/react-query';
import type { ReactElement, ReactNode } from 'react';
import { EmptyState } from '../EmptyState';
import { ErrorBoundary, ErrorFallback, type FallbackProps } from '../ErrorBoundary';
import { LoadingState } from './LoadingState';

/**
 * Minimal interface for query-like objects.
 * Allows AsyncBoundary to work with any object that has these properties.
 */
type QueryLike<TData> = Pick<UseQueryResult<TData>, 'data' | 'isLoading' | 'isPending' | 'error' | 'refetch'>;

interface AsyncBoundaryProps<TData> {
  /** TanStack Query result object or any object matching QueryLike interface */
  query: QueryLike<TData>;

  /** Render function called with data when available and not empty */
  children: (data: TData) => ReactNode;

  /** Custom loading component (defaults to centered Loader) */
  loadingFallback?: ReactNode;

  /** Custom error component factory */
  errorFallback?: (props: FallbackProps) => ReactNode;

  /** Custom empty state (shown when data is empty array or null) */
  emptyFallback?: ReactNode;

  /** Function to determine if data should be considered empty */
  isEmpty?: (data: TData) => boolean;

  /** Keys that trigger error boundary reset when changed */
  resetKeys?: unknown[];

  /** Minimum height for loading state */
  loadingMinHeight?: number | string;
}

/**
 * Default isEmpty check - handles arrays and null/undefined.
 */
function defaultIsEmpty<TData>(data: TData): boolean {
  if (data === null || data === undefined) return true;
  if (Array.isArray(data)) return data.length === 0;
  return false;
}

/**
 * Declaratively handles loading, error, and empty states for async data.
 * Wraps children in an ErrorBoundary for render error handling.
 *
 * @example
 * ```tsx
 * // Basic usage with query hook
 * const romsQuery = useRoms();
 *
 * <AsyncBoundary query={romsQuery}>
 *   {(roms) => <RomTable roms={roms} />}
 * </AsyncBoundary>
 *
 * // With custom empty state
 * <AsyncBoundary
 *   query={romsQuery}
 *   emptyFallback={
 *     <EmptyState
 *       title="No ROMs uploaded"
 *       action={{ label: 'Upload', onClick: openModal }}
 *     />
 *   }
 * >
 *   {(roms) => <RomTable roms={roms} />}
 * </AsyncBoundary>
 *
 * // With custom isEmpty check for objects
 * <AsyncBoundary
 *   query={statsQuery}
 *   isEmpty={(stats) => stats.totalRomFiles === '0'}
 * >
 *   {(stats) => <StatsDisplay stats={stats} />}
 * </AsyncBoundary>
 * ```
 */
export function AsyncBoundary<TData>({
  query,
  children,
  loadingFallback,
  errorFallback,
  emptyFallback,
  isEmpty = defaultIsEmpty,
  resetKeys = [],
  loadingMinHeight = 200,
}: AsyncBoundaryProps<TData>): ReactElement {
  const { data, isLoading, isPending, error, refetch } = query;

  // Loading state
  if (isLoading || isPending) {
    return <>{loadingFallback ?? <LoadingState minHeight={loadingMinHeight} />}</>;
  }

  // Error state
  if (error) {
    const errorFallbackProps: FallbackProps = {
      error: error instanceof Error ? error : new Error(String(error)),
      resetErrorBoundary: () => refetch(),
    };

    if (errorFallback) {
      return <>{errorFallback(errorFallbackProps)}</>;
    }

    return <ErrorFallback {...errorFallbackProps} />;
  }

  // Empty state
  if (data !== undefined && isEmpty(data)) {
    return <>{emptyFallback ?? <EmptyState />}</>;
  }

  // Data available - wrap in error boundary for render errors
  if (data !== undefined) {
    return (
      <ErrorBoundary FallbackComponent={ErrorFallback} onReset={() => refetch()} resetKeys={resetKeys}>
        {children(data)}
      </ErrorBoundary>
    );
  }

  // Fallback (should not reach in normal flow)
  return <LoadingState minHeight={loadingMinHeight} />;
}
