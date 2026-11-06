import { screen } from '@testing-library/react';
import { expect, it } from 'vitest';
import { render } from '../../test/utils/render';
import { HealthIndicator } from './HealthIndicator';

it('takes reported issues to ROMs needing attention', () => {
  render(
    <HealthIndicator
      health={{
        totalRoms: 10,
        identifiedCount: 8,
        unidentifiedCount: 2,
        routedCount: 9,
        unroutedCount: 1,
        failedJobCount: 0,
      }}
      isLoading={false}
      isError={false}
    />,
  );
  expect(
    screen.getByRole('link', {
      name: 'Library: needs attention',
    }),
  ).toHaveAttribute('href', '/roms?tab=attention');
});
