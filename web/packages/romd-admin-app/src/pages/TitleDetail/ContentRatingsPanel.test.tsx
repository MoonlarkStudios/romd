import { MantineProvider } from '@mantine/core';
import {
  deleteTitleContentRating,
  getTitle,
  replaceTitleContentRating,
  type TitleContentRating,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render as renderBase, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DraftGuardProvider } from '../../components/Administration/DraftGuard';
import type { RatingBoardOption } from '../../hooks/api/useContentRating';
import { useTitleDetail } from '../../hooks/api/useTitleDetail';
import { writtenTitleFixture } from '../../test/msw/fixtures/enrichment';
import { render } from '../../test/utils/render';
import { ContentRatingsPanel } from './ContentRatingsPanel';

vi.mock('@romd/admin-api-client', async (load) => ({
  ...(await load<typeof import('@romd/admin-api-client')>()),
  getTitle: vi.fn(),
  replaceTitleContentRating: vi.fn(),
  deleteTitleContentRating: vi.fn(),
}));

const permissions = vi.hoisted(() => ({
  canManageTitles: true,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => permissions,
}));

const boardCatalog: RatingBoardOption[] = [
  {
    board: 'Esrb',
    categories: [
      {
        code: 'E',
        minimumAge: 0,
        designation: 'Rated',
      },
      {
        code: 'E10+',
        minimumAge: 10,
        designation: 'Rated',
      },
      {
        code: 'RP',
        minimumAge: null,
        designation: 'RatingPending',
      },
    ],
  },
  {
    board: 'Pegi',
    categories: [
      {
        code: 'PEGI 7',
        minimumAge: 7,
        designation: 'Rated',
      },
    ],
  },
];
vi.mock('../../hooks/api/useContentRating', async (load) => ({
  ...(await load<typeof import('../../hooks/api/useContentRating')>()),
  useRatingBoardCatalog: () => ({
    data: boardCatalog,
    isLoading: false,
    isError: false,
  }),
}));

const titleId = 'title-1';
const providerRating: TitleContentRating = {
  board: 'Esrb',
  code: 'E',
  designation: 'Rated',
  minimumAge: 0,
  sourceId: 'igdb',
  descriptors: [
    'Mild violence',
  ],
  synopsis: 'The original rating synopsis.',
};
const titleFixture = (ratings: TitleContentRating[], revision = 'rev-1') =>
  writtenTitleFixture({
    id: titleId,
    revision,
    contentRatings: ratings,
  });

function response<T>(data: T) {
  return {
    data,
    error: undefined,
    request: new Request('http://localhost'),
    response: new Response(),
  };
}

function RatingRoute() {
  const { data } = useTitleDetail(titleId);
  return data ? (
    <ContentRatingsPanel
      title={data}
      titleId={titleId}
    />
  ) : null;
}

function renderPanel(search = '') {
  return render(<RatingRoute />, {
    routerOptions: {
      initialEntries: [
        `/titles/${titleId}/ratings${search}`,
      ],
    },
  });
}

describe('age ratings', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    permissions.canManageTitles = true;
    vi.mocked(getTitle).mockResolvedValue(
      response(
        titleFixture([
          providerRating,
        ]),
      ),
    );
  });

  it('shows official rating marks and provenance only for assigned boards', async () => {
    renderPanel();
    const table = await screen.findByRole('table', {
      name: 'Age ratings',
    });
    expect(
      within(table).getByRole('img', {
        name: 'ESRB Everyone',
      }),
    ).toBeVisible();
    expect(within(table).getByText('All ages')).toBeVisible();
    expect(
      within(table).getByRole('cell', {
        name: 'North America',
      }),
    ).toBeVisible();
    expect(within(table).getByText('Mild violence')).toBeVisible();
    expect(within(table).getByText('IGDB')).toBeVisible();
    expect(
      within(table).queryByRole('button', {
        name: 'PEGI rating',
      }),
    ).toBeNull();
  });

  it('opens a board from the URL and saves the draft against the revision it started with', async () => {
    const user = userEvent.setup();
    vi.mocked(replaceTitleContentRating).mockResolvedValue(
      response(
        titleFixture(
          [
            {
              ...providerRating,
              code: 'E10+',
              sourceId: 'user',
              minimumAge: 10,
            },
          ],
          'rev-2',
        ),
      ),
    );
    renderPanel('?board=Esrb');

    const editor = await screen.findByRole('complementary', {
      name: 'ESRB rating',
    });
    const fields = within(editor);
    const rating = fields.getByRole('combobox', {
      name: 'Age rating',
    });
    expect(rating).toHaveValue('E');
    expect(
      within(rating)
        .getAllByRole('option')
        .map((option) => option.getAttribute('value')),
    ).toEqual([
      '',
      'E',
      'E10+',
      'RP',
    ]);
    expect(
      fields.getByRole('button', {
        name: 'Save changes',
      }),
    ).toBeDisabled();
    await user.selectOptions(rating, 'E10+');
    await user.type(
      fields.getByRole('combobox', {
        name: 'Descriptors',
      }),
      'Fantasy themes{Enter}',
    );
    const synopsis = fields.getByRole('textbox', {
      name: 'Synopsis',
    });
    await user.clear(synopsis);
    await user.type(synopsis, 'Verified by a curator.');
    expect(replaceTitleContentRating).not.toHaveBeenCalled();

    await user.click(
      fields.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() =>
      expect(replaceTitleContentRating).toHaveBeenCalledWith({
        path: {
          titleId,
          board: 'Esrb',
        },
        headers: {
          'If-Match': '"rev-1"',
        },
        body: {
          code: 'E10+',
          descriptors: [
            'Mild violence',
            'Fantasy themes',
          ],
          synopsis: 'Verified by a curator.',
        },
      }),
    );
    expect(await screen.findByText('Age rating saved')).toBeVisible();
  });

  it('offers only unrated boards and leaves the newly saved rating selected', async () => {
    const user = userEvent.setup();
    vi.mocked(replaceTitleContentRating).mockResolvedValue(
      response(
        titleFixture(
          [
            providerRating,
            {
              board: 'Pegi',
              code: 'PEGI 7',
              sourceId: 'user',
              designation: 'Rated',
              minimumAge: 7,
            },
          ],
          'rev-2',
        ),
      ),
    );
    renderPanel();
    await user.click(
      await screen.findByRole('button', {
        name: 'Add rating',
      }),
    );

    const editor = await screen.findByRole('complementary', {
      name: 'Add age rating',
    });
    const board = within(editor).getByRole('combobox', {
      name: 'Rating board',
    });
    expect(
      within(board).queryByRole('option', {
        name: /ESRB/,
      }),
    ).toBeNull();
    await user.selectOptions(board, 'Pegi');
    await user.selectOptions(
      within(editor).getByRole('combobox', {
        name: 'Age rating',
      }),
      'PEGI 7',
    );
    await user.click(
      within(editor).getByRole('button', {
        name: 'Save changes',
      }),
    );

    expect(
      await screen.findByRole('complementary', {
        name: 'PEGI rating',
      }),
    ).toBeVisible();
    expect(replaceTitleContentRating).toHaveBeenCalledWith({
      path: {
        titleId,
        board: 'Pegi',
      },
      headers: {
        'If-Match': '"rev-1"',
      },
      body: {
        code: 'PEGI 7',
      },
    });
  });

  it('clears a manual rating and displays the provider fallback', async () => {
    const user = userEvent.setup();
    vi.mocked(getTitle).mockResolvedValue(
      response(
        titleFixture([
          {
            ...providerRating,
            code: 'E10+',
            sourceId: 'user',
            minimumAge: 10,
          },
        ]),
      ),
    );
    vi.mocked(deleteTitleContentRating).mockResolvedValue(
      response(
        titleFixture(
          [
            providerRating,
          ],
          'rev-2',
        ),
      ),
    );
    renderPanel();
    await user.click(
      await screen.findByRole('button', {
        name: 'ESRB rating',
      }),
    );

    const editor = await screen.findByRole('complementary', {
      name: 'ESRB rating',
    });
    await user.click(
      within(editor).getByRole('button', {
        name: 'Clear override',
      }),
    );
    const confirmation = screen.getByRole('dialog', {
      name: 'Clear the ESRB override?',
    });
    await user.click(
      within(confirmation).getByRole('button', {
        name: 'Clear override',
      }),
    );

    await waitFor(() =>
      expect(deleteTitleContentRating).toHaveBeenCalledWith({
        path: {
          titleId,
          board: 'Esrb',
        },
        headers: {
          'If-Match': '"rev-1"',
        },
      }),
    );
    const table = screen.getByRole('table', {
      name: 'Age ratings',
    });
    expect(await within(table).findByText('IGDB')).toBeVisible();
    expect(
      within(editor).getByRole('combobox', {
        name: 'Age rating',
      }),
    ).toHaveValue('E');
  });

  it('keeps the input after a revision conflict', async () => {
    const user = userEvent.setup();
    vi.mocked(replaceTitleContentRating).mockResolvedValue({
      ...response(undefined),
      response: new Response(null, {
        status: 412,
      }),
    } as Awaited<ReturnType<typeof replaceTitleContentRating>>);
    renderPanel('?board=Esrb');

    const editor = await screen.findByRole('complementary', {
      name: 'ESRB rating',
    });
    const rating = within(editor).getByRole('combobox', {
      name: 'Age rating',
    });
    await user.selectOptions(rating, 'E10+');
    await user.click(
      within(editor).getByRole('button', {
        name: 'Save changes',
      }),
    );

    expect(
      await within(editor).findByText('This title changed since you started editing.'),
    ).toBeVisible();
    expect(rating).toHaveValue('E10+');
    expect(
      within(editor).getByRole('button', {
        name: 'Save changes',
      }),
    ).toBeDisabled();
  });

  it('reloads the server revision after a conflict even while the cached title is fresh', async () => {
    const user = userEvent.setup();
    const cache = new QueryClient({
      defaultOptions: {
        queries: {
          staleTime: 300_000,
          retry: false,
        },
      },
    });
    vi.mocked(getTitle)
      .mockResolvedValueOnce(
        response(
          titleFixture([
            providerRating,
          ]),
        ),
      )
      .mockResolvedValue(
        response(
          titleFixture(
            [
              {
                ...providerRating,
                code: 'RP',
                designation: 'RatingPending',
                minimumAge: null,
              },
            ],
            'rev-2',
          ),
        ),
      );
    vi.mocked(replaceTitleContentRating)
      .mockResolvedValueOnce({
        ...response(undefined),
        response: new Response(null, {
          status: 412,
        }),
      } as Awaited<ReturnType<typeof replaceTitleContentRating>>)
      .mockResolvedValueOnce(
        response(
          titleFixture(
            [
              {
                ...providerRating,
                code: 'E10+',
                sourceId: 'user',
                minimumAge: 10,
              },
            ],
            'rev-3',
          ),
        ),
      );
    render(
      <QueryClientProvider client={cache}>
        <RatingRoute />
      </QueryClientProvider>,
      {
        routerOptions: {
          initialEntries: [
            `/titles/${titleId}/ratings?board=Esrb`,
          ],
        },
      },
    );

    const editor = await screen.findByRole('complementary', {
      name: 'ESRB rating',
    });
    const fields = within(editor);
    const field = fields.getByRole('combobox', {
      name: 'Age rating',
    });
    const save = fields.getByRole('button', {
      name: 'Save changes',
    });
    await user.selectOptions(field, 'E10+');
    await user.click(save);

    await user.click(
      await fields.findByRole('button', {
        name: 'Reload latest',
      }),
    );
    await user.click(
      fields.getByRole('button', {
        name: 'Reload latest',
      }),
    );
    await waitFor(() => expect(field).toHaveValue('RP'));
    expect(getTitle).toHaveBeenCalledTimes(2);
    expect(save).toBeDisabled();

    await user.selectOptions(field, 'E10+');
    await user.click(save);
    const table = screen.getByRole('table', {
      name: 'Age ratings',
    });
    expect(await within(table).findByText('Manual')).toBeVisible();
    expect(replaceTitleContentRating).toHaveBeenNthCalledWith(2, {
      path: {
        titleId,
        board: 'Esrb',
      },
      headers: {
        'If-Match': '"rev-2"',
      },
      body: {
        code: 'E10+',
        descriptors: providerRating.descriptors,
        synopsis: providerRating.synopsis,
      },
    });
  });

  it('keeps unrecognized legacy classifications visible when no official mark exists', async () => {
    vi.mocked(getTitle).mockResolvedValue(
      response(
        titleFixture([
          {
            ...providerRating,
            code: 'Legacy rating',
          },
        ]),
      ),
    );
    renderPanel();
    const table = await screen.findByRole('table', {
      name: 'Age ratings',
    });
    expect(within(table).getByText('ESRB Legacy rating')).toBeVisible();
    expect(within(table).queryByRole('img')).toBeNull();
  });

  it('can keep the provider code as an explicit manual override', async () => {
    const user = userEvent.setup();
    vi.mocked(replaceTitleContentRating).mockResolvedValue(
      response(
        titleFixture(
          [
            {
              ...providerRating,
              sourceId: 'user',
            },
          ],
          'rev-2',
        ),
      ),
    );
    renderPanel('?board=Esrb');

    const editor = await screen.findByRole('complementary', {
      name: 'ESRB rating',
    });
    await user.click(
      within(editor).getByRole('button', {
        name: 'Age rating source: IGDB',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Use as manual rating',
      }),
    );
    await user.click(
      within(editor).getByRole('button', {
        name: 'Save changes',
      }),
    );

    expect(
      await within(
        screen.getByRole('table', {
          name: 'Age ratings',
        }),
      ).findByText('Manual'),
    ).toBeVisible();
    expect(replaceTitleContentRating).toHaveBeenCalledWith({
      path: {
        titleId,
        board: 'Esrb',
      },
      headers: {
        'If-Match': '"rev-1"',
      },
      body: {
        code: 'E',
        descriptors: providerRating.descriptors,
        synopsis: providerRating.synopsis,
      },
    });
  });

  it('keeps a dirty inspector open until keyboard discard is confirmed', async () => {
    const user = userEvent.setup();
    const router = createMemoryRouter(
      [
        {
          path: '/titles/:titleId/ratings',
          element: (
            <DraftGuardProvider>
              <RatingRoute />
            </DraftGuardProvider>
          ),
        },
      ],
      {
        initialEntries: [
          `/titles/${titleId}/ratings?board=Esrb`,
        ],
      },
    );
    renderBase(
      <MantineProvider env="test">
        <QueryClientProvider client={new QueryClient()}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </MantineProvider>,
    );
    const editor = await screen.findByRole('complementary', {
      name: 'ESRB rating',
    });
    const field = within(editor).getByRole('combobox', {
      name: 'Age rating',
    });
    await user.selectOptions(field, 'E10+');
    await user.keyboard('{Escape}');

    const dialog = await screen.findByRole('dialog', {
      name: 'Discard unsaved changes?',
    });
    await user.click(
      within(dialog).getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(field).toHaveValue('E10+');
    expect(router.state.location.search).toBe('?board=Esrb');

    await user.keyboard('{Escape}');
    await user.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard unsaved changes?',
        }),
      ).getByRole('button', {
        name: 'Discard',
      }),
    );
    await waitFor(() => expect(router.state.location.search).toBe(''));
    expect(
      screen.queryByRole('complementary', {
        name: 'ESRB rating',
      }),
    ).toBeNull();
    expect(
      screen.getByRole('button', {
        name: 'ESRB rating',
      }),
    ).toHaveFocus();
  });

  it('keeps rating details available for users who cannot edit', async () => {
    const user = userEvent.setup();
    permissions.canManageTitles = false;
    renderPanel();
    await user.click(
      await screen.findByRole('button', {
        name: 'ESRB rating',
      }),
    );
    const editor = await screen.findByRole('complementary', {
      name: 'ESRB rating',
    });
    expect(within(editor).getByText(providerRating.synopsis ?? '')).toBeVisible();
    expect(
      within(editor).queryByRole('button', {
        name: 'Save changes',
      }),
    ).toBeNull();
    expect(
      screen.queryByRole('button', {
        name: 'Add rating',
      }),
    ).toBeNull();
  });
});
