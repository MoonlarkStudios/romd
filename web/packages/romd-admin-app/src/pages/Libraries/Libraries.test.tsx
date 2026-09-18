import { MantineProvider } from '@mantine/core';
import type { LibraryDto } from '@romd/admin-api-client';
import { getLibraryById, updateCollection } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render as renderBare, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useUpdateCollection } from '../../hooks/api/useCollectionManagement';
import { render } from '../../test/utils/render';
import { Libraries } from './index';
import { LibraryCollections } from './LibraryCollections';
import { LibraryGames } from './LibraryGames';
import { LibraryPreview } from './LibraryPreview';
import { LibraryRules } from './LibraryRules';
import { LibraryWorkspace } from './LibraryWorkspace';
import { toConfiguration, toFormState } from './libraryForm';
import { defaultLibraryConfiguration } from './libraryOptions';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getLibraryById: vi.fn(), listLibraries: vi.fn(),
  createLibrary: vi.fn(),
  evaluateLibrary: vi.fn(),
  updateLibrary: vi.fn(),
  updateCollection: vi.fn(),
  getLibraryAttachments: vi.fn(),
  setLibraryAttachments: vi.fn(),
  getLibraryPreview: vi.fn(),
  listCollections: vi.fn(),
  listAdminSystems: vi.fn(),
  listDats: vi.fn(),
  getCatalogFilters: vi.fn(),
  getCurrentUser: vi.fn(),
  getTitleById: vi.fn(),
  searchCatalog: vi.fn(),
}));

import {
  createLibrary,
  evaluateLibrary,
  getCatalogFilters,
  getLibraryAttachments,
  getLibraryPreview,
  getTitleById,
  listAdminSystems,
  listCollections,
  listDats,
  listLibraries,
  setLibraryAttachments,
  updateLibrary,
} from '@romd/admin-api-client';

const library: LibraryDto = {
  id: 'kids',
  name: 'Kids',
  collectionIds: ['coop'],
  configuration: defaultLibraryConfiguration,
  configurationState: 'Valid',
  isDefault: false,
  needsMaterialization: false,
  itemCount: 12,
  createdAt: '2026-09-01T00:00:00Z',
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
  vi.mocked(evaluateLibrary).mockResolvedValue({data: {evaluatedAt:'2026-09-07T00:00:00Z', savedCount:12, matchingCount:12, addedCount:0, removedCount:0, removalReasons:[],items:[],nextCursor:null}} as never);
  vi.mocked(getLibraryById).mockResolvedValue({data:library} as never);
  vi.mocked(getLibraryPreview).mockResolvedValue({data:{items:[],nextCursor:null}} as never);
  vi.mocked(listLibraries).mockResolvedValue({
    data: [
      library,
    ],
  } as never);
  vi.mocked(getLibraryAttachments).mockResolvedValue({
    data: [
      attachment,
    ],
  } as never);
  vi.mocked(setLibraryAttachments).mockResolvedValue({
    data: undefined,
  } as never);
  vi.mocked(listCollections).mockResolvedValue({
    data: [
      {
        id: 'coop',
        name: 'Couch Co-op',
      },
      {
        id: 'arcade',
        name: 'Arcade Essentials',
      },
    ],
  } as never);
  vi.mocked(listAdminSystems).mockResolvedValue({
    data: [],
  } as never);
  vi.mocked(listDats).mockResolvedValue({
    data: [],
  } as never);
  vi.mocked(getCatalogFilters).mockResolvedValue({
    data: {
      genres: [],
    },
  } as never);
  vi.mocked(getTitleById).mockResolvedValue({
    data: {
      id: 'game',
      name: 'Mario',
    },
  } as never);
});

describe('audience libraries', () => {
  it('shows collection counts and keeps definitions on demand', async () => {
    render(<MantineProvider env="test"><Libraries /></MantineProvider>);
    expect(await screen.findByText('1 library')).toBeInTheDocument();
    expect(screen.getByText('1 collection')).toBeInTheDocument();
    expect(screen.queryByText(/A library defines/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'About libraries' }));
    await waitFor(() => expect(screen.getByText(/A library defines/)).toBeVisible());
  });

  it('creates an empty handpicked library and opens title selection', async () => {
    vi.mocked(createLibrary).mockResolvedValue({ data: library } as never);
    const router = createMemoryRouter([
      { path: '/libraries', element: <Libraries /> },
      { path: '/libraries/:libraryId', element: <div>Title selection</div> },
    ], { initialEntries: ['/libraries'] });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderBare(<MantineProvider><QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider></MantineProvider>);
    await userEvent.click(screen.getByRole('button', { name: 'New library' }));
    await userEvent.type(await screen.findByRole('textbox', { name: 'Library name' }), 'Kids');
    await userEvent.click(screen.getByLabelText('Initial titles', { selector: 'input' }));
    await userEvent.click(await screen.findByRole('option', { name: 'None - choose titles individually', hidden: true }));
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Create library' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/libraries/kids'));
    expect(router.state.location.search).toBe('?tab=games');
    expect(createLibrary).toHaveBeenCalledWith(expect.objectContaining({ body: expect.objectContaining({ configuration: expect.objectContaining({ titleSelectionMode: 'IncludeOnly', includeTitleIds: [] }) }) }));
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
    await userEvent.click(screen.getByLabelText('Maximum content-rating age', { selector: 'input' }));
    await userEvent.click(await screen.findByRole('option', { name: '8+', hidden: true }));
    await userEvent.click(
      within(await screen.findByRole('dialog')).getByRole('button', {
        name: 'Create library',
      }),
    );
    await waitFor(() => expect(createLibrary).toHaveBeenCalledWith(expect.objectContaining({ body: expect.objectContaining({ configuration: expect.objectContaining({ contentRatingPolicy: expect.objectContaining({ maxMinimumAge: 8, unknownRatingPolicy: 'NeedsReview' }) }) }) })));
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
      expect(setLibraryAttachments).toHaveBeenCalledWith({
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
      expect(setLibraryAttachments).toHaveBeenCalledWith({
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
      expect(setLibraryAttachments).toHaveBeenCalledWith({
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
    vi.mocked(setLibraryAttachments).mockResolvedValue({
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
  vi.mocked(getLibraryPreview)
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
  expect(getLibraryPreview).toHaveBeenLastCalledWith({
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
  const router = createMemoryRouter([{path:'/libraries/:libraryId',element:<LibraryWorkspace/>}], {initialEntries:['/libraries/kids?tab=games']});
  const client = new QueryClient({defaultOptions:{queries:{retry:false}}});
  renderBare(<MantineProvider><QueryClientProvider client={client}><RouterProvider router={router}/></QueryClientProvider></MantineProvider>);
  await userEvent.click(await screen.findByRole('switch',{name:/Include missing games/}));
  await userEvent.click(screen.getByRole('tab',{name:'Preview'}));
  expect(await screen.findByText('Leave unsaved changes?')).toBeInTheDocument();
  await userEvent.click(screen.getByRole('button',{name:'Keep editing'}));
  expect(screen.getByRole('switch',{name:/Include missing games/})).toBeChecked();
  expect(router.state.location.search).toBe('?tab=games');
  await userEvent.click(screen.getByRole('tab',{name:'Preview'}));
  await userEvent.click(await screen.findByRole('button',{name:'Discard changes'}));
  await waitFor(() => expect(router.state.location.search).toBe('?tab=preview'));
  expect(await screen.findByText('Through their eyes')).toBeInTheDocument();
});


it('refreshes attached library cards after editing the shared collection', async () => {
  vi.mocked(getLibraryAttachments).mockResolvedValueOnce({data:[attachment]} as never)
    .mockResolvedValue({data:[{...attachment,name:'Weekend Co-op'}]} as never);
  vi.mocked(updateCollection).mockResolvedValue({data:{id:'coop',name:'Weekend Co-op'}} as never);
  function SharedEditor() {
    const update = useUpdateCollection();
    return <button type="button" onClick={() => update.mutate({collectionId:'coop',request:{name:'Weekend Co-op'}})}>Rename shared collection</button>;
  }
  render(<><LibraryCollections libraryId="kids"/><SharedEditor/></>);
  expect(await screen.findByText('Couch Co-op')).toBeInTheDocument();
  await userEvent.click(screen.getByRole('button',{name:'Rename shared collection'}));
  expect(await screen.findByText('Weekend Co-op')).toBeInTheDocument();
});


describe('draft Games evaluation', () => {
  it('hides outdated counts as rules change and evaluates without saving', async () => {
    render(<LibraryGames library={library} onDirtyChange={() => {}} />);
    expect(await screen.findByText('12 eligible games')).toBeInTheDocument();
    vi.mocked(evaluateLibrary).mockResolvedValue({data: {evaluatedAt:'2026-09-07T00:01:00Z', savedCount:12, matchingCount:8, addedCount:0, removedCount:4, removalReasons:[{reason:'ContentRating',count:4}],items:[],nextCursor:null}} as never);
    await userEvent.click(screen.getByLabelText('Maximum content-rating age', {selector:'input'}));
    await userEvent.click(await screen.findByRole('option', {name:'8+',hidden:true}));
    expect(screen.queryByText('12 eligible games')).not.toBeInTheDocument();
    expect(await screen.findByText('8 eligible games', {}, {timeout:3000})).toBeInTheDocument();
    expect(screen.getByText('4 · Exceeds the content-rating ceiling')).toBeInTheDocument();
    expect(evaluateLibrary).toHaveBeenLastCalledWith(expect.objectContaining({body:expect.objectContaining({configuration:expect.objectContaining({contentRatingPolicy:expect.objectContaining({maxMinimumAge:8})})}),signal:expect.any(AbortSignal)}));
    expect(updateLibrary).not.toHaveBeenCalled();
  });

  it('searches excluded titles across the catalog and shows an honest error', async () => {
    render(<LibraryGames library={library} onDirtyChange={() => {}} />);
    await screen.findByText('12 eligible games');
    vi.mocked(evaluateLibrary).mockResolvedValue({error:{detail:'The catalog is updating.'}} as never);
    await userEvent.type(screen.getByLabelText('Find a game in the catalog'), 'Mario');
    expect(await screen.findByText('The catalog is updating.', {}, {timeout:3000})).toBeInTheDocument();
    expect(screen.queryByText('12 eligible games')).not.toBeInTheDocument();
    expect(evaluateLibrary).toHaveBeenLastCalledWith(expect.objectContaining({body:expect.objectContaining({view:'all',search:'Mario'})}));
  });
});
