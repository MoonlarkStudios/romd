import { MantineProvider } from '@mantine/core';
import type { DatCatalogDirectoryDto, DatCatalogSubscriptionDto } from '@romd/admin-api-client';
import {
  checkCatalogSubscription,
  discoverDatCatalogs,
  getCatalogSubscription,
} from '@romd/admin-api-client';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { CatalogSubscriptions } from './CatalogSubscriptions';

vi.mock('@romd/admin-api-client', () => ({
  discoverDatCatalogs: vi.fn(),
  checkCatalogSubscription: vi.fn(),
  getCatalogSubscription: vi.fn(),
}));
vi.mock('./DatReplacementReviewModal', () => ({
  DatReplacementReviewModal: () => <div>Saved candidate review</div>,
}));
const directory: DatCatalogDirectoryDto = {
  enabled: true,
  message: null,
  catalogs: [
    {
      catalogId: 'redump/psx/discs',
      systemId: 'psx',
      name: 'Sony - PlayStation',
      provider: 'redump',
      health: 'healthy',
      documentHash: 'a'.repeat(64),
      entryCount: 10974,
      fileCount: 60444,
      lastChangedAt: null,
    },
  ],
  subscriptions: [],
};
const subscription: DatCatalogSubscriptionDto = {
  id: 'sub1',
  catalogId: 'redump/psx/discs',
  systemId: 'psx',
  name: 'Sony - PlayStation',
  systemKey: 'platform1',
  activeDatId: null,
  state: 'ReadyToImport',
  lastCheckedAt: null,
  message: null,
  candidateSha256: 'a'.repeat(64),
  jobId: null,
};
const response = (data: DatCatalogDirectoryDto) =>
  ({
    data,
  }) as Awaited<ReturnType<typeof discoverDatCatalogs>>;
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(discoverDatCatalogs).mockResolvedValue(response(directory));
});
function show() {
  render(
    <MantineProvider>
      <CatalogSubscriptions onClose={vi.fn()} />
    </MantineProvider>,
  );
}
it('checks a published catalog and requires a separate review before initial import', async () => {
  const user = userEvent.setup();
  vi.mocked(checkCatalogSubscription).mockResolvedValue({
    data: subscription,
  } as Awaited<ReturnType<typeof checkCatalogSubscription>>);
  show();
  await user.click(
    await screen.findByRole('button', {
      name: 'Subscribe and check',
    }),
  );
  expect(checkCatalogSubscription).toHaveBeenCalledWith({
    body: {
      catalogId: 'redump/psx/discs',
    },
  });
  expect(await screen.findByText('Ready for first import')).toBeInTheDocument();
  expect(screen.queryByText('Saved candidate review')).not.toBeInTheDocument();
  await user.click(
    screen.getByRole('button', {
      name: 'Review first import',
    }),
  );
  expect(screen.getByText('Saved candidate review')).toBeInTheDocument();
});
it('does not advertise seeded definitions as available catalogs', async () => {
  vi.mocked(discoverDatCatalogs).mockResolvedValue(
    response({
      ...directory,
      catalogs: [],
    }),
  );
  show();
  expect(await screen.findByText('No published catalogs yet')).toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: 'Subscribe and check',
    }),
  ).not.toBeInTheDocument();
});
it('keeps local subscription status and retry visible during publisher failure', async () => {
  vi.mocked(discoverDatCatalogs).mockResolvedValue(
    response({
      ...directory,
      catalogs: [],
      message: 'Publisher unavailable',
      subscriptions: [
        {
          ...subscription,
          state: 'CheckFailed',
          activeDatId: 'active1',
          message: 'Previous catalog retained',
        },
      ],
    }),
  );
  show();
  expect(await screen.findByText('Previous catalog retained')).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Retry check',
    }),
  ).toBeEnabled();
  expect(
    screen.queryByRole('button', {
      name: 'Review first import',
    }),
  ).not.toBeInTheDocument();
});

it('observes an accepted job automatically and stops when its catalog is active', async () => {
  const completed = {
    ...subscription,
    state: 'UpToDate',
    activeDatId: 'active1',
    jobId: 'job1',
  };
  vi.mocked(discoverDatCatalogs).mockResolvedValue(
    response({
      ...directory,
      subscriptions: [
        {
          ...subscription,
          state: 'Applying',
          jobId: 'job1',
        },
      ],
    }),
  );
  vi.mocked(getCatalogSubscription).mockResolvedValue({
    data: completed,
  } as Awaited<ReturnType<typeof getCatalogSubscription>>);
  const changed = vi.fn();
  render(
    <MantineProvider>
      <CatalogSubscriptions
        onClose={vi.fn()}
        onCatalogChanged={changed}
      />
    </MantineProvider>,
  );
  expect(await screen.findByText('Up to date')).toBeInTheDocument();
  expect(getCatalogSubscription).toHaveBeenCalledTimes(1);
  expect(changed).toHaveBeenCalledTimes(1);
  expect(checkCatalogSubscription).not.toHaveBeenCalled();
});

it('cancels job observation on close without starting an update check', async () => {
  vi.mocked(discoverDatCatalogs).mockResolvedValue(
    response({
      ...directory,
      subscriptions: [
        {
          ...subscription,
          state: 'Applying',
          jobId: 'job1',
        },
      ],
    }),
  );
  let finish!: (value: Awaited<ReturnType<typeof getCatalogSubscription>>) => void;
  vi.mocked(getCatalogSubscription).mockImplementation(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  const changed = vi.fn();
  const view = render(
    <MantineProvider>
      <CatalogSubscriptions
        onClose={vi.fn()}
        onCatalogChanged={changed}
      />
    </MantineProvider>,
  );
  await waitFor(() => expect(getCatalogSubscription).toHaveBeenCalledTimes(1));
  const signal = vi.mocked(getCatalogSubscription).mock.calls[0][0].signal;
  view.unmount();
  expect(signal?.aborted).toBe(true);
  await act(async () =>
    finish({
      data: {
        ...subscription,
        state: 'UpToDate',
        jobId: 'job1',
      },
    } as Awaited<ReturnType<typeof getCatalogSubscription>>),
  );
  expect(changed).not.toHaveBeenCalled();
  expect(checkCatalogSubscription).not.toHaveBeenCalled();
});
