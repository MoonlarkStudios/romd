import { IconSearch } from '@tabler/icons-react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen, userEvent } from '../../test/utils';
import { EmptyState } from './EmptyState';

describe('EmptyState', () => {
  it('renders default title when no props provided', () => {
    render(<EmptyState />);
    expect(screen.getByText('No data')).toBeInTheDocument();
  });

  it('renders custom title and description', () => {
    render(<EmptyState title="No items found" description="Try adjusting your filters" />);

    expect(screen.getByText('No items found')).toBeInTheDocument();
    expect(screen.getByText('Try adjusting your filters')).toBeInTheDocument();
  });

  it('renders action button and calls onClick', async () => {
    const user = userEvent.setup();
    const onClick = vi.fn();

    render(<EmptyState title="Empty" action={{ label: 'Add item', onClick }} />);

    const button = screen.getByRole('button', { name: /add item/i });
    expect(button).toBeInTheDocument();

    await user.click(button);
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('renders secondary action button', async () => {
    const user = userEvent.setup();
    const primaryClick = vi.fn();
    const secondaryClick = vi.fn();

    render(
      <EmptyState
        title="Empty"
        action={{ label: 'Primary', onClick: primaryClick }}
        secondaryAction={{ label: 'Secondary', onClick: secondaryClick }}
      />
    );

    expect(screen.getByRole('button', { name: /primary/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /secondary/i })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /secondary/i }));
    expect(secondaryClick).toHaveBeenCalledTimes(1);
  });

  it('renders custom icon', () => {
    render(<EmptyState icon={<IconSearch data-testid="custom-icon" />} title="Search" />);

    expect(screen.getByTestId('custom-icon')).toBeInTheDocument();
  });

  it('renders children content', () => {
    render(
      <EmptyState title="Empty">
        <div data-testid="custom-content">Custom content here</div>
      </EmptyState>
    );

    expect(screen.getByTestId('custom-content')).toBeInTheDocument();
    expect(screen.getByText('Custom content here')).toBeInTheDocument();
  });

  it('renders action with icon', () => {
    render(
      <EmptyState
        title="Empty"
        action={{
          label: 'Upload',
          onClick: vi.fn(),
          icon: <IconSearch data-testid="action-icon" size={14} />,
        }}
      />
    );

    expect(screen.getByTestId('action-icon')).toBeInTheDocument();
  });

  it('does not render action section when no actions provided', () => {
    render(<EmptyState title="Empty" />);

    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });
});
