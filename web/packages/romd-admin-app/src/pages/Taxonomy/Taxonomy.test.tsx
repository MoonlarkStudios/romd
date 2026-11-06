import type { AliasDto, LanguageDto, RegionDto } from '@romd/admin-api-client';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Permissions, Role } from '../../hooks/usePermissions';
import { render } from '../../test/utils/render';
import { Taxonomy } from './index';

let permissions: Permissions;

vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => permissions,
}));

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    previewRegionMerge: vi.fn(),
    previewLanguageMerge: vi.fn(),
    listRegions: vi.fn(),
    listLanguages: vi.fn(),
    listRegionAliases: vi.fn(),
    listLanguageAliases: vi.fn(),
    createRegionAlias: vi.fn(),
    deleteRegionAlias: vi.fn(),
    mergeRegion: vi.fn(),
    createLanguageAlias: vi.fn(),
    deleteLanguageAlias: vi.fn(),
    mergeLanguage: vi.fn(),
    getMe: vi.fn(),
  };
});

import {
  createRegionAlias,
  getMe,
  listLanguages,
  listRegionAliases,
  listRegions,
  mergeRegion,
  previewRegionMerge,
} from '@romd/admin-api-client';

const mockListRegions = listRegions as ReturnType<typeof vi.fn>;
const mockListLanguages = listLanguages as ReturnType<typeof vi.fn>;
const mockListRegionAliases = listRegionAliases as ReturnType<typeof vi.fn>;
const mockAddRegionAlias = createRegionAlias as ReturnType<typeof vi.fn>;
const mockMergeRegions = mergeRegion as ReturnType<typeof vi.fn>;

/** One page holding every row, the shape both collections serve. */
function page<T>(items: T[]) {
  return {
    data: {
      items,
      nextCursor: null,
      hasNextPage: false,
    },
    error: undefined,
  };
}
const mockGetCurrentUser = getMe as ReturnType<typeof vi.fn>;

/** A row's embedded first alias page, complete unless a continuation cursor is given. */
function aliases(items: AliasDto[], nextCursor: string | null = null) {
  return {
    items,
    nextCursor,
    hasNextPage: nextCursor !== null,
  };
}

function makePermissions(role: Role, canManageUsers: boolean): Permissions {
  return {
    canUploadRoms: true,
    canManageTitles: true,
    canTriggerEnrichment: true,
    canManageUsers,
    canEditReferenceData: canManageUsers,
    canConfigureIntegrations: canManageUsers,
    canEditMetadataPolicy: role === 'Admin' || role === 'Manager',
    role,
    hasRole: () => true,
  };
}

const regions: RegionDto[] = [
  {
    key: 'dat-usa',
    ownership: null,
    builtInVersion: null,
    name: 'USA',
    description: null,
    sortOrder: 0,
    retired: false,
    isAutoCreated: false,
    canMerge: true,
    aliases: aliases([
      {
        id: '1',
        alias: 'United States',
        ownership: 'Installation',
      },
    ]),
  },
  {
    key: 'dat-eur',
    ownership: null,
    builtInVersion: null,
    name: 'Europe',
    description: null,
    sortOrder: 1,
    retired: false,
    isAutoCreated: true,
    canMerge: true,
    aliases: aliases([]),
  },
];

const languages: LanguageDto[] = [
  {
    key: 'en',
    ownership: 'Romd',
    builtInVersion: 1,
    name: 'English',
    description: null,
    code: 'En',
    sortOrder: 0,
    retired: false,
    isAutoCreated: false,
    canMerge: false,
    aliases: aliases([
      {
        id: '5',
        alias: 'Eng',
        ownership: 'Installation',
      },
    ]),
  },
];

describe('Taxonomy', () => {
  beforeEach(() => {
    vi.mocked(previewRegionMerge).mockResolvedValue({
      data: {
        aliases: 2,
        sourceGames: 4,
        catalogReleases: 3,
      },
    } as Awaited<ReturnType<typeof previewRegionMerge>>);
    permissions = makePermissions('Admin', true);
    mockListRegions.mockResolvedValue(page(regions));
    mockListLanguages.mockResolvedValue(page(languages));
    mockAddRegionAlias.mockResolvedValue({
      data: undefined,
      error: undefined,
    });
    mockMergeRegions.mockResolvedValue({
      data: undefined,
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

  it('renders regions with their aliases and an auto-created badge', async () => {
    render(<Taxonomy />);

    expect(await screen.findByText('USA')).toBeInTheDocument();
    expect(screen.getByText('United States · Local')).toBeInTheDocument();
    expect(screen.getByText('Europe')).toBeInTheDocument();
    expect(screen.getByText('Auto')).toBeInTheDocument();
  });

  it('hides invalid managed actions and distinguishes alias ownership', async () => {
    mockListRegions.mockResolvedValue(
      page([
        {
          ...regions[0],
          canMerge: false,
          aliases: aliases([
            {
              id: '1',
              alias: 'Managed alias',
              ownership: 'Romd',
            },
            {
              id: '2',
              alias: 'Local alias',
              ownership: 'Installation',
            },
            {
              id: '3',
              alias: 'Legacy alias',
              ownership: 'Unresolved',
            },
          ]),
        },
        regions[1],
      ]),
    );
    render(<Taxonomy />);
    await screen.findByText('USA');
    expect(
      screen.queryByRole('button', {
        name: 'Merge USA into another value',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Merge Europe into another value',
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: 'Remove alias Managed alias from USA',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Remove alias Local alias from USA',
      }),
    ).toBeInTheDocument();
    expect(screen.getByText('Managed alias · ROMD')).toBeInTheDocument();
    expect(screen.getByText('Legacy alias · Ownership unknown')).toBeInTheDocument();
    expect(screen.getByText(/matching aliases may reappear/)).toBeInTheDocument();
  });

  it('adds an alias to a region as an admin', async () => {
    const user = userEvent.setup();
    render(<Taxonomy />);

    await screen.findByText('USA');
    // First row after sortOrder sort is USA.
    await user.click(
      screen.getAllByRole('button', {
        name: /Add alias to/i,
      })[0],
    );
    await user.type(await screen.findByLabelText('New alias'), 'Canada{Enter}');

    await waitFor(() => {
      expect(mockAddRegionAlias).toHaveBeenCalledWith({
        path: {
          regionKey: 'dat-usa',
        },
        body: {
          alias: 'Canada',
        },
      });
    });
  });

  it('merges a region into another, excluding itself as a target', async () => {
    const user = userEvent.setup();
    render(<Taxonomy />);

    await screen.findByText('USA');
    // Open the merge modal for USA (first row).
    await user.click(
      screen.getAllByRole('button', {
        name: /Merge .* into/i,
      })[0],
    );

    const dialog = await screen.findByRole('dialog');
    // Open the target combobox. Mantine renders options into the DOM but keeps
    // the dropdown display:none under jsdom (no layout), so query with
    // { hidden: true } and fire the option click directly.
    await user.click(within(dialog).getByRole('combobox'));

    // The source (USA) must not be offered as its own merge target.
    expect(
      screen.queryByRole('option', {
        name: 'USA',
        hidden: true,
      }),
    ).not.toBeInTheDocument();

    fireEvent.click(
      screen.getByRole('option', {
        name: 'Europe',
        hidden: true,
      }),
    );
    await user.click(
      within(dialog).getByRole('button', {
        name: /^merge$/i,
      }),
    );

    await waitFor(() => {
      expect(vi.mocked(previewRegionMerge)).toHaveBeenCalledWith({
        path: {
          regionKey: 'dat-usa',
        },
        body: {
          targetKey: 'dat-eur',
        },
      });
      expect(mockMergeRegions).toHaveBeenCalledWith({
        path: {
          regionKey: 'dat-usa',
        },
        body: {
          targetKey: 'dat-eur',
        },
      });
    });
  });

  it('preserves the server explanation when a merge is rejected', async () => {
    const user = userEvent.setup();
    const detail = 'A registered reference identity cannot be merged into another identity.';
    mockMergeRegions.mockResolvedValue({
      error: {
        detail,
      },
      response: {
        status: 409,
      },
    });
    render(<Taxonomy />);
    await screen.findByText('USA');
    await user.click(
      screen.getByRole('button', {
        name: 'Merge USA into another value',
      }),
    );
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('combobox'));
    fireEvent.click(
      screen.getByRole('option', {
        name: 'Europe',
        hidden: true,
      }),
    );
    await user.click(
      within(dialog).getByRole('button', {
        name: /^merge$/i,
      }),
    );
    expect(await screen.findByText(detail)).toBeInTheDocument();
    expect(screen.queryByText('Failed to merge regions.')).not.toBeInTheDocument();
  });

  it('is read-only without admin rights', async () => {
    permissions = makePermissions('Manager', false);
    render(<Taxonomy />);

    await screen.findByText('USA');
    expect(
      screen.queryByRole('button', {
        name: /Add alias to/i,
      }),
    ).not.toBeInTheDocument();
    expect(screen.getByText(/editing requires an administrator/i)).toBeInTheDocument();
  });

  it('searches names and every alias on the server, keeping the complete list as merge targets', async () => {
    const user = userEvent.setup();
    mockListRegions.mockImplementation(
      ({
        query,
      }: {
        query: {
          query?: string;
        };
      }) => Promise.resolve(page(query.query ? regions.slice(0, 1) : regions)),
    );
    render(<Taxonomy />);
    await screen.findByText('Europe');

    await user.type(screen.getByPlaceholderText('Filter regions or aliases...'), 'estados');

    await waitFor(() => expect(screen.queryByText('Europe')).not.toBeInTheDocument());
    expect(mockListRegions).toHaveBeenCalledWith({
      query: {
        cursor: undefined,
        limit: 100,
        query: 'estados',
      },
    });
    await user.click(
      screen.getByRole('button', {
        name: 'Merge USA into another value',
      }),
    );
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('combobox'));
    expect(
      screen.getByRole('option', {
        name: 'Europe',
        hidden: true,
      }),
    ).toBeInTheDocument();
  });

  it('pages the aliases of a row beyond its embedded first page', async () => {
    const user = userEvent.setup();
    mockListRegions.mockResolvedValue(
      page([
        {
          ...regions[0],
          aliases: aliases(regions[0].aliases.items, 'after-first'),
        },
        regions[1],
      ]),
    );
    mockListRegionAliases.mockImplementation(
      ({
        query,
      }: {
        query: {
          cursor: string;
        };
      }) =>
        Promise.resolve({
          data:
            query.cursor === 'after-first'
              ? aliases(
                  [
                    {
                      id: '11',
                      alias: 'Estados Unidos',
                      ownership: 'Installation',
                    },
                  ],
                  'after-second',
                )
              : aliases([
                  {
                    id: '12',
                    alias: 'Amerika',
                    ownership: 'Romd',
                  },
                ]),
          error: undefined,
        }),
    );
    render(<Taxonomy />);
    await screen.findByText('United States · Local');
    expect(mockListRegionAliases).not.toHaveBeenCalled();

    await user.click(
      screen.getByRole('button', {
        name: 'Show more aliases of USA',
      }),
    );
    expect(await screen.findByText('Estados Unidos · Local')).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Show more aliases of USA',
      }),
    );

    expect(await screen.findByText('Amerika · ROMD')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Remove alias Estados Unidos from USA',
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: 'Show more aliases of USA',
      }),
    ).not.toBeInTheDocument();
    expect(mockListRegionAliases.mock.calls.map(([options]) => options)).toEqual([
      {
        path: {
          regionKey: 'dat-usa',
        },
        query: {
          cursor: 'after-first',
          limit: 50,
        },
      },
      {
        path: {
          regionKey: 'dat-usa',
        },
        query: {
          cursor: 'after-second',
          limit: 50,
        },
      },
    ]);
  });

  it('shows languages with their code on the Languages tab', async () => {
    const user = userEvent.setup();
    render(<Taxonomy />);

    await user.click(
      screen.getByRole('tab', {
        name: /languages/i,
      }),
    );

    expect(await screen.findByText('English')).toBeInTheDocument();
    expect(screen.getByText('En')).toBeInTheDocument();
    expect(screen.getByText('Eng · Local')).toBeInTheDocument();
  });
});
