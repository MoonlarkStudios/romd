import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { StatusState } from './StatusState';

describe('StatusState', () => {
  it('announces loading with a polite status role', () => {
    render(
      <StatusState
        kind="loading"
        label="Loading your library"
      />,
    );

    expect(screen.getByRole('status')).toHaveTextContent('Loading your library');
  });

  it('surfaces errors with an alert role and retries on demand', async () => {
    const onRetry = vi.fn();
    render(
      <StatusState
        kind="error"
        message="The catalog could not be reached."
        onRetry={onRetry}
      />,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('The catalog could not be reached.');
    await userEvent.click(screen.getByRole('button', { name: /try again/i }));
    expect(onRetry).toHaveBeenCalledOnce();
  });

  it('hides the retry action when no handler is given', () => {
    render(
      <StatusState
        kind="error"
        message="The catalog could not be reached."
      />,
    );

    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('renders empty states with title, message, and action', () => {
    render(
      <StatusState
        kind="empty"
        title="No games match your filters"
        message="Try clearing a filter or widening your search."
        action={<button type="button">Clear all</button>}
      />,
    );

    expect(screen.getByText('No games match your filters')).toBeInTheDocument();
    expect(screen.getByText('Try clearing a filter or widening your search.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Clear all' })).toBeInTheDocument();
  });
});
