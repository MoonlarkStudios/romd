import { MantineProvider } from '@mantine/core';
import type { DatCatalogSubscriptionDto } from '@romd/admin-api-client';
import { getCatalogSubscription } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { SourceSubscriptionActions } from './SourceSubscriptionActions';

vi.mock('@romd/admin-api-client', () => ({
  getCatalogSubscription: vi.fn(),
  checkCatalogSubscription: vi.fn(),
  stopCatalogSubscription: vi.fn(),
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
  const subscription: DatCatalogSubscriptionDto = {
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
  vi.mocked(getCatalogSubscription).mockResolvedValue({
    data: subscription,
  } as Awaited<ReturnType<typeof getCatalogSubscription>>);
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
