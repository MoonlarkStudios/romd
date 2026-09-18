import type { ManagedSystemDto } from '@romd/admin-api-client';
import { discoverDatCatalogs, listManagedSystems } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { Systems } from './index';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  listManagedSystems: vi.fn(),
  discoverDatCatalogs: vi.fn(),
}));
vi.mock('./AddSystemFlow', async () => ({
  ...(await vi.importActual('./AddSystemFlow')),
  AddSystemFlow: () => <div>Guided setup</div>,
}));
const systems: ManagedSystemDto[] = [
  {
    key: 'psx',
    name: 'PlayStation',
    shortName: 'psx',
    manufacturer: 'Sony',
    aliases: [
      'PS1',
    ],
    enabled: true,
    state: 'Ready',
    message: null,
    catalogCount: 1,
    ownedTitles: 4,
    trackedTitles: 2,
  },
  {
    key: 'snes',
    name: 'Super Nintendo',
    shortName: 'snes',
    manufacturer: 'Nintendo',
    aliases: [],
    enabled: false,
    state: 'NeedsCatalog',
    message: null,
    catalogCount: 0,
    ownedTitles: 0,
  },
];
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(discoverDatCatalogs).mockResolvedValue({ data: { catalogs: [], subscriptions: [] } } as never);
  vi.mocked(listManagedSystems).mockResolvedValue({
    data: systems,
  } as Awaited<ReturnType<typeof listManagedSystems>>);
});
it('shows enabled systems only, owned counts and readiness', async () => {
  render(<Systems />);
  expect(
    await screen.findByRole('link', {
      name: 'PlayStation',
    }),
  ).toHaveAttribute('href', '/systems/psx');
  expect(screen.queryByText('Super Nintendo')).not.toBeInTheDocument();
  expect(screen.getByText('Catalog ready')).toBeInTheDocument();
  expect(screen.getByText(/2 tracked · 4 with files/)).toBeInTheDocument();
  expect(screen.getByRole('table', { name: 'Systems' })).toBeInTheDocument();
});
it('surfaces pending updates separately from catalog readiness and filters attention', async () => {
  vi.mocked(discoverDatCatalogs).mockResolvedValue({ data: { catalogs: [], subscriptions: [{ systemKey: 'psx', state: 'UpdateAvailable' }] } } as never);
  render(<Systems />);
  expect(await screen.findByRole('link', { name: '1 update to review' })).toHaveAttribute('href', '/systems/psx?tab=sources');
  await userEvent.click(screen.getByRole('checkbox', { name: 'Needs attention' }));
  expect(screen.getByText('PlayStation')).toBeInTheDocument();
  expect(screen.getByText('Catalog ready')).toBeInTheDocument();
});
it('does not present unavailable subscription data as a clean status', async () => {
  vi.mocked(discoverDatCatalogs).mockResolvedValue({ error: {} } as never);
  render(<Systems />);
  expect(await screen.findByText('Status unavailable')).toBeInTheDocument();
  expect(screen.getByRole('checkbox', { name: 'Needs attention' })).toBeDisabled();
  expect(screen.queryByText('No pending reviews')).not.toBeInTheDocument();
});
it('searches familiar aliases within your systems', async () => {
  const user = userEvent.setup();
  render(<Systems />);
  await screen.findByText('PlayStation');
  await user.type(screen.getByLabelText('Filter your systems'), 'PS1');
  expect(screen.getByText('PlayStation')).toBeInTheDocument();
});
it('offers an intentional first-system empty state despite populated supported registry', async () => {
  vi.mocked(listManagedSystems).mockResolvedValue({
    data: systems.map((s) => ({
      ...s,
      enabled: false,
    })),
  } as Awaited<ReturnType<typeof listManagedSystems>>);
  const user = userEvent.setup();
  render(<Systems />);
  await user.click(
    await screen.findByRole('button', {
      name: 'Choose your first system',
    }),
  );
  expect(screen.getByText('Guided setup')).toBeInTheDocument();
});
