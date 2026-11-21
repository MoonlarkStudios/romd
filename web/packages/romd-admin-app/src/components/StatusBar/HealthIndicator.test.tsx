import { screen } from '@testing-library/react';
import { expect, it } from 'vitest';
import { render } from '../../test/utils/render';
import { HealthIndicator } from './HealthIndicator';

it.each([
  {
    unidentifiedCount: 2,
    unroutedCount: 1,
    label: '3 ROMs need attention',
  },
  {
    unidentifiedCount: 0,
    unroutedCount: 1,
    label: '1 unrouted ROM',
  },
  {
    unidentifiedCount: 2,
    unroutedCount: 0,
    label: '2 unidentified ROMs',
  },
])('shows $label and links to ROMs needing attention', ({
  unidentifiedCount,
  unroutedCount,
  label,
}) => {
  render(
    <HealthIndicator
      health={{
        totalRoms: 10,
        identifiedCount: 8,
        unidentifiedCount,
        routedCount: 9,
        unroutedCount,
        failedJobCount: 0,
      }}
      isLoading={false}
      isError={false}
    />,
  );
  expect(
    screen.getByRole('link', {
      name: `Library: ${label}`,
    }),
  ).toHaveAttribute('href', '/roms?tab=attention');
});
