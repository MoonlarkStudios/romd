import { MantineProvider } from '@mantine/core';
import type { Library } from '@romd/admin-api-client';
import { getLibrary, updateCollection } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render as renderBare, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DraftGuardProvider } from '../../components/Administration/DraftGuard';
import { useUpdateCollection } from '../../hooks/api/useCollectionManagement';
import { completePage } from '../../test/fixtures/referencePages';
import { render } from '../../test/utils/render';
import { evaluationPage, finalPage } from '../../test/workspacePages';
import { Libraries } from './index';
import { LibraryCollections } from './LibraryCollections';
import { LibraryGames } from './LibraryGames';
import { LibraryPreview } from './LibraryPreview';
import { LibraryRules } from './LibraryRules';
import { LibraryWorkspace } from './LibraryWorkspace';

vi.mock('../../hooks/api/useJobs', async (original) => ({
  ...(await original<typeof import('../../hooks/api/useJobs')>()),
  useJobs: () => ({
    data: [],
  }),
}));

import { toConfiguration, toFormState } from './libraryForm';
import { defaultLibraryConfiguration } from './libraryOptions';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getLibrary: vi.fn(),
  listLibraries: vi.fn(),
  materializeLibrary: vi.fn(),
  createLibrary: vi.fn(),
  previewLibraryConfiguration: vi.fn(),
  updateLibrary: vi.fn(),
  updateCollection: vi.fn(),
  listLibraryAttachments: vi.fn(),
  replaceLibraryAttachments: vi.fn(),
  listLibraryPreviewTitles: vi.fn(),
  listCollections: vi.fn(),
  listAdminSystems: vi.fn(),
  listDats: vi.fn(),
  getCatalogFilters: vi.fn(),
  getMe: vi.fn(),
  getTitle: vi.fn(),
  listCatalogTitles: vi.fn(),
  exportLibrary: vi.fn(),
}));

import {
  createLibrary,
  exportLibrary,
  getCatalogFilters,
  getTitle,
  listAdminSystems,
  listCollections,
  listDats,
  listLibraries,
  listLibraryAttachments,
  listLibraryPreviewTitles,
  materializeLibrary,
  previewLibraryConfiguration,
  replaceLibraryAttachments,
  updateLibrary,
} from '@romd/admin-api-client';

const library: Library = {
  id: 'kids',
  name: 'Kids',
  collectionIds: [
    'coop',
  ],
  configuration: defaultLibraryConfiguration,
  configurationState: 'Valid',
  isDefault: false,
  needsMaterialization: false,
  itemCount: 12,
  exportFormats: [
    {
      format: 'original',
      label: 'Original',
    },
    {
      format: 'rvz',
      label: 'RVZ',
    },
    {
      format: 'chd',
      label: 'CHD',
    },
  ],
  createdAt: '2026-09-01T00:00:00Z',
  lastMaterializedAt: '2026-09-01T00:00:00Z',
  updatedAt: '2026-09-01T00:00:00Z',
};
const attachment = {
  collectionId: 'coop',
  name: 'Couch Co-op',
  description: 'Play together',
  coverUrl: null,
  sortOrder: 0,
  isFeatured: true,
  visibleCount: 12,
  totalCount: 20,
  libraryCount: 2,
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(previewLibraryConfiguration).mockResolvedValue({
    data: evaluationPage({
      evaluatedAt: '2026-09-07T00:00:00Z',
      savedCount: 12,
      matchingCount: 12,
      addedCount: 0,
      removedCount: 0,
      removalReasons: [],
      items: [],
      nextCursor: null,
      hasNextPage: false,
    }),
  } as never);
  vi.mocked(getLibrary).mockResolvedValue({
    data: library,
  } as never);
  vi.mocked(listLibraryPreviewTitles).mockResolvedValue({
    data: {
      items: [],
      nextCursor: null,
      hasNextPage: false,
    },
  } as never);
  vi.mocked(listLibraries).mockResolvedValue({
    data: finalPage([
      library,
    ]),
  } as never);
  vi.mocked(listLibraryAttachments).mockResolvedValue({
    data: [
      attachment,
    ],
  } as never);
  vi.mocked(replaceLibraryAttachments).mockResolvedValue({
    data: undefined,
  } as never);
  vi.mocked(listCollections).mockResolvedValue({
    data: finalPage([
      {
        id: 'coop',
        name: 'Couch Co-op',
      },
      {
        id: 'arcade',
        name: 'Arcade Essentials',
      },
    ]),
  } as never);
  vi.mocked(listAdminSystems).mockResolvedValue({
    data: completePage([]),
  } as never);
  vi.mocked(listDats).mockResolvedValue({
    data: finalPage([]),
  } as never);
  vi.mocked(getCatalogFilters).mockResolvedValue({
    data: {
      genres: [],
    },
  } as never);
  vi.mocked(getTitle).mockResolvedValue({
    data: {
      id: 'game',
      name: 'Mario',
    },
  } as never);
});

it('queues an explicit library ZIP with the selected format and links its progress', async () => {
  vi.mocked(exportLibrary).mockResolvedValue({
    data: {
      jobId: 'export-job',
    },
  } as never);
  const router = createMemoryRouter(
    [
      {
        path: '/libraries/:libraryId',
        element: <LibraryWorkspace />,
      },
    ],
    {
      initialEntries: [
        '/libraries/kids',
      ],
    },
  );
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  renderBare(
    <MantineProvider env="test">
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  const user = userEvent.setup();
  await user.selectOptions(
    await screen.findByRole('combobox', {
      name: 'Library export format',
    }),
    'rvz',
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Export library ZIP',
    }),
  );
  expect(exportLibrary).toHaveBeenCalledWith({
    body: {
      libraryId: 'kids',
      format: 'rvz',
    },
  });
  expect(
    await screen.findByRole('link', {
      name: 'View export progress',
    }),
  ).toHaveAttribute('href', '/jobs/export-job');
  expect(screen.getByRole('alert')).toHaveTextContent('RVZ where available');
});

it('defaults library ZIPs to original and surfaces queue failures without a progress link', async () => {
  vi.mocked(exportLibrary).mockResolvedValue({
    error: {
      status: 409,
    },
  } as never);
  const router = createMemoryRouter(
    [
      {
        path: '/libraries/:libraryId',
        element: <LibraryWorkspace />,
      },
    ],
    {
      initialEntries: [
        '/libraries/kids',
      ],
    },
  );
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  renderBare(
    <MantineProvider env="test">
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  const button = await screen.findByRole('button', {
    name: 'Export library ZIP',
  });
  await userEvent.click(button);
  expect(exportLibrary).toHaveBeenCalledWith({
    body: {
      libraryId: 'kids',
      format: 'original',
    },
  });
  expect(await screen.findByText('Failed to start export')).toBeInTheDocument();
  expect(
    screen.queryByRole('link', {
      name: 'View export progress',
    }),
  ).not.toBeInTheDocument();
});

it.each([
  'queued',
  'failed',
] as const)('keeps the explicit CHD library request and shows honest %s progress', async (outcome) => {
  vi.mocked(exportLibrary).mockResolvedValue(
    (outcome === 'queued'
      ? {
          data: {
            jobId: 'chd-export-job',
          },
        }
      : {
          error: {
            status: 403,
          },
        }) as never,
  );
  const router = createMemoryRouter(
    [
      {
        path: '/libraries/:libraryId',
        element: <LibraryWorkspace />,
      },
    ],
    {
      initialEntries: [
        '/libraries/kids',
      ],
    },
  );
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  renderBare(
    <MantineProvider env="test">
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  const user = userEvent.setup();
  const selection = await screen.findByRole('combobox', {
    name: 'Library export format',
  });
  expect(selection).toHaveValue('original');
  await user.selectOptions(selection, 'chd');
  await user.click(
    screen.getByRole('button', {
      name: 'Export library ZIP',
    }),
  );
  expect(exportLibrary).toHaveBeenCalledExactlyOnceWith({
    body: {
      libraryId: 'kids',
      format: 'chd',
    },
  });
  if (outcome === 'queued') {
    expect(
      await screen.findByRole('link', {
        name: 'View export progress',
      }),
    ).toHaveAttribute('href', '/jobs/chd-export-job');
    expect(screen.getByRole('alert')).toHaveTextContent('CHD where available');
  } else {
    expect(await screen.findByText('Failed to start export')).toBeInTheDocument();
    expect(
      screen.queryByRole('link', {
        name: 'View export progress',
      }),
    ).not.toBeInTheDocument();
  }
  await waitFor(() => expect(selection).toBeEnabled());
  expect(selection).toHaveValue('chd');
});

function renderExportWorkspace(exportFormats: Library['exportFormats']) {
  vi.mocked(getLibrary).mockResolvedValue({
    data: {
      ...library,
      exportFormats,
    },
  } as never);
  const router = createMemoryRouter(
    [
      {
        path: '/libraries/:libraryId',
        element: <LibraryWorkspace />,
      },
    ],
    {
      initialEntries: [
        '/libraries/kids',
      ],
    },
  );
  renderBare(
    <MantineProvider env="test">
      <QueryClientProvider
        client={
          new QueryClient({
            defaultOptions: {
              queries: {
                retry: false,
              },
            },
          })
        }
      >
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

it('lists only the export formats the server offers for the library, with its labels', async () => {
  vi.mocked(exportLibrary).mockResolvedValue({
    data: {
      jobId: 'disc-export-job',
    },
  } as never);
  renderExportWorkspace([
    {
      format: 'original',
      label: 'Original',
    },
    {
      format: 'rvz',
      label: 'Disc image',
    },
  ]);
  const user = userEvent.setup();
  const selection = await screen.findByRole('combobox', {
    name: 'Library export format',
  });
  expect(
    Array.from(selection.querySelectorAll('option')).map((option) => option.textContent),
  ).toEqual([
    'Original',
    'Disc image where available',
  ]);
  await user.selectOptions(selection, 'rvz');
  await user.click(
    screen.getByRole('button', {
      name: 'Export library ZIP',
    }),
  );
  expect(exportLibrary).toHaveBeenCalledExactlyOnceWith({
    body: {
      libraryId: 'kids',
      format: 'rvz',
    },
  });
  expect(await screen.findByRole('alert')).toHaveTextContent('Disc image where available');
});

it('exports an original-only library without a format choice', async () => {
  vi.mocked(exportLibrary).mockResolvedValue({
    data: {
      jobId: 'original-export-job',
    },
  } as never);
  renderExportWorkspace([
    {
      format: 'original',
      label: 'Original',
    },
  ]);
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Export library ZIP',
    }),
  );
  expect(
    screen.queryByRole('combobox', {
      name: 'Library export format',
    }),
  ).not.toBeInTheDocument();
  expect(exportLibrary).toHaveBeenCalledExactlyOnceWith({
    body: {
      libraryId: 'kids',
      format: 'original',
    },
  });
  expect(await screen.findByRole('alert')).toHaveTextContent('Original');
});

function renderNavigableExportWorkspace() {
  vi.mocked(getLibrary)
    .mockResolvedValueOnce({
      data: library,
    } as never)
    .mockResolvedValueOnce({
      data: {
        ...library,
        id: 'family',
        name: 'Family',
      },
    } as never);
  const router = createMemoryRouter(
    [
      {
        path: '/libraries/:libraryId',
        element: <LibraryWorkspace />,
      },
    ],
    {
      initialEntries: [
        '/libraries/kids',
      ],
    },
  );
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  renderBare(
    <MantineProvider env="test">
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return {
    router,
    client,
  };
}

it.each([
  'queued',
  'failed',
] as const)('clears the previous library ZIP %s state when navigating to another library', async (outcome) => {
  vi.mocked(exportLibrary).mockResolvedValue(
    (outcome === 'queued'
      ? {
          data: {
            jobId: 'kids-export',
          },
        }
      : {
          error: {
            status: 409,
          },
        }) as never,
  );
  const { router } = renderNavigableExportWorkspace();
  const user = userEvent.setup();
  await user.click(
    await screen.findByRole('button', {
      name: 'Export library ZIP',
    }),
  );
  if (outcome === 'queued')
    await screen.findByRole('link', {
      name: 'View export progress',
    });
  else await screen.findByText('Export unavailable');
  await act(async () => {
    await router.navigate('/libraries/family');
  });
  await screen.findByRole('heading', {
    name: 'Family',
  });
  expect(
    screen.queryByRole('link', {
      name: 'View export progress',
    }),
  ).not.toBeInTheDocument();
  expect(screen.queryByText('Export unavailable')).not.toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Export library ZIP',
    }),
  ).toBeEnabled();
});

it.each([
  'queued',
  'failed',
] as const)('keeps a delayed previous-library ZIP %s response out of the current library', async (outcome) => {
  let complete!: (result: unknown) => void;
  vi.mocked(exportLibrary).mockReturnValueOnce(
    new Promise((resolve) => {
      complete = resolve;
    }) as never,
  );
  const { router, client } = renderNavigableExportWorkspace();
  const user = userEvent.setup();
  await user.click(
    await screen.findByRole('button', {
      name: 'Export library ZIP',
    }),
  );
  expect(exportLibrary).toHaveBeenCalledWith({
    body: {
      libraryId: 'kids',
      format: 'original',
    },
  });
  expect(
    screen.getByRole('button', {
      name: 'Export library ZIP',
    }),
  ).toBeDisabled();
  await act(async () => {
    await router.navigate('/libraries/family');
  });
  await screen.findByRole('heading', {
    name: 'Family',
  });
  expect
    .soft(
      screen.getByRole('button', {
        name: 'Export library ZIP',
      }),
    )
    .toBeEnabled();
  await act(async () => {
    complete(
      outcome === 'queued'
        ? {
            data: {
              jobId: 'kids-export',
            },
          }
        : {
            error: {
              status: 409,
            },
          },
    );
  });
  await waitFor(() => expect(client.isMutating()).toBe(0));
  expect(
    screen.queryByRole('link', {
      name: 'View export progress',
    }),
  ).not.toBeInTheDocument();
  expect(screen.queryByText('Export unavailable')).not.toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Export library ZIP',
    }),
  ).toBeEnabled();
  expect(exportLibrary).toHaveBeenCalledTimes(1);
});

describe('audience libraries', () => {
  it('shows collection counts and keeps definitions on demand', async () => {
    render(
      <MantineProvider env="test">
        <Libraries />
      </MantineProvider>,
    );
    expect(await screen.findByText('1 library')).toBeInTheDocument();
    expect(screen.getByText('1 collection')).toBeInTheDocument();
    expect(screen.queryByText(/A library defines/)).not.toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'About libraries',
      }),
    );
    await waitFor(() => expect(screen.getByText(/A library defines/)).toBeVisible());
  });

  it('creates an empty handpicked library and opens title selection', async () => {
    vi.mocked(createLibrary).mockResolvedValue({
      data: library,
    } as never);
    const router = createMemoryRouter(
      [
        {
          path: '/libraries',
          element: <Libraries />,
        },
        {
          path: '/libraries/:libraryId',
          element: <div>Title selection</div>,
        },
      ],
      {
        initialEntries: [
          '/libraries',
        ],
      },
    );
    const client = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    renderBare(
      <MantineProvider>
        <QueryClientProvider client={client}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </MantineProvider>,
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'New library',
      }),
    );
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Library name',
      }),
      'Kids',
    );
    await userEvent.click(
      screen.getByLabelText('Initial titles', {
        selector: 'input',
      }),
    );
    await userEvent.click(
      await screen.findByRole('option', {
        name: 'None - choose titles individually',
        hidden: true,
      }),
    );
    await userEvent.click(
      within(screen.getByRole('dialog')).getByRole('button', {
        name: 'Create library',
      }),
    );
    await waitFor(() => expect(router.state.location.pathname).toBe('/libraries/kids'));
    expect(router.state.location.search).toBe('?tab=games');
    expect(createLibrary).toHaveBeenCalledWith(
      expect.objectContaining({
        body: expect.objectContaining({
          configuration: expect.objectContaining({
            titleSelectionMode: 'IncludeOnly',
            includeTitleIds: [],
          }),
        }),
      }),
    );
  });
  it('links audience cards to an addressable workspace and filters by name', async () => {
    render(<Libraries />);
    expect(
      await screen.findByRole('link', {
        name: /Kids/,
      }),
    ).toHaveAttribute('href', '/libraries/kids');
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Search libraries',
      }),
      'missing',
    );
    expect(screen.getByText('No matching libraries')).toBeInTheDocument();
    expect(
      screen.queryByRole('link', {
        name: /Kids/,
      }),
    ).not.toBeInTheDocument();
  });
  it('shows server errors when creation fails and preserves the entered name', async () => {
    vi.mocked(createLibrary).mockResolvedValue({
      error: {
        detail: 'This name is already used.',
      },
      response: {
        status: 400,
      },
    } as never);
    render(<Libraries />);
    await userEvent.click(
      screen.getByRole('button', {
        name: 'New library',
      }),
    );
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: /Library name/,
      }),
      'Everyone',
    );
    await userEvent.click(
      screen.getByLabelText('Maximum content-rating age', {
        selector: 'input',
      }),
    );
    await userEvent.click(
      await screen.findByRole('option', {
        name: '8+',
        hidden: true,
      }),
    );
    await userEvent.click(
      within(await screen.findByRole('dialog')).getByRole('button', {
        name: 'Create library',
      }),
    );
    await waitFor(() =>
      expect(createLibrary).toHaveBeenCalledWith(
        expect.objectContaining({
          body: expect.objectContaining({
            configuration: expect.objectContaining({
              contentRatingPolicy: expect.objectContaining({
                maxMinimumAge: 8,
                unknownRatingPolicy: 'NeedsReview',
              }),
            }),
          }),
        }),
      ),
    );
    expect(await screen.findByText('This name is already used.')).toBeInTheDocument();
    expect(
      await screen.findByRole('textbox', {
        name: /Library name/,
      }),
    ).toHaveValue('Everyone');
  });
  it('preserves rating restrictions when switching to handpicked selection', () => {
    const form = toFormState({
      ...library,
      configuration: {
        ...defaultLibraryConfiguration,
        contentRatingPolicy: {
          ...defaultLibraryConfiguration.contentRatingPolicy,
          maxMinimumAge: 8,
          unknownRatingPolicy: 'Hide',
        },
      },
    });
    form.titleSelectionMode = 'IncludeOnly';
    form.includeTitleIds = [
      'game',
    ];
    const configuration = toConfiguration(form);
    expect(configuration.contentRatingPolicy?.maxMinimumAge).toBe(8);
    expect(configuration.contentRatingPolicy?.unknownRatingPolicy).toBe('Hide');
    expect(configuration.includeTitleIds).toEqual([
      'game',
    ]);
  });
  it('defaults to tracked titles and preserves the opt-in across selection modes', () => {
    expect(toConfiguration(toFormState(null)).includeUntrackedTitles).toBe(false);
    const form = toFormState({
      ...library,
      configuration: {
        ...defaultLibraryConfiguration,
        includeUntrackedTitles: true,
      },
    });
    expect(toConfiguration(form).includeUntrackedTitles).toBe(true);
    expect(
      toConfiguration({
        ...form,
        titleSelectionMode: 'IncludeOnly',
      }).includeUntrackedTitles,
    ).toBe(true);
  });

  it('evaluates and saves the untracked-title option for handpicked games', async () => {
    vi.mocked(updateLibrary).mockResolvedValue({
      data: library,
    } as never);
    render(
      <LibraryGames
        library={{
          ...library,
          configuration: {
            ...defaultLibraryConfiguration,
            titleSelectionMode: 'IncludeOnly',
            includeTitleIds: [
              'game',
            ],
          },
        }}
        onDirtyChange={() => {}}
      />,
    );
    const toggle = screen.getByRole('switch', {
      name: /Include untracked titles/,
    });
    expect(toggle).not.toBeChecked();
    await userEvent.click(toggle);
    await waitFor(() =>
      expect(previewLibraryConfiguration).toHaveBeenLastCalledWith(
        expect.objectContaining({
          body: expect.objectContaining({
            configuration: expect.objectContaining({
              includeUntrackedTitles: true,
            }),
          }),
          signal: expect.any(AbortSignal),
        }),
      ),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() =>
      expect(updateLibrary).toHaveBeenCalledWith(
        expect.objectContaining({
          body: expect.objectContaining({
            configuration: expect.objectContaining({
              includeUntrackedTitles: true,
              includeTitleIds: [
                'game',
              ],
            }),
          }),
        }),
      ),
    );
  });

  it('saves rating changes in Games without discarding handpicked games', async () => {
    vi.mocked(updateLibrary).mockResolvedValue({
      data: library,
    } as never);
    render(
      <LibraryRules
        library={{
          ...library,
          configuration: {
            ...defaultLibraryConfiguration,
            titleSelectionMode: 'IncludeOnly',
            includeTitleIds: [
              'game',
            ],
          },
        }}
        section="games"
      />,
    );
    await userEvent.click(
      screen.getByLabelText('Maximum content-rating age', {
        selector: 'input',
      }),
    );
    await userEvent.click(
      await screen.findByRole('option', {
        name: '8+',
        hidden: true,
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() =>
      expect(updateLibrary).toHaveBeenCalledWith(
        expect.objectContaining({
          body: expect.objectContaining({
            configuration: expect.objectContaining({
              includeTitleIds: [
                'game',
              ],
              contentRatingPolicy: expect.objectContaining({
                maxMinimumAge: 8,
              }),
            }),
          }),
        }),
      ),
    );
  });
});

describe('shared collection placement', () => {
  it('shows audience-specific counts and detaches without deleting shared curation', async () => {
    render(<LibraryCollections libraryId="kids" />);
    expect(await screen.findByText('12 of 20 games visible')).toBeInTheDocument();
    expect(screen.getByText('Shared · 2 libraries')).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Detach Couch Co-op',
      }),
    );
    await waitFor(() =>
      expect(replaceLibraryAttachments).toHaveBeenCalledWith({
        path: {
          libraryId: 'kids',
        },
        body: {
          collections: [],
        },
      }),
    );
  });
  it('changes featured placement only for this library', async () => {
    render(<LibraryCollections libraryId="kids" />);
    await userEvent.click(
      await screen.findByRole('switch', {
        name: 'Featured shelf',
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
              collectionId: 'coop',
              isFeatured: false,
            },
          ],
        },
      }),
    );
  });
  it('attaches an existing collection after current placements', async () => {
    render(<LibraryCollections libraryId="kids" />);
    await screen.findByText('Couch Co-op');
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Attach collection',
      }),
    );
    await userEvent.click(
      await screen.findByLabelText('Collection', {
        selector: 'input',
      }),
    );
    await userEvent.click(
      await screen.findByRole('option', {
        name: 'Arcade Essentials',
        hidden: true,
      }),
    );
    await userEvent.click(
      within(await screen.findByRole('dialog')).getByRole('button', {
        name: 'Attach collection',
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
              collectionId: 'coop',
              isFeatured: true,
            },
            {
              collectionId: 'arcade',
              isFeatured: true,
            },
          ],
        },
      }),
    );
  });
  it('shows a failed save without presenting it as successful', async () => {
    vi.mocked(replaceLibraryAttachments).mockResolvedValue({
      error: {
        detail: 'Collection no longer exists.',
      },
    } as never);
    render(<LibraryCollections libraryId="kids" />);
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Detach Couch Co-op',
      }),
    );
    expect(await screen.findByText(/Collection no longer exists/)).toBeInTheDocument();
    expect(screen.getByText('Couch Co-op')).toBeInTheDocument();
  });
});

it('previews the actual library projection, with bounded pagination', async () => {
  vi.mocked(listLibraryPreviewTitles)
    .mockResolvedValueOnce({
      data: {
        items: [
          {
            id: 'one',
            name: 'Mario',
            platformName: 'NES',
            coverUrl: null,
          },
        ],
        nextCursor: '0:one',
        hasNextPage: true,
      },
    } as never)
    .mockResolvedValueOnce({
      data: {
        items: [
          {
            id: 'two',
            name: 'Zelda',
            platformName: 'NES',
            coverUrl: null,
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      },
    } as never);
  render(
    <LibraryPreview
      libraryId="kids"
      name="Kids"
    />,
  );
  expect(await screen.findByText('Mario')).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Show more games',
    }),
  );
  expect(await screen.findByText('Zelda')).toBeInTheDocument();
  expect(listLibraryPreviewTitles).toHaveBeenLastCalledWith({
    path: {
      libraryId: 'kids',
    },
    query: {
      collectionId: undefined,
      cursor: '0:one',
    },
  });
});

it('keeps unsaved audience rules until the user explicitly discards them', async () => {
  const router = createMemoryRouter(
    [
      {
        path: '/libraries/:libraryId',
        element: <LibraryWorkspace />,
      },
    ],
    {
      initialEntries: [
        '/libraries/kids?tab=games',
      ],
    },
  );
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  renderBare(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  await userEvent.click(
    await screen.findByRole('switch', {
      name: /Include missing games/,
    }),
  );
  await userEvent.click(
    screen.getByRole('tab', {
      name: 'Preview',
    }),
  );
  expect(await screen.findByText('Discard unsaved changes?')).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Keep editing',
    }),
  );
  expect(
    screen.getByRole('switch', {
      name: /Include missing games/,
    }),
  ).toBeChecked();
  expect(router.state.location.search).toBe('?tab=games');
  await userEvent.click(
    screen.getByRole('tab', {
      name: 'Preview',
    }),
  );
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Discard',
    }),
  );
  await waitFor(() => expect(router.state.location.search).toBe('?tab=preview'));
  expect(await screen.findByText('Through their eyes')).toBeInTheDocument();
});

it('drops the library details draft after a discarded Back within the same library', async () => {
  const router = createMemoryRouter(
    [
      {
        element: (
          <DraftGuardProvider>
            <Outlet />
          </DraftGuardProvider>
        ),
        children: [
          {
            path: '/libraries/:libraryId',
            element: <LibraryWorkspace />,
          },
        ],
      },
    ],
    {
      initialEntries: [
        '/libraries/kids?tab=collections',
        '/libraries/kids',
      ],
      initialIndex: 1,
    },
  );
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  // jsdom has no layout, so outside the test env Mantine hides an open menu once floating-ui reports its target as hidden.
  renderBare(
    <MantineProvider env="test">
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Library actions',
    }),
  );
  await userEvent.click(
    await screen.findByRole('menuitem', {
      name: 'Library details',
    }),
  );
  const details = await screen.findByRole('dialog', {
    name: 'Library details',
  });
  await userEvent.type(
    within(details).getByRole('textbox', {
      name: 'Library name',
    }),
    ' updated',
  );
  await act(async () => {
    await router.navigate(-1);
  });
  await userEvent.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).getByRole('button', {
      name: 'Discard',
    }),
  );
  await waitFor(() => expect(router.state.location.search).toBe('?tab=collections'));
  await waitFor(() =>
    expect(
      screen.queryByRole('dialog', {
        name: 'Library details',
      }),
    ).not.toBeInTheDocument(),
  );
  await act(async () => {
    await router.navigate('/libraries/kids?tab=access');
  });
  expect(router.state.location.search).toBe('?tab=access');
  expect(
    screen.queryByRole('dialog', {
      name: 'Discard unsaved changes?',
    }),
  ).not.toBeInTheDocument();
});

it('refreshes attached library cards after editing the shared collection', async () => {
  vi.mocked(listLibraryAttachments)
    .mockResolvedValueOnce({
      data: [
        attachment,
      ],
    } as never)
    .mockResolvedValue({
      data: [
        {
          ...attachment,
          name: 'Weekend Co-op',
        },
      ],
    } as never);
  vi.mocked(updateCollection).mockResolvedValue({
    data: {
      id: 'coop',
      name: 'Weekend Co-op',
    },
  } as never);
  function SharedEditor() {
    const update = useUpdateCollection();
    return (
      <button
        type="button"
        onClick={() =>
          update.mutate({
            collectionId: 'coop',
            request: {
              name: 'Weekend Co-op',
            },
          })
        }
      >
        Rename shared collection
      </button>
    );
  }
  render(
    <>
      <LibraryCollections libraryId="kids" />
      <SharedEditor />
    </>,
  );
  expect(await screen.findByText('Couch Co-op')).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Rename shared collection',
    }),
  );
  expect(await screen.findByText('Weekend Co-op')).toBeInTheDocument();
});

describe('draft Games evaluation', () => {
  it('evaluates the saved rules once when Games opens', async () => {
    render(
      <LibraryGames
        library={library}
        onDirtyChange={() => {}}
      />,
    );
    expect(await screen.findByText('12 eligible games')).toBeInTheDocument();
    expect(previewLibraryConfiguration).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({
        body: expect.objectContaining({
          configuration: defaultLibraryConfiguration,
        }),
      }),
    );
  });

  it('hides outdated counts as rules change and evaluates without saving', async () => {
    render(
      <LibraryGames
        library={library}
        onDirtyChange={() => {}}
      />,
    );
    expect(await screen.findByText('12 eligible games')).toBeInTheDocument();
    vi.mocked(previewLibraryConfiguration).mockResolvedValue({
      data: evaluationPage({
        evaluatedAt: '2026-09-07T00:01:00Z',
        savedCount: 12,
        matchingCount: 8,
        addedCount: 0,
        removedCount: 4,
        removalReasons: [
          {
            reason: 'ContentRating',
            count: 4,
          },
        ],
        items: [],
        nextCursor: null,
        hasNextPage: false,
      }),
    } as never);
    await userEvent.click(
      screen.getByLabelText('Maximum content-rating age', {
        selector: 'input',
      }),
    );
    await userEvent.click(
      await screen.findByRole('option', {
        name: '8+',
        hidden: true,
      }),
    );
    expect(screen.queryByText('12 eligible games')).not.toBeInTheDocument();
    expect(
      await screen.findByText(
        '8 eligible games',
        {},
        {
          timeout: 3000,
        },
      ),
    ).toBeInTheDocument();
    expect(screen.getByText('4 · Exceeds the content-rating ceiling')).toBeInTheDocument();
    expect(previewLibraryConfiguration).toHaveBeenLastCalledWith(
      expect.objectContaining({
        body: expect.objectContaining({
          configuration: expect.objectContaining({
            contentRatingPolicy: expect.objectContaining({
              maxMinimumAge: 8,
            }),
          }),
        }),
        signal: expect.any(AbortSignal),
      }),
    );
    expect(updateLibrary).not.toHaveBeenCalled();
  });

  it('searches excluded titles across the catalog and shows an honest error', async () => {
    render(
      <LibraryGames
        library={library}
        onDirtyChange={() => {}}
      />,
    );
    await screen.findByText('12 eligible games');
    vi.mocked(previewLibraryConfiguration).mockResolvedValue({
      error: {
        detail: 'The catalog is updating.',
      },
    } as never);
    await userEvent.type(screen.getByLabelText('Find a game in the catalog'), 'Mario');
    expect(
      await screen.findByText(
        'The catalog is updating.',
        {},
        {
          timeout: 3000,
        },
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText('12 eligible games')).not.toBeInTheDocument();
    expect(previewLibraryConfiguration).toHaveBeenLastCalledWith(
      expect.objectContaining({
        body: expect.objectContaining({
          view: 'all',
          query: 'Mario',
        }),
      }),
    );
  });
});

it('keeps published previews available after uploads and refreshes only on request', async () => {
  vi.mocked(getLibrary).mockResolvedValue({
    data: {
      ...library,
      updatedAt: '2026-09-02T00:00:00Z',
    },
  } as never);
  vi.mocked(materializeLibrary).mockResolvedValue({
    data: {
      jobId: 'refresh-job',
    },
  } as never);
  const router = createMemoryRouter(
    [
      {
        path: '/libraries/:libraryId',
        element: <LibraryWorkspace />,
      },
    ],
    {
      initialEntries: [
        '/libraries/kids?tab=preview',
      ],
    },
  );
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  renderBare(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  expect(await screen.findByText('Updates available')).toBeInTheDocument();
  expect(
    screen.getByText(/Consumers can continue using the published library/),
  ).toBeInTheDocument();
  expect(await screen.findByText('Through their eyes')).toBeInTheDocument();
  expect(materializeLibrary).not.toHaveBeenCalled();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Refresh library',
    }),
  );
  await waitFor(() =>
    expect(materializeLibrary).toHaveBeenCalledWith({
      path: {
        libraryId: 'kids',
      },
    }),
  );
});

it('keeps the published preview while access-narrowing changes wait for an explicit refresh', async () => {
  vi.mocked(getLibrary).mockResolvedValue({
    data: {
      ...library,
      needsMaterialization: true,
    },
  } as never);
  const router = createMemoryRouter(
    [
      {
        path: '/libraries/:libraryId',
        element: <LibraryWorkspace />,
      },
    ],
    {
      initialEntries: [
        '/libraries/kids?tab=preview',
      ],
    },
  );
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  renderBare(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  expect(
    await screen.findByText(
      /Some changes affect which games consumers can see or play\. Consumers keep the published library until you refresh\./,
    ),
  ).toBeInTheDocument();
  expect(await screen.findByText('Through their eyes')).toBeInTheDocument();
  expect(materializeLibrary).not.toHaveBeenCalled();
});

it('never describes a withdrawn publication as the published library', async () => {
  vi.mocked(getLibrary).mockResolvedValue({
    data: {
      ...library,
      needsMaterialization: false,
      lastMaterializedAt: null,
    },
  } as never);
  const router = createMemoryRouter(
    [
      {
        path: '/libraries/:libraryId',
        element: <LibraryWorkspace />,
      },
    ],
    {
      initialEntries: [
        '/libraries/kids',
      ],
    },
  );
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  renderBare(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  expect(
    await screen.findByText(
      /Refresh this library to publish its contents and enable consumer access/,
    ),
  ).toBeInTheDocument();
  expect(
    screen.queryByText(/Consumers can continue using the published library/),
  ).not.toBeInTheDocument();
});

it('requires a first refresh before a new library is published', async () => {
  vi.mocked(getLibrary).mockResolvedValue({
    data: {
      ...library,
      needsMaterialization: true,
      lastMaterializedAt: null,
    },
  } as never);
  const router = createMemoryRouter(
    [
      {
        path: '/libraries/:libraryId',
        element: <LibraryWorkspace />,
      },
    ],
    {
      initialEntries: [
        '/libraries/kids?tab=preview',
      ],
    },
  );
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  renderBare(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  expect(
    await screen.findByText(
      /Refresh this library to publish its contents and enable consumer access/,
    ),
  ).toBeInTheDocument();
  expect(screen.queryByText('Through their eyes')).not.toBeInTheDocument();
  expect(materializeLibrary).not.toHaveBeenCalled();
});
