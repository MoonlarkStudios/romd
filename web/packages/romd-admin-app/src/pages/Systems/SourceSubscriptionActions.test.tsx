import { MantineProvider } from '@mantine/core';
import type { DatCatalogSubscription } from '@romd/admin-api-client';
import { checkDatSubscription, getDatSubscription } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { SourceSubscriptionActions } from './SourceSubscriptionActions';

vi.mock('@romd/admin-api-client', () => ({
  getDatSubscription: vi.fn(),
  checkDatSubscription: vi.fn(),
  deleteDatSubscription: vi.fn(),
}));
vi.mock('./DatReplacementReviewModal', () => ({
  DatReplacementReviewModal: () => null,
}));

it.each([
  [
    'UpToDate',
    false,
    /Next automatic check:/,
  ],
  [
    'CheckFailed',
    false,
    /Automatic retry:/,
  ],
  [
    'UpdateAvailable',
    false,
    /Waiting for your review/,
  ],
  [
    'UpToDate',
    true,
    /Automatic checks are paused/,
  ],
] as const)('explains the schedule for %s (paused: %s)', async (state, paused, copy) => {
  const subscription: DatCatalogSubscription = {
    id: 'subscription',
    catalogId: 'redump/psx/discs',
    systemId: 'psx',
    systemKey: 'psx',
    activeDatId: 'dat',
    lastCheckedAt: null,
    message: null,
    candidateSha256: null,
    jobId: null,
    name: 'PlayStation',
    state,
    automaticChecksPaused: paused,
    nextCheckAt: '2026-09-07T12:00:00Z',
  };
  vi.mocked(getDatSubscription).mockResolvedValue({
    data: subscription,
  } as Awaited<ReturnType<typeof getDatSubscription>>);
  render(
    <MantineProvider>
      <SourceSubscriptionActions
        subscription={subscription}
        onAccepted={vi.fn()}
      />
    </MantineProvider>,
  );
  expect(await screen.findByText(copy)).toBeInTheDocument();
  if (state === 'UpdateAvailable')
    expect(screen.queryByText(/Next automatic check:/)).not.toBeInTheDocument();
});
it('checks the existing subscription by its id', async () => {
  const subscription: DatCatalogSubscription = {
    id: 'subscription',
    catalogId: 'redump/psx/discs',
    systemId: 'psx',
    systemKey: 'psx',
    activeDatId: 'dat',
    lastCheckedAt: null,
    message: null,
    candidateSha256: null,
    jobId: null,
    name: 'PlayStation',
    state: 'UpToDate',
  };
  vi.mocked(getDatSubscription).mockResolvedValue({
    data: subscription,
  } as Awaited<ReturnType<typeof getDatSubscription>>);
  vi.mocked(checkDatSubscription).mockResolvedValue({
    data: {
      ...subscription,
      state: 'UpdateAvailable',
    },
  } as Awaited<ReturnType<typeof checkDatSubscription>>);
  render(
    <MantineProvider>
      <SourceSubscriptionActions
        subscription={subscription}
        onAccepted={vi.fn()}
      />
    </MantineProvider>,
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Check for updates',
    }),
  );
  await waitFor(() =>
    expect(checkDatSubscription).toHaveBeenCalledWith({
      path: {
        subscriptionId: 'subscription',
      },
    }),
  );
  expect(await screen.findByText('Update available')).toBeInTheDocument();
});
