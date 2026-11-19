import {
  checkDatSubscription,
  createDatReplacement,
  createDatSubscription,
  deleteSystemAlias,
  deleteSystemOverride,
  getAdminSystem,
  getDatSubscription,
  getDatSubscriptionChange,
  getDatSubscriptionChangeCount,
  getDatSubscriptionPreview,
  getJob,
  getManagedSystemDetails,
  getSystemOverrides,
  listCatalogTitles,
  listDatCatalogs,
  listDatSubscriptionChanges,
  listDatSubscriptions,
  listManagedSystems,
  listSourceEntries,
  listSystemAliases,
  listSystemDats,
  previewArtworkCompletion,
  previewDatReplacement,
  previewSystemNameAliases,
  queueArtworkCompletion,
  triggerSystemBulkEnrichment,
  updateManagedSystemPresentation,
} from '@romd/admin-api-client';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { parsePath, Route, Routes, useLocation } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { completePage } from '../../test/fixtures/referencePages';
import { render } from '../../test/utils/render';
import { SourceWorkspace } from './SourceWorkspace';
import { SystemHub } from './SystemHub';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getAdminSystem: vi.fn(),
  deleteSystemAlias: vi.fn(),
  deleteSystemOverride: vi.fn(),
  getSystemOverrides: vi.fn(),
  getManagedSystemDetails: vi.fn(),
  updateManagedSystemPresentation: vi.fn(),
  previewSystemNameAliases: vi.fn(),
  listSystemAliases: vi.fn(),
  listSystemDats: vi.fn(),
  listSourceEntries: vi.fn(),
  listManagedSystems: vi.fn(),
  listDatCatalogs: vi.fn(),
  listDatSubscriptions: vi.fn(),
  getDatSubscription: vi.fn(),
  previewDatReplacement: vi.fn(),
  createDatReplacement: vi.fn(),
  getDatSubscriptionPreview: vi.fn(),
  listDatReplacementChanges: vi.fn(),
  listDatSubscriptionChanges: vi.fn(),
  getDatSubscriptionChange: vi.fn(),
  getDatSubscriptionChangeCount: vi.fn(),
  checkDatSubscription: vi.fn(),
  createDatSubscription: vi.fn(),
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
  canEditReferenceData: true,
  canManageTitles: false,
  canTriggerEnrichment: false,
  role: 'Admin',
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
const builtInAlias = {
  id: 'built-in-alias',
  type: 'name',
  value: 'PS1',
  ownership: 'Romd',
};
const localAlias = {
  id: 'local-alias',
  type: 'name',
  value: 'Portable disc reference',
  ownership: 'Installation',
};
const playStationDetails = {
  system: {
    key: 'psx',
    name: 'PlayStation',
    compactLabel: 'PS1',
    ownership: 'Romd',
    revision: 'revision-at-open',
    description: 'Sony home console.',
    manufacturers: [
      {
        key: 'sony',
        name: 'Sony',
      },
    ],
  },
  hardware: {
    category: 'console',
    generation: 5,
    releaseYear: 1994,
  },
};
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
  hasSubscription: true,
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
function CurrentUrl() {
  const location = useLocation();
  return <output data-testid="url">{`${location.pathname}${location.search}`}</output>;
}
function hub(url = '/systems/psx?tab=sources', systemsReturn?: string) {
  render(
    <>
      <CurrentUrl />
      <Routes>
        <Route
          path="/systems/:systemKey/sources/:sourceId"
          element={<SourceWorkspace />}
        />
        <Route
          path="/systems/:systemKey"
          element={<SystemHub />}
        />
      </Routes>
    </>,
    {
      routerOptions: {
        initialEntries: [
          systemsReturn
            ? {
                ...parsePath(url),
                state: {
                  systemsReturn,
                },
              }
            : url,
        ],
      },
    },
  );
}
beforeEach(() => {
  Object.assign(access, {
    role: 'Admin',
    canManageSources: true,
    canEditReferenceData: true,
    canManageTitles: false,
    canTriggerEnrichment: false,
  });
  vi.resetAllMocks();
  vi.mocked(listCatalogTitles).mockResolvedValue(result(page([])));
  vi.mocked(getManagedSystemDetails).mockResolvedValue(result(playStationDetails));
  vi.mocked(getSystemOverrides).mockResolvedValue(
    result({
      hasDescription: false,
    }),
  );
  vi.mocked(previewSystemNameAliases).mockResolvedValue(result([]));
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
        builtInAlias,
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
it('prioritizes a source ready for review without requesting entry pages', async () => {
  vi.mocked(listSystemDats).mockResolvedValueOnce(
    result(
      page([
        manual,
        redump,
      ]),
    ),
  );
  hub();
  const redumpRow = await screen.findByRole('article', {
    name: redump.name,
  });
  const manualRow = screen.getByRole('article', {
    name: manual.name,
  });
  expect(screen.getAllByRole('article')[0]).toBe(redumpRow);
  expect(within(redumpRow).getByText('Subscription')).toBeInTheDocument();
  expect(
    within(redumpRow).getByRole('link', {
      name: 'Review update',
    }),
  ).toHaveAttribute('href', '/systems/psx/sources/source1?tab=updates');
  expect(within(manualRow).getByText('Manual updates')).toBeInTheDocument();
  expect(
    within(manualRow).getByRole('link', {
      name: manual.name,
    }),
  ).toBeInTheDocument();
  expect(screen.getByText('Catalog ready')).toBeInTheDocument();
  expect(listSourceEntries).not.toHaveBeenCalled();
  expect(
    within(manualRow).getByRole('link', {
      name: 'Upload new version',
    }),
  ).toHaveAttribute('href', '/systems/psx/sources/source2?tab=updates');
});
it('connects a compatible subscription from the exact installed source row', async () => {
  const catalogId = 'no-intro/psx/test';
  vi.mocked(listDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      message: null,
      catalogs: [
        {
          catalogId,
          name: manual.name,
          systemId: 'psx',
        },
      ],
    }),
  );
  const connected = {
    ...sub,
    id: 'connected',
    catalogId,
    activeDatId: manual.id,
    state: 'UpToDate',
    jobId: 'previous-job',
  };
  vi.mocked(createDatSubscription).mockImplementation(async () => {
    vi.mocked(listDatSubscriptions).mockResolvedValue(
      result([
        sub,
        connected,
      ]),
    );
    vi.mocked(getDatSubscription).mockImplementation(async ({ path }) =>
      result(path.subscriptionId === connected.id ? connected : sub),
    );
    return result(connected);
  });
  hub();
  const row = await screen.findByRole('article', {
    name: manual.name,
  });
  await userEvent.click(
    await within(row).findByRole('button', {
      name: 'Connect subscription',
    }),
  );
  expect(createDatSubscription).toHaveBeenCalledExactlyOnceWith({
    body: {
      catalogId,
      datId: manual.id,
    },
  });
  expect(
    await within(row).findByRole('button', {
      name: 'Check for updates',
    }),
  ).toBeVisible();
  expect(
    within(row).queryByRole('link', {
      name: /job/,
    }),
  ).not.toBeInTheDocument();
});

it('keeps manual replacement available when the subscription directory cannot be loaded', async () => {
  vi.mocked(listDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      message: 'The directory could not be verified.',
      catalogs: [],
    }),
  );
  hub();
  expect(await screen.findByText(/Subscription directory unavailable/)).toBeVisible();
  const manualRow = screen.getByRole('article', {
    name: manual.name,
  });
  await userEvent.click(
    within(manualRow).getByRole('link', {
      name: 'Upload new version',
    }),
  );
  expect(await screen.findByTestId('dat-upload-zone')).toBeVisible();
});

it('lets Managers replace a manual DAT without calling Admin subscription endpoints', async () => {
  access.role = 'Manager';
  hub();
  const manualRow = await screen.findByRole('article', {
    name: manual.name,
  });
  const subscribedRow = screen.getByRole('article', {
    name: redump.name,
  });
  expect(within(subscribedRow).getByText('Subscription')).toBeVisible();
  expect(
    within(subscribedRow).queryByRole('link', {
      name: 'Upload new version',
    }),
  ).not.toBeInTheDocument();
  await userEvent.click(
    within(manualRow).getByRole('link', {
      name: 'Upload new version',
    }),
  );
  expect(await screen.findByTestId('dat-upload-zone')).toBeVisible();
  expect(listDatCatalogs).not.toHaveBeenCalled();
  expect(listDatSubscriptions).not.toHaveBeenCalled();
});

it('hides manual replacement from users below Manager on the direct Updates route', async () => {
  access.role = 'Contributor';
  access.canManageSources = false;
  hub('/systems/psx/sources/source2?tab=updates');
  expect(
    await screen.findByRole('heading', {
      name: 'Manual updates',
    }),
  ).toBeVisible();
  expect(screen.queryByTestId('dat-upload-zone')).not.toBeInTheDocument();
  expect(listDatSubscriptions).not.toHaveBeenCalled();
});

it('keeps the filtered Systems return destination when switching detail tabs', async () => {
  const systemsReturn = '/systems?q=psx&attention=1&system=psx';
  hub('/systems/psx?tab=sources', systemsReturn);
  await userEvent.click(
    await screen.findByRole('tab', {
      name: 'Titles',
    }),
  );
  expect(
    screen.getByRole('link', {
      name: 'All systems',
    }),
  ).toHaveAttribute('href', systemsReturn);
});
it('opens on Metadata and saves a new alias after resolving a cross-system conflict', async () => {
  vi.mocked(previewSystemNameAliases).mockResolvedValueOnce(
    result([
      {
        alias: 'GBA',
        systemName: 'Game Boy Advance',
        builtIn: true,
      },
    ]),
  );
  vi.mocked(updateManagedSystemPresentation).mockImplementation(async () => {
    vi.mocked(listSystemAliases).mockResolvedValue(
      result(
        page([
          builtInAlias,
          localAlias,
        ]),
      ),
    );
    return result(playStationDetails.system);
  });
  const user = userEvent.setup();
  hub('/systems/psx');

  expect(
    await screen.findByRole('tab', {
      name: 'Metadata',
    }),
  ).toHaveAttribute('aria-selected', 'true');
  expect(
    await screen.findByRole('textbox', {
      name: 'System key',
    }),
  ).toHaveAttribute('readonly');
  expect(
    screen.getByRole('textbox', {
      name: 'Manufacturers',
    }),
  ).toHaveValue('Sony');
  expect(
    screen.getByRole('textbox', {
      name: 'Category',
    }),
  ).toHaveValue('Console');
  expect(
    screen.getByRole('textbox', {
      name: 'Generation',
    }),
  ).toHaveValue('5');
  expect(
    screen.getByRole('textbox', {
      name: 'Release year',
    }),
  ).toHaveValue('1994');
  expect(
    screen.getByRole('textbox', {
      name: 'Description',
    }),
  ).toHaveValue('Sony home console.');
  const save = screen.getByRole('button', {
    name: 'Save changes',
  });
  expect(save).toBeDisabled();
  const aliases = screen.getByRole('combobox', {
    name: 'Aliases',
  });
  await user.type(aliases, 'gBa{Enter}');
  expect(
    await screen.findByText('“GBA” is already a built-in alias for Game Boy Advance.'),
  ).toBeVisible();
  expect(save).toBeDisabled();
  expect(updateManagedSystemPresentation).not.toHaveBeenCalled();

  await user.click(
    screen.getByRole('button', {
      name: 'Remove alias gBa',
    }),
  );
  await user.type(aliases, 'Portable disc reference{Enter}');
  await waitFor(() => expect(save).toBeEnabled());
  await user.click(save);
  expect(await screen.findByText('System details saved.')).toBeVisible();
  expect(screen.getByText('Portable disc reference')).toBeVisible();
  expect(updateManagedSystemPresentation).toHaveBeenCalledExactlyOnceWith({
    path: {
      systemKey: 'psx',
    },
    query: {
      enableForSetup: false,
    },
    headers: {
      'If-Match': '"revision-at-open"',
    },
    body: {
      nameAliases: [
        'Portable disc reference',
      ],
    },
  });
});

it('edits, clears, and restores the default description without discarding another field', async () => {
  const original = playStationDetails.system;
  vi.mocked(updateManagedSystemPresentation).mockImplementation(async ({ body }) => {
    const updated = {
      ...original,
      ...body,
      revision: body.description === null ? 'description-cleared' : 'description-edited',
    };
    vi.mocked(getManagedSystemDetails).mockResolvedValue(
      result({
        ...playStationDetails,
        system: updated,
      }),
    );
    vi.mocked(getSystemOverrides).mockResolvedValue(
      result({
        hasDescription: true,
      }),
    );
    return result(updated);
  });
  vi.mocked(deleteSystemOverride).mockImplementation(async () => {
    const restored = {
      ...original,
      revision: 'description-restored',
    };
    vi.mocked(getManagedSystemDetails).mockResolvedValue(
      result({
        ...playStationDetails,
        system: restored,
      }),
    );
    vi.mocked(getSystemOverrides).mockResolvedValue(
      result({
        hasDescription: false,
      }),
    );
    return {
      response: new Response(null, {
        status: 204,
        headers: {
          ETag: '"description-restored"',
        },
      }),
    } as Awaited<ReturnType<typeof deleteSystemOverride>>;
  });
  hub('/systems/psx');
  const user = userEvent.setup();
  const description = await screen.findByRole('textbox', {
    name: 'Description',
  });
  const save = screen.getByRole('button', {
    name: 'Save changes',
  });
  expect(description).toHaveValue(original.description);
  expect(
    screen.queryByRole('button', {
      name: 'Restore default',
    }),
  ).not.toBeInTheDocument();

  await user.clear(description);
  await user.type(description, 'My PlayStation description.');
  await user.click(save);
  await screen.findByText('System details saved.');
  expect(description).toHaveValue('My PlayStation description.');

  await user.click(
    screen.getByRole('button', {
      name: 'Clear description',
    }),
  );
  expect(description).toHaveValue('');
  await user.click(save);
  await screen.findByText('System details saved.');
  expect(updateManagedSystemPresentation).toHaveBeenLastCalledWith(
    expect.objectContaining({
      body: {
        description: null,
      },
    }),
  );
  expect(description).toHaveValue('');

  const name = screen.getByRole('textbox', {
    name: 'Display name',
  });
  await user.clear(name);
  await user.type(name, 'My PlayStation');
  await user.click(
    screen.getByRole('button', {
      name: 'Restore default',
    }),
  );
  expect(await screen.findByText('Default description restored.')).toBeVisible();
  expect(description).toHaveValue(original.description);
  expect(name).toHaveValue('My PlayStation');
  expect(deleteSystemOverride).toHaveBeenCalledExactlyOnceWith({
    path: {
      systemKey: 'psx',
      field: 'description',
    },
    headers: {
      'If-Match': '"description-cleared"',
    },
  });

  await user.click(save);
  await waitFor(() =>
    expect(updateManagedSystemPresentation).toHaveBeenLastCalledWith(
      expect.objectContaining({
        body: {
          name: 'My PlayStation',
        },
        headers: {
          'If-Match': '"description-restored"',
        },
      }),
    ),
  );
  await screen.findByText('System details saved.');
});

it('removes a saved local alias and keeps protected aliases locked', async () => {
  const unresolvedAlias = {
    id: 'old-alias',
    type: 'name',
    value: 'Old name',
    ownership: 'Unresolved',
  };
  vi.mocked(listSystemAliases).mockResolvedValue(
    result(
      page([
        builtInAlias,
        localAlias,
        unresolvedAlias,
      ]),
    ),
  );
  vi.mocked(deleteSystemAlias).mockImplementation(async () => {
    vi.mocked(listSystemAliases).mockResolvedValue(
      result(
        page([
          builtInAlias,
          unresolvedAlias,
        ]),
      ),
    );
    return result(undefined);
  });
  hub('/systems/psx');

  const user = userEvent.setup();
  const aliases = within(
    await screen.findByRole('group', {
      name: 'Aliases',
    }),
  );
  const entry = aliases.getByRole('combobox', {
    name: 'Aliases',
  });
  const remove = await aliases.findByRole('button', {
    name: 'Remove alias Portable disc reference from this system',
  });
  expect(aliases.getByTitle('Built-in alias')).toHaveTextContent('PS1');
  expect(aliases.getByTitle('Ownership unknown')).toHaveTextContent('Old name');
  expect(
    screen.getAllByRole('button', {
      name: /Remove alias/,
    }),
  ).toEqual([
    remove,
  ]);

  await user.type(entry, '{Backspace}');
  expect(remove).toBeVisible();
  expect(aliases.getByTitle('Ownership unknown')).toBeVisible();
  expect(deleteSystemAlias).not.toHaveBeenCalled();

  await user.click(remove);

  expect(await screen.findByText('Alias removed.')).toBeVisible();
  expect(screen.queryByText('Portable disc reference')).not.toBeInTheDocument();
  expect(screen.getByTitle('Built-in alias')).toBeVisible();
  expect(updateManagedSystemPresentation).not.toHaveBeenCalled();
  expect(deleteSystemAlias).toHaveBeenCalledExactlyOnceWith({
    path: {
      systemKey: 'psx',
      aliasId: 'local-alias',
    },
  });
});

it('shows metadata to users without reference edit permission', async () => {
  access.canEditReferenceData = false;
  vi.mocked(listSystemAliases).mockResolvedValue(
    result(
      page([
        builtInAlias,
        localAlias,
      ]),
    ),
  );
  hub('/systems/psx');

  const name = await screen.findByRole('textbox', {
    name: 'Display name',
  });
  expect(name).toHaveValue('PlayStation');
  expect(name).toHaveAttribute('readonly');
  expect(
    screen.queryByRole('combobox', {
      name: 'Aliases',
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: 'Save changes',
    }),
  ).not.toBeInTheDocument();
  expect(await screen.findByText('Portable disc reference')).toBeVisible();
  expect(
    screen.queryByRole('button', {
      name: /Remove alias/,
    }),
  ).not.toBeInTheDocument();
});

it('defaults to tracked titles and keeps the all-title escape scoped to this system', async () => {
  hub('/systems/psx?tab=titles');
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
    '/systems/psx?tab=titles',
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
it('opens old settings links on Updates and keeps the update method in the source menu', async () => {
  vi.mocked(getDatSubscriptionPreview).mockReturnValue(new Promise(() => {}));
  const user = userEvent.setup();
  hub('/systems/psx/sources/source1?tab=settings');
  expect(
    await screen.findByRole('tab', {
      name: /Updates/,
      selected: true,
    }),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('tab', {
      name: 'Settings',
    }),
  ).not.toBeInTheDocument();
  await waitFor(() =>
    expect(screen.getByTestId('url')).toHaveTextContent('/systems/psx/sources/source1?tab=updates'),
  );
  await user.click(
    screen.getByRole('button', {
      name: /^Actions for /,
    }),
  );
  expect(
    await screen.findByRole('menuitem', {
      name: 'Switch to manual updates',
    }),
  ).toBeInTheDocument();
  expect(listDatCatalogs).not.toHaveBeenCalled();
  expect(listSourceEntries).not.toHaveBeenCalled();
});
it('says when a subscribed source has no new update and checks again on request', async () => {
  const upToDate = {
    ...sub,
    state: 'UpToDate',
    lastCheckedAt: new Date().toISOString(),
    nextCheckAt: new Date(Date.now() + 2 * 60 * 60_000).toISOString(),
  };
  vi.mocked(listDatSubscriptions).mockResolvedValue(
    result([
      upToDate,
    ]),
  );
  vi.mocked(getDatSubscription).mockResolvedValue(result(upToDate));
  vi.mocked(checkDatSubscription).mockResolvedValue(result(upToDate) as never);
  const user = userEvent.setup();
  hub('/systems/psx/sources/source1?tab=updates');
  const status = await screen.findByRole('region', {
    name: 'Update status',
  });
  expect(within(status).getByText('No new updates')).toBeInTheDocument();
  expect(
    within(status).getByText(
      new Date(upToDate.nextCheckAt).toLocaleString(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
      }),
    ),
  ).toHaveAttribute('datetime', upToDate.nextCheckAt);
  await user.click(
    within(status).getByRole('button', {
      name: 'Check for updates',
    }),
  );
  await waitFor(() => expect(checkDatSubscription).toHaveBeenCalled());
  expect(getDatSubscriptionPreview).not.toHaveBeenCalled();
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
  expect(await screen.findByText('No DAT installed')).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Add first DAT',
    }),
  ).toBeEnabled();
});
it('opens catalog setup from its direct link', async () => {
  vi.mocked(listSystemDats).mockResolvedValue(result(page([])));
  vi.mocked(listDatSubscriptions).mockResolvedValue(result([]));
  hub('/systems/psx?tab=sources&setup=1');
  expect(await screen.findByRole('dialog')).toBeInTheDocument();
  expect(listDatCatalogs).toHaveBeenCalled();
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
  expect(await screen.findByText('No DAT installed')).toBeInTheDocument();
  expect(
    await screen.findByRole('button', {
      name: 'Review first DAT',
    }),
  ).toBeInTheDocument();
});
it.each([
  [
    'Completed',
    'Applied 4',
  ],
  [
    'Cancelled',
    'Update cancelled',
  ],
  [
    'Failed',
    'Update needs attention',
  ],
])('reports a %s manual replacement after the hash-bound review', async (phase, notice) => {
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
      phase,
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
  const review = await screen.findByRole('region', {
    name: 'Review catalog update',
  });
  const approve = await within(review).findByRole('button', {
    name: 'Apply uploaded DAT',
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
  expect(await screen.findByText(notice)).toBeInTheDocument();
  if (phase === 'Completed') expect(screen.getByText('1 added')).toBeInTheDocument();
  else {
    expect(screen.queryByText('Applied 4')).not.toBeInTheDocument();
    expect(screen.queryByText('1 added')).not.toBeInTheDocument();
  }
  expect(
    screen.queryByRole('region', {
      name: 'Review catalog update',
    }),
  ).not.toBeInTheDocument();
});
it('shows a pending subscription update as the Updates tab itself', async () => {
  vi.mocked(getDatSubscriptionPreview).mockResolvedValue(
    result({
      activeSha256: 'before',
      candidateSha256: 'after',
      activeVersion: '20261012-015604',
      candidateVersion: '20261025-205653',
      unchanged: false,
      activeEntries: 1,
      candidateEntries: 1,
      entriesAdded: 0,
      entriesRemoved: 0,
      entriesChanged: 0,
      filesAdded: 0,
      filesRemoved: 0,
      filesChanged: 0,
      hashesChanged: 0,
      activeBiosEntries: 0,
      candidateBiosEntries: 0,
      changes: [],
      truncated: false,
    }),
  );
  hub('/systems/psx/sources/source1?tab=updates&review=subscription');
  expect(
    await screen.findByRole('tab', {
      name: 'Updates, update ready',
    }),
  ).toBeInTheDocument();
  const review = await screen.findByRole('region', {
    name: 'Review catalog update',
  });
  expect(await within(review).findByText('No supported entry changes')).toBeInTheDocument();
  expect(
    within(review).getByRole('button', {
      name: /^Apply (25 Oct|Oct 25) update$/,
    }),
  ).toBeInTheDocument();
  expect(
    within(review).getByRole('button', {
      name: 'Check again',
    }),
  ).toBeInTheDocument();
  // The review is the tab; there is no separate page to close back to.
  expect(
    within(review).queryByRole('button', {
      name: 'Decide later',
    }),
  ).not.toBeInTheDocument();
  await waitFor(() => expect(screen.getByTestId('url')).toHaveTextContent(/\?tab=updates$/));
});
it('restores the reviewed entry and filter from the URL and keeps them as the reader moves', async () => {
  const impact = {
    storedRoms: 0,
    lostRomMatches: 0,
    newRomMatches: 0,
    titleLink: null,
    titleLinkAtRisk: false,
    sha1ChangedFiles: 0,
    metadataOnly: false,
    otherChecksumOnly: false,
    needsReview: false,
    titleId: null,
    titleOtherSourceLinks: 0,
  };
  const removed = {
    id: 'disc-b',
    name: 'Disc B',
    change: 'Removed',
    filesAdded: 0,
    filesRemoved: 1,
    filesChanged: 0,
    impact,
  };
  vi.mocked(getDatSubscriptionPreview).mockResolvedValue(
    result({
      activeSha256: 'before',
      candidateSha256: 'after',
      activeVersion: '1',
      candidateVersion: '2',
      unchanged: false,
      activeEntries: 2,
      candidateEntries: 1,
      entriesAdded: 0,
      entriesRemoved: 1,
      entriesChanged: 0,
      filesAdded: 0,
      filesRemoved: 1,
      filesChanged: 0,
      hashesChanged: 0,
      activeBiosEntries: 0,
      candidateBiosEntries: 0,
      changes: [],
      truncated: false,
    }),
  );
  vi.mocked(listDatSubscriptionChanges).mockResolvedValue(
    result({
      items: [
        {
          ...removed,
          id: 'disc-a',
          name: 'Disc A',
        },
      ],
      nextCursor: 'second-page',
      hasNextPage: true,
    }),
  );
  vi.mocked(getDatSubscriptionChangeCount).mockResolvedValue(
    result({
      total: 2,
    }),
  );
  vi.mocked(getDatSubscriptionChange).mockResolvedValue(
    result({
      activeSha256: 'before',
      candidateSha256: 'after',
      id: 'disc-b',
      name: 'Disc B',
      change: 'Removed',
      fields: [],
      impact,
      files: {
        items: [],
        nextCursor: null,
        hasNextPage: false,
      },
    }),
  );
  const user = userEvent.setup();
  hub('/systems/psx/sources/source1?tab=updates&filter=removed&entry=disc-b');
  const detail = await screen.findByRole('region', {
    name: 'Entry changes',
  });
  expect(
    await within(detail).findByRole('heading', {
      name: 'Disc B',
    }),
  ).toBeInTheDocument();
  expect(
    await within(detail).findByRole('button', {
      name: 'Next entry',
    }),
  ).toBeDisabled();
  expect(
    within(detail).getByRole('button', {
      name: 'Previous entry',
    }),
  ).toBeDisabled();
  expect(listDatSubscriptionChanges).toHaveBeenCalledWith(
    expect.objectContaining({
      query: expect.objectContaining({
        change: 'Removed',
      }),
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Removed 1',
      pressed: true,
    }),
  ).toBeInTheDocument();
  await user.click(
    screen.getByRole('button', {
      name: 'All changes 1',
    }),
  );
  await waitFor(() => expect(screen.getByTestId('url')).toHaveTextContent(/\?tab=updates$/));
  await user.click(
    screen.getByRole('tab', {
      name: 'Entries',
    }),
  );
  await waitFor(() => expect(screen.getByTestId('url')).toHaveTextContent(/\?tab=entries$/));
});
