import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { render, screen, userEvent } from '../../test/utils';
import { ErrorBoundary, ErrorFallback } from './index';

// Component that throws an error
function ThrowingComponent({ shouldThrow = true }: { shouldThrow?: boolean }) {
  if (shouldThrow) {
    throw new Error('Test error message');
  }
  return <div>Success content</div>;
}

describe('ErrorBoundary', () => {
  // Suppress React error boundary console output during tests
  const originalError = console.error;
  beforeAll(() => {
    console.error = vi.fn();
  });
  afterAll(() => {
    console.error = originalError;
  });

  it('renders children when no error occurs', () => {
    render(
      <ErrorBoundary fallback={<div>Error occurred</div>}>
        <div>Content</div>
      </ErrorBoundary>
    );

    expect(screen.getByText('Content')).toBeInTheDocument();
    expect(screen.queryByText('Error occurred')).not.toBeInTheDocument();
  });

  it('renders fallback when error occurs', () => {
    render(
      <ErrorBoundary fallback={<div>Error occurred</div>}>
        <ThrowingComponent />
      </ErrorBoundary>
    );

    expect(screen.getByText('Error occurred')).toBeInTheDocument();
    expect(screen.queryByText('Success content')).not.toBeInTheDocument();
  });

  it('renders FallbackComponent with error and reset props', () => {
    render(
      <ErrorBoundary FallbackComponent={ErrorFallback}>
        <ThrowingComponent />
      </ErrorBoundary>
    );

    expect(screen.getByText('Something went wrong')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /try again/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /show details/i })).toBeInTheDocument();
  });

  it('calls onError callback when error occurs', () => {
    const onError = vi.fn();

    render(
      <ErrorBoundary fallback={<div>Error</div>} onError={onError}>
        <ThrowingComponent />
      </ErrorBoundary>
    );

    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError).toHaveBeenCalledWith(
      expect.objectContaining({ message: 'Test error message' }),
      expect.objectContaining({ componentStack: expect.any(String) })
    );
  });

  it('resets error state when Try again button is clicked', async () => {
    const user = userEvent.setup();

    const { rerender } = render(
      <ErrorBoundary FallbackComponent={ErrorFallback}>
        <ThrowingComponent shouldThrow={true} />
      </ErrorBoundary>
    );

    expect(screen.getByText('Something went wrong')).toBeInTheDocument();

    // Re-render with non-throwing component before clicking retry
    rerender(
      <ErrorBoundary FallbackComponent={ErrorFallback}>
        <ThrowingComponent shouldThrow={false} />
      </ErrorBoundary>
    );

    await user.click(screen.getByRole('button', { name: /try again/i }));

    expect(screen.getByText('Success content')).toBeInTheDocument();
    expect(screen.queryByText('Something went wrong')).not.toBeInTheDocument();
  });

  it('resets when resetKeys change', () => {
    const { rerender } = render(
      <ErrorBoundary fallback={<div>Error</div>} resetKeys={[1]}>
        <ThrowingComponent shouldThrow={true} />
      </ErrorBoundary>
    );

    expect(screen.getByText('Error')).toBeInTheDocument();

    // Rerender with different resetKey and non-throwing component
    rerender(
      <ErrorBoundary fallback={<div>Error</div>} resetKeys={[2]}>
        <ThrowingComponent shouldThrow={false} />
      </ErrorBoundary>
    );

    expect(screen.getByText('Success content')).toBeInTheDocument();
    expect(screen.queryByText('Error')).not.toBeInTheDocument();
  });

  it('calls onReset when error boundary resets', async () => {
    const user = userEvent.setup();
    const onReset = vi.fn();

    const { rerender } = render(
      <ErrorBoundary FallbackComponent={ErrorFallback} onReset={onReset}>
        <ThrowingComponent shouldThrow={true} />
      </ErrorBoundary>
    );

    // Re-render with non-throwing component
    rerender(
      <ErrorBoundary FallbackComponent={ErrorFallback} onReset={onReset}>
        <ThrowingComponent shouldThrow={false} />
      </ErrorBoundary>
    );

    await user.click(screen.getByRole('button', { name: /try again/i }));

    expect(onReset).toHaveBeenCalledTimes(1);
  });
});

describe('ErrorFallback', () => {
  it('displays custom title and description', () => {
    render(
      <ErrorFallback
        error={new Error('Test')}
        resetErrorBoundary={vi.fn()}
        title="Custom title"
        description="Custom description"
      />
    );

    expect(screen.getByText('Custom title')).toBeInTheDocument();
    expect(screen.getByText('Custom description')).toBeInTheDocument();
  });

  it('toggles error details visibility', async () => {
    const user = userEvent.setup();
    const error = new Error('Detailed error message');

    render(<ErrorFallback error={error} resetErrorBoundary={vi.fn()} />);

    // Details toggle should show "Show details" initially
    expect(screen.getByRole('button', { name: /show details/i })).toBeInTheDocument();

    // Click to show details
    await user.click(screen.getByRole('button', { name: /show details/i }));
    expect(screen.getByText(/Detailed error message/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /hide details/i })).toBeInTheDocument();

    // Click to hide details
    await user.click(screen.getByRole('button', { name: /hide details/i }));
    expect(screen.getByRole('button', { name: /show details/i })).toBeInTheDocument();
  });

  it('hides details toggle when showDetails is false', () => {
    render(<ErrorFallback error={new Error('Test')} resetErrorBoundary={vi.fn()} showDetails={false} />);

    expect(screen.queryByRole('button', { name: /show details/i })).not.toBeInTheDocument();
  });
});
