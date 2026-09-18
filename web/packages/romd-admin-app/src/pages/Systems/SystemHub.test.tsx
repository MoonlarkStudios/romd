import {
  applyReviewedDatReplacement,
  discoverDatCatalogs,
  getAdminSystems,
  getCatalogSubscription,
  getJobById,
  listDatsByPlatform,
  listManagedSystems,
  listPlatformAliases,
  listSourceEntries,
  previewDatReplacement,
  searchCatalog,
} from '@romd/admin-api-client';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { SourceWorkspace } from './SourceWorkspace';
import { SystemHub } from './SystemHub';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getAdminSystems: vi.fn(),
  listPlatformAliases: vi.fn(),
  listDatsByPlatform: vi.fn(),
  listSourceEntries: vi.fn(),
  listManagedSystems: vi.fn(),
  discoverDatCatalogs: vi.fn(),
  getCatalogSubscription: vi.fn(),
  previewDatReplacement: vi.fn(),
  applyReviewedDatReplacement: vi.fn(),
  getJobById: vi.fn(),
  searchCatalog: vi.fn(),
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canManageSources: true,
  }),
}));
const result = <T,>(data: T) =>
  ({
    data,
  }) as never;
const manual = {
  id: 'manual',
  sourceId: 'source2',
  name: 'Community translations',
  systemKey: 'psx',
  sourceStatus: 'Active',
  version: 'v3',
  gameCount: '25',
  romCount: '26',
  type: 'Other',
};
const redump = {
  ...manual,
  id: 'redump',
  sourceId: 'source1',
  name: 'Sony - PlayStation',
  version: '2026',
  gameCount: '10974',
};
const sub = {
  id: 'sub',
  catalogId: 'redump/psx/discs',
  systemKey: 'psx',
  activeDatId: 'redump',
  name: 'Redump',
  state: 'UpdateAvailable',
};
function hub(url = '/systems/psx?tab=sources') {
  render(
    <Routes>
      <Route path="/systems/:systemKey/sources/:sourceId" element={<SourceWorkspace />} />
      <Route
        path="/systems/:systemKey"
        element={<SystemHub />}
      />
    </Routes>,
    {
      routerOptions: {
        initialEntries: [
          url,
        ],
      },
    },
  );
}
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(searchCatalog).mockResolvedValue(result({ items: [], hasNextPage: false }));
  vi.mocked(getAdminSystems).mockResolvedValue(
    result({
      key: 'psx',
      name: 'PlayStation',
      compactLabel: 'psx',
      manufacturer: 'Sony',
    }),
  );
  vi.mocked(listPlatformAliases).mockResolvedValue(
    result([
      {
        type: 'name',
        value: 'PS1',
      },
    ]),
  );
  vi.mocked(listManagedSystems).mockResolvedValue(
    result([
      {
        key: 'psx',
        name: 'PlayStation',
        shortName: 'psx',
        aliases: [
          'PS1',
        ],
        enabled: true,
        state: 'Ready',
        catalogCount: 2,
        ownedTitles: 3,
      },
    ]),
  );
  vi.mocked(listDatsByPlatform).mockResolvedValue(
    result([
      redump,
      manual,
    ]),
  );
  vi.mocked(discoverDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      catalogs: [],
      subscriptions: [
        sub,
      ],
    }),
  );
  vi.mocked(getCatalogSubscription).mockResolvedValue(result(sub));
  vi.mocked(listSourceEntries).mockResolvedValue(
    result({
      items: [],
      hasNextPage: false,
    }),
  );
});
it('lists mixed sources and does not eagerly request any entry pages', async () => {
  hub();
  const redumpRow = await screen.findByRole('article', {
    name: redump.name,
  });
  const manualRow = screen.getByRole('article', {
    name: manual.name,
  });
  expect(within(redumpRow).getByText('Subscription')).toBeInTheDocument();
  expect(
    within(redumpRow).getByRole('button', {
      name: 'Review update',
    }),
  ).toBeInTheDocument();
  expect(within(manualRow).getByText('Manual updates')).toBeInTheDocument();
  expect(
    within(manualRow).getByRole('link', {
      name: manual.name,
    }),
  ).toBeInTheDocument();
  expect(screen.getByText('Catalog ready')).toBeInTheDocument();
  expect(listSourceEntries).not.toHaveBeenCalled();
});
it('defaults to tracked titles and keeps the all-title escape scoped to this system', async () => {
  hub('/systems/psx');
  expect(await screen.findByRole('tab', { name: 'Titles' })).toHaveAttribute('aria-selected', 'true');
  const browse = await screen.findByRole('link', { name: 'Browse all titles' });
  expect(browse).toHaveAttribute('href', '/systems/psx?tab=titles&view=all');
  expect(searchCatalog).toHaveBeenCalledWith(expect.objectContaining({ query: expect.objectContaining({ systemKey: 'psx', tracked: 'tracked' }) }));
  await userEvent.click(screen.getByRole('radio', { name: 'All titles' }));
  await waitFor(() => expect(searchCatalog).toHaveBeenLastCalledWith(expect.objectContaining({ query: expect.objectContaining({ systemKey: 'psx', tracked: 'all' }) })));
  expect(screen.queryByRole('tab', { name: 'Collection' })).not.toBeInTheDocument();
});
it('opens only the requested source entries and preserves the deep link', async () => {
  hub('/systems/psx?tab=sources&dat=manual&sourceTitle=title');
  await screen.findByRole('heading', {
    name: manual.name,
  });
  await waitFor(() =>
    expect(listSourceEntries).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          datId: 'manual',
        },
        query: expect.objectContaining({ titleId: 'title' }),
      }),
    ),
  );
  expect(
    vi.mocked(listSourceEntries).mock.calls.every(([arg]) => arg?.path.datId === 'manual'),
  ).toBe(true);
});
it('loads entries on demand when a source is opened', async () => {
  const user = userEvent.setup();
  hub();
  await user.click(
    await screen.findByRole('link', {
      name: manual.name,
    }),
  );
  await waitFor(() =>
    expect(listSourceEntries).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          datId: 'manual',
        },
      }),
    ),
  );
});
it('opens the current DAT through a stable source URL after replacement', async () => {
  vi.mocked(listDatsByPlatform).mockResolvedValue(result([{ ...manual, id: 'new-dat', version: 'v4' }]));
  hub('/systems/psx/sources/source2');
  expect(await screen.findByText('Installed DAT: v4')).toBeInTheDocument();
  await waitFor(() => expect(listSourceEntries).toHaveBeenCalledWith(expect.objectContaining({ path: { datId: 'new-dat' } })));
});
it('keeps updates and settings addressable without fetching entries', async () => {
  hub('/systems/psx/sources/source1?tab=settings');
  expect(await screen.findByRole('button', { name: 'Switch to manual updates' })).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Review update' })).not.toBeInTheDocument();
  expect(listSourceEntries).not.toHaveBeenCalled();
  await userEvent.click(screen.getByRole('tab', { name: 'Updates' }));
  expect(await screen.findByRole('button', { name: 'Review update' })).toBeInTheDocument();
  expect(listSourceEntries).not.toHaveBeenCalled();
});
it('handles a removed source without falling back to an unrelated DAT', async () => {
  hub('/systems/psx/sources/missing');
  expect(await screen.findByText('Source not found')).toBeInTheDocument();
  expect(listSourceEntries).not.toHaveBeenCalled();
});
it('shows source query failures with a retry instead of an empty collection', async () => {
  vi.mocked(listDatsByPlatform).mockResolvedValue({
    error: {
      status: 500,
    },
  } as never);
  hub();
  expect(await screen.findByText('Sources could not be loaded')).toBeInTheDocument();
  expect(screen.queryByText('Add your first catalog source')).not.toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Retry sources',
    }),
  ).toBeInTheDocument();
});
it('shows first-source guidance independently of adding the system', async () => {
  vi.mocked(listDatsByPlatform).mockResolvedValue(result([]));
  vi.mocked(discoverDatCatalogs).mockResolvedValue(
    result({
      catalogs: [],
      subscriptions: [],
    }),
  );
  hub();
  expect(await screen.findByText('No sources yet')).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Choose a source',
    }),
  ).toBeEnabled();
});
it('keeps a pending subscription visible before its first DAT exists', async () => {
  vi.mocked(discoverDatCatalogs).mockResolvedValue(
    result({
      catalogs: [],
      subscriptions: [
        {
          ...sub,
          activeDatId: null,
          state: 'ReadyToImport',
        },
      ],
    }),
  );
  vi.mocked(getCatalogSubscription).mockResolvedValue(
    result({
      ...sub,
      activeDatId: null,
      state: 'ReadyToImport',
    }),
  );
  hub();
  expect(await screen.findByText('No installed DAT')).toBeInTheDocument();
  expect(
    await screen.findByRole('button', {
      name: 'Review source',
    }),
  ).toBeInTheDocument();
});
it('updates the chosen manual source only after a hash-bound review', async () => {
  vi.mocked(previewDatReplacement).mockResolvedValue(
    result({
      activeSha256: 'before',
      candidateSha256: 'after',
      activeVersion: '3',
      candidateVersion: '4',
      unchanged: false,
      activeEntries: 1,
      candidateEntries: 2,
      entriesAdded: 1,
      entriesRemoved: 0,
      entriesChanged: 0,
      filesAdded: 1,
      filesRemoved: 0,
      filesChanged: 0,
      hashesChanged: 0,
      activeBiosEntries: 0,
      candidateBiosEntries: 0,
      changes: [],
      truncated: false,
    }),
  );
  vi.mocked(applyReviewedDatReplacement).mockResolvedValue(
    result({
      jobId: 'job',
    }),
  );
  vi.mocked(getJobById).mockResolvedValue(
    result({
      id: 'job',
      phase: 'Completed',
      isTerminal: true,
      hasErrors: false,
    }),
  );
  const user = userEvent.setup();
  hub('/systems/psx/sources/source2?tab=updates');
  const file = new File(
    [
      '<datafile/>',
    ],
    'new.dat',
    {
      type: 'text/xml',
    },
  );
  const input = (await screen.findByTestId('dat-upload-zone')).querySelector('input');
  if (!input) throw new Error('No upload input');
  fireEvent.change(input, {
    target: {
      files: [
        file,
      ],
    },
  });
  const approve = await screen.findByRole('button', {
    name: 'Apply reviewed update',
  });
  expect(applyReviewedDatReplacement).not.toHaveBeenCalled();
  await user.click(approve);
  await waitFor(() =>
    expect(applyReviewedDatReplacement).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          datId: 'manual',
        },
        body: {
          file,
          activeSha256: 'before',
          candidateSha256: 'after',
        },
      }),
    ),
  );
});
