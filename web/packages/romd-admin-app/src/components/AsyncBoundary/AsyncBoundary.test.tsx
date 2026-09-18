import { describe, expect, it, vi } from 'vitest';
import { render, screen, userEvent } from '../../test/utils';
import { EmptyState } from '../EmptyState';
import { AsyncBoundary } from './AsyncBoundary';

// Helper to create mock query objects
function createMockQuery<T>(overrides: Partial<{
  data: T;
  isLoading: boolean;
  isPending: boolean;
  error: Error | null;
  refetch: () => void;
}>) {
  return {
    data: undefined as T | undefined,
    isLoading: false,
    isPending: false,
    error: null as Error | null,
    refetch: vi.fn(),
    ...overrides,
  };
}

describe('AsyncBoundary', () => {
  it('shows loading state when isLoading is true', () => {
    const query = createMockQuery({ isLoading: true });

    render(<AsyncBoundary query={query}>{() => <div>Content</div>}</AsyncBoundary>);

    // Loader should be present (Mantine Loader has role="status" or we can check for the loader)
    expect(screen.queryByText('Content')).not.toBeInTheDocument();
  });

  it('shows loading state when isPending is true', () => {
    const query = createMockQuery({ isPending: true });

    render(<AsyncBoundary query={query}>{() => <div>Content</div>}</AsyncBoundary>);

    expect(screen.queryByText('Content')).not.toBeInTheDocument();
  });

  it('shows custom loading fallback when provided', () => {
    const query = createMockQuery({ isLoading: true });

    render(
      <AsyncBoundary query={query} loadingFallback={<div>Custom loading...</div>}>
        {() => <div>Content</div>}
      </AsyncBoundary>
    );

    expect(screen.getByText('Custom loading...')).toBeInTheDocument();
  });

  it('shows error state when error is present', () => {
    const query = createMockQuery({ error: new Error('Failed to fetch') });

    render(<AsyncBoundary query={query}>{() => <div>Content</div>}</AsyncBoundary>);

    expect(screen.getByText('Something went wrong')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /try again/i })).toBeInTheDocument();
    expect(screen.queryByText('Content')).not.toBeInTheDocument();
  });

  it('calls refetch when try again is clicked on error', async () => {
    const user = userEvent.setup();
    const refetch = vi.fn();
    const query = createMockQuery({ error: new Error('Failed'), refetch });

    render(<AsyncBoundary query={query}>{() => <div>Content</div>}</AsyncBoundary>);

    await user.click(screen.getByRole('button', { name: /try again/i }));

    expect(refetch).toHaveBeenCalledTimes(1);
  });

  it('shows custom error fallback when provided', () => {
    const query = createMockQuery({ error: new Error('Custom error') });

    render(
      <AsyncBoundary
        query={query}
        errorFallback={({ error }) => <div>Custom error: {error.message}</div>}
      >
        {() => <div>Content</div>}
      </AsyncBoundary>
    );

    expect(screen.getByText('Custom error: Custom error')).toBeInTheDocument();
  });

  it('shows default empty state for empty array', () => {
    const query = createMockQuery({ data: [] });

    render(<AsyncBoundary query={query}>{(data) => <div>Items: {data.length}</div>}</AsyncBoundary>);

    expect(screen.getByText('No data')).toBeInTheDocument();
    expect(screen.queryByText('Items: 0')).not.toBeInTheDocument();
  });

  it('shows custom empty fallback when provided', () => {
    const query = createMockQuery({ data: [] });

    render(
      <AsyncBoundary
        query={query}
        emptyFallback={<EmptyState title="No items" description="Add some items" />}
      >
        {(data) => <div>Items: {data.length}</div>}
      </AsyncBoundary>
    );

    expect(screen.getByText('No items')).toBeInTheDocument();
    expect(screen.getByText('Add some items')).toBeInTheDocument();
  });

  it('renders children with data when available', () => {
    const query = createMockQuery({ data: ['item1', 'item2'] });

    render(
      <AsyncBoundary query={query}>{(data) => <div>Items: {data.join(', ')}</div>}</AsyncBoundary>
    );

    expect(screen.getByText('Items: item1, item2')).toBeInTheDocument();
  });

  it('uses custom isEmpty function', () => {
    interface Stats {
      count: number;
    }
    const query = createMockQuery<Stats>({ data: { count: 0 } });

    render(
      <AsyncBoundary
        query={query}
        isEmpty={(data) => data.count === 0}
        emptyFallback={<div>No stats yet</div>}
      >
        {(data) => <div>Count: {data.count}</div>}
      </AsyncBoundary>
    );

    expect(screen.getByText('No stats yet')).toBeInTheDocument();
    expect(screen.queryByText('Count: 0')).not.toBeInTheDocument();
  });

  it('renders data when custom isEmpty returns false', () => {
    interface Stats {
      count: number;
    }
    const query = createMockQuery<Stats>({ data: { count: 5 } });

    render(
      <AsyncBoundary query={query} isEmpty={(data) => data.count === 0}>
        {(data) => <div>Count: {data.count}</div>}
      </AsyncBoundary>
    );

    expect(screen.getByText('Count: 5')).toBeInTheDocument();
  });

  it('does not show empty state for non-empty array', () => {
    const query = createMockQuery({ data: [1, 2, 3] });

    render(<AsyncBoundary query={query}>{(data) => <div>Count: {data.length}</div>}</AsyncBoundary>);

    expect(screen.getByText('Count: 3')).toBeInTheDocument();
    expect(screen.queryByText('No data')).not.toBeInTheDocument();
  });

  it('handles null data as empty', () => {
    const query = createMockQuery({ data: null });

    render(
      <AsyncBoundary query={query} emptyFallback={<div>Nothing here</div>}>
        {() => <div>Content</div>}
      </AsyncBoundary>
    );

    expect(screen.getByText('Nothing here')).toBeInTheDocument();
  });

  it('prioritizes loading state over error state', () => {
    // In real TanStack Query, both wouldn't be true, but test the priority
    const query = createMockQuery({ isLoading: true, error: new Error('Error') });

    render(<AsyncBoundary query={query}>{() => <div>Content</div>}</AsyncBoundary>);

    expect(screen.queryByText('Something went wrong')).not.toBeInTheDocument();
  });
});
