import {
  createDatReplacement,
  getAdminSystem,
  getDatSubscription,
  getJob,
  listCatalogTitles,
  listDatCatalogs,
  listDatSubscriptions,
  listManagedSystems,
  listSourceEntries,
  listSystemAliases,
  listSystemDats,
  previewArtworkCompletion,
  previewDatReplacement,
  queueArtworkCompletion,
  triggerSystemBulkEnrichment,
} from '@romd/admin-api-client';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { completePage } from '../../test/fixtures/referencePages';
import { render } from '../../test/utils/render';
import { SourceWorkspace } from './SourceWorkspace';
import { SystemHub } from './SystemHub';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getAdminSystem: vi.fn(),
  listSystemAliases: vi.fn(),
  listSystemDats: vi.fn(),
  listSourceEntries: vi.fn(),
  listManagedSystems: vi.fn(),
  listDatCatalogs: vi.fn(),
  listDatSubscriptions: vi.fn(),
  getDatSubscription: vi.fn(),
  previewDatReplacement: vi.fn(),
  createDatReplacement: vi.fn(),
  getJob: vi.fn(),
  listCatalogTitles: vi.fn(),
  getCatalogStats: vi.fn().mockResolvedValue({
    data: {
      totalCount: 1,
    },
  }),
  previewArtworkCompletion: vi.fn(),
  queueArtworkCompletion: vi.fn(),
  triggerSystemBulkEnrichment: vi.fn(),
}));
const access = vi.hoisted(() => ({
  canManageSources: true,
  canManageTitles: false,
  canTriggerEnrichment: false,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => access,
}));
const page = <T,>(items: T[]) => ({
  items,
  nextCursor: null,
  hasNextPage: false,
});
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
  gameCount: 25,
  romCount: 26,
  type: 'Other',
};
const redump = {
  ...manual,
  id: 'redump',
  sourceId: 'source1',
  name: 'Sony - PlayStation',
  version: '2026',
  gameCount: 10974,
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
      <Route
        path="/systems/:systemKey/sources/:sourceId"
        element={<SourceWorkspace />}
      />
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
  Object.assign(access, {
    canManageTitles: false,
    canTriggerEnrichment: false,
  });
  vi.resetAllMocks();
  vi.mocked(listCatalogTitles).mockResolvedValue(result(page([])));
  vi.mocked(getAdminSystem).mockResolvedValue(
    result({
      key: 'psx',
      name: 'PlayStation',
      compactLabel: 'psx',
      manufacturer: 'Sony',
    }),
  );
  vi.mocked(listSystemAliases).mockResolvedValue(
    result(
      page([
        {
          type: 'name',
          value: 'PS1',
        },
      ]),
    ),
  );
  vi.mocked(listManagedSystems).mockResolvedValue(
    result(
      completePage([
        {
          key: 'psx',
          name: 'PlayStation',
          shortName: 'psx',
          enabled: true,
          state: 'Ready',
          catalogCount: 2,
          ownedTitles: 3,
        },
      ]),
    ),
  );
  vi.mocked(listSystemDats).mockResolvedValue(
    result(
      page([
        redump,
        manual,
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
  vi.mocked(listDatSubscriptions).mockResolvedValue(
    result([
      sub,
    ]),
  );
  vi.mocked(getDatSubscription).mockResolvedValue(result(sub));
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
  expect(
    await screen.findByRole('tab', {
      name: 'Titles',
    }),
  ).toHaveAttribute('aria-selected', 'true');
  await screen.findByText('Browse all titles');
  const browse = screen.getByRole('link', {
    name: 'Browse all titles',
  });
  expect(browse).toHaveAttribute('href', '/systems/psx?tab=titles&view=all');
  expect(listCatalogTitles).toHaveBeenCalledWith(
    expect.objectContaining({
      query: expect.objectContaining({
        systemKey: 'psx',
        tracked: 'tracked',
      }),
    }),
  );
  await userEvent.click(
    screen.getByRole('radio', {
      name: 'All titles',
    }),
  );
  await waitFor(() =>
    expect(listCatalogTitles).toHaveBeenLastCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          systemKey: 'psx',
          tracked: 'all',
        }),
      }),
    ),
  );
  expect(
    screen.queryByRole('tab', {
      name: 'Collection',
    }),
  ).not.toBeInTheDocument();
});
it.each([
  [
    '/systems/psx',
    'tracked',
  ],
  [
    '/systems/psx?view=all',
    'all',
  ],
])('keeps the system title catalog in %s scoped without enrichment launchers', async (url, tracked) => {
  Object.assign(access, {
    canManageTitles: true,
    canTriggerEnrichment: true,
  });
  hub(url);
  await waitFor(() =>
    expect(listCatalogTitles).toHaveBeenCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          systemKey: 'psx',
          tracked,
        }),
      }),
    ),
  );
  expect(
    screen.queryByRole('button', {
      name: 'Enrich titles',
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: 'Fill missing artwork',
    }),
  ).not.toBeInTheDocument();
  expect(previewArtworkCompletion).not.toHaveBeenCalled();
  expect(queueArtworkCompletion).not.toHaveBeenCalled();
  expect(triggerSystemBulkEnrichment).not.toHaveBeenCalled();
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
        query: expect.objectContaining({
          titleId: 'title',
        }),
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
  vi.mocked(listSystemDats).mockResolvedValue(
    result(
      page([
        {
          ...manual,
          id: 'new-dat',
          version: 'v4',
        },
      ]),
    ),
  );
  hub('/systems/psx/sources/source2');
  expect(await screen.findByText('Installed DAT: v4')).toBeInTheDocument();
  await waitFor(() =>
    expect(listSourceEntries).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          datId: 'new-dat',
        },
      }),
    ),
  );
});
it('keeps updates and settings addressable without fetching entries', async () => {
  hub('/systems/psx/sources/source1?tab=settings');
  expect(
    await screen.findByRole('button', {
      name: 'Switch to manual updates',
    }),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: 'Review update',
    }),
  ).not.toBeInTheDocument();
  expect(listSourceEntries).not.toHaveBeenCalled();
  await userEvent.click(
    screen.getByRole('tab', {
      name: 'Updates',
    }),
  );
  expect(
    await screen.findByRole('button', {
      name: 'Review update',
    }),
  ).toBeInTheDocument();
  expect(listSourceEntries).not.toHaveBeenCalled();
});
it('handles a removed source without falling back to an unrelated DAT', async () => {
  hub('/systems/psx/sources/missing');
  expect(await screen.findByText('Source not found')).toBeInTheDocument();
  expect(listSourceEntries).not.toHaveBeenCalled();
});
it('shows source query failures with a retry instead of an empty collection', async () => {
  vi.mocked(listSystemDats).mockResolvedValue({
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
  vi.mocked(listSystemDats).mockResolvedValue(result(page([])));
  vi.mocked(listDatSubscriptions).mockResolvedValue(result([]));
  hub();
  expect(await screen.findByText('No sources yet')).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Choose a source',
    }),
  ).toBeEnabled();
});
it('keeps a pending subscription visible before its first DAT exists', async () => {
  vi.mocked(listDatSubscriptions).mockResolvedValue(
    result([
      {
        ...sub,
        activeDatId: null,
        state: 'ReadyToImport',
      },
    ]),
  );
  vi.mocked(getDatSubscription).mockResolvedValue(
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
  vi.mocked(createDatReplacement).mockResolvedValue(
    result({
      jobId: 'job',
    }),
  );
  vi.mocked(getJob).mockResolvedValue(
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
  expect(createDatReplacement).not.toHaveBeenCalled();
  await user.click(approve);
  await waitFor(() =>
    expect(createDatReplacement).toHaveBeenCalledWith(
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
