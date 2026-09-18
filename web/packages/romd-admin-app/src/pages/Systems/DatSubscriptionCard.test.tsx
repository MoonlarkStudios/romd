import { MantineProvider } from '@mantine/core';
import type { Dat, DatSubscriptionStatus } from '@romd/admin-api-client';
import { checkDatSubscription, getDatSubscription } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { DatSubscriptionCard } from './DatSubscriptionCard';

vi.mock('@romd/admin-api-client', () => ({
  getDatSubscription: vi.fn(),
  checkDatSubscription: vi.fn(),
}));
vi.mock('./DatReplacementReviewModal', () => ({
  DatReplacementReviewModal: () => <div>Saved candidate review</div>,
}));
const dat = {
  id: 'psx',
  name: 'Sony - PlayStation',
} as Dat;
const initial: DatSubscriptionStatus = {
  available: true,
  subscribed: false,
  state: 'NotSubscribed',
};
function response(status: DatSubscriptionStatus) {
  return {
    data: status,
  } as Awaited<ReturnType<typeof getDatSubscription>>;
}
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(getDatSubscription).mockResolvedValue(response(initial));
});
it('enrolls explicitly and opens the saved review only on request', async () => {
  const user = userEvent.setup();
  vi.mocked(checkDatSubscription).mockResolvedValue(
    response({
      ...initial,
      subscribed: true,
      state: 'UpdateAvailable',
    }),
  );
  render(
    <MantineProvider>
      <DatSubscriptionCard
        dat={dat}
        onAccepted={vi.fn()}
      />
    </MantineProvider>,
  );
  await user.click(
    await screen.findByRole('button', {
      name: 'Use subscription',
    }),
  );
  expect(await screen.findByText('Update available')).toBeInTheDocument();
  expect(screen.queryByText('Saved candidate review')).not.toBeInTheDocument();
  await user.click(
    screen.getByRole('button', {
      name: 'Review update',
    }),
  );
  expect(screen.getByText('Saved candidate review')).toBeInTheDocument();
});
it('shows a failed check and lets the user retry without activating', async () => {
  const user = userEvent.setup();
  vi.mocked(checkDatSubscription)
    .mockResolvedValueOnce(
      response({
        ...initial,
        subscribed: true,
        state: 'CheckFailed',
        message: 'Current catalog unchanged.',
      }),
    )
    .mockResolvedValueOnce(
      response({
        ...initial,
        subscribed: true,
        state: 'UpToDate',
      }),
    );
  const accepted = vi.fn();
  render(
    <MantineProvider>
      <DatSubscriptionCard
        dat={dat}
        onAccepted={accepted}
      />
    </MantineProvider>,
  );
  await user.click(
    await screen.findByRole('button', {
      name: 'Use subscription',
    }),
  );
  expect(await screen.findByText('Current catalog unchanged.')).toBeInTheDocument();
  await user.click(
    screen.getByRole('button', {
      name: 'Retry check',
    }),
  );
  expect(await screen.findByText('Up to date')).toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: 'Review update',
    }),
  ).not.toBeInTheDocument();
  expect(accepted).not.toHaveBeenCalled();
});

it('offers status retry after a failed initial read', async () => {
  vi.mocked(getDatSubscription)
    .mockRejectedValueOnce(new Error('offline'))
    .mockResolvedValueOnce(response(initial));
  const user = userEvent.setup();
  render(
    <MantineProvider>
      <DatSubscriptionCard
        dat={dat}
        onAccepted={vi.fn()}
      />
    </MantineProvider>,
  );
  await user.click(
    await screen.findByRole('button', {
      name: 'Retry status',
    }),
  );
  expect(
    await screen.findByRole('button', {
      name: 'Use subscription',
    }),
  ).toBeInTheDocument();
  expect(checkDatSubscription).not.toHaveBeenCalled();
});
