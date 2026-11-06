import { MantineProvider } from '@mantine/core';
import { createDatSubscription } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render as renderWithClient, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { managedSystemKeys } from '../../hooks/api/useManagedSystems';
import { render } from '../../test/utils/render';
import { DatSubscriptionCard } from './DatSubscriptionCard';

vi.mock('@romd/admin-api-client', () => ({
  createDatSubscription: vi.fn(),
}));
beforeEach(() => {
  vi.resetAllMocks();
});
function renderCard() {
  render(
    <MantineProvider>
      <DatSubscriptionCard
        dat={{
          id: 'dat',
        }}
        catalogId="redump/psx/discs"
      />
    </MantineProvider>,
  );
}
it('subscribes this source explicitly by its catalog and DAT', async () => {
  const user = userEvent.setup();
  vi.mocked(createDatSubscription).mockResolvedValue({
    data: {
      id: 'sub',
      catalogId: 'redump/psx/discs',
      systemId: 'psx',
      systemKey: 'psx',
      activeDatId: 'dat',
      lastCheckedAt: null,
      message: null,
      candidateSha256: null,
      jobId: null,
      name: 'Sony - PlayStation',
      state: 'UpToDate',
    },
  } as Awaited<ReturnType<typeof createDatSubscription>>);
  renderCard();
  await user.click(
    screen.getByRole('button', {
      name: 'Use subscription',
    }),
  );
  await waitFor(() =>
    expect(createDatSubscription).toHaveBeenCalledWith({
      body: {
        catalogId: 'redump/psx/discs',
        datId: 'dat',
      },
    }),
  );
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
});
it('shows why the source could not be subscribed and keeps it on manual updates', async () => {
  const user = userEvent.setup();
  vi.mocked(createDatSubscription).mockResolvedValue({
    error: {
      errorCode: 'DatEnrollment.Ambiguous',
      detail: 'More than one local source matches this catalog.',
      traceId: 'trace',
    },
  } as Awaited<ReturnType<typeof createDatSubscription>>);
  renderCard();
  await user.click(
    screen.getByRole('button', {
      name: 'Use subscription',
    }),
  );
  expect(
    await screen.findByText('More than one local source matches this catalog.'),
  ).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Use subscription',
    }),
  ).toBeEnabled();
});
it('explains when the catalog is already tracked by a pending subscription without this source', async () => {
  const user = userEvent.setup();
  vi.mocked(createDatSubscription).mockResolvedValue({
    data: {
      id: 'pending',
      catalogId: 'redump/psx/discs',
      systemId: 'psx',
      systemKey: 'psx',
      activeDatId: null,
      lastCheckedAt: null,
      message: null,
      candidateSha256: 'a'.repeat(64),
      jobId: null,
      name: 'Sony - PlayStation',
      state: 'ReadyToImport',
    },
  } as Awaited<ReturnType<typeof createDatSubscription>>);
  renderCard();
  await user.click(
    screen.getByRole('button', {
      name: 'Use subscription',
    }),
  );
  expect(
    await screen.findByText(
      'This catalog is already tracked by a pending subscription. Review or remove it from Sources first.',
    ),
  ).toBeInTheDocument();
});
it('refreshes the local subscriptions after subscribing so the source shows its new subscription', async () => {
  const user = userEvent.setup();
  vi.mocked(createDatSubscription).mockResolvedValue({
    data: {
      id: 'sub',
      catalogId: 'redump/psx/discs',
      systemId: 'psx',
      systemKey: 'psx',
      activeDatId: 'dat',
      lastCheckedAt: null,
      message: null,
      candidateSha256: null,
      jobId: null,
      name: 'Sony - PlayStation',
      state: 'UpToDate',
    },
  } as Awaited<ReturnType<typeof createDatSubscription>>);
  const client = new QueryClient();
  const invalidate = vi.spyOn(client, 'invalidateQueries');
  renderWithClient(
    <QueryClientProvider client={client}>
      <MantineProvider>
        <DatSubscriptionCard
          dat={{
            id: 'dat',
          }}
          catalogId="redump/psx/discs"
        />
      </MantineProvider>
    </QueryClientProvider>,
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Use subscription',
    }),
  );
  await waitFor(() =>
    expect(invalidate).toHaveBeenCalledWith({
      queryKey: managedSystemKeys.subscriptions,
    }),
  );
});
