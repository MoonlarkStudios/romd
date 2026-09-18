import type { SourceLifecycleStatus } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { render } from '../../test/utils/render';
import { SourceStatusBadge } from './SourceStatusBadge';

const cases: Array<{ status: SourceLifecycleStatus; color: string }> = [
  { status: 'Active', color: 'green' },
  { status: 'Discontinued', color: 'orange' },
  { status: 'Disabled', color: 'red' },
];

describe('SourceStatusBadge', () => {
  it.each(cases)('renders $status with the $color palette', ({ status, color }) => {
    render(<SourceStatusBadge status={status} />);

    const badge = screen.getByText(status);
    expect(badge).toBeInTheDocument();
    expect(badge.closest('.mantine-Badge-root')).toHaveStyle({
      '--badge-color': `var(--mantine-color-${color}-light-color)`,
    });
  });
});
