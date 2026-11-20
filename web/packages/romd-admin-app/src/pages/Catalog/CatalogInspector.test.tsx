import {
  type CatalogTitle,
  type EnrichmentAssessment,
  getAdminSystem,
  getCatalogStats,
  getEnrichmentWorkbenchStats,
  getTitle,
  getTitleArtwork,
  getTitleEnrichmentAssessment,
  listAdminSystems,
  listCatalogTitles,
  listEnrichmentWorkbench,
  replaceTitleEnrichmentOmission,
  type TitleDetail,
} from '@romd/admin-api-client';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes, useLocation, useNavigate, useNavigationType } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import type { AuthContextValue } from '../../contexts/AuthContext';
import { render } from '../../test/utils/render';
import { finalPage } from '../../test/workspacePages';
import { Catalog } from './index';

const dimensions = vi.hoisted(() => ({
  width: 1200,
}));
vi.mock('@mantine/hooks', async () => ({
  ...(await vi.importActual('@mantine/hooks')),
  useElementSize: () => ({
    ref: () => {},
    width: dimensions.width,
    height: 45,
  }),
}));
vi.mock('../../hooks/api/useManagedSystems', () => ({
  useManagedSystems: () => ({
    data: [],
  }),
}));
vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getAdminSystem: vi.fn(),
  getCatalogStats: vi.fn(),
  getEnrichmentWorkbenchStats: vi.fn(),
  getTitle: vi.fn(),
  getTitleArtwork: vi.fn(),
  getTitleEnrichmentAssessment: vi.fn(),
  listAdminSystems: vi.fn(),
  listEnrichmentWorkbench: vi.fn(),
  listCatalogTitles: vi.fn(),
  replaceTitleEnrichmentOmission: vi.fn(),
}));

const auth: AuthContextValue = {
  user: {
    id: 'manager',
    email: 'manager@example.test',
    roles: [
      'Manager',
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
const titles: CatalogTitle[] = [
  {
    id: 'alpha',
    name: 'Alpine Circuit',
    systemKey: 'snes',
    isTracked: true,
  },
  {
    id: 'beta',
    name: 'Bramble Run',
    systemKey: 'snes',
    isTracked: true,
  },
];

function detail(id: string): TitleDetail | undefined {
  const row = titles.find((item) => item.id === id);
  if (!row) return undefined;
  return {
    ...row,
    revision: 'title-1',
    enrichmentStatus: 'Completed',
    description: 'A synthetic title for workspace validation.',
    media: finalPage([]),
    releases: finalPage([]),
  };
}

function assessment(id: string, settled = false): EnrichmentAssessment {
  return {
    titleId: id,
    titleName: detail(id)?.name ?? id,
    platformName: 'Super Nintendo',
    revision: settled ? 'title-2' : 'title-1',
    needsReview: !settled,
    canEnrichAutomatically: false,
    hasActiveWork: false,
    hasOperationalBlocker: false,
    requirements: [
      {
        key: 'Metadata:Publisher',
        kind: 'Metadata',
        subject: 'Publisher',
        state: settled ? 'Satisfied' : 'NeedsReview',
        satisfiedBy: settled ? 'AcceptedOmission' : null,
        cause: settled ? null : 'NotProvided',
        actions: settled
          ? []
          : [
              'ReviewMetadata',
              'AcceptOmission',
            ],
      },
    ],
    acceptedOmissions: settled
      ? [
          'Publisher',
        ]
      : [],
  };
}

function Location() {
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <>
      <output
        data-testid="url"
        data-action={useNavigationType()}
      >
        {location.pathname}
        {location.search}
      </output>
      <button
        type="button"
        onClick={() => navigate(-1)}
      >
        Browser back
      </button>
    </>
  );
}

function open(path = '/catalog?view=all') {
  return render(
    <Routes>
      <Route
        path="/catalog"
        element={
          <>
            <Catalog />
            <Location />
          </>
        }
      />
      <Route
        path="/titles/:titleId/:section"
        element={<Location />}
      />
    </Routes>,
    {
      auth,
      routerOptions: {
        initialEntries: [
          path,
        ],
      },
    },
  );
}

beforeEach(() => {
  vi.resetAllMocks();
  localStorage.clear();
  dimensions.width = 1200;
  vi.mocked(listAdminSystems).mockResolvedValue({
    data: finalPage([]),
  } as never);
  vi.mocked(getAdminSystem).mockResolvedValue({
    data: {
      key: 'snes',
      name: 'Super Nintendo',
    },
  } as never);
  vi.mocked(listCatalogTitles).mockResolvedValue({
    data: finalPage(titles),
  } as never);
  vi.mocked(getCatalogStats).mockResolvedValue({
    data: {
      totalCount: 2,
    },
  } as never);
  vi.mocked(getEnrichmentWorkbenchStats).mockResolvedValue({
    data: {
      needsReview: 2,
    },
  } as never);
  vi.mocked(getTitleArtwork).mockResolvedValue({
    data: {
      roles: [],
      revision: 'art-1',
    },
  } as never);
  vi.mocked(getTitleEnrichmentAssessment).mockImplementation(
    async ({ path }) =>
      ({
        data: assessment(path.titleId),
      }) as never,
  );
  vi.mocked(getTitle).mockImplementation(async ({ path }) => {
    const data = detail(path.titleId);
    return {
      data,
      response: new Response(null, {
        status: data ? 200 : 404,
      }),
    } as never;
  });
});

it('keeps previews separate from bulk selection, manages focus and restores the preview after Back', async () => {
  const user = userEvent.setup();
  open();
  const alpha = await screen.findByRole('button', {
    name: 'Preview Alpine Circuit',
  });
  expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
  expect(getTitle).not.toHaveBeenCalled();
  await user.click(
    screen.getByRole('checkbox', {
      name: 'Select Alpine Circuit',
    }),
  );
  await user.click(alpha);
  const first = await screen.findByRole('complementary', {
    name: 'Alpine Circuit',
  });
  expect(alpha.closest('tr')).toHaveAttribute('aria-current', 'true');
  expect(alpha.closest('tr')).toHaveAttribute('data-selected', 'true');
  expect(screen.getByTestId('url')).toHaveTextContent('/catalog?view=all&title=alpha');
  expect(screen.getByTestId('url')).toHaveAttribute('data-action', 'REPLACE');
  expect(within(first).getByText('A synthetic title for workspace validation.')).toBeVisible();

  await user.keyboard('{ArrowDown}');
  const beta = screen.getByRole('button', {
    name: 'Preview Bramble Run',
  });
  expect(beta).toHaveFocus();
  const second = await screen.findByRole('complementary', {
    name: 'Bramble Run',
  });
  expect(alpha.closest('tr')).toHaveAttribute('data-selected', 'true');
  await user.click(
    within(second).getByRole('link', {
      name: 'Open title',
    }),
  );
  expect(screen.getByTestId('url')).toHaveTextContent('/titles/beta/metadata');
  await user.click(
    screen.getByRole('button', {
      name: 'Browser back',
    }),
  );
  const restored = await screen.findByRole('complementary', {
    name: 'Bramble Run',
  });
  expect(screen.getByTestId('url')).toHaveTextContent('/catalog?view=all&title=beta');
  await user.click(
    within(restored).getByRole('button', {
      name: 'Close inspector',
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Preview Bramble Run',
    }),
  ).toHaveFocus();
  expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
});

it('walks and closes previews in the grid without changing the real title link', async () => {
  const user = userEvent.setup();
  open('/catalog?view=all&title=alpha');
  await screen.findByRole('complementary', {
    name: 'Alpine Circuit',
  });
  await user.click(
    screen.getByRole('button', {
      name: 'Grid view',
    }),
  );
  const grid = screen.getByTestId('catalog-grid');
  const alpha = within(grid).getByRole('button', {
    name: 'Preview Alpine Circuit',
  });
  expect(
    within(grid).getByRole('link', {
      name: 'Alpine Circuit',
    }),
  ).toHaveAttribute('href', '/titles/alpha/metadata');
  alpha.focus();
  await user.keyboard('{ArrowDown}');
  const beta = within(grid).getByRole('button', {
    name: 'Preview Bramble Run',
  });
  expect(beta).toHaveFocus();
  await screen.findByRole('complementary', {
    name: 'Bramble Run',
  });
  await user.keyboard('{Escape}');
  expect(beta).toHaveFocus();
  expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
});

it('clears an unknown selection without hiding the list', async () => {
  open('/catalog?view=all&title=missing');
  await screen.findByRole('link', {
    name: 'Alpine Circuit',
  });
  await waitFor(() => expect(screen.getByTestId('url')).toHaveTextContent(/^\/catalog\?view=all$/));
  expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
});

it('uses the full title route below the workspace width threshold', async () => {
  dimensions.width = 879;
  open('/catalog?view=all&title=alpha');
  const link = await screen.findByRole('link', {
    name: 'Alpine Circuit',
  });
  expect(
    screen.queryByRole('button', {
      name: 'Preview Alpine Circuit',
    }),
  ).not.toBeInTheDocument();
  expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
  await userEvent.click(link);
  expect(screen.getByTestId('url')).toHaveTextContent('/titles/alpha/metadata');
});

it('keeps a decided row in place until advancing and preserves keyboard focus', async () => {
  let settled = false;
  vi.mocked(listEnrichmentWorkbench).mockImplementation(
    async () =>
      ({
        data: finalPage(
          settled
            ? [
                assessment('beta'),
              ]
            : [
                assessment('alpha'),
                assessment('beta'),
              ],
        ),
      }) as never,
  );
  vi.mocked(getTitleEnrichmentAssessment).mockImplementation(
    async ({ path }) =>
      ({
        data: assessment(path.titleId, settled && path.titleId === 'alpha'),
      }) as never,
  );
  vi.mocked(replaceTitleEnrichmentOmission).mockImplementation(async () => {
    settled = true;
    return {
      data: {
        ...detail('alpha'),
        revision: 'title-2',
      },
    } as never;
  });

  const user = userEvent.setup();
  open('/catalog?view=review');
  const first = await screen.findByRole('button', {
    name: 'Preview Alpine Circuit',
  });
  first.focus();
  await user.keyboard('{Enter}');
  const panel = await screen.findByRole('complementary', {
    name: 'Alpine Circuit',
  });
  await user.click(
    within(panel).getByRole('button', {
      name: /Keep publisher empty/i,
    }),
  );
  const confirmation = await screen.findByRole('dialog');
  await user.click(
    within(confirmation).getByRole('button', {
      name: /Keep publisher empty/i,
    }),
  );
  expect(await screen.findByText('No longer in this view')).toBeVisible();
  expect(first.closest('tr')).toHaveAttribute('aria-current', 'true');
  await waitFor(() => expect(first).toHaveFocus());
  await user.keyboard('{ArrowDown}');
  expect(
    await screen.findByRole('complementary', {
      name: 'Bramble Run',
    }),
  ).toBeVisible();
  expect(
    screen.queryByRole('button', {
      name: 'Preview Alpine Circuit',
    }),
  ).not.toBeInTheDocument();
});

it('does not advance to a fetched page while a review decision is open', async () => {
  let finishPage:
    | ((result: Awaited<ReturnType<typeof listEnrichmentWorkbench>>) => void)
    | undefined;
  vi.mocked(listEnrichmentWorkbench).mockImplementation(async (options) => {
    if (options?.query?.cursor)
      return new Promise((resolve) => {
        finishPage = resolve;
      });
    return {
      data: {
        items: [
          assessment('alpha'),
        ],
        nextCursor: 'next',
        hasNextPage: true,
      },
    } as never;
  });
  const user = userEvent.setup();
  open('/catalog?view=review&title=alpha');
  const panel = await screen.findByRole('complementary', {
    name: 'Alpine Circuit',
  });
  await user.click(
    within(panel).getByRole('button', {
      name: 'Preview next item',
    }),
  );
  await waitFor(() => expect(finishPage).toBeDefined());
  await user.click(
    within(panel).getByRole('button', {
      name: 'Keep publisher empty',
    }),
  );
  const dialog = await screen.findByRole('dialog', {
    name: 'Keep publisher empty?',
  });
  finishPage?.({
    data: finalPage([
      assessment('beta'),
    ]),
  } as never);
  await screen.findByRole('button', {
    name: 'Preview Bramble Run',
  });
  expect(dialog).toBeVisible();
  expect(screen.getByTestId('url')).toHaveTextContent('/catalog?view=review&title=alpha');
  await user.click(
    within(dialog).getByRole('button', {
      name: 'Cancel',
    }),
  );
  await user.click(
    within(panel).getByRole('button', {
      name: 'Preview next item',
    }),
  );
  expect(
    await screen.findByRole('complementary', {
      name: 'Bramble Run',
    }),
  ).toBeVisible();
});

it('offers full details for an off-queue review bookmark on a narrow screen', async () => {
  dimensions.width = 390;
  vi.mocked(listEnrichmentWorkbench).mockResolvedValue({
    data: finalPage([
      assessment('beta'),
    ]),
  } as never);
  open('/catalog?view=review&title=alpha');
  expect(
    await screen.findByRole('link', {
      name: 'Open linked title',
    }),
  ).toHaveAttribute('href', '/titles/alpha/metadata');
  expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
});
