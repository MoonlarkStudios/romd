import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { createMemoryRouter, MemoryRouter, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { titleDetailKeys } from '../../hooks/api/useTitleDetail';
import { writtenTitleFixture, writtenTitleViewFixture } from '../../test/msw/fixtures/enrichment';
import { server } from '../../test/msw/server';
import { DraftGuardProvider } from '../Administration/DraftGuard';
import type { ArtworkRole } from '../artworkPresentation';
import { ArtworkPanel } from './ArtworkPanel';
import { openManual } from './manualDocument';
import type { TitleEditorOutcome } from './titleWorkbenchModel';

/** pdf.js does not run under jsdom; a four-page manual stands in for the document it would open. */
vi.mock('./manualDocument', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./manualDocument')>()),
  openManual: vi.fn(async () => ({
    promise: Promise.resolve({
      numPages: 4,
      getData: async () =>
        new Uint8Array([
          37,
          80,
          68,
          70,
        ]),
      getPage: async () => ({
        getViewport: ({ scale }: { scale: number }) => ({
          width: 100 * scale,
          height: 130 * scale,
        }),
        render: () => ({
          promise: Promise.resolve(),
          cancel: () => undefined,
        }),
        cleanup: () => true,
      }),
    }),
    cancel: vi.fn(),
  })),
}));

/** The title the page shows; uploads send its revision in If-Match. */
const shownTitle = http.get('http://localhost/api/titles/title1', () =>
  HttpResponse.json(
    writtenTitleFixture({
      id: 'title1',
      revision: 'rev-1',
    }),
  ),
);

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: {
      roles: [
        'Manager',
      ],
    },
  }),
}));
const title = writtenTitleViewFixture({
  id: 'title1',
  systemKey: 'platform1',
  name: 'Test Game',
  enrichmentStatus: 'None',
});
const selections = [
  {
    role: 'Poster',
    mode: 'Pinned',
    revision: '1',
    pinnedAssetId: 'asset1',
    pendingJobId: null,
  },
  {
    role: 'Hero',
    mode: 'Automatic',
    revision: '0',
    pinnedAssetId: null,
    pendingJobId: null,
  },
  {
    role: 'Backdrop',
    mode: 'Automatic',
    revision: '0',
    pinnedAssetId: null,
    pendingJobId: null,
  },
  {
    role: 'Logo',
    mode: 'Automatic',
    revision: '0',
    pinnedAssetId: null,
    pendingJobId: null,
  },
];
const titles = 'http://localhost/api/titles/title1';
/** The artwork family the panel reads: every role and the family revision. */
const family = (roles = selections) => ({
  revision: 'family-1',
  roles,
});
/** A role as a role write answers it. */
const written = (role: string, mode: string, revision: string) => ({
  role,
  mode,
  revision,
  pinnedAssetId: null,
  pendingJobId: null,
});
const candidate = {
  reference: 'protected-reference',
  candidateId: 'candidate-7',
  providerAssetId: '7',
  role: 'Poster',
  width: 600,
  height: 900,
  style: 'alternate',
  attribution: 'Artist',
  sourcePageUrl: 'https://www.steamgriddb.com/grid/7',
};
const createUrl = vi.fn(() => 'blob:artwork-preview');
const revokeUrl = vi.fn();
function renderPanel(
  value: TitleDetail = title,
  outcome: TitleEditorOutcome = {},
  initialRole: ArtworkRole | null = 'Backdrop',
  tab: 'presentation' | 'library' = 'presentation',
) {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const element = (current: TitleDetail) => (
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <MemoryRouter
          initialEntries={[
            tab === 'library' ? '/?artwork=library' : '/',
          ]}
        >
          <ArtworkPanel
            title={current}
            titleId="title1"
            initialRole={initialRole ?? undefined}
            {...outcome}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
  const view = render(element(value));
  return {
    ...view,
    cache,
    rerenderTitle: (current: TitleDetail) => view.rerender(element(current)),
  };
}
async function discover() {
  const user = userEvent.setup();
  await user.click(
    await screen.findByRole('button', {
      name: 'Edit poster',
    }),
  );
  await user.click(
    await screen.findByRole('radio', {
      name: 'SteamGridDB',
    }),
  );
  await screen.findByRole('button', {
    name: 'Select artwork 7',
  });
  return user;
}
describe('Artwork curation', () => {
  beforeEach(() => {
    client.setConfig({
      baseUrl: 'http://localhost',
    });
    vi.stubGlobal(
      'URL',
      class extends URL {
        static createObjectURL = createUrl;
        static revokeObjectURL = revokeUrl;
      },
    );
    createUrl.mockClear();
    revokeUrl.mockClear();
    server.use(
      http.get(`${titles}/artwork-rejections`, () =>
        HttpResponse.json({
          items: [],
          nextCursor: null,
          hasNextPage: false,
        }),
      ),
      http.get('http://localhost/api/titles/title1/provider-matches', () =>
        HttpResponse.json([
          {
            providerId: 'steamgriddb',
            providerName: 'SteamGridDB',
            isAvailable: true,
            capabilities: [
              'artwork',
            ],
            revision: '00000000-0000-0000-0000-000000000001',
            state: 'Confirmed',
            externalId: '42',
            game: {
              id: '42',
              name: 'Test Game',
              url: 'https://www.steamgriddb.com/game/42',
            },
            metadataNeedsRefresh: false,
          },
        ]),
      ),
      http.get('http://localhost/api/titles/title1/artwork-acquisition', () =>
        HttpResponse.json({
          outcomes: [],
          activeJobId: null,
        }),
      ),
      http.get('http://localhost/api/systems/platform1', () =>
        HttpResponse.json({
          id: 'platform1',
          name: 'Test platform',
        }),
      ),
      http.get(`http://localhost/api/artwork-providers`, () =>
        HttpResponse.json([
          {
            id: 'steamgriddb',
            name: 'SteamGridDB',
            isAvailable: true,
            supportsLanguageFilter: false,
            gameId: '42',
            gameName: 'Test Game',
            gameUrl: 'https://www.steamgriddb.com/game/42',
            roles: [
              {
                role: 'Backdrop',
                dimensions: [
                  '1920x620',
                ],
                styles: [
                  'alternate',
                ],
              },
              {
                role: 'Poster',
                dimensions: [
                  '600x900',
                ],
                styles: [
                  'alternate',
                ],
              },
              {
                role: 'Hero',
                dimensions: [
                  '1920x620',
                ],
                styles: [
                  'alternate',
                ],
              },
              {
                role: 'Logo',
                dimensions: [],
                styles: [
                  'official',
                ],
              },
            ],
          },
        ]),
      ),
      http.get(`${titles}/artwork`, () => HttpResponse.json(family())),
      http.get(`${titles}/artwork-assets`, () =>
        HttpResponse.json({
          items: [],
          nextCursor: null,
          hasNextPage: false,
        }),
      ),
      http.get(`${titles}/artwork-candidates`, () =>
        HttpResponse.json({
          items: [
            candidate,
          ],
          hasNextPage: false,
          nextCursor: null,
        }),
      ),
      http.post(
        `${titles}/artwork-candidates/preview`,
        () =>
          new HttpResponse(
            new Uint8Array([
              1,
              2,
              3,
            ]),
            {
              headers: {
                'Content-Type': 'image/png',
              },
            },
          ),
      ),
    );
  });
  afterEach(() => {
    client.setConfig({
      baseUrl: '',
    });
    vi.unstubAllGlobals();
    sessionStorage.clear();
  });
  it('opens on Presentation with each role as players see it, its state and one edit action', async () => {
    renderPanel(
      {
        ...title,
        artwork: [
          {
            role: 'Poster',
            assetId: 'asset1',
            contentVersion: null,
            url: '/artwork/poster',
            width: null,
            height: null,
            originalWidth: null,
            originalHeight: null,
            focalX: 20,
            focalY: 80,
            fit: 'Cover',
            fallbackReason: 'None',
            variants: [],
          },
        ],
      },
      {},
      null,
    );
    expect(
      screen.getByRole('tab', {
        name: 'Presentation',
      }),
    ).toHaveAttribute('aria-selected', 'true');
    const roles = screen.getByRole('group', {
      name: 'Presentation roles',
    });
    const canvas = screen.getByRole('region', {
      name: 'Presentation canvas',
    });
    expect(roles.compareDocumentPosition(canvas) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(
      within(roles)
        .getAllByRole('button')
        .map((button) => button.getAttribute('aria-label')),
    ).toEqual([
      'Edit poster',
      'Edit backdrop',
      'Edit banner',
      'Edit logo',
    ]);
    const poster = await within(roles).findByRole('button', {
      name: 'Edit poster',
      description: 'Pinned',
    });
    expect(poster.querySelector('img')).toHaveStyle({
      objectFit: 'cover',
      objectPosition: '20% 80%',
    });
    expect(
      within(roles).getByRole('button', {
        name: 'Edit logo',
        description: 'Missing',
      }),
    ).toBeEnabled();
    expect(
      screen.queryByRole('region', {
        name: / editor$/,
      }),
    ).not.toBeInTheDocument();

    const user = userEvent.setup();
    const edit = within(roles).getByRole('button', {
      name: 'Edit banner',
    });
    await user.click(edit);
    expect(edit).toHaveAttribute('aria-expanded', 'true');
    expect(
      screen.getByRole('region', {
        name: 'Banner editor',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('radio', {
        name: 'Saved',
      }),
    ).toBeChecked();
    await user.keyboard('{Escape}');
    expect(
      screen.queryByRole('region', {
        name: / editor$/,
      }),
    ).not.toBeInTheDocument();
    expect(edit).toHaveFocus();
  });

  it('keeps the image library one tab away and returns to Presentation', async () => {
    renderPanel(title, {}, null);
    const user = userEvent.setup();
    await user.click(
      screen.getByRole('tab', {
        name: 'Image library',
      }),
    );
    expect(await screen.findByText('The library is empty')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Collect from providers',
      }),
    ).toBeEnabled();
    expect(
      screen.queryByRole('group', {
        name: 'Presentation roles',
      }),
    ).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('tab', {
        name: 'Presentation',
      }),
    );
    expect(
      await screen.findByRole('button', {
        name: 'Edit logo',
        description: 'Missing',
      }),
    ).toBeInTheDocument();
  });

  it('saves role and focal-point drafts from the selected image and retries only unfinished roles', async () => {
    let familyState = selections;
    const poster = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"1"');
      expect(await request.json()).toEqual({
        mode: 'Pinned',
        assetId: 'retained-cover',
        mediaId: null,
        focalX: 50,
        focalY: 50,
      });
      const next = {
        ...written('Poster', 'Pinned', '2'),
        pinnedAssetId: 'retained-cover',
      };
      familyState = familyState.map((role) => (role.role === 'Poster' ? next : role));
      return HttpResponse.json(next);
    });
    const backdrop = vi
      .fn<({ request }: { request: Request }) => Promise<Response>>()
      .mockResolvedValueOnce(
        HttpResponse.json(
          {
            detail: 'Temporary failure',
          },
          {
            status: 503,
          },
        ),
      )
      .mockImplementation(async ({ request }) => {
        expect(request.headers.get('If-Match')).toBe('"0"');
        expect(await request.json()).toEqual({
          mode: 'Pinned',
          assetId: null,
          mediaId: 'cover1',
          focalX: 51,
          focalY: 60,
        });
        return HttpResponse.json(written('Backdrop', 'Pinned', '1'));
      });
    server.use(
      http.get(`${titles}/artwork`, () => HttpResponse.json(family(familyState))),
      http.get(`${titles}/artwork-assets`, () =>
        HttpResponse.json({
          items: [
            {
              id: 'retained-cover',
              role: 'Poster',
              sourceId: 'igdb',
              mediaIds: [
                'cover1',
              ],
              artwork: {
                role: 'Poster',
                url: '/artwork/retained-cover',
              },
            },
          ],
          nextCursor: null,
          hasNextPage: false,
        }),
      ),
      http.put(`${titles}/artwork/Poster`, poster),
      http.put(`${titles}/artwork/Backdrop`, backdrop),
    );
    renderPanel(
      {
        ...title,
        media: [
          {
            id: 'cover1',
            type: 'Cover',
            sourceId: 'igdb',
            url: '/media/cover1',
            isPrimary: true,
          },
        ],
      },
      {},
      null,
      'library',
    );
    const user = userEvent.setup();
    const preview = screen.getByRole('button', {
      name: 'Preview cover from IGDB',
    });
    await user.click(preview);
    const editor = within(
      screen.getByRole('dialog', {
        name: 'Cover',
      }),
    );
    expect(
      editor.getByRole('link', {
        name: 'Download',
      }),
    ).toHaveAttribute('href', '/media/cover1');
    expect(preview).toHaveAttribute('aria-haspopup', 'dialog');
    await waitFor(() =>
      expect(
        editor.getByRole('combobox', {
          name: 'Poster assignment',
        }),
      ).toBeEnabled(),
    );
    await user.selectOptions(
      editor.getByRole('combobox', {
        name: 'Poster assignment',
      }),
      'Pinned',
    );
    await user.selectOptions(
      editor.getByRole('combobox', {
        name: 'Backdrop assignment',
      }),
      'Pinned',
    );
    await user.click(
      editor.getByRole('combobox', {
        name: 'Focal point role',
      }),
    );
    await user.click(
      await screen.findByRole('option', {
        name: 'Backdrop',
        exact: true,
      }),
    );
    const focal = editor.getByRole('slider', {
      name: 'Focal point',
    });
    act(() => focal.focus());
    await user.keyboard('{ArrowRight}{Shift>}{ArrowDown}{/Shift}');
    expect(focal).toHaveAttribute('aria-valuetext', 'Horizontal 51%, vertical 60%');
    expect(poster).not.toHaveBeenCalled();
    expect(backdrop).not.toHaveBeenCalled();

    const save = editor.getByRole('button', {
      name: 'Save changes',
    });
    await user.click(save);
    expect(await editor.findByRole('alert')).toHaveTextContent(
      '1 role was saved. Backdrop: Temporary failure',
    );
    expect(
      editor.getByRole('combobox', {
        name: 'Poster assignment',
      }),
    ).toHaveValue('');
    expect(
      editor.getByRole('combobox', {
        name: 'Backdrop assignment',
      }),
    ).toHaveValue('Pinned');
    await user.click(save);
    await screen.findByText('Changes saved.');
    expect(poster).toHaveBeenCalledOnce();
    expect(backdrop).toHaveBeenCalledTimes(2);

    await user.keyboard('{Escape}');
    expect(
      screen.queryByRole('dialog', {
        name: 'Cover',
      }),
    ).not.toBeInTheDocument();
    await waitFor(() => expect(preview).toHaveFocus());
  });

  it('clears an assigned role from the selected image and preserves the old selection on discard', async () => {
    const clear = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"1"');
      expect(await request.json()).toEqual({
        mode: 'Empty',
      });
      return HttpResponse.json(written('Poster', 'Empty', '2'));
    });
    server.use(http.put(`${titles}/artwork/Poster`, clear));
    renderPanel(
      {
        ...title,
        media: [
          {
            id: 'cover1',
            type: 'Cover',
            url: '/media/cover1',
            sourceId: 'user',
            isPrimary: true,
          },
        ],
        artwork: [
          {
            role: 'Poster',
            url: '/media/cover1',
            assetId: null,
            contentVersion: null,
            width: null,
            height: null,
            originalWidth: null,
            originalHeight: null,
            fit: 'Cover',
            fallbackReason: 'None',
            variants: [],
          },
        ],
      },
      {},
      null,
      'library',
    );
    const user = userEvent.setup();
    const preview = screen.getByRole('button', {
      name: 'Preview cover from Your uploads',
    });
    expect(within(preview).getByText('Poster')).toBeInTheDocument();
    await user.click(preview);
    const editor = within(
      screen.getByRole('dialog', {
        name: 'Cover',
      }),
    );
    const poster = editor.getByRole('combobox', {
      name: 'Poster assignment',
    });
    await waitFor(() => expect(poster).toBeEnabled());
    await user.selectOptions(poster, 'Empty');
    await user.click(
      editor.getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(poster).toHaveValue('');
    expect(clear).not.toHaveBeenCalled();
    await user.selectOptions(poster, 'Empty');
    await user.click(
      editor.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() => expect(clear).toHaveBeenCalledOnce());
  });

  it('asks before Escape closes an image inspector with an unsaved draft', async () => {
    const router = createMemoryRouter(
      [
        {
          path: '/',
          element: (
            <DraftGuardProvider>
              <ArtworkPanel
                titleId="title1"
                title={{
                  ...title,
                  media: [
                    {
                      id: 'cover1',
                      type: 'Cover',
                      url: '/media/cover1',
                      sourceId: 'user',
                      isPrimary: true,
                    },
                  ],
                }}
              />
            </DraftGuardProvider>
          ),
        },
      ],
      {
        initialEntries: [
          '/?artwork=library',
        ],
      },
    );
    render(
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
    const user = userEvent.setup();
    const preview = screen.getByRole('button', {
      name: 'Preview cover from Your uploads',
    });
    await user.click(preview);
    const editor = within(
      screen.getByRole('dialog', {
        name: 'Cover',
      }),
    );
    const poster = editor.getByRole('combobox', {
      name: 'Poster assignment',
    });
    await waitFor(() => expect(poster).toBeEnabled());
    await user.selectOptions(poster, 'Pinned');

    await user.keyboard('{Escape}');
    const confirmation = within(
      await screen.findByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    );
    await user.click(
      confirmation.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(poster).toHaveValue('Pinned');
    expect(router.state.location.search).toBe('?artwork=library&image=media-cover1');

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
    await waitFor(() =>
      expect(
        screen.queryByRole('dialog', {
          name: 'Cover',
        }),
      ).not.toBeInTheDocument(),
    );
    expect(router.state.location.search).toBe('?artwork=library');
    await waitFor(() => expect(preview).toHaveFocus());
  });

  it('shows wide SteamGridDB backdrops by default and applies only after crop review', async () => {
    const apply = vi.fn(
      async ({
        request,
        params,
      }: {
        request: Request;
        params: {
          role?: string;
        };
      }) => {
        expect(params.role).toBe('Backdrop');
        expect(request.headers.get('If-Match')).toBe('"0"');
        expect(await request.json()).toMatchObject({
          candidateReference: candidate.reference,
          focalX: 50,
          focalY: 50,
        });
        return HttpResponse.json({
          jobId: 'backdrop-job',
        });
      },
    );
    server.use(
      http.get(`${titles}/artwork-candidates`, ({ request }) => {
        expect(new URL(request.url).searchParams.get('role')).toBe('Backdrop');
        return HttpResponse.json({
          items: [
            {
              ...candidate,
              role: 'Backdrop',
              width: 1920,
              height: 620,
            },
          ],
          hasNextPage: false,
          nextCursor: null,
        });
      }),
      http.post(`${titles}/artwork/:role/apply`, apply),
      http.get('http://localhost/api/jobs/backdrop-job', () =>
        HttpResponse.json({
          id: 'backdrop-job',
          status: 'Queued',
          isTerminal: false,
        }),
      ),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('radio', {
        name: 'SteamGridDB',
      }),
    );
    const choice = await screen.findByRole('button', {
      name: 'Select artwork 7',
    });
    expect(
      screen.getByRole('switch', {
        name: /Suitable landscape dimensions/,
      }),
    ).not.toBeChecked();
    expect(within(choice).getByText(/Requires cropping/)).toBeInTheDocument();
    expect(
      await within(choice).findByRole('img', {
        name: 'Test Game backdrop',
      }),
    ).toBeInTheDocument();
    await user.click(choice);
    await user.click(
      screen.getByRole('radio', {
        name: 'Adjust crop',
      }),
    );
    expect(
      await screen.findByRole('slider', {
        name: 'Focal point',
      }),
    ).toBeInTheDocument();
    expect(apply).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
        exact: true,
      }),
    );
    await waitFor(() => expect(apply).toHaveBeenCalledOnce());
  });
  it('renders linked provider capabilities without provider-specific filters or labels', async () => {
    const queries: URLSearchParams[] = [];
    server.use(
      http.get(`http://localhost/api/artwork-providers`, ({ request }) => {
        queries.push(new URL(request.url).searchParams);
        return HttpResponse.json([
          {
            id: 'igdb',
            name: 'IGDB',
            isAvailable: true,
            gameId: '42',
            gameName: 'Test Game',
            gameUrl: 'https://www.igdb.com/games/test-game',
            roles: [
              {
                role: 'Poster',
                dimensions: [],
                styles: [],
              },
            ],
          },
        ]);
      }),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit poster',
      }),
    );
    await user.click(
      await screen.findByRole('radio', {
        name: 'IGDB',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Select artwork 7',
      }),
    );
    expect(
      screen.queryByRole('combobox', {
        name: 'Dimensions',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('combobox', {
        name: 'Style',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('link', {
        name: 'Test Game',
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('radio', {
        name: 'SteamGridDB',
      }),
    ).not.toBeInTheDocument();
    expect(
      queries.some((query) => query.get('titleId') === 'title1' && query.get('role') === 'Poster'),
    ).toBe(true);
  });

  it('opens backdrop review with suitable candidates and previews without applying', async () => {
    const apply = vi.fn();
    server.use(
      http.get('http://localhost/api/titles/title1/artwork-acquisition', () =>
        HttpResponse.json({
          outcomes: [
            {
              role: 'Backdrop',
              status: 'NeedsReview',
              updatedAt: '2026-09-15T00:00:00Z',
            },
          ],
          activeJobId: null,
        }),
      ),
      http.get(`http://localhost/api/artwork-providers`, () =>
        HttpResponse.json([
          {
            id: 'igdb',
            name: 'IGDB',
            isAvailable: true,
            gameId: '42',
            roles: [
              {
                role: 'Backdrop',
                dimensions: [],
                styles: [],
              },
            ],
          },
        ]),
      ),
      http.get(`${titles}/artwork-candidates`, () =>
        HttpResponse.json({
          items: [
            {
              ...candidate,
              role: 'Backdrop',
              providerAssetId: 'scene',
              candidateId: 'candidate-scene',
              reference: 'scene-ref',
              width: 1920,
              height: 1080,
            },
            {
              ...candidate,
              role: 'Backdrop',
              providerAssetId: 'banner',
              candidateId: 'candidate-banner',
              reference: 'banner-ref',
              width: 1920,
              height: 620,
            },
          ],
          hasNextPage: false,
          nextCursor: null,
        }),
      ),
      http.post(`${titles}/artwork/:role/apply`, apply),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Review backdrop',
      }),
    );
    await screen.findByRole('button', {
      name: 'Select artwork scene',
    });
    expect(
      screen.queryByRole('button', {
        name: 'Select artwork banner',
      }),
    ).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Select artwork scene',
      }),
    );

    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('radio', {
        name: 'Mobile',
      }),
    );
    expect(
      within(
        screen.getByRole('region', {
          name: 'Presentation canvas',
        }),
      ).getByRole('figure', {
        name: 'Mobile preview, 390 pixels wide',
      }),
    ).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
        exact: true,
      }),
    );
    expect(apply).not.toHaveBeenCalled();
  });

  it('previews a hero upload locally and cancellation never uploads it', async () => {
    const upload = vi.fn(() => HttpResponse.json({}));
    server.use(shownTitle, http.post('http://localhost/api/titles/title1/media', upload));
    const view = renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit banner',
      }),
    );
    await user.click(
      screen.getByRole('radio', {
        name: 'Upload',
      }),
    );
    const input = document.querySelector<HTMLInputElement>('input[type="file"]');
    if (!input) throw new Error('Missing artwork upload input');
    await user.upload(
      input,
      new File(
        [
          'image',
        ],
        'hero.png',
        {
          type: 'image/png',
        },
      ),
    );
    await screen.findByRole('region', {
      name: 'Banner editor',
    });
    expect(screen.queryByLabelText('Image type')).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
        exact: true,
      }),
    );
    expect(upload).not.toHaveBeenCalled();
    view.unmount();
    expect(revokeUrl).toHaveBeenCalled();
  });
  it('uploads a logo through the existing workflow and pins the retained snapshot', async () => {
    const upload = vi.fn(({ request }: { request: Request }) => {
      expect(new URL(request.url).searchParams.get('type')).toBe('logo');
      return HttpResponse.json({
        id: 'logo1',
        type: 'Logo',
        sourceId: 'user',
        url: '/media/logo1',
      });
    });
    const pin = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"0"');
      expect(await request.json()).toEqual({
        mode: 'Pinned',
        mediaId: 'logo1',
        assetId: null,
        focalX: 50,
        focalY: 50,
      });
      return HttpResponse.json(written('Poster', 'Pinned', '1'));
    });
    server.use(
      shownTitle,
      http.post('http://localhost/api/titles/title1/media', upload),
      http.put(`${titles}/artwork/Logo`, pin),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit logo',
      }),
    );
    await user.click(
      screen.getByRole('radio', {
        name: 'Upload',
      }),
    );
    const input = document.querySelector<HTMLInputElement>('input[type="file"]');
    if (!input) throw new Error('Missing artwork upload input');
    await user.upload(
      input,
      new File(
        [
          'image',
        ],
        'logo.png',
        {
          type: 'image/png',
        },
      ),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );

    await waitFor(() => expect(upload).toHaveBeenCalledOnce());
    await waitFor(() => expect(pin).toHaveBeenCalledOnce());
  });
  it.each([
    {
      role: 'Hero',
      label: 'banner',
    },
    {
      role: 'Backdrop',
      label: 'backdrop',
    },
  ])('assigns an existing cover to $label independently of its image type', async ({
    role,
    label,
  }) => {
    const pin = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"0"');
      expect(await request.json()).toEqual({
        mode: 'Pinned',
        mediaId: 'cover1',
        assetId: null,
        focalX: 0,
        focalY: 100,
      });
      return HttpResponse.json(written('Poster', 'Pinned', '1'));
    });
    server.use(http.put(`${titles}/artwork/${role}`, pin));
    renderPanel({
      ...title,
      media: [
        {
          id: 'cover1',
          type: 'Cover',
          sourceId: 'igdb',
          url: '/media/cover1',
          isPrimary: true,
        },
      ],
    });
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: `Edit ${label}`,
      }),
    );
    await user.click(
      within(
        screen.getByRole('region', {
          name: / editor$/,
        }),
      ).getByRole('button', {
        name: 'Actions for Cover cover1',
      }),
    );
    await user.click(
      screen.getByRole('menuitem', {
        name: `Use as ${label}`,
      }),
    );
    await screen.findByRole('region', {
      name: new RegExp(`^${label} editor$`, 'i'),
    });
    expect(pin).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('radio', {
        name: 'Mobile',
      }),
    );
    expect(
      screen.getByRole('radio', {
        name: 'Mobile',
      }),
    ).toBeChecked();
    expect(
      screen.getByRole('figure', {
        name: 'Mobile preview, 390 pixels wide',
      }),
    ).toBeInTheDocument();
    await user.click(
      screen.getByRole('radio', {
        name: 'Adjust crop',
      }),
    );
    const focal = await screen.findByRole('slider', {
      name: 'Focal point',
    });
    act(() => focal.focus());
    await user.keyboard('{Shift>}{ArrowLeft>5/}{ArrowDown>5/}{/Shift}');
    expect(focal).toHaveAttribute('aria-valuetext', 'Horizontal 0%, vertical 100%');
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() => expect(pin).toHaveBeenCalledOnce());
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.queryByText('Primary')).not.toBeInTheDocument();
  });

  it('saves a dragged and keyed focal point once through Save changes', async () => {
    const pin = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"0"');
      expect(await request.json()).toEqual({
        mode: 'Pinned',
        mediaId: 'cover1',
        assetId: null,
        focalX: 23,
        focalY: 65,
      });
      return HttpResponse.json(written('Poster', 'Pinned', '1'));
    });
    server.use(http.put(`${titles}/artwork/Backdrop`, pin));
    vi.stubGlobal(
      'PointerEvent',
      class extends MouseEvent {
        readonly pointerId = 1;
      },
    );
    Object.defineProperty(Element.prototype, 'setPointerCapture', {
      configurable: true,
      value: vi.fn(),
    });
    try {
      renderPanel({
        ...title,
        media: [
          {
            id: 'cover1',
            type: 'Cover',
            sourceId: 'igdb',
            url: '/media/cover1',
            isPrimary: true,
          },
        ],
      });
      const user = userEvent.setup();
      await user.click(
        await screen.findByRole('button', {
          name: 'Edit backdrop',
        }),
      );
      await user.click(
        within(
          screen.getByRole('region', {
            name: / editor$/,
          }),
        ).getByRole('button', {
          name: 'Actions for Cover cover1',
        }),
      );
      await user.click(
        screen.getByRole('menuitem', {
          name: 'Use as backdrop',
        }),
      );
      await user.click(
        screen.getByRole('radio', {
          name: 'Adjust crop',
        }),
      );
      const focal = await screen.findByRole('slider', {
        name: 'Focal point',
      });
      vi.spyOn(focal, 'getBoundingClientRect').mockReturnValue({
        left: 0,
        top: 0,
        width: 200,
        height: 100,
      } as DOMRect);
      fireEvent.pointerDown(focal, {
        button: 0,
        clientX: 10,
        clientY: 10,
      });
      fireEvent.pointerMove(focal, {
        clientX: 50,
        clientY: 75,
      });
      fireEvent.pointerUp(focal);
      fireEvent.keyDown(focal, {
        key: 'ArrowLeft',
      });
      fireEvent.keyDown(focal, {
        key: 'ArrowLeft',
      });
      fireEvent.keyDown(focal, {
        key: 'ArrowUp',
        shiftKey: true,
      });
      fireEvent.keyUp(focal, {
        key: 'ArrowUp',
      });
      expect(focal).toHaveAttribute('aria-valuetext', 'Horizontal 23%, vertical 65%');
      expect(pin).not.toHaveBeenCalled();
      await user.click(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      );
      await waitFor(() => expect(pin).toHaveBeenCalledOnce());
    } finally {
      Reflect.deleteProperty(Element.prototype, 'setPointerCapture');
    }
  });

  it('reloads the latest selection after a revision conflict only once the curator confirms discarding the preview', async () => {
    let selectionReads = 0;
    server.use(
      http.get(`${titles}/artwork`, () => {
        selectionReads += 1;
        return HttpResponse.json(family());
      }),
      http.get(`${titles}/artwork-candidates`, () =>
        HttpResponse.json({
          items: [
            {
              ...candidate,
              role: 'Backdrop',
              width: 1920,
              height: 1080,
            },
          ],
          hasNextPage: false,
          nextCursor: null,
        }),
      ),
      http.post(`${titles}/artwork/:role/apply`, () =>
        HttpResponse.json(
          {
            detail: 'Selection changed',
          },
          {
            status: 412,
          },
        ),
      ),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('radio', {
        name: 'SteamGridDB',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Select artwork 7',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Save changes',
        exact: true,
      }),
    );
    const composer = screen.getByRole('region', {
      name: 'Backdrop editor',
    });
    expect(
      await within(composer).findByText(
        'The backdrop changed since you opened it. Reload the latest artwork, then choose again.',
      ),
    ).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Reload latest',
      }),
    );
    await user.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Reload the latest artwork?',
        }),
      ).getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(within(composer).getByText('Draft · New image')).toBeInTheDocument();
    const readsBeforeReload = selectionReads;
    await user.click(
      screen.getByRole('button', {
        name: 'Reload latest',
      }),
    );
    await user.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Reload the latest artwork?',
        }),
      ).getByRole('button', {
        name: 'Reload latest',
      }),
    );
    expect(within(composer).queryByText('Draft · New image')).not.toBeInTheDocument();
    expect(
      within(composer).queryByRole('button', {
        name: 'Reload latest',
      }),
    ).not.toBeInTheDocument();
    await waitFor(() => expect(selectionReads).toBeGreaterThan(readsBeforeReload));
    expect(revokeUrl).toHaveBeenCalledWith('blob:artwork-preview');
  });

  it('tells the workbench when artwork is applied and when a preview is discarded', async () => {
    const onResolved = vi.fn();
    const onDismissed = vi.fn();
    server.use(
      http.get(`${titles}/artwork-candidates`, () =>
        HttpResponse.json({
          items: [
            {
              ...candidate,
              role: 'Backdrop',
              width: 1920,
              height: 1080,
            },
          ],
          hasNextPage: false,
          nextCursor: null,
        }),
      ),
      http.post(`${titles}/artwork/:role/apply`, () =>
        HttpResponse.json({
          jobId: 'backdrop-job',
        }),
      ),
      http.get('http://localhost/api/jobs/backdrop-job', () =>
        HttpResponse.json({
          id: 'backdrop-job',
          status: 'Queued',
          isTerminal: false,
        }),
      ),
    );
    renderPanel(title, {
      onResolved,
      onDismissed,
    });
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('radio', {
        name: 'SteamGridDB',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Select artwork 7',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Discard',
      }),
    );
    expect(onDismissed).toHaveBeenCalledOnce();
    expect(onResolved).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Select artwork 7',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Save changes',
        exact: true,
      }),
    );
    await waitFor(() => expect(onResolved).toHaveBeenCalledOnce());
    expect(onDismissed).toHaveBeenCalledOnce();
  });

  it('keeps the chooser open on a stale selection conflict', async () => {
    server.use(
      http.put(`${titles}/artwork/Hero`, () =>
        HttpResponse.json(
          {
            status: 412,
          },
          {
            status: 412,
          },
        ),
      ),
    );
    renderPanel({
      ...title,
      media: [
        {
          id: 'cover1',
          type: 'Cover',
          sourceId: 'user',
          url: '/media/cover1',
        },
      ],
    });
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit banner',
      }),
    );
    await user.click(
      within(
        screen.getByRole('region', {
          name: / editor$/,
        }),
      ).getByRole('button', {
        name: 'Actions for Cover cover1',
      }),
    );
    await user.click(
      screen.getByRole('menuitem', {
        name: 'Use as banner',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    expect(
      await within(
        screen.getByRole('region', {
          name: 'Banner editor',
        }),
      ).findByText(/The banner changed since you opened it/),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Reload latest',
      }),
    ).toBeInTheDocument();
  });

  it('reuses retained artwork while providers are unavailable', async () => {
    const pin = vi.fn(() => HttpResponse.json(written('Poster', 'Pinned', '2')));
    server.use(
      http.get(`http://localhost/api/artwork-providers`, () => HttpResponse.json([])),
      http.get(`${titles}/artwork-assets`, () =>
        HttpResponse.json({
          items: [
            {
              id: 'saved1',
              role: 'Poster',
              sourceId: 'steamgriddb',
              mediaIds: [],
              artwork: {
                role: 'Poster',
                assetId: 'saved1',
                url: '/artwork/saved1',
              },
            },
          ],
          nextCursor: null,
          hasNextPage: false,
        }),
      ),
      http.put(`${titles}/artwork/Poster`, pin),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit poster',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Actions for Cover saved1',
      }),
    );
    await user.click(
      screen.getByRole('menuitem', {
        name: 'Use as poster',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() => expect(pin).toHaveBeenCalledOnce());
  });
  it('cancels an image preview without changing the assignment', async () => {
    const pin = vi.fn(() => HttpResponse.json(written('Poster', 'Pinned', '1')));
    server.use(http.put(`${titles}/artwork/Hero`, pin));
    renderPanel({
      ...title,
      media: [
        {
          id: 'cover1',
          type: 'Cover',
          sourceId: 'user',
          url: '/media/cover1',
        },
      ],
    });
    const user = userEvent.setup();
    expect(
      screen.queryByRole('button', {
        name: 'Use as banner',
      }),
    ).not.toBeInTheDocument();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit banner',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Actions for Cover cover1',
      }),
    );
    await user.click(
      screen.getByRole('menuitem', {
        name: 'Use as banner',
      }),
    );
    await user.click(
      await within(
        screen.getByRole('region', {
          name: 'Presentation canvas',
        }),
      ).findByRole('radio', {
        name: 'Saved',
        exact: true,
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
        exact: true,
      }),
    );
    expect(
      screen.queryByRole('button', {
        name: 'Save changes',
      }),
    ).not.toBeInTheDocument();
    expect(pin).not.toHaveBeenCalled();
  });
  it('previews without applying and revokes temporary image URLs on close', async () => {
    const apply = vi.fn();
    server.use(http.post(`${titles}/artwork/:role/apply`, apply));
    const view = renderPanel();
    const user = await discover();
    await user.click(
      screen.getByRole('button', {
        name: 'Select artwork 7',
      }),
    );

    await waitFor(() => expect(createUrl).toHaveBeenCalled());
    expect(
      await screen.findByRole('button', {
        name: 'Save changes',
      }),
    ).toBeInTheDocument();
    expect(apply).not.toHaveBeenCalled();
    view.unmount();
    expect(revokeUrl).toHaveBeenCalledWith('blob:artwork-preview');
  });
  it('works without randomUUID and reuses the selected request UUID on retry', async () => {
    vi.stubGlobal('crypto', {
      getRandomValues: crypto.getRandomValues.bind(crypto),
    });
    expect(crypto.randomUUID).toBeUndefined();
    const requests: unknown[] = [];
    server.use(
      http.post(`${titles}/artwork/:role/apply`, async ({ request }) => {
        requests.push(await request.json());
        return HttpResponse.json(
          {
            detail: 'Temporary failure',
          },
          {
            status: 503,
          },
        );
      }),
    );
    renderPanel();
    const user = await discover();
    await user.click(
      screen.getByRole('button', {
        name: 'Select artwork 7',
      }),
    );

    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await screen.findByText('Temporary failure');
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() => expect(requests).toHaveLength(2));
    expect(requests[0]).toEqual(requests[1]);
    expect(requests[0]).toEqual({
      candidateReference: candidate.reference,
      focalX: 50,
      focalY: 50,
      requestId: expect.stringMatching(
        /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
      ),
    });
  });
  it('returns a pin to automatic without contacting the provider', async () => {
    const automatic = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"1"');
      expect(await request.json()).toEqual({
        mode: 'Automatic',
      });
      return HttpResponse.json(written('Poster', 'Automatic', '2'));
    });
    server.use(http.put(`${titles}/artwork/Poster`, automatic));
    renderPanel();
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Edit poster',
      }),
    );
    const user = userEvent.setup();
    await user.click(
      screen.getByRole('button', {
        name: 'Automatic',
        exact: true,
      }),
    );
    expect(
      screen.getByRole('button', {
        name: 'Automatic',
        exact: true,
      }),
    ).toHaveAttribute('aria-pressed', 'true');
    expect(automatic).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() => expect(automatic).toHaveBeenCalledOnce());
  });

  it('stages an intentionally empty role, supports undo, and writes only on Save changes', async () => {
    const leave = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"0"');
      expect(await request.json()).toEqual({
        mode: 'Empty',
      });
      return HttpResponse.json(written('Logo', 'Empty', '1'));
    });
    server.use(http.put(`${titles}/artwork/Logo`, leave));
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit logo',
      }),
    );
    const empty = screen.getByRole('button', {
      name: 'Intentionally empty',
    });
    await user.click(empty);
    expect(empty).toHaveAttribute('aria-pressed', 'true');
    expect(leave).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Undo logo change',
      }),
    );
    expect(empty).toHaveAttribute('aria-pressed', 'false');
    expect(
      screen.queryByRole('button', {
        name: 'Save changes',
      }),
    ).not.toBeInTheDocument();
    await user.click(empty);
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() => expect(leave).toHaveBeenCalledOnce());
  });

  it('requires pinning an automatic image before crop edits and preserves staged mode choices', async () => {
    renderPanel({
      ...title,
      artwork: [
        {
          role: 'Backdrop',
          assetId: 'current-backdrop',
          url: '/artwork/current-backdrop',
          contentVersion: null,
          width: null,
          height: null,
          originalWidth: null,
          originalHeight: null,
          fit: 'Cover',
          focalX: 30,
          focalY: 70,
          fallbackReason: 'None',
          variants: [],
        },
      ],
    });
    const user = userEvent.setup();
    const editor = within(
      await screen.findByRole('region', {
        name: 'Backdrop editor',
      }),
    );
    await user.click(
      editor.getByRole('radio', {
        name: 'Adjust crop',
      }),
    );
    const pin = await editor.findByRole('button', {
      name: 'Pin this image to adjust its crop',
    });
    expect(editor.queryByRole('slider')).not.toBeInTheDocument();
    await waitFor(() => expect(pin).toBeEnabled());
    await user.click(pin);
    expect(editor.getByRole('slider')).toHaveAttribute(
      'aria-valuetext',
      'Horizontal 30%, vertical 70%',
    );

    const automatic = editor.getByRole('button', {
      name: 'Automatic',
      exact: true,
    });
    await user.click(automatic);
    expect(automatic).toHaveAttribute('aria-pressed', 'true');
    expect(editor.queryByRole('slider')).not.toBeInTheDocument();
    expect(
      editor.queryByRole('button', {
        name: 'Pin this image to adjust its crop',
      }),
    ).not.toBeInTheDocument();

    const empty = editor.getByRole('button', {
      name: 'Intentionally empty',
    });
    await user.click(empty);
    expect(empty).toHaveAttribute('aria-pressed', 'true');
    expect(editor.queryByRole('slider')).not.toBeInTheDocument();
    const canvas = within(
      screen.getByRole('region', {
        name: 'Presentation canvas',
      }),
    );
    const canvasElement = screen.getByRole('region', {
      name: 'Presentation canvas',
    });
    expect(canvasElement.querySelector('.romd-detail-backdrop img')).not.toBeInTheDocument();
    await user.click(
      canvas.getByRole('radio', {
        name: 'Saved',
      }),
    );
    expect(canvasElement.querySelector('.romd-detail-backdrop img')).toHaveAttribute(
      'src',
      '/artwork/current-backdrop',
    );
  });

  it('saves multiple roles together and retries only the failed role', async () => {
    let familyState = selections;
    const poster = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"1"');
      expect(await request.json()).toEqual({
        mode: 'Automatic',
      });
      const current = written('Poster', 'Automatic', '2');
      familyState = familyState.map((role) => (role.role === 'Poster' ? current : role));
      return HttpResponse.json(current);
    });
    const logo = vi
      .fn(async ({ request }: { request: Request }) => {
        expect(request.headers.get('If-Match')).toBe('"0"');
        expect(await request.json()).toEqual({
          mode: 'Empty',
        });
        const current = written('Logo', 'Empty', '1');
        familyState = familyState.map((role) => (role.role === 'Logo' ? current : role));
        return HttpResponse.json(current);
      })
      .mockResolvedValueOnce(
        HttpResponse.json(
          {
            detail: 'Temporary failure',
          },
          {
            status: 503,
          },
        ),
      );
    server.use(
      http.get(`${titles}/artwork`, () => HttpResponse.json(family(familyState))),
      http.put(`${titles}/artwork/Poster`, poster),
      http.put(`${titles}/artwork/Logo`, logo),
    );
    const cache = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    const router = createMemoryRouter([
      {
        path: '/',
        element: (
          <DraftGuardProvider>
            <ArtworkPanel
              titleId="title1"
              title={title}
            />
          </DraftGuardProvider>
        ),
      },
    ]);
    render(
      <MantineProvider env="test">
        <QueryClientProvider client={cache}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </MantineProvider>,
    );
    const user = userEvent.setup();
    const posterRole = await screen.findByRole('button', {
      name: 'Edit poster',
      description: 'Missing',
    });
    await user.click(posterRole);
    await user.click(
      screen.getByRole('button', {
        name: 'Automatic',
        exact: true,
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Edit logo',
        exact: true,
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Intentionally empty',
      }),
    );
    const bar = within(
      screen.getByRole('region', {
        name: 'Unsaved presentation changes',
      }),
    );
    const save = bar.getByRole('button', {
      name: 'Save changes',
    });
    expect(bar.getByText(/2 unsaved changes/)).toBeInTheDocument();
    await user.click(
      screen.getByRole('tab', {
        name: 'Image library',
      }),
    );
    const leave = within(
      await screen.findByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    );
    await user.click(
      leave.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(router.state.location.search).toBe('?image=role-Logo');
    expect(poster).not.toHaveBeenCalled();
    expect(logo).not.toHaveBeenCalled();
    await user.click(save);
    expect(
      await screen.findByRole('button', {
        name: 'Edit logo',
        description: 'Needs attention',
      }),
    ).toBeInTheDocument();
    expect(posterRole).toHaveAccessibleDescription('Missing · Saved');
    expect(bar.getByText(/1 unsaved change: Logo/)).toBeInTheDocument();
    await user.click(save);
    expect(
      await screen.findByRole('button', {
        name: 'Edit logo',
        description: 'Intentionally empty · Saved',
      }),
    ).toBeInTheDocument();
    expect(poster).toHaveBeenCalledOnce();
    expect(logo).toHaveBeenCalledTimes(2);
  });

  it('keeps the unsaved composition while browsing different capability filters', async () => {
    const filters: string[] = [];
    server.use(
      http.get(`${titles}/artwork-candidates`, ({ request }) => {
        filters.push(new URL(request.url).searchParams.get('style') ?? '');
        return HttpResponse.json({
          items: [
            candidate,
          ],
          hasNextPage: false,
          nextCursor: null,
        });
      }),
    );
    renderPanel();
    const user = await discover();
    await user.click(
      screen.getByRole('button', {
        name: 'Select artwork 7',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('combobox', {
        name: 'Style',
      }),
    );
    await user.click(
      screen.getByRole('option', {
        name: 'alternate',
      }),
    );
    await waitFor(() => expect(filters).toContain('alternate'));
    expect(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    ).toBeEnabled();
  });
  it('browses and applies a logo with its own revision after comparison on both backgrounds', async () => {
    const logo = {
      ...candidate,
      reference: 'logo-reference',
      role: 'Logo',
      width: 1024,
      height: 400,
      style: 'official',
      sourcePageUrl: 'https://www.steamgriddb.com/logo/7',
    };
    const queries: URLSearchParams[] = [];
    const apply = vi.fn(
      async ({
        request,
        params,
      }: {
        request: Request;
        params: {
          role?: string;
        };
      }) => {
        expect(params.role).toBe('Logo');
        expect(request.headers.get('If-Match')).toBe('"0"');
        expect(await request.json()).toEqual({
          candidateReference: 'logo-reference',
          focalX: 50,
          focalY: 50,
          requestId: expect.any(String),
        });
        return HttpResponse.json(
          {
            detail: 'Temporary failure',
          },
          {
            status: 503,
          },
        );
      },
    );
    server.use(
      http.get(`${titles}/artwork-candidates`, ({ request }) => {
        queries.push(new URL(request.url).searchParams);
        return HttpResponse.json({
          items: [
            logo,
          ],
          hasNextPage: false,
          nextCursor: null,
        });
      }),
      http.post(`${titles}/artwork/:role/apply`, apply),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit logo',
      }),
    );
    await user.click(
      await screen.findByRole('radio', {
        name: 'SteamGridDB',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Select artwork 7',
      }),
    );
    expect(
      screen.queryByRole('combobox', {
        name: 'Dimensions',
      }),
    ).not.toBeInTheDocument();

    const composer = within(
      await screen.findByRole('region', {
        name: 'Logo editor',
      }),
    );
    await user.click(
      composer.getByRole('radio', {
        name: 'Preview logo',
      }),
    );
    expect(composer.getByText('Logo on light and dark backgrounds')).toBeInTheDocument();
    expect(
      within(
        screen.getByRole('region', {
          name: 'Presentation canvas',
        }),
      ).getByRole('radio', {
        name: 'Mobile',
      }),
    ).toBeInTheDocument();
    expect(composer.getByText('1024 × 400 · official · By Artist')).toBeInTheDocument();
    expect(composer.queryByRole('slider')).not.toBeInTheDocument();
    for (const image of composer.getAllByRole('img', {
      name: 'Test Game logo',
    }))
      expect(image).toHaveStyle({
        '--image-object-fit': 'contain',
      });
    expect(queries.every((query) => query.get('role') === 'Logo')).toBe(true);
    expect(apply).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await screen.findByText('Temporary failure');
    expect(apply).toHaveBeenCalledOnce();
  });

  it('keeps retained logos selectable after their gallery originals are removed', async () => {
    const pin = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"0"');
      expect(await request.json()).toEqual({
        mode: 'Pinned',
        assetId: 'saved-logo',
        mediaId: null,
        focalX: 50,
        focalY: 50,
      });
      return HttpResponse.json(written('Poster', 'Pinned', '1'));
    });
    server.use(
      http.get(`http://localhost/api/artwork-providers`, () => HttpResponse.json([])),
      http.get(`${titles}/artwork-assets`, () =>
        HttpResponse.json({
          items: [
            {
              id: 'saved-logo',
              role: 'Logo',
              sourceId: 'steamgriddb',
              mediaIds: [],
              artwork: {
                role: 'Logo',
                url: '/artwork/logo',
              },
            },
          ],
          nextCursor: null,
          hasNextPage: false,
        }),
      ),
      http.put(`${titles}/artwork/Logo`, pin),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit logo',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Actions for Logo saved-logo',
      }),
    );
    expect(
      await screen.findByRole('menuitem', {
        name: 'Use as logo',
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('menuitem', {
        name: 'Use as banner',
      }),
    ).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('menuitem', {
        name: 'Use as logo',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() => expect(pin).toHaveBeenCalledOnce());
  });

  it('confirms deletion with affected roles and keeps the image when deletion fails', async () => {
    const remove = vi.fn(() =>
      HttpResponse.json(
        {
          detail: 'Temporary failure',
        },
        {
          status: 503,
        },
      ),
    );
    server.use(
      http.delete(`${titles}/artwork-assets/:assetId`, remove),
      http.get(`${titles}/artwork-assets`, () =>
        HttpResponse.json({
          items: [
            {
              id: 'saved-logo',
              role: 'Logo',
              sourceId: 'steamgriddb',
              mediaIds: [],
              artwork: {
                role: 'Logo',
                url: '/artwork/logo',
              },
            },
          ],
          nextCursor: null,
          hasNextPage: false,
        }),
      ),
    );
    renderPanel({
      ...title,
      artwork: [
        {
          role: 'Logo',
          assetId: 'saved-logo',
          url: '/artwork/logo',
        },
      ],
    });
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit logo',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Actions for Logo saved-logo',
      }),
    );
    await user.click(
      await screen.findByRole('menuitem', {
        name: 'Delete image',
      }),
    );
    const dialog = within(
      await screen.findByRole('dialog', {
        name: 'Delete image?',
      }),
    );
    expect(dialog.getByText(/Currently used for Logo/)).toBeInTheDocument();
    expect(remove).not.toHaveBeenCalled();
    await user.click(
      dialog.getByRole('button', {
        name: 'Cancel',
      }),
    );
    expect(remove).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Actions for Logo saved-logo',
      }),
    );
    await user.click(
      await screen.findByRole('menuitem', {
        name: 'Delete image',
      }),
    );
    await user.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Delete image?',
        }),
      ).getByRole('button', {
        name: 'Delete image',
      }),
    );
    expect(
      await within(screen.getByRole('dialog')).findByText('Temporary failure'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Actions for Logo saved-logo',
      }),
    ).toBeInTheDocument();
    expect(screen.queryByText('Rejected images')).not.toBeInTheDocument();
  });

  it('deletes a saved image under the family revision and refreshes the library', async () => {
    let deleted = false;
    server.use(
      http.get(`${titles}/artwork-assets`, () =>
        HttpResponse.json({
          items: deleted
            ? []
            : [
                {
                  id: 'saved-logo',
                  role: 'Logo',
                  sourceId: 'user',
                  mediaIds: [],
                  artwork: {
                    role: 'Logo',
                    url: '/artwork/logo',
                  },
                },
              ],
          nextCursor: null,
          hasNextPage: false,
        }),
      ),
      http.delete(`${titles}/artwork-assets/:assetId`, ({ request, params }) => {
        expect(params.assetId).toBe('saved-logo');
        expect(request.headers.get('If-Match')).toBe('"family-1"');
        deleted = true;
        return HttpResponse.json(family());
      }),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit logo',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Actions for Logo saved-logo',
      }),
    );
    await user.click(
      await screen.findByRole('menuitem', {
        name: 'Delete image',
      }),
    );
    await user.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Delete image?',
        }),
      ).getByRole('button', {
        name: 'Delete image',
      }),
    );
    await screen.findByText('Image deleted from this title.');
    expect(
      screen.queryByRole('button', {
        name: 'Actions for Logo saved-logo',
      }),
    ).not.toBeInTheDocument();
  });

  it.each([
    true,
    false,
  ])('deletes title media through the title media delete, saved as artwork or not (saved: %s)', async (saved) => {
    // The one title media delete also removes the image from the artwork, so it runs under the title's ETag.
    const remove = vi.fn(({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"rev-1"');
      return HttpResponse.json(
        writtenTitleFixture({
          id: 'title1',
          revision: 'rev-2',
        }),
      );
    });
    server.use(
      shownTitle,
      http.get(`${titles}/artwork-assets`, () =>
        HttpResponse.json({
          items: saved
            ? [
                {
                  id: 'saved-cover',
                  role: 'Poster',
                  sourceId: 'igdb',
                  mediaIds: [
                    'cover1',
                  ],
                  artwork: {
                    role: 'Poster',
                    url: '/artwork/saved-cover',
                  },
                },
              ]
            : [],
          nextCursor: null,
          hasNextPage: false,
        }),
      ),
      http.delete(`${titles}/media/cover1`, remove),
    );
    renderPanel(
      {
        ...title,
        media: [
          {
            id: 'cover1',
            type: 'Cover',
            sourceId: 'igdb',
            url: '/media/cover1',
            isPrimary: true,
          },
        ],
      },
      {},
      null,
      'library',
    );
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Preview cover from IGDB',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Delete image',
      }),
    );
    await user.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Delete image?',
        }),
      ).getByRole('button', {
        name: 'Delete image',
      }),
    );
    await screen.findByText('Image deleted from this title.');
    expect(remove).toHaveBeenCalledOnce();
  });

  it('hides a provider candidate without importing it', async () => {
    let hidden = false;
    const apply = vi.fn();
    server.use(
      http.put(`${titles}/hidden-artwork-candidates/:candidateId`, async ({ request, params }) => {
        expect(params.candidateId).toBe(candidate.candidateId);
        expect(request.headers.get('If-Match')).toBe('"family-1"');
        expect(await request.json()).toEqual({
          candidateReference: candidate.reference,
        });
        hidden = true;
        return HttpResponse.json(family());
      }),
      http.get(`${titles}/artwork-candidates`, () =>
        HttpResponse.json({
          items: hidden
            ? []
            : [
                candidate,
              ],
          hasNextPage: false,
          nextCursor: null,
        }),
      ),
      http.post(`${titles}/artwork/:role/apply`, apply),
    );
    renderPanel();
    const user = await discover();
    await user.click(
      screen.getByRole('button', {
        name: 'Hide image 7',
      }),
    );
    await waitFor(() =>
      expect(
        screen.queryByRole('button', {
          name: 'Select artwork 7',
        }),
      ).not.toBeInTheDocument(),
    );
    expect(apply).not.toHaveBeenCalled();
  });

  it('refreshes the title after a hide, since the hide rotated its revision', async () => {
    server.use(
      http.put(`${titles}/hidden-artwork-candidates/:candidateId`, () =>
        HttpResponse.json(family()),
      ),
    );
    const { cache } = renderPanel();
    const user = await discover();
    // Seeded once the browser is shown, so only the hide can invalidate it.
    cache.setQueryData(titleDetailKeys.detail('title1'), title);
    expect(cache.getQueryState(titleDetailKeys.detail('title1'))?.isInvalidated).toBe(false);
    await user.click(
      screen.getByRole('button', {
        name: 'Hide image 7',
      }),
    );
    await waitFor(() =>
      expect(cache.getQueryState(titleDetailKeys.detail('title1'))?.isInvalidated).toBe(true),
    );
  });

  it('reloads the artwork when a hide carried a stale family revision', async () => {
    let familyReads = 0;
    server.use(
      http.get(`${titles}/artwork`, () => {
        familyReads += 1;
        return HttpResponse.json(family());
      }),
      http.put(`${titles}/hidden-artwork-candidates/:candidateId`, () =>
        HttpResponse.json(
          {
            status: 412,
          },
          {
            status: 412,
          },
        ),
      ),
    );
    renderPanel();
    const user = await discover();
    const reads = familyReads;
    await user.click(
      screen.getByRole('button', {
        name: 'Hide image 7',
      }),
    );
    await screen.findByText('The artwork changed. It was reloaded; hide the image again.');
    await waitFor(() => expect(familyReads).toBeGreaterThan(reads));
    expect(
      screen.getByRole('button', {
        name: 'Select artwork 7',
      }),
    ).toBeInTheDocument();
  });

  it('reloads the artwork and keeps the image when a delete carried a stale family revision', async () => {
    let familyReads = 0;
    server.use(
      http.get(`${titles}/artwork`, () => {
        familyReads += 1;
        return HttpResponse.json(family());
      }),
      http.get(`${titles}/artwork-assets`, () =>
        HttpResponse.json({
          items: [
            {
              id: 'saved-logo',
              role: 'Logo',
              sourceId: 'user',
              mediaIds: [],
              artwork: {
                role: 'Logo',
                url: '/artwork/logo',
              },
            },
          ],
          nextCursor: null,
          hasNextPage: false,
        }),
      ),
      http.delete(`${titles}/artwork-assets/:assetId`, () =>
        HttpResponse.json(
          {
            status: 412,
          },
          {
            status: 412,
          },
        ),
      ),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit logo',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Actions for Logo saved-logo',
      }),
    );
    await user.click(
      await screen.findByRole('menuitem', {
        name: 'Delete image',
      }),
    );
    const reads = familyReads;
    await user.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Delete image?',
        }),
      ).getByRole('button', {
        name: 'Delete image',
      }),
    );
    expect(
      await within(screen.getByRole('dialog')).findByText(
        'The artwork changed since you opened it. It was reloaded; delete the image again.',
      ),
    ).toBeInTheDocument();
    await waitFor(() => expect(familyReads).toBeGreaterThan(reads));
    expect(
      screen.getByRole('button', {
        name: 'Actions for Logo saved-logo',
      }),
    ).toBeInTheDocument();
  });

  it('keeps the composition visible from initial load through preview and discard', async () => {
    renderPanel();
    expect(
      screen.getByRole('region', {
        name: 'Backdrop editor',
      }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    const user = await discover();
    await user.click(
      screen.getByRole('radio', {
        name: 'Mobile',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Select artwork 7',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeEnabled(),
    );
    expect(
      screen.getByRole('radio', {
        name: 'Mobile',
      }),
    ).toBeChecked();
    const canvas = within(
      screen.getByRole('region', {
        name: 'Presentation canvas',
      }),
    );
    expect(
      canvas.getByRole('figure', {
        name: 'Mobile preview, 390 pixels wide',
      }),
    ).toBeInTheDocument();
    expect(
      canvas.getByRole('radio', {
        name: 'Game details',
      }),
    ).toBeChecked();
    expect(
      screen.getByRole('button', {
        name: 'Edit logo',
      }),
    ).toBeEnabled();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(
      screen.getByRole('region', {
        name: 'Poster editor',
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: 'Save changes',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Edit logo',
      }),
    ).toBeEnabled();
    expect(
      screen.getByRole('button', {
        name: 'Select artwork 7',
      }),
    ).toHaveAttribute('aria-pressed', 'false');
  });

  it('removes a historical backdrop review prompt when the confirmed image is published', async () => {
    server.use(
      http.get('http://localhost/api/titles/title1/artwork-acquisition', () =>
        HttpResponse.json({
          outcomes: [
            {
              role: 'Backdrop',
              status: 'NeedsReview',
              updatedAt: '2026-09-15T00:00:00Z',
            },
          ],
          activeJobId: null,
        }),
      ),
    );
    const view = renderPanel();
    expect(
      await screen.findByRole('button', {
        name: 'Review backdrop',
      }),
    ).toBeInTheDocument();
    view.rerenderTitle({
      ...title,
      artwork: [
        {
          role: 'Backdrop',
          assetId: 'chosen-scene',
          url: '/artwork/chosen',
          fallbackReason: 'None',
        },
      ],
    });
    expect(
      screen.queryByRole('button', {
        name: 'Review backdrop',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('region', {
        name: 'Backdrop editor',
      }),
    ).toBeInTheDocument();
  });

  it.each([
    'Empty',
    'Pinned',
  ])('does not demand backdrop review after an explicit %s selection', async (mode) => {
    server.use(
      http.get(`${titles}/artwork`, () =>
        HttpResponse.json(
          family(
            selections.map((item) =>
              item.role === 'Backdrop'
                ? {
                    ...item,
                    mode,
                  }
                : item,
            ),
          ),
        ),
      ),
      http.get('http://localhost/api/titles/title1/artwork-acquisition', () =>
        HttpResponse.json({
          outcomes: [
            {
              role: 'Backdrop',
              status: 'NeedsReview',
              updatedAt: '2026-09-15T00:00:00Z',
            },
          ],
          activeJobId: null,
        }),
      ),
    );
    renderPanel();
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Edit backdrop',
        }),
      ).toBeEnabled(),
    );
    expect(
      screen.queryByRole('button', {
        name: 'Review backdrop',
      }),
    ).not.toBeInTheDocument();
  });

  it('replaces a banner in context, keeps the current one visible while importing, and recovers from a failed import', async () => {
    const requestIds: string[] = [];
    let phase = 'Running';
    const apply = vi.fn(async ({ request }: { request: Request }) => {
      const body = (await request.json()) as {
        requestId: string;
      };
      requestIds.push(body.requestId);
      return HttpResponse.json({
        jobId: `banner-job-${requestIds.length}`,
      });
    });
    server.use(
      http.get(`${titles}/artwork-candidates`, () =>
        HttpResponse.json({
          items: [
            {
              ...candidate,
              role: 'Hero',
              width: 1920,
              height: 620,
            },
          ],
          hasNextPage: false,
          nextCursor: null,
        }),
      ),
      http.post(`${titles}/artwork/:role/apply`, apply),
      http.get('http://localhost/api/jobs/:jobId', ({ params }) =>
        HttpResponse.json({
          id: params.jobId,
          phase,
          isTerminal: phase === 'Failed',
          hasErrors: phase === 'Failed',
        }),
      ),
    );
    renderPanel(
      {
        ...title,
        artwork: [
          {
            role: 'Hero',
            assetId: null,
            contentVersion: null,
            url: '/artwork/current-banner',
            width: null,
            height: null,
            originalWidth: null,
            originalHeight: null,
            fit: 'Cover',
            fallbackReason: 'None',
            variants: [],
          },
        ],
      },
      {},
      null,
    );
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit banner',
      }),
    );
    await user.click(
      await screen.findByRole('radio', {
        name: 'SteamGridDB',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Select artwork 7',
      }),
    );
    expect(
      await screen.findByRole('radio', {
        name: 'Draft',
      }),
    ).toBeChecked();
    await user.click(
      within(
        screen.getByRole('region', {
          name: 'Presentation canvas',
        }),
      ).getByRole('radio', {
        name: 'Saved',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() => expect(apply).toHaveBeenCalledOnce());
    const banner = await screen.findByRole('region', {
      name: 'Banner editor',
    });
    const roleButton = await screen.findByRole('button', {
      name: 'Edit banner',
      description: 'Importing',
    });
    expect(
      within(banner).getByRole('progressbar', {
        name: 'Importing banner',
      }),
    ).toBeInTheDocument();
    expect(roleButton.querySelector('img')).toHaveAttribute('src', '/artwork/current-banner');

    phase = 'Failed';
    const retry = await within(banner).findByRole(
      'button',
      {
        name: 'Retry',
        exact: true,
      },
      {
        timeout: 4000,
      },
    );
    expect(
      await screen.findByRole('button', {
        name: 'Edit banner',
        description: 'Needs attention',
      }),
    ).toBe(roleButton);
    expect(
      within(banner).getByText('The banner import failed. The current image is unchanged.'),
    ).toBeInTheDocument();
    expect(roleButton.querySelector('img')).toHaveAttribute('src', '/artwork/current-banner');
    await user.click(retry);
    await waitFor(() => expect(apply).toHaveBeenCalledTimes(2));
    expect(requestIds[1]).not.toBe(requestIds[0]);

    await user.click(
      await within(banner).findByRole(
        'button',
        {
          name: 'Choose another',
        },
        {
          timeout: 4000,
        },
      ),
    );
    expect(
      screen.getByRole('region', {
        name: 'Banner editor',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('radio', {
        name: 'SteamGridDB',
      }),
    ).toBeChecked();
  }, 15_000);

  it('saves a provider candidate to the library for later without changing the role', async () => {
    const apply = vi.fn(() =>
      HttpResponse.json({
        jobId: 'unexpected',
      }),
    );
    const imported = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"rev-1"');
      expect(await request.json()).toEqual({
        candidateReference: candidate.reference,
      });
      return HttpResponse.json(
        {
          id: 'media9',
          type: 'Cover',
          url: '/media/media9',
          sourceId: 'gallery:steamgriddb',
          isPrimary: false,
          contentType: 'image/png',
        },
        {
          status: 201,
          headers: {
            ETag: '"rev-2"',
          },
        },
      );
    });
    server.use(
      shownTitle,
      http.post(`${titles}/artwork-candidates/:candidateId/import`, imported),
      http.post(`${titles}/artwork/:role/apply`, apply),
    );
    renderPanel(title, {}, null);
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Edit poster',
      }),
    );
    await user.click(
      await screen.findByRole('radio', {
        name: 'SteamGridDB',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Select artwork 7',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Save to library',
      }),
    );
    expect(
      await screen.findByRole('button', {
        name: 'Saved to library',
      }),
    ).toBeDisabled();
    expect(imported).toHaveBeenCalledOnce();
    expect(
      screen.getByText('Saved to the image library. The poster is unchanged.'),
    ).toBeInTheDocument();
    // The preview stays open so the curator can still use it, or leave the poster as it is.
    expect(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    ).toBeEnabled();
    expect(apply).not.toHaveBeenCalled();
  });

  describe('image library', () => {
    const releases = [
      {
        id: 'release-usa',
        datId: 'dat1',
        name: 'Test Game (USA)',
        catalogReleaseId: 'cr-usa',
        region: 'USA',
        sources: [],
      },
      {
        id: 'release-europe',
        datId: 'dat1',
        name: 'Test Game (Europe)',
        catalogReleaseId: 'cr-europe',
        region: 'Europe',
        sources: [],
      },
    ];
    const scan = (id: string, releaseId: string, region: string, isPreferred: boolean) => ({
      id,
      type: 'BoxFront',
      sourceId: 'user',
      url: `/media/${id}`,
      isPrimary: false,
      contentType: 'image/png',
      catalogReleaseId: releaseId,
      region,
      isPreferred,
    });
    beforeEach(() => {
      vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(
        {} as CanvasRenderingContext2D,
      );
    });

    it('steps through filtered images without replacing the grid and restores focus on close', async () => {
      renderPanel(
        {
          ...title,
          releases,
          media: [
            scan('usa-front', 'cr-usa', 'USA', true),
            scan('usa-front-alt', 'cr-usa', 'USA', false),
            scan('europe-front', 'cr-europe', 'Europe', true),
          ],
        },
        {},
        null,
        'library',
      );
      const user = userEvent.setup();
      const grid = screen.getByRole('group', {
        name: 'Image library',
      });
      await user.selectOptions(
        screen.getByRole('combobox', {
          name: 'Release',
        }),
        'cr-usa',
      );
      const [first, second] = within(grid).getAllByRole('button');
      await user.click(first);
      const lightbox = within(
        await screen.findByRole('dialog', {
          name: 'Box front',
        }),
      );
      expect(lightbox.getByText('1 of 2')).toBeInTheDocument();
      expect(
        lightbox.getByRole('button', {
          name: 'Previous item',
        }),
      ).toBeDisabled();
      await user.click(
        lightbox.getByRole('button', {
          name: 'Next item',
        }),
      );
      expect(await screen.findByText('2 of 2')).toBeInTheDocument();
      expect(within(grid).getAllByRole('button')).toEqual([
        first,
        second,
      ]);
      act(() =>
        screen
          .getByRole('button', {
            name: 'Previous item',
          })
          .focus(),
      );
      await user.keyboard('{ArrowLeft}');
      expect(await screen.findByText('1 of 2')).toBeInTheDocument();
      act(() =>
        screen
          .getByRole('button', {
            name: 'Close',
            exact: true,
          })
          .focus(),
      );
      await user.keyboard('{Escape}');
      await waitFor(() => expect(first).toHaveFocus());
    });

    it('keeps regional variants of one packaging type and prefers one scan per release', async () => {
      const classify = vi.fn(async ({ request }: { request: Request }) => {
        expect(request.headers.get('If-Match')).toBe('"rev-1"');
        expect(await request.json()).toEqual({
          classification: {
            type: 'BoxFront',
            catalogReleaseId: 'cr-europe',
            region: 'Europe',
            language: 'French',
            isPreferred: true,
          },
        });
        return HttpResponse.json(
          writtenTitleFixture({
            id: 'title1',
            revision: 'rev-2',
          }),
          {
            headers: {
              ETag: '"rev-2"',
            },
          },
        );
      });
      server.use(shownTitle, http.patch(`${titles}/media/europe-front`, classify));
      renderPanel(
        {
          ...title,
          releases,
          media: [
            scan('usa-front', 'cr-usa', 'USA', true),
            scan('usa-front-alt', 'cr-usa', 'USA', false),
            scan('europe-front', 'cr-europe', 'Europe', false),
            {
              id: 'shot1',
              type: 'Screenshot',
              sourceId: 'igdb',
              url: '/media/shot1',
              isPrimary: true,
              contentType: 'image/png',
            },
          ],
        },
        {},
        null,
        'library',
      );
      const user = userEvent.setup();
      const library = screen.getByRole('group', {
        name: 'Image library',
      });
      expect(within(library).getAllByRole('button')).toHaveLength(4);
      await user.selectOptions(
        screen.getByRole('combobox', {
          name: 'Type',
        }),
        'packaging',
      );
      expect(within(library).getAllByRole('button')).toHaveLength(3);
      expect(within(library).getAllByText('Preferred')).toHaveLength(1);
      await user.selectOptions(
        screen.getByRole('combobox', {
          name: 'Release',
        }),
        'cr-europe',
      );
      const europe = within(library).getByRole('button', {
        name: 'Preview box front from Your uploads, Test Game (Europe) · Europe',
      });
      expect(within(library).getAllByRole('button')).toHaveLength(1);

      await user.click(europe);
      const inspector = within(
        screen.getByRole('dialog', {
          name: 'Box front',
        }),
      );
      expect(inspector.queryByText('Role assignments')).not.toBeInTheDocument();
      expect(
        inspector.getByRole('combobox', {
          name: 'Release',
        }),
      ).toHaveValue('cr-europe');
      await user.type(
        inspector.getByRole('textbox', {
          name: 'Language',
        }),
        'French',
      );
      await user.click(
        inspector.getByRole('switch', {
          name: /Preferred box front for Test Game \(Europe\)/,
        }),
      );
      await user.click(
        inspector.getByRole('button', {
          name: 'Save changes',
        }),
      );
      await waitFor(() => expect(classify).toHaveBeenCalledOnce());
      expect(await screen.findByText('Changes saved.')).toBeInTheDocument();
    });

    it('shows the server reason when a classification is refused', async () => {
      server.use(
        shownTitle,
        http.patch(`${titles}/media/usa-front`, () =>
          HttpResponse.json(
            {
              status: 400,
              detail: 'The release does not belong to this title.',
            },
            {
              status: 400,
            },
          ),
        ),
      );
      renderPanel(
        {
          ...title,
          releases,
          media: [
            scan('usa-front', 'cr-usa', 'USA', false),
          ],
        },
        {},
        null,
        'library',
      );
      const user = userEvent.setup();
      await user.click(
        screen.getByRole('button', {
          name: 'Preview box front from Your uploads, Test Game (USA) · USA',
        }),
      );
      const inspector = within(screen.getByRole('dialog'));
      await user.selectOptions(
        inspector.getByRole('combobox', {
          name: 'Type',
        }),
        'BoxBack',
      );
      await user.click(
        inspector.getByRole('button', {
          name: 'Save changes',
        }),
      );
      expect(await inspector.findByRole('alert')).toHaveTextContent(
        'The release does not belong to this title.',
      );
      expect(
        inspector.getByRole('combobox', {
          name: 'Type',
        }),
      ).toHaveValue('BoxBack');
    });

    it('opens a manual from its single library entry and pages through it', async () => {
      renderPanel(
        {
          ...title,
          releases,
          media: [
            {
              id: 'manual1',
              type: 'Manual',
              sourceId: 'user',
              url: '/media/manual1',
              isPrimary: true,
              contentType: 'application/pdf',
              catalogReleaseId: 'cr-usa',
              region: 'USA',
              language: 'English',
            },
          ],
        },
        {},
        null,
        'library',
      );
      const user = userEvent.setup();
      const library = screen.getByRole('group', {
        name: 'Image library',
      });
      const entry = within(library).getByRole('button', {
        name: 'Preview manual from Your uploads, Test Game (USA) · USA · English',
      });
      expect(within(library).getAllByRole('button')).toHaveLength(1);
      await user.click(entry);
      const inspector = within(
        screen.getByRole('dialog', {
          name: 'Manual',
        }),
      );
      expect(
        await inspector.findByRole('img', {
          name: 'Cover of manual',
        }),
      ).toBeInTheDocument();
      expect(openManual).toHaveBeenCalledWith('title1', 'manual1', {
        preview: true,
      });
      expect(inspector.queryByText('Role assignments')).not.toBeInTheDocument();
      expect(
        inspector.queryByRole('link', {
          name: 'Download',
        }),
      ).not.toBeInTheDocument();
      const open = inspector.getByRole('button', {
        name: 'Open manual',
      });
      await user.click(open);
      const viewer = within(
        await screen.findByRole('dialog', {
          name: /^Manual ·/,
        }),
      );
      expect(await viewer.findByText('Page 1 of 4')).toBeInTheDocument();
      await user.click(
        viewer.getByRole('button', {
          name: 'Next page',
        }),
      );
      expect(viewer.getByText('Page 2 of 4')).toBeInTheDocument();
      await user.keyboard('{ArrowRight}');
      expect(viewer.getByText('Page 3 of 4')).toBeInTheDocument();
      await user.click(
        viewer.getByRole('radio', {
          name: 'Spreads',
        }),
      );
      expect(viewer.getByText('Pages 2–3 of 4')).toBeInTheDocument();
      expect(
        viewer.getAllByRole('img', {
          name: /^Page \d$/,
        }),
      ).toHaveLength(2);
      await user.click(
        viewer.getByRole('button', {
          name: 'Next page',
        }),
      );
      expect(viewer.getByText('Page 4 of 4')).toBeInTheDocument();
      await user.keyboard('{Home}');
      expect(viewer.getByText('Page 1 of 4')).toBeInTheDocument();
      await user.keyboard('{Escape}');
      await waitFor(() =>
        expect(
          screen.queryByRole('dialog', {
            name: /^Manual ·/,
          }),
        ).not.toBeInTheDocument(),
      );
      await waitFor(() => expect(open).toHaveFocus());
    });
  });
});
