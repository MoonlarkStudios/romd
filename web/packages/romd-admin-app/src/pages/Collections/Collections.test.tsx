import { MantineProvider } from '@mantine/core';
import type { CollectionSummary, SystemResource } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { CollectionDetailView as CollectionDetail } from '../../hooks/api/useCollectionManagement';
import { completePage } from '../../test/fixtures/referencePages';
import { render } from '../../test/utils/render';
import { collectionPage, evaluationPage, finalPage } from '../../test/workspacePages';
import { defaultLibraryConfiguration } from '../Libraries/libraryOptions';
import { CollectionAudiences } from './CollectionAudiences';
import { CollectionWorkspace } from './CollectionWorkspace';
import { Collections } from './index';

const permissions = vi.hoisted(() => ({
  canManageUsers: true,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canUploadRoms: true,
    canManageTitles: true,
    canTriggerEnrichment: true,
    canManageUsers: permissions.canManageUsers,
    role: 'Admin',
    hasRole: () => true,
  }),
}));

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    replaceLibraryAttachments: vi.fn(),
    listCollections: vi.fn(),
    listCollectionLibraryPlacements: vi.fn(),
    getLibrary: vi.fn(),
    listLibraryAttachments: vi.fn(),
    listLibraryPreviewTitles: vi.fn(),
    listLibraries: vi.fn(),
    createCollectionItem: vi.fn(),
    updateCollection: vi.fn(),
    replaceCollectionItemOrder: vi.fn(),
    previewLibraryConfiguration: vi.fn(),
    getCollection: vi.fn(),
    createCollection: vi.fn(),
    listAdminSystems: vi.fn(),
    listCatalogTitles: vi.fn(),
    getMe: vi.fn(),
  };
});

import {
  createCollection,
  createCollectionItem,
  getCollection,
  getLibrary,
  getMe,
  listAdminSystems,
  listCatalogTitles,
  listCollectionLibraryPlacements,
  listCollections,
  listLibraries,
  listLibraryAttachments,
  listLibraryPreviewTitles,
  previewLibraryConfiguration,
  replaceCollectionItemOrder,
  replaceLibraryAttachments,
  updateCollection,
} from '@romd/admin-api-client';

const mockListCollections = listCollections as ReturnType<typeof vi.fn>;
const mockGetCollectionDetail = getCollection as ReturnType<typeof vi.fn>;
const mockCreateCollection = createCollection as ReturnType<typeof vi.fn>;
const mockListPlatforms = listAdminSystems as ReturnType<typeof vi.fn>;
const mockSearchCatalog = listCatalogTitles as ReturnType<typeof vi.fn>;
const mockGetCurrentUser = getMe as ReturnType<typeof vi.fn>;

const collections: CollectionSummary[] = [
  {
    id: 'rpgs',
    name: 'Essential RPGs',
    description: 'The greats',
    systemKey: null,
    coverUrl: null,
    itemCount: 3,
    isSystem: false,
    sortOrder: 0,
    createdAt: '2026-01-01T00:00:00Z',
  },
  {
    id: 'recent',
    name: 'Recently Added',
    systemKey: null,
    itemCount: 10,
    isSystem: true,
    sortOrder: 1,
    createdAt: '2026-01-01T00:00:00Z',
  },
];

const rpgDetail: CollectionDetail = {
  id: 'rpgs',
  name: 'Essential RPGs',
  description: 'The greats',
  systemKey: null,
  coverUrl: null,
  itemCount: 2,
  isSystem: false,
  sortOrder: 0,
  createdAt: '2026-01-01T00:00:00Z',
  items: [
    {
      titleId: 't1',
      titleName: 'Chrono Trigger',
      systemKey: 'snes',
      coverUrl: null,
      note: 'A classic',
      sortOrder: 0,
      addedAt: '2026-01-01T00:00:00Z',
    },
    {
      titleId: 't2',
      titleName: 'Final Fantasy VI',
      systemKey: 'snes',
      coverUrl: null,
      note: null,
      sortOrder: 1,
      addedAt: '2026-01-01T00:00:00Z',
    },
  ],
};

const recentDetail: CollectionDetail = {
  id: 'recent',
  name: 'Recently Added',
  systemKey: null,
  coverUrl: null,
  itemCount: 1,
  isSystem: true,
  sortOrder: 1,
  createdAt: '2026-01-01T00:00:00Z',
  items: [
    {
      titleId: 't9',
      titleName: 'Super Metroid',
      systemKey: 'snes',
      coverUrl: null,
      note: null,
      sortOrder: 0,
      addedAt: '2026-01-01T00:00:00Z',
    },
  ],
};

const platforms: SystemResource[] = [
  {
    key: 'snes',
    name: 'Super Nintendo',
    compactLabel: 'SNES',
    ownership: 'Romd',
    revision: 'a'.repeat(64),
    builtInVersion: 1,
    description: null,
    icon: null,
    manufacturers: [],
    retired: false,
  },
];

describe('Collections', () => {
  beforeEach(() => {
    permissions.canManageUsers = true;
    vi.mocked(listCollectionLibraryPlacements).mockResolvedValue({
      data: finalPage([
        {
          libraryId: 'kids',
          name: 'Kids',
          isFeatured: true,
        },
      ]),
    } as never);
    vi.mocked(getLibrary).mockResolvedValue({
      data: {
        id: 'kids',
        name: 'Kids',
        configuration: defaultLibraryConfiguration,
        configurationState: 'Valid',
        needsMaterialization: false,
      },
    } as never);
    vi.mocked(listLibraries).mockResolvedValue({
      data: finalPage([]),
    } as never);
    vi.mocked(listLibraryAttachments).mockResolvedValue({
      data: [
        {
          collectionId: 'rpgs',
          visibleCount: 1,
          totalCount: 2,
        },
      ],
    } as never);
    vi.mocked(listLibraryPreviewTitles).mockResolvedValue({
      data: {
        items: [
          {
            id: 't1',
            name: 'Chrono Trigger',
            platformName: 'SNES',
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      },
    } as never);
    mockListCollections.mockResolvedValue({
      data: finalPage(collections),
      error: undefined,
    });
    mockGetCollectionDetail.mockImplementation(
      ({
        path,
      }: {
        path: {
          collectionId: string;
        };
      }) =>
        Promise.resolve({
          data: collectionPage(path.collectionId === 'recent' ? recentDetail : rpgDetail),
          error: undefined,
        }),
    );
    mockCreateCollection.mockResolvedValue({
      data: {
        ...collections[0],
        id: 'new',
        name: 'Co-op Night',
      },
      error: undefined,
    });
    mockListPlatforms.mockResolvedValue({
      data: completePage(platforms),
      error: undefined,
    });
    mockSearchCatalog.mockResolvedValue({
      data: {
        items: [],
        hasNextPage: false,
        nextCursor: null,
      },
      error: undefined,
    });
    mockGetCurrentUser.mockResolvedValue({
      data: null,
      error: undefined,
    });
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('renders a scannable list with per-collection game counts', async () => {
    render(
      <Routes>
        <Route
          path="/"
          element={<Collections />}
        />
        <Route
          path="/collections"
          element={<Collections />}
        />
        <Route
          path="/collections/:collectionId"
          element={<CollectionWorkspace />}
        />
      </Routes>,
    );

    expect(await screen.findByText('Essential RPGs')).toBeInTheDocument();
    expect(screen.getByText('Recently Added')).toBeInTheDocument();
    expect(screen.getAllByText('System').length).toBeGreaterThanOrEqual(1);
    // Counts belong to each collection; no misleading summed unique-game count.
    expect(screen.getByText(/3 titles/)).toBeInTheDocument();
    expect(screen.getByText(/10 titles/)).toBeInTheDocument();
  });

  it('shows vocabulary only on demand', async () => {
    render(
      <MantineProvider env="test">
        <Collections />
      </MantineProvider>,
    );
    expect(screen.queryByText(/A collection is an ordered selection/)).not.toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'About collections',
      }),
    );
    await waitFor(() =>
      expect(screen.getByText(/A collection is an ordered selection/)).toBeVisible(),
    );
  });

  it('links batched library associations without per-collection requests', async () => {
    vi.mocked(listLibraries).mockResolvedValue({
      data: finalPage([
        {
          id: 'kids',
          name: 'Kids',
          collectionIds: [
            'rpgs',
          ],
        },
      ]),
    } as never);
    render(<Collections />);
    expect(
      await screen.findByRole('link', {
        name: 'Kids',
      }),
    ).toHaveAttribute('href', '/libraries/kids?tab=collections');
    expect(
      screen.getByRole('link', {
        name: 'Not used in a library',
      }),
    ).toHaveAttribute('href', '/collections/recent?tab=audiences');
    expect(listCollectionLibraryPlacements).not.toHaveBeenCalled();
    expect(listLibraries).toHaveBeenCalledTimes(1);
  });

  it('does not request library data for curators without user-management access', async () => {
    permissions.canManageUsers = false;
    render(<Collections />);
    expect(await screen.findByText('Essential RPGs')).toBeInTheDocument();
    expect(listLibraries).not.toHaveBeenCalled();
    expect(screen.queryByText('Not used in a library')).not.toBeInTheDocument();
  });

  it('does not mistake unavailable associations for an unused collection', async () => {
    vi.mocked(listLibraries).mockResolvedValue({
      error: {},
      response: {
        status: 500,
      },
    } as never);
    render(<Collections />);
    expect(
      (
        await screen.findAllByRole('button', {
          name: 'Retry library associations',
        })
      ).length,
    ).toBe(2);
    expect(screen.queryByText('Not used in a library')).not.toBeInTheDocument();
  });

  it('uses singular counts and omits placeholder descriptions', async () => {
    mockListCollections.mockResolvedValue({
      data: finalPage([
        {
          ...collections[0],
          itemCount: 1,
          description: null,
        },
      ]),
    });
    render(<Collections />);
    expect(await screen.findByText('1 collection')).toBeInTheDocument();
    expect(screen.getByText(/1 title · No artwork/)).toBeInTheDocument();
    expect(screen.queryByText('A shared, ordered selection of games.')).not.toBeInTheDocument();
  });

  it('loads a collection detail with its ordered items when selected', async () => {
    const user = userEvent.setup();
    render(
      <Routes>
        <Route
          path="/"
          element={<Collections />}
        />
        <Route
          path="/collections"
          element={<Collections />}
        />
        <Route
          path="/collections/:collectionId"
          element={<CollectionWorkspace />}
        />
      </Routes>,
    );

    await user.click(await screen.findByText('Essential RPGs'));

    expect(await screen.findByText('Chrono Trigger')).toBeInTheDocument();
    expect(screen.getByText('Final Fantasy VI')).toBeInTheDocument();
    expect(screen.getByText('A classic')).toBeInTheDocument();
  });

  it('renders a system collection read-only (no edit or add controls)', async () => {
    const user = userEvent.setup();
    render(
      <Routes>
        <Route
          path="/"
          element={<Collections />}
        />
        <Route
          path="/collections"
          element={<Collections />}
        />
        <Route
          path="/collections/:collectionId"
          element={<CollectionWorkspace />}
        />
      </Routes>,
    );

    await user.click(await screen.findByText('Recently Added'));

    // Items still display...
    expect(await screen.findByText('Super Metroid')).toBeInTheDocument();
    // ...but authoring controls are suppressed for system-managed collections.
    expect(screen.queryByText('Add a title')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: /^edit$/i,
      }),
    ).not.toBeInTheDocument();
  });

  it('creates a collection from the modal', async () => {
    const user = userEvent.setup();
    render(
      <Routes>
        <Route
          path="/"
          element={<Collections />}
        />
        <Route
          path="/collections"
          element={<Collections />}
        />
        <Route
          path="/collections/:collectionId"
          element={<CollectionWorkspace />}
        />
      </Routes>,
    );

    await user.click(
      await screen.findByRole('button', {
        name: /new collection/i,
      }),
    );
    await user.type(await screen.findByLabelText(/name/i), 'Co-op Night');
    await user.click(
      screen.getByRole('button', {
        name: /create collection/i,
      }),
    );

    await waitFor(() => {
      expect(mockCreateCollection).toHaveBeenCalledWith({
        body: {
          name: 'Co-op Night',
          description: null,
          systemKey: null,
        },
      });
    });
  });
  it('opens legacy collection links in the dedicated workspace', async () => {
    render(
      <Routes>
        <Route
          path="/collections"
          element={<Collections />}
        />
        <Route
          path="/collections/:collectionId"
          element={<CollectionWorkspace />}
        />
      </Routes>,
      {
        routerOptions: {
          initialEntries: [
            '/collections?collection=rpgs',
          ],
        },
      },
    );
    expect(
      await screen.findByRole('heading', {
        name: 'Essential RPGs',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('tab', {
        name: 'Build',
      }),
    ).toBeInTheDocument();
  });

  it('keeps existing artwork when editing collection details', async () => {
    mockGetCollectionDetail.mockResolvedValue({
      data: collectionPage({
        ...rpgDetail,
        coverUrl: '/media/cover1',
      }),
    });
    vi.mocked(updateCollection).mockResolvedValue({
      data: {
        ...collections[0],
        coverUrl: '/media/cover1',
      },
    } as never);
    render(
      <Routes>
        <Route
          path="/collections/:collectionId"
          element={<CollectionWorkspace />}
        />
      </Routes>,
      {
        routerOptions: {
          initialEntries: [
            '/collections/rpgs',
          ],
        },
      },
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Edit details & artwork',
      }),
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() =>
      expect(updateCollection).toHaveBeenCalledWith(
        expect.objectContaining({
          body: expect.objectContaining({
            coverMediaId: 'cover1',
            name: 'Essential RPGs',
          }),
        }),
      ),
    );
  });

  it('keeps failed multi-add selections and reports partial success', async () => {
    const acceptedItem = {
      titleId: 't3',
      titleName: 'EarthBound',
      systemKey: 'snes',
      sortOrder: 2,
      note: null,
      coverUrl: null,
      addedAt: '2026-10-21T00:00:00Z',
    };
    mockSearchCatalog.mockResolvedValue({
      data: {
        items: [
          {
            id: 't3',
            name: 'EarthBound',
            systemKey: 'snes',
          },
          {
            id: 't4',
            name: 'Secret of Mana',
            systemKey: 'snes',
          },
        ],
        hasNextPage: false,
      },
    });
    vi.mocked(createCollectionItem)
      .mockImplementationOnce(async () => {
        mockGetCollectionDetail.mockResolvedValue({
          data: collectionPage({
            ...rpgDetail,
            items: [
              ...rpgDetail.items,
              acceptedItem,
            ],
          }),
        });
        return {
          data: acceptedItem,
          request: new Request('http://localhost'),
          response: new Response(null, {
            status: 201,
          }),
        };
      })
      .mockResolvedValueOnce({
        error: {
          detail: 'Try again later.',
        },
        response: {
          status: 400,
        },
      } as never);
    render(
      <Routes>
        <Route
          path="/collections/:collectionId"
          element={<CollectionWorkspace />}
        />
      </Routes>,
      {
        routerOptions: {
          initialEntries: [
            '/collections/rpgs',
          ],
        },
      },
    );
    await userEvent.click(
      await screen.findByRole('checkbox', {
        name: 'Select EarthBound',
      }),
    );
    await userEvent.click(
      screen.getByRole('checkbox', {
        name: 'Select Secret of Mana',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Add 2 selected games',
      }),
    );
    expect(await screen.findByText(/Added 1 of 2 games/)).toBeInTheDocument();
    expect(
      screen.getByRole('checkbox', {
        name: 'Select Secret of Mana',
      }),
    ).toBeChecked();
    expect(
      screen.getByRole('checkbox', {
        name: 'Select EarthBound',
      }),
    ).toBeDisabled();
    expect(
      screen.getByRole('button', {
        name: 'Add 1 selected games',
      }),
    ).toBeEnabled();
  });

  it('saves the complete order when moving a game', async () => {
    vi.mocked(replaceCollectionItemOrder).mockResolvedValue({
      data: {
        ...rpgDetail,
        items: [
          {
            ...rpgDetail.items[1],
            sortOrder: 0,
          },
          {
            ...rpgDetail.items[0],
            sortOrder: 1,
          },
        ],
      },
    } as never);
    render(
      <Routes>
        <Route
          path="/collections/:collectionId"
          element={<CollectionWorkspace />}
        />
      </Routes>,
      {
        routerOptions: {
          initialEntries: [
            '/collections/rpgs',
          ],
        },
      },
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Move Chrono Trigger down',
      }),
    );
    await waitFor(() =>
      expect(replaceCollectionItemOrder).toHaveBeenCalledWith({
        path: {
          collectionId: 'rpgs',
        },
        body: {
          titleIds: [
            't2',
            't1',
          ],
        },
      }),
    );
  });

  it('previews the selected audience and explains a hidden collection game', async () => {
    vi.mocked(previewLibraryConfiguration).mockResolvedValue({
      data: evaluationPage({
        items: [
          {
            id: 't2',
            isEligible: false,
            isOwned: false,
            reason: 'ContentRating',
            ratingBoard: 'Esrb',
            ratingCategory: 'T',
            ratingAge: 13,
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      }),
    } as never);
    render(
      <Routes>
        <Route
          path="/collections/:collectionId"
          element={<CollectionWorkspace />}
        />
      </Routes>,
      {
        routerOptions: {
          initialEntries: [
            '/collections/rpgs?tab=preview',
          ],
        },
      },
    );
    expect(await screen.findByText('Chrono Trigger')).toBeInTheDocument();
    expect(listLibraryPreviewTitles).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          libraryId: 'kids',
        },
        query: expect.objectContaining({
          collectionId: 'rpgs',
        }),
      }),
    );
    await userEvent.click(
      screen.getByLabelText('Investigate a game', {
        selector: 'input',
      }),
    );
    await userEvent.click(
      await screen.findByRole('option', {
        name: 'Final Fantasy VI',
        hidden: true,
      }),
    );
    expect(await screen.findByText('Hidden from Kids')).toBeInTheDocument();
    expect(screen.getByText('Exceeds the content-rating ceiling')).toBeInTheDocument();
    expect(
      screen.getByRole('link', {
        name: 'Review library policy',
      }),
    ).toHaveAttribute('href', '/libraries/kids?tab=games');
  });
  it('detaches an audience while preserving other collection placements and refreshes audiences', async () => {
    vi.mocked(listLibraryAttachments).mockResolvedValue({
      data: [
        {
          collectionId: 'before',
          isFeatured: false,
        },
        {
          collectionId: 'rpgs',
          isFeatured: true,
        },
        {
          collectionId: 'after',
          isFeatured: true,
        },
      ],
    } as never);
    vi.mocked(replaceLibraryAttachments).mockImplementation(async () => {
      vi.mocked(listCollectionLibraryPlacements).mockResolvedValue({
        data: finalPage([]),
      } as never);
      return {} as never;
    });
    render(<CollectionAudiences collectionId="rpgs" />);
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Detach Kids',
      }),
    );
    await waitFor(() =>
      expect(replaceLibraryAttachments).toHaveBeenCalledWith({
        path: {
          libraryId: 'kids',
        },
        body: {
          collections: [
            {
              collectionId: 'before',
              isFeatured: false,
            },
            {
              collectionId: 'after',
              isFeatured: true,
            },
          ],
        },
      }),
    );
    expect(await screen.findByText('Not attached to an audience yet.')).toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: 'Detach Kids',
      }),
    ).not.toBeInTheDocument();
  });

  it('previews the published audience library while its changes wait for a refresh', async () => {
    vi.mocked(getLibrary).mockResolvedValue({
      data: {
        id: 'kids',
        name: 'Kids',
        configuration: defaultLibraryConfiguration,
        configurationState: 'Valid',
        needsMaterialization: true,
        lastMaterializedAt: '2026-09-01T00:00:00Z',
        updatedAt: '2026-09-02T00:00:00Z',
      },
    } as never);
    render(
      <Routes>
        <Route
          path="/collections/:collectionId"
          element={<CollectionWorkspace />}
        />
      </Routes>,
      {
        routerOptions: {
          initialEntries: [
            '/collections/rpgs?tab=preview',
          ],
        },
      },
    );

    expect(await screen.findByText('Chrono Trigger')).toBeInTheDocument();
    expect(
      screen.getByText(
        'This preview shows the published library. Changes appear after the library is refreshed.',
      ),
    ).toBeInTheDocument();
  });

  it('asks for a first refresh before previewing an unpublished audience library', async () => {
    vi.mocked(getLibrary).mockResolvedValue({
      data: {
        id: 'kids',
        name: 'Kids',
        configuration: defaultLibraryConfiguration,
        configurationState: 'Valid',
        needsMaterialization: true,
        lastMaterializedAt: null,
      },
    } as never);
    render(
      <Routes>
        <Route
          path="/collections/:collectionId"
          element={<CollectionWorkspace />}
        />
      </Routes>,
      {
        routerOptions: {
          initialEntries: [
            '/collections/rpgs?tab=preview',
          ],
        },
      },
    );

    expect(
      await screen.findByText(/This audience’s library has not been published yet/),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('link', {
        name: 'Refresh the library',
      }),
    ).toHaveAttribute('href', '/libraries/kids');
    expect(listLibraryPreviewTitles).not.toHaveBeenCalled();
  });

  it('labels an unpublished audience library instead of counting its games', async () => {
    vi.mocked(getLibrary).mockResolvedValue({
      data: {
        id: 'kids',
        name: 'Kids',
        configuration: defaultLibraryConfiguration,
        configurationState: 'Valid',
        needsMaterialization: true,
        lastMaterializedAt: null,
      },
    } as never);
    render(<CollectionAudiences collectionId="rpgs" />);

    expect(await screen.findByText('Not published yet')).toBeInTheDocument();
    expect(screen.queryByText('1 of 2 games visible')).not.toBeInTheDocument();
  });

  it('counts a published audience library while its changes wait for a refresh', async () => {
    vi.mocked(getLibrary).mockResolvedValue({
      data: {
        id: 'kids',
        name: 'Kids',
        configuration: defaultLibraryConfiguration,
        configurationState: 'Valid',
        needsMaterialization: true,
        lastMaterializedAt: '2026-09-01T00:00:00Z',
      },
    } as never);
    render(<CollectionAudiences collectionId="rpgs" />);

    expect(await screen.findByText('1 of 2 games visible')).toBeInTheDocument();
  });

  it('keeps the audience attached and reports a failed detach', async () => {
    vi.mocked(replaceLibraryAttachments).mockResolvedValue({
      error: {
        detail: 'Could not save placement.',
      },
    } as never);
    render(<CollectionAudiences collectionId="rpgs" />);
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Detach Kids',
      }),
    );
    expect(await screen.findByText('Could not save placement.')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Detach Kids',
      }),
    ).toBeEnabled();
  });
});
