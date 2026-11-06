import { readFileSync } from 'node:fs';
import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TitleDetailView as TitleDetail } from '../../hooks/api/useTitleDetail';
import { titleDetailKeys } from '../../hooks/api/useTitleDetail';
import { writtenTitleFixture, writtenTitleViewFixture } from '../../test/msw/fixtures/enrichment';
import { server } from '../../test/msw/server';
import { ArtworkPanel } from './ArtworkPanel';
import type { TitleEditorOutcome } from './titleWorkbenchModel';

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
function renderPanel(value: TitleDetail = title, outcome: TitleEditorOutcome = {}) {
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
        <ArtworkPanel
          title={current}
          titleId="title1"
          {...outcome}
        />
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
    await screen.findByRole('radio', {
      name: 'Poster',
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
  it('offers roles and image sources as single-level radio groups with statuses and arrow keys', async () => {
    renderPanel({
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
          fit: 'Cover',
          fallbackReason: 'None',
          variants: [],
        },
      ],
    });
    const roles = screen.getByRole('radiogroup', {
      name: 'Artwork roles',
    });
    expect(document.getElementById(roles.getAttribute('aria-labelledby') ?? '')).toHaveStyle({
      display: 'none',
    });
    expect(
      within(roles)
        .getAllByRole('radio')
        .map((radio) => radio.getAttribute('aria-label')),
    ).toEqual([
      'Poster',
      'Backdrop',
      'Banner',
      'Logo',
    ]);
    const backdrop = within(roles).getByRole('radio', {
      name: 'Backdrop',
    });
    expect(backdrop).toHaveAttribute('aria-checked', 'true');
    expect(backdrop).toHaveAttribute('tabindex', '0');
    expect(
      within(roles).getByRole('radio', {
        name: 'Logo',
      }),
    ).toHaveAttribute('tabindex', '-1');
    expect(
      await within(roles).findByRole('radio', {
        name: 'Poster',
        description: 'Selected by you',
      }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(
      screen.getByRole('radio', {
        name: 'Saved',
      }),
    ).toBeChecked();
    act(() => backdrop.focus());
    fireEvent.keyDown(backdrop, {
      key: 'ArrowRight',
      code: 'ArrowRight',
    });
    const banner = within(roles).getByRole('radio', {
      name: 'Banner',
    });
    await waitFor(() => expect(banner).toHaveAttribute('aria-checked', 'true'));
    expect(banner).toHaveFocus();
    expect(
      screen.getByRole('region', {
        name: 'Compose banner',
      }),
    ).toBeInTheDocument();
  });

  it('marks the host density on the editor container and tiers spacing through custom properties', () => {
    const { unmount } = renderPanel();
    expect(
      screen
        .getByRole('radiogroup', {
          name: 'Artwork roles',
        })
        .closest('[data-density]'),
    ).toHaveAttribute('data-density', 'comfortable');
    unmount();
    renderPanel(title, {
      density: 'compact',
    });
    expect(
      screen
        .getByRole('radiogroup', {
          name: 'Artwork roles',
        })
        .closest('[data-density]'),
    ).toHaveAttribute('data-density', 'compact');
    const styles = readFileSync(`${import.meta.dirname}/ArtworkWorkspace.module.css`, 'utf8');
    expect(styles).not.toMatch(/@media/);
    expect(styles).toMatch(/\.workspace \{\s*container: artwork-editor \/ inline-size;/);
    const compact = styles.slice(styles.indexOf(".workspace[data-density='compact'] {"));
    const forced = styles.slice(styles.indexOf('@container artwork-editor (max-width: 600px) {'));
    for (const tier of [
      compact,
      forced,
    ]) {
      expect(tier).toMatch(
        /--artwork-role-columns: 2;[^}]*--artwork-panel-padding: var\(--mantine-spacing-sm\);/,
      );
    }
    expect(styles).toContain(
      'grid-template-columns: repeat(var(--artwork-role-columns), minmax(0, 1fr))',
    );
    expect(styles).toContain(
      '@container artwork-editor (max-width: 1000px) { .editorLayout { grid-template-columns: minmax(0, 1fr); }',
    );
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
    expect(
      await screen.findByRole('slider', {
        name: 'Horizontal focal point',
      }),
    ).toBeInTheDocument();
    expect(apply).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Apply backdrop',
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
      await screen.findByRole('radio', {
        name: 'Poster',
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
        name: 'View on IGDB',
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
          name: 'Apply backdrop',
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('radio', {
        name: 'Mobile',
      }),
    );
    expect(
      within(screen.getByTestId('artwork-composition-preview')).getByRole('figure', {
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
      await screen.findByRole('radio', {
        name: 'Banner',
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
      name: 'Compose banner',
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
      await screen.findByRole('radio', {
        name: 'Logo',
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
          name: 'Apply logo',
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('button', {
        name: /^Apply (poster|backdrop|banner|logo)$/,
      }),
    );

    await waitFor(() => expect(upload).toHaveBeenCalledOnce());
    await waitFor(() => expect(pin).toHaveBeenCalledOnce());
  });
  it.each([
    {
      role: 'Hero',
      label: 'banner',
      name: 'Banner',
    },
    {
      role: 'Backdrop',
      label: 'backdrop',
      name: 'Backdrop',
    },
  ])('assigns an existing cover to $label independently of its image type', async ({
    role,
    label,
    name,
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
      await screen.findByRole('radio', {
        name,
      }),
    );
    await user.click(
      within(screen.getByRole('complementary')).getByRole('button', {
        name: 'Actions for Cover cover1',
      }),
    );
    await user.click(
      screen.getByRole('menuitem', {
        name: `Use as ${label}`,
      }),
    );
    await screen.findByRole('region', {
      name: `Compose ${label}`,
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
    act(() =>
      screen
        .getByRole('slider', {
          name: 'Horizontal focal point',
        })
        .focus(),
    );
    await user.keyboard('{Home}');
    act(() =>
      screen
        .getByRole('slider', {
          name: 'Vertical focal point',
        })
        .focus(),
    );
    await user.keyboard('{End}');
    await user.click(
      screen.getByRole('button', {
        name: /^Apply (poster|backdrop|banner|logo)$/,
      }),
    );
    await waitFor(() => expect(pin).toHaveBeenCalledOnce());
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.queryByText('Primary')).not.toBeInTheDocument();
  });

  it('saves a dragged and keyed focal point once through Apply', async () => {
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
        await screen.findByRole('radio', {
          name: 'Backdrop',
        }),
      );
      await user.click(
        within(screen.getByRole('complementary')).getByRole('button', {
          name: 'Actions for Cover cover1',
        }),
      );
      await user.click(
        screen.getByRole('menuitem', {
          name: 'Use as backdrop',
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
          name: 'Apply backdrop',
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
        name: 'Apply backdrop',
        exact: true,
      }),
    );
    const composer = screen.getByRole('region', {
      name: 'Compose backdrop',
    });
    expect(
      await within(composer).findByText(
        'The selection changed. Reload the latest artwork, then choose again.',
      ),
    ).toBeInTheDocument();
    await user.click(
      within(composer).getByRole('button', {
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
    expect(within(composer).getByText('Unsaved preview')).toBeInTheDocument();
    const readsBeforeReload = selectionReads;
    await user.click(
      within(composer).getByRole('button', {
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
    expect(await within(composer).findByText('Showing saved artwork')).toBeInTheDocument();
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
        name: 'Apply backdrop',
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
      await screen.findByRole('radio', {
        name: 'Banner',
      }),
    );
    await user.click(
      within(screen.getByRole('complementary')).getByRole('button', {
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
          name: /^Apply (poster|backdrop|banner|logo)$/,
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('button', {
        name: /^Apply (poster|backdrop|banner|logo)$/,
      }),
    );
    expect(
      await within(
        screen.getByRole('region', {
          name: 'Compose banner',
        }),
      ).findByText(
        'The selection changed. Discard this preview and choose again to review the latest artwork.',
      ),
    ).toBeInTheDocument();
    expect(
      within(
        screen.getByRole('region', {
          name: 'Compose banner',
        }),
      ).getByRole('button', {
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
      await screen.findByRole('radio', {
        name: 'Poster',
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
          name: /^Apply (poster|backdrop|banner|logo)$/,
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('button', {
        name: /^Apply (poster|backdrop|banner|logo)$/,
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
      await screen.findByRole('radio', {
        name: 'Banner',
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
      await screen.findByRole('radio', {
        name: 'Current',
        exact: true,
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
        exact: true,
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Apply banner',
        }),
      ).toBeDisabled(),
    );
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
        name: /^Apply (poster|backdrop|banner|logo)$/,
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
          name: /^Apply (poster|backdrop|banner|logo)$/,
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('button', {
        name: /^Apply (poster|backdrop|banner|logo)$/,
      }),
    );
    await screen.findByText('Temporary failure');
    await user.click(
      screen.getByRole('button', {
        name: /^Apply (poster|backdrop|banner|logo)$/,
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
      await screen.findByRole('radio', {
        name: 'Poster',
      }),
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Poster actions',
      }),
    );
    const button = await screen.findByRole('menuitem', {
      name: 'Return poster to automatic',
    });
    await waitFor(() => expect(button).toBeEnabled());
    await userEvent.setup().click(button);
    await waitFor(() => expect(automatic).toHaveBeenCalledOnce());
  });
  it('confirms before leaving a role empty, and cancel leaves it unchanged', async () => {
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
    const openConfirmation = async () => {
      await user.click(
        await screen.findByRole('button', {
          name: 'Logo actions',
        }),
      );
      await user.click(
        await screen.findByRole('menuitem', {
          name: 'Leave empty',
        }),
      );
      return screen.findByRole('dialog', {
        name: 'Leave logo empty?',
      });
    };
    await user.click(
      await screen.findByRole('radio', {
        name: 'Logo',
      }),
    );
    const dialog = await openConfirmation();
    expect(
      within(dialog).getByText(
        'The consumer app shows no logo for this title and automatic runs will not fill it. Use Return to automatic to undo.',
      ),
    ).toBeInTheDocument();
    await user.click(
      within(dialog).getByRole('button', {
        name: 'Cancel',
      }),
    );
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(leave).not.toHaveBeenCalled();
    await user.click(
      within(await openConfirmation()).getByRole('button', {
        name: 'Leave logo empty',
      }),
    );
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(leave).toHaveBeenCalledOnce();
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
          name: 'Apply poster',
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
        name: 'Apply poster',
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
      await screen.findByRole('radio', {
        name: 'Logo',
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
        name: 'Compose logo',
      }),
    );
    await user.click(composer.getByText('Inspect logo on light and dark backgrounds'));
    expect(composer.getByText('Dark background')).toBeInTheDocument();
    expect(composer.getByText('Light background')).toBeInTheDocument();
    expect(
      composer.getByRole('radio', {
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
      composer.getByRole('button', {
        name: /^Apply (poster|backdrop|banner|logo)$/,
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
      await screen.findByRole('radio', {
        name: 'Logo',
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
          name: /^Apply (poster|backdrop|banner|logo)$/,
        }),
      ).toBeEnabled(),
    );
    await user.click(
      screen.getByRole('button', {
        name: /^Apply (poster|backdrop|banner|logo)$/,
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
      await screen.findByRole('radio', {
        name: 'Logo',
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
      within(await screen.findByRole('dialog')).getByRole('button', {
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
      await screen.findByRole('radio', {
        name: 'Logo',
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
      within(await screen.findByRole('dialog')).getByRole('button', {
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
        name: 'Actions for Cover cover1',
      }),
    );
    await user.click(
      await screen.findByRole('menuitem', {
        name: 'Delete image',
      }),
    );
    await user.click(
      within(await screen.findByRole('dialog')).getByRole('button', {
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
      await screen.findByRole('radio', {
        name: 'Logo',
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
      within(await screen.findByRole('dialog')).getByRole('button', {
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
        name: 'Compose backdrop',
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
          name: 'Apply poster',
        }),
      ).toBeEnabled(),
    );
    expect(
      screen.getByRole('radio', {
        name: 'Mobile',
      }),
    ).toBeChecked();
    expect(screen.getByText('Consumer library card at mobile size.')).toBeInTheDocument();
    expect(
      screen.getByRole('radio', {
        name: 'Logo',
      }),
    ).toBeDisabled();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(
      screen.getByRole('region', {
        name: 'Compose poster',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Apply poster',
      }),
    ).toBeDisabled();
    expect(
      screen.getByRole('radio', {
        name: 'Logo',
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
        name: 'Compose backdrop',
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
          name: 'Backdrop actions',
        }),
      ).toBeEnabled(),
    );
    expect(
      screen.queryByRole('button', {
        name: 'Review backdrop',
      }),
    ).not.toBeInTheDocument();
  });
});
