import { listDatCatalogs, listDatSubscriptions, listManagedSystems } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import { completePage } from '../../test/fixtures/referencePages';
import { render } from '../../test/utils/render';
import { SystemSetupSummary } from './SystemSetupSummary';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  listDatCatalogs: vi.fn(),
  listDatSubscriptions: vi.fn(),
  listManagedSystems: vi.fn(),
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    role: 'Admin',
    canManageSources: true,
  }),
}));
const result = <T,>(data: T) =>
  ({
    data,
  }) as never;
beforeEach(() => {
  vi.mocked(listManagedSystems).mockResolvedValue(
    result(
      completePage([
        {
          key: 'one',
          name: 'PlayStation',
          enabled: true,
          state: 'Ready',
          catalogCount: 2,
          ownedTitles: 3,
          knownTitles: 223,
        },
      ]),
    ),
  );
  vi.mocked(listDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      message: null,
      catalogs: [],
    }),
  );
  vi.mocked(listDatSubscriptions).mockResolvedValue(result([]));
});
it('summarizes multiple sources without assigning one update method to the system', async () => {
  render(<SystemSetupSummary systemKey="one" />);
  expect(await screen.findByText('Catalog ready')).toBeInTheDocument();
  expect(screen.getByText('2 DATs · 223 known titles · 3 titles with files')).toBeInTheDocument();
  expect(screen.queryByText('Manual upload')).not.toBeInTheDocument();
});
it('keeps a ready system available when one subscription check fails', async () => {
  vi.mocked(listDatSubscriptions).mockResolvedValue(
    result([
      {
        id: 'sub',
        systemKey: 'one',
        state: 'CheckFailed',
      },
      {
        id: 'second',
        systemKey: 'one',
        activeDatId: 'installed',
        state: 'UpdateAvailable',
      },
    ]),
  );
  render(<SystemSetupSummary systemKey="one" />);
  expect(await screen.findByText('1 source needs attention')).toBeInTheDocument();
  expect(screen.getByText('Catalog ready')).toBeInTheDocument();
  expect(screen.getByText('1 update to review')).toBeInTheDocument();
  expect(screen.queryByText('Needs attention')).not.toBeInTheDocument();
});
it('distinguishes a first DAT review from an update to an installed DAT', async () => {
  vi.mocked(listDatSubscriptions).mockResolvedValue(
    result([
      {
        id: 'first',
        systemKey: 'one',
        state: 'ReadyToImport',
      },
    ]),
  );
  render(<SystemSetupSummary systemKey="one" />);
  expect(
    await screen.findByRole('link', {
      name: 'First DAT ready for review',
    }),
  ).toHaveAttribute('href', '/systems/one?tab=sources');
  expect(screen.queryByText('1 update to review')).not.toBeInTheDocument();
});
it('marks subscription status unavailable when local subscriptions cannot be read', async () => {
  vi.mocked(listDatSubscriptions).mockResolvedValue({
    error: {},
  } as never);
  render(<SystemSetupSummary systemKey="one" />);
  expect(await screen.findByText('Subscription status unavailable')).toBeInTheDocument();
});
