import type { Dat, DatCatalogSubscription, ManagedSystem } from '@romd/admin-api-client';
import {
  getDatSubscription,
  listDatCatalogs,
  listDatSubscriptions,
  listManagedSystems,
  listSystemDats,
} from '@romd/admin-api-client';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Link, Route, Routes, useLocation, useNavigate } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { AuthContextValue } from '../../contexts/AuthContext';
import { completePage } from '../../test/fixtures/referencePages';
import { render } from '../../test/utils/render';
import { Systems } from './index';
import { systemsListReturnPath } from './systemsListNavigation';

const workspace = vi.hoisted(() => ({
  width: 700,
}));
vi.mock('@mantine/hooks', async () => ({
  ...(await vi.importActual('@mantine/hooks')),
  useElementSize: () => ({
    ref: () => {},
    width: workspace.width,
    height: 0,
  }),
}));
vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getDatSubscription: vi.fn(),
  getAdminSystem: vi.fn(async () => ({
    data: {
      description: 'A Sony home console.',
    },
  })),
  listManagedSystems: vi.fn(),
  listDatCatalogs: vi.fn(),
  listDatSubscriptions: vi.fn(),
  listSystemDats: vi.fn(),
}));
vi.mock('./AddSystemFlow', () => ({
  AddSystemFlow: ({ startWithUpload }: { startWithUpload?: boolean }) => (
    <div>{startWithUpload ? 'Upload setup' : 'Guided setup'}</div>
  ),
}));

const admin: AuthContextValue = {
  user: {
    id: 'admin',
    email: 'admin@example.test',
    roles: [
      'Admin',
    ],
    libraryId: null,
  },
  isAuthenticated: true,
  isLoading: false,
  error: null,
  retry: async () => {},
  login: async () => {},
  logout: async () => {},
};
const systems: ManagedSystem[] = [
  {
    key: 'psx',
    name: 'PlayStation',
    shortName: 'psx',
    manufacturer: 'Sony',
    enabled: true,
    state: 'Ready',
    message: null,
    catalogCount: 1,
    ownedTitles: 4,
    trackedTitles: 2,
    knownTitles: 8435,
    lastDatImportedAt: '2026-11-03T12:00:00Z',
  },
  {
    key: 'gba',
    name: 'Game Boy Advance',
    shortName: 'gba',
    manufacturer: 'Nintendo',
    enabled: true,
    state: 'Ready',
    message: null,
    catalogCount: 1,
    ownedTitles: 8,
  },
  {
    key: 'nes',
    name: 'Nintendo Entertainment System',
    shortName: 'nes',
    manufacturer: 'Nintendo',
    enabled: true,
    state: 'NeedsCatalog',
    message: null,
    catalogCount: 0,
    ownedTitles: 0,
  },
  {
    key: 'snes',
    name: 'Super Nintendo',
    shortName: 'snes',
    manufacturer: 'Nintendo',
    enabled: false,
    state: 'NeedsCatalog',
    message: null,
    catalogCount: 0,
    ownedTitles: 0,
  },
];
const playStationUpdate: DatCatalogSubscription = {
  id: 'redump-subscription',
  catalogId: 'redump/psx/discs',
  systemId: 'psx',
  systemKey: 'psx',
  activeDatId: 'installed-psx',
  state: 'UpdateAvailable',
  name: 'Redump PlayStation',
  lastCheckedAt: null,
  message: null,
  candidateSha256: 'candidate',
  jobId: null,
};
function installedDat(key: string, name: string): Dat {
  return {
    id: `installed-${key}`,
    name,
    description: '',
    type: 'Logiqx',
    systemKey: key,
    version: '2026-11-03',
    gameCount: 200,
    sourceId: `source-${key}`,
    catalogSourceId: `catalog-${key}`,
    lifecycle: 'Active',
    sourceStatus: 'Active',
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  workspace.width = 700;
  vi.mocked(listDatSubscriptions).mockResolvedValue({
    data: [],
  } as Awaited<ReturnType<typeof listDatSubscriptions>>);
  vi.mocked(listManagedSystems).mockImplementation(
    async (options) =>
      ({
        data: completePage(
          options?.query?.query === 'PS1'
            ? [
                systems[0],
              ]
            : systems,
        ),
      }) as Awaited<ReturnType<typeof listManagedSystems>>,
  );
  vi.mocked(listSystemDats).mockImplementation(
    async ({ path }) =>
      ({
        data: completePage([
          installedDat(
            path.systemKey,
            path.systemKey === 'psx' ? 'Redump PlayStation' : 'Game Boy Advance DAT',
          ),
        ]),
      }) as Awaited<ReturnType<typeof listSystemDats>>,
  );
  vi.mocked(getDatSubscription).mockResolvedValue({
    data: playStationUpdate,
  } as Awaited<ReturnType<typeof getDatSubscription>>);
});
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

it('uses links and local summaries without fetching source details in a narrow workspace', async () => {
  render(<Systems />, {
    auth: admin,
  });
  const table = await screen.findByRole('table', {
    name: 'Systems',
  });
  expect(
    within(table).getByRole('link', {
      name: 'PlayStation',
    }),
  ).toHaveAttribute('href', '/systems/psx');
  expect(within(table).queryByText('Super Nintendo')).not.toBeInTheDocument();
  const row = within(table).getByRole('row', {
    name: /PlayStation/,
  });
  expect(
    within(row).getByRole('cell', {
      name: 'Ready',
      exact: true,
    }),
  ).toBeInTheDocument();
  expect(await within(row).findByText('Manual updates')).toBeInTheDocument();
  expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
  expect(listSystemDats).not.toHaveBeenCalled();
  expect(listDatCatalogs).not.toHaveBeenCalled();
});

it('shows dedicated title, DAT and import date cells on wide screens', async () => {
  workspace.width = 1220;
  render(<Systems />, {
    auth: admin,
  });
  const table = await screen.findByRole('table', {
    name: 'Systems',
  });
  expect(
    within(table).getByRole('columnheader', {
      name: 'Known titles',
    }),
  ).toBeVisible();
  expect(
    within(table).getByRole('columnheader', {
      name: 'DATs',
      exact: true,
    }),
  ).toBeVisible();
  expect(
    within(table).getByRole('columnheader', {
      name: 'Last DAT import',
    }),
  ).toBeVisible();
  const row = within(table).getByRole('row', {
    name: /PlayStation/,
  });
  expect(
    within(row).getByRole('cell', {
      name: '8,435',
    }),
  ).toBeVisible();
  expect(
    within(row).getByRole('cell', {
      name: '1',
    }),
  ).toBeVisible();
  expect(row.querySelector('time')).toHaveAttribute('datetime', '2026-11-03T12:00:00Z');
  const inspector = screen.getByRole('complementary');
  expect(
    within(inspector).getByRole('link', {
      name: '8,435 known titles',
    }),
  ).toHaveAttribute('href', '/systems/psx?tab=titles&view=all');
});

it('keeps known title and DAT counts visible in compact rows', async () => {
  workspace.width = 390;
  render(<Systems />, {
    auth: admin,
  });
  const table = await screen.findByRole('table', {
    name: 'Systems',
  });
  expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
  const row = within(table).getByRole('row', {
    name: /PlayStation/,
  });
  expect(within(row).getByText('8,435 known titles · 1 DAT')).toBeVisible();
});

it('keeps catalog readiness separate from subscription warnings and filters them without the publisher', async () => {
  vi.mocked(listDatSubscriptions).mockResolvedValue({
    data: [
      playStationUpdate,
    ],
  } as Awaited<ReturnType<typeof listDatSubscriptions>>);
  render(<Systems />, {
    auth: admin,
  });
  const row = await screen.findByRole('row', {
    name: /PlayStation/,
  });
  expect(await within(row).findByText('Update to review')).toBeInTheDocument();
  expect(
    within(row).getByRole('cell', {
      name: 'Ready',
      exact: true,
    }),
  ).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('checkbox', {
      name: /Needs attention/,
    }),
  );
  expect(
    screen.getByRole('link', {
      name: 'PlayStation',
    }),
  ).toBeInTheDocument();
  expect(
    screen.getByRole('link', {
      name: 'Nintendo Entertainment System',
    }),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('link', {
      name: 'Game Boy Advance',
    }),
  ).not.toBeInTheDocument();
  expect(listDatCatalogs).not.toHaveBeenCalled();
});

it('keeps known catalog issues filterable when local subscription status fails', async () => {
  vi.mocked(listDatSubscriptions).mockResolvedValue({
    error: {},
  } as Awaited<ReturnType<typeof listDatSubscriptions>>);
  render(<Systems />, {
    auth: admin,
  });
  expect(await screen.findByText('Subscription status is unavailable')).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('checkbox', {
      name: /Needs attention/,
    }),
  );
  expect(
    screen.getByRole('link', {
      name: 'Nintendo Entertainment System',
    }),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('link', {
      name: 'PlayStation',
    }),
  ).not.toBeInTheDocument();
  expect(screen.getByText('Status unavailable')).toBeInTheDocument();
});

it('refreshes catalog availability when a local import finishes without an open inspector', async () => {
  vi.useFakeTimers();
  vi.mocked(listDatSubscriptions)
    .mockResolvedValueOnce({
      data: [
        {
          ...playStationUpdate,
          activeDatId: null,
          state: 'Applying',
          jobId: 'import-job',
        },
      ],
    } as Awaited<ReturnType<typeof listDatSubscriptions>>)
    .mockResolvedValue({
      data: [
        {
          ...playStationUpdate,
          state: 'UpToDate',
        },
      ],
    } as Awaited<ReturnType<typeof listDatSubscriptions>>);
  const view = render(<Systems />, {
    auth: admin,
  });
  try {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1);
    });
    expect(screen.getByText('Importing DAT')).toBeInTheDocument();
    expect(listManagedSystems).toHaveBeenCalledTimes(1);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3002);
    });
    expect(screen.getByText('Up to date')).toBeInTheDocument();
    expect(listManagedSystems).toHaveBeenCalledTimes(2);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3000);
    });
    expect(listDatSubscriptions).toHaveBeenCalledTimes(2);
    expect(listDatCatalogs).not.toHaveBeenCalled();
  } finally {
    view.unmount();
    vi.useRealTimers();
  }
});

it('searches familiar aliases within your enabled systems', async () => {
  const user = userEvent.setup();
  render(<Systems />, {
    auth: admin,
  });
  await screen.findByRole('link', {
    name: 'PlayStation',
  });
  await user.type(screen.getByLabelText('Filter your systems'), 'PS1');
  await waitFor(() =>
    expect(listManagedSystems).toHaveBeenCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          query: 'PS1',
        }),
      }),
    ),
  );
  expect(
    await screen.findByRole('link', {
      name: 'PlayStation',
    }),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('link', {
      name: 'Game Boy Advance',
    }),
  ).not.toBeInTheDocument();
});

it('loads only the selected wide-workspace catalog and keeps a focused action beside Open system', async () => {
  workspace.width = 1100;
  vi.mocked(listDatSubscriptions).mockResolvedValue({
    data: [
      playStationUpdate,
    ],
  } as Awaited<ReturnType<typeof listDatSubscriptions>>);
  render(<Systems />, {
    auth: admin,
  });
  const inspector = await screen.findByRole('complementary', {
    name: 'PlayStation',
  });
  expect(await within(inspector).findByText('Redump PlayStation')).toBeInTheDocument();
  expect(within(inspector).getByText('2026-11-03 · Subscription')).toBeInTheDocument();
  expect(
    within(inspector).getByRole('link', {
      name: '8,435 known titles',
    }),
  ).toBeInTheDocument();
  expect(within(inspector).getByText('4 with files')).toBeInTheDocument();
  expect(
    within(inspector).getByRole('button', {
      name: 'Review update',
    }),
  ).toBeInTheDocument();
  expect(
    within(inspector).getByRole('link', {
      name: 'Open system',
    }),
  ).toHaveAttribute('href', '/systems/psx');
  expect(listSystemDats).toHaveBeenCalledTimes(1);
  const title = screen.getByRole('link', {
    name: 'Game Boy Advance',
  });
  expect(title).toHaveAttribute('href', '/systems/gba');
  const selectOther = screen.getByRole('button', {
    name: 'Preview Game Boy Advance in sidebar',
  });
  title.focus();
  await userEvent.tab();
  expect(selectOther).toHaveFocus();
  await userEvent.keyboard('{Enter}');
  expect(selectOther).toHaveAttribute('aria-pressed', 'true');
  expect(
    await within(
      screen.getByRole('complementary', {
        name: 'Game Boy Advance',
      }),
    ).findByText('Game Boy Advance DAT'),
  ).toBeInTheDocument();
  expect(listSystemDats).toHaveBeenCalledTimes(2);
  expect(listSystemDats).toHaveBeenLastCalledWith(
    expect.objectContaining({
      path: {
        systemKey: 'gba',
      },
    }),
  );
});

it('opens first catalog setup for an empty selected system', async () => {
  workspace.width = 1100;
  render(<Systems />, {
    auth: admin,
    routerOptions: {
      initialEntries: [
        '/systems?system=nes',
      ],
    },
  });
  const inspector = await screen.findByRole('complementary', {
    name: 'Nintendo Entertainment System',
  });
  expect(
    within(inspector).getByRole('link', {
      name: 'Set up catalog',
    }),
  ).toHaveAttribute('href', '/systems/nes?tab=sources&setup=1');
  expect(listSystemDats).not.toHaveBeenCalled();
});

function DetailDestination() {
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <>
      <Link to={systemsListReturnPath(location.state)}>All systems</Link>
      <button
        type="button"
        onClick={() => void navigate(-1)}
      >
        Browser back
      </button>
    </>
  );
}

it.each([
  {
    width: 480,
    back: 'Browser back',
  },
  {
    width: 1100,
    back: 'All systems',
  },
])('opens a system from its name at $width px and restores the list via $back', async ({
  width,
  back,
}) => {
  workspace.width = width;
  vi.stubGlobal('scrollY', 240);
  const scroll = vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  vi.mocked(listDatSubscriptions).mockResolvedValue({
    data: [
      playStationUpdate,
    ],
  } as Awaited<ReturnType<typeof listDatSubscriptions>>);
  render(
    <Routes>
      <Route
        path="/systems"
        element={<Systems />}
      />
      <Route
        path="/systems/:systemKey"
        element={<DetailDestination />}
      />
    </Routes>,
    {
      auth: admin,
      routerOptions: {
        initialEntries: [
          '/systems?q=PS1&attention=1&system=psx',
        ],
      },
    },
  );
  const playStation = await screen.findByRole('link', {
    name: 'PlayStation',
  });
  expect(playStation).toHaveAttribute('href', '/systems/psx');
  await userEvent.click(playStation);
  expect(
    screen.getByRole('link', {
      name: 'All systems',
    }),
  ).toHaveAttribute('href', '/systems?q=PS1&attention=1&system=psx');
  vi.stubGlobal('scrollY', 0);
  await userEvent.click(
    screen.getByRole(back === 'All systems' ? 'link' : 'button', {
      name: back,
    }),
  );
  expect(
    await screen.findByRole('link', {
      name: 'PlayStation',
    }),
  ).toBeInTheDocument();
  expect(screen.getByLabelText('Filter your systems')).toHaveValue('PS1');
  expect(
    screen.getByRole('checkbox', {
      name: /Needs attention/,
    }),
  ).toBeChecked();
  expect(scroll).toHaveBeenCalledWith({
    top: 240,
    behavior: 'instant',
  });
});

it('offers both an intentional empty state and a file-first upload entry', async () => {
  vi.mocked(listManagedSystems).mockResolvedValue({
    data: completePage(
      systems.map((system) => ({
        ...system,
        enabled: false,
      })),
    ),
  } as Awaited<ReturnType<typeof listManagedSystems>>);
  const user = userEvent.setup();
  render(<Systems />, {
    auth: admin,
  });
  expect(
    await screen.findByRole('button', {
      name: 'Choose your first system',
    }),
  ).toBeInTheDocument();
  await user.click(
    screen.getByRole('button', {
      name: 'Upload a DAT',
    }),
  );
  expect(screen.getByText('Upload setup')).toBeInTheDocument();
});

it('keeps the default selection local, restores row focus on close, and walks the preview by keyboard', async () => {
  workspace.width = 1000;
  const user = userEvent.setup();
  render(
    <>
      <Systems />
      <SystemSelectionLocation />
    </>,
    {
      auth: admin,
      routerOptions: {
        initialEntries: [
          '/systems?q=',
        ],
      },
    },
  );
  expect(
    await screen.findByRole('complementary', {
      name: 'PlayStation',
    }),
  ).toBeVisible();
  expect(screen.getByTestId('system-selection')).toHaveTextContent('?q=');
  const playStation = screen.getByRole('button', {
    name: 'Preview PlayStation in sidebar',
  });
  const gameBoy = screen.getByRole('button', {
    name: 'Preview Game Boy Advance in sidebar',
  });
  playStation.focus();
  await user.keyboard('{ArrowDown}');
  expect(gameBoy).toHaveFocus();
  expect(gameBoy.closest('tr')).toHaveAttribute('aria-current', 'true');
  expect(screen.getByTestId('system-selection')).toHaveTextContent('system=gba');
  await user.keyboard('{Escape}');
  expect(gameBoy).toHaveFocus();
  expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
  expect(screen.getByTestId('system-selection')).not.toHaveTextContent('system=');
  await user.keyboard('{Enter}');
  const panel = await screen.findByRole('complementary', {
    name: 'Game Boy Advance',
  });
  await user.click(
    within(panel).getByRole('button', {
      name: 'Preview previous item',
    }),
  );
  expect(
    await screen.findByRole('complementary', {
      name: 'PlayStation',
    }),
  ).toBeVisible();
  await user.click(
    screen.getByRole('button', {
      name: 'Close inspector',
    }),
  );
  expect(playStation).toHaveFocus();
});

function SystemSelectionLocation() {
  return <output data-testid="system-selection">{useLocation().search}</output>;
}
