import { Component, type ErrorInfo, type ReactNode } from 'react';

export interface FallbackProps {
  error: Error;
  resetErrorBoundary: () => void;
}

interface ErrorBoundaryProps {
  children: ReactNode;
  /** Static fallback element */
  fallback?: ReactNode;
  /** Component that receives error and reset function */
  FallbackComponent?: React.ComponentType<FallbackProps>;
  /** Called when an error is caught */
  onError?: (error: Error, info: ErrorInfo) => void;
  /** Called when the error boundary resets */
  onReset?: () => void;
  /** When any of these values change, the error boundary will reset */
  resetKeys?: unknown[];
}

interface ErrorBoundaryState {
  hasError: boolean;
  error: Error | null;
}

/**
 * Error boundary component that catches JavaScript errors anywhere in the child
 * component tree and displays a fallback UI instead of crashing.
 *
 * @example
 * ```tsx
 * // With FallbackComponent
 * <ErrorBoundary FallbackComponent={ErrorFallback}>
 *   <MyComponent />
 * </ErrorBoundary>
 *
 * // With static fallback
 * <ErrorBoundary fallback={<div>Something went wrong</div>}>
 *   <MyComponent />
 * </ErrorBoundary>
 *
 * // With resetKeys for automatic reset
 * <ErrorBoundary FallbackComponent={ErrorFallback} resetKeys={[userId]}>
 *   <UserProfile userId={userId} />
 * </ErrorBoundary>
 * ```
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  constructor(props: ErrorBoundaryProps) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    this.props.onError?.(error, info);
  }

  componentDidUpdate(prevProps: ErrorBoundaryProps) {
    // Reset when resetKeys change
    if (
      this.state.hasError &&
      this.props.resetKeys &&
      prevProps.resetKeys &&
      !arraysEqual(prevProps.resetKeys, this.props.resetKeys)
    ) {
      this.resetErrorBoundary();
    }
  }

  resetErrorBoundary = () => {
    this.props.onReset?.();
    this.setState({ hasError: false, error: null });
  };

  render() {
    const { hasError, error } = this.state;
    const { children, fallback, FallbackComponent } = this.props;

    if (hasError && error) {
      if (FallbackComponent) {
        return <FallbackComponent error={error} resetErrorBoundary={this.resetErrorBoundary} />;
      }
      return fallback ?? null;
    }

    return children;
  }
}

function arraysEqual(a: unknown[], b: unknown[]): boolean {
  if (a.length !== b.length) return false;
  return a.every((val, index) => val === b[index]);
}
