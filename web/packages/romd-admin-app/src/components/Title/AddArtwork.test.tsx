import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { writtenTitleFixture } from '../../test/msw/fixtures/enrichment';
import { server } from '../../test/msw/server';
import { AddArtwork } from './AddArtwork';

/** The title the page shows; uploads send its revision in If-Match. */
const shownTitle = http.get('http://localhost/api/titles/title1', () =>
  HttpResponse.json(
    writtenTitleFixture({
      id: 'title1',
      revision: 'rev-1',
    }),
  ),
);

const titles = 'http://localhost/api/titles/title1';
const provider = {
  id: 'igdb',
  name: 'IGDB',
  isAvailable: true,
  supportsLanguageFilter: false,
  roles: [
    {
      role: 'Poster',
      dimensions: [],
      styles: [],
    },
    {
      role: 'Hero',
      dimensions: [],
      styles: [],
    },
  ],
  mediaTypes: [
    'Cover',
    'Background',
    'Screenshot',
  ],
  gameId: '42',
  gameName: 'Test game',
};
const revoke = vi.fn();
function show() {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
      mutations: {
        retry: false,
      },
    },
  });
  return render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <AddArtwork
          opened
          onClose={vi.fn()}
          titleId="title1"
          titleName="Test game"
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('Add artwork', () => {
  beforeEach(() => {
    client.setConfig({
      baseUrl: 'http://localhost',
    });
    vi.stubGlobal(
      'URL',
      class extends URL {
        static createObjectURL = () => 'blob:test';
        static revokeObjectURL = revoke;
      },
    );
    revoke.mockClear();
    server.use(
      http.get(`http://localhost/api/artwork-providers`, () =>
        HttpResponse.json([
          provider,
        ]),
      ),
      http.get('http://localhost/api/titles/title1/provider-matches', () =>
        HttpResponse.json([
          {
            providerId: 'igdb',
            providerName: 'IGDB',
            isAvailable: true,
            state: 'Confirmed',
          },
        ]),
      ),
      http.get(`${titles}/artwork-candidates`, ({ request }) => {
        expect(new URL(request.url).searchParams.get('mediaType')).toBe('Screenshot');
        return HttpResponse.json({
          items: [
            1,
            2,
            3,
          ].map((id) => ({
            reference: `ref${id}`,
            candidateId: `candidate${id}`,
            providerAssetId: `${id}`,
            role: 'Hero',
            mediaType: 'Screenshot',
            width: 640,
            height: 480,
            sourcePageUrl: 'https://www.igdb.com/games/test',
            isSaved: id === 3,
          })),
          hasNextPage: false,
          nextCursor: null,
        });
      }),
      http.post(
        `${titles}/artwork-candidates/preview`,
        () =>
          new HttpResponse(
            new Blob(
              [
                'image',
              ],
              {
                type: 'image/png',
              },
            ),
          ),
      ),
    );
  });
  afterEach(() => vi.unstubAllGlobals());

  it('keeps browsing after an empty provider page and deduplicates stable candidates across continuations', async () => {
    const cursors: (string | null)[] = [];
    const image = (id: number) => ({
      reference: `ref${id}`,
      candidateId: `candidate${id}`,
      providerAssetId: `${id}`,
      role: 'Hero',
      mediaType: 'Screenshot',
      width: 640,
      height: 480,
      sourcePageUrl: 'https://www.igdb.com/games/test',
      isSaved: false,
    });
    server.use(
      http.get(`${titles}/artwork-candidates`, ({ request }) => {
        const cursor = new URL(request.url).searchParams.get('cursor');
        cursors.push(cursor);
        return HttpResponse.json(
          cursor === null
            ? {
                items: [],
                nextCursor: 'page-one',
                hasNextPage: true,
              }
            : cursor === 'page-one'
              ? {
                  items: [
                    image(1),
                    image(2),
                  ],
                  nextCursor: 'page-two',
                  hasNextPage: true,
                }
              : {
                  items: [
                    image(2),
                    image(3),
                  ],
                  nextCursor: null,
                  hasNextPage: false,
                },
        );
      }),
    );
    show();
    const user = userEvent.setup();
    await screen.findByText('No images on this provider page. Load more to keep browsing.');
    await user.click(
      screen.getByRole('button', {
        name: 'Load more images',
      }),
    );
    await screen.findByText('2 images loaded');
    await user.click(
      screen.getByRole('button', {
        name: 'Load more images',
      }),
    );
    await screen.findByText('3 images loaded');
    expect(
      screen.getAllByRole('button', {
        name: 'Select image 2',
      }),
    ).toHaveLength(1);
    expect(
      screen.queryByRole('button', {
        name: 'Load more images',
      }),
    ).not.toBeInTheDocument();
    expect(cursors).toEqual([
      null,
      'page-one',
      'page-two',
    ]);
  });

  it('collects screenshots without assigning artwork and retries only the failed image', async () => {
    const calls: string[] = [];
    const ifMatches: (string | null)[] = [];
    let imports = 0;
    let fail = true;
    server.use(
      shownTitle,
      http.post(`${titles}/artwork-candidates/:candidateId/import`, async ({ request, params }) => {
        const body = (await request.json()) as {
          candidateReference: string;
        };
        // The route names the candidate the signed reference proves.
        expect(params.candidateId).toBe(body.candidateReference.replace('ref', 'candidate'));
        calls.push(body.candidateReference);
        ifMatches.push(request.headers.get('If-Match'));
        if (body.candidateReference === 'ref2' && fail)
          return HttpResponse.json(
            {
              detail: 'Provider unavailable',
            },
            {
              status: 503,
            },
          );
        imports += 1;
        return HttpResponse.json(
          {
            id: body.candidateReference,
            type: 'Screenshot',
            sourceId: 'gallery:igdb',
            isPrimary: false,
            url: '/media/image',
          },
          {
            status: 201,
            headers: {
              ETag: `"rev-${imports + 1}"`,
            },
          },
        );
      }),
    );
    show();
    const user = userEvent.setup();
    // Poll the loaded count: a polled role query names every button on each DOM change.
    await screen.findByText('3 images loaded');
    expect(
      screen.getByRole('button', {
        name: 'Select image 3',
      }),
    ).toBeDisabled();
    await user.click(
      screen.getByRole('button', {
        name: 'Select visible',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Add 2 images',
      }),
    );
    await screen.findByText('Provider unavailable');
    expect(calls).toEqual([
      'ref1',
      'ref2',
    ]);
    expect(
      screen.getByRole('button', {
        name: 'Select image 1',
      }),
    ).toBeDisabled();
    fail = false;
    await user.click(
      screen.getByRole('button', {
        name: 'Retry selected images',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Select image 2',
        }),
      ).toBeDisabled(),
    );
    expect(calls).toEqual([
      'ref1',
      'ref2',
      'ref2',
    ]);
    // Each import sends the revision the previous one returned.
    expect(ifMatches).toEqual([
      '"rev-1"',
      '"rev-2"',
      '"rev-2"',
    ]);
  });

  it('stops at a stale title revision and keeps every image it did not add selected', async () => {
    const calls: string[] = [];
    server.use(
      shownTitle,
      http.post(`${titles}/artwork-candidates/:candidateId/import`, async ({ request }) => {
        const body = (await request.json()) as {
          candidateReference: string;
        };
        calls.push(body.candidateReference);
        return HttpResponse.json(
          {
            status: 412,
          },
          {
            status: 412,
          },
        );
      }),
    );
    show();
    const user = userEvent.setup();
    await screen.findByText('3 images loaded');
    await user.click(
      screen.getByRole('button', {
        name: 'Select visible',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Add 2 images',
      }),
    );
    await screen.findByText(
      'The title changed since you opened it. It was reloaded; add the remaining images again.',
    );
    // Every later import would carry the same stale revision, so none is sent.
    expect(calls).toEqual([
      'ref1',
    ]);
    for (const id of [
      '1',
      '2',
    ])
      expect(
        screen.getByRole('button', {
          name: `Select image ${id}`,
        }),
      ).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByText('2 selected')).toBeInTheDocument();
  });

  it('uploads a batch with individual image types and keeps successful uploads on retry', async () => {
    const calls: string[] = [];
    let fail = true;
    let finishRetry = () => {};
    const retryResponse = new Promise<void>((resolve) => {
      finishRetry = resolve;
    });
    server.use(
      shownTitle,
      http.post('http://localhost/api/titles/title1/media', async ({ request }) => {
        const type = new URL(request.url).searchParams.get('type');
        const file = {
          name: type === 'logo' ? 'logo.png' : 'screen.png',
        };
        calls.push(`${file.name}:${type}`);
        if (file.name === 'logo.png' && fail)
          return HttpResponse.json(
            {},
            {
              status: 500,
            },
          );
        if (file.name === 'logo.png') await retryResponse;
        return HttpResponse.json({
          id: file.name,
          type: new URL(request.url).searchParams.get('type'),
          sourceId: 'user',
          url: '/media/new',
          isPrimary: false,
        });
      }),
    );
    const view = show();
    const user = userEvent.setup();
    await user.click(
      screen.getByRole('tab', {
        name: 'Upload images',
      }),
    );
    const input = document.querySelector<HTMLInputElement>('input[type="file"]');
    if (!input) throw new Error('Missing artwork upload input');
    await user.upload(input, [
      new File(
        [
          'a',
        ],
        'screen.png',
        {
          type: 'image/png',
        },
      ),
      new File(
        [
          'b',
        ],
        'logo.png',
        {
          type: 'image/png',
        },
      ),
    ]);
    await user.selectOptions(screen.getByLabelText('Type for logo.png'), 'Logo');
    await user.click(
      screen.getByRole('button', {
        name: 'Add 2 images',
      }),
    );
    await screen.findByText('Upload failed. Retry this image.');
    fail = false;
    await user.click(
      screen.getByRole('button', {
        name: 'Retry remaining images',
      }),
    );
    expect(
      screen.getByRole('progressbar', {
        name: 'Batch progress',
      }),
    ).toHaveAttribute('aria-valuenow', '0');
    expect(screen.getByText('0 of 1 processed this attempt')).toBeInTheDocument();
    expect(
      screen.getByRole('progressbar', {
        name: 'Transfer progress for logo.png',
      }),
    ).toBeInTheDocument();
    finishRetry();
    await screen.findByText('2 of 2 added');
    expect(calls).toEqual([
      'screen.png:screenshot',
      'logo.png:logo',
      'logo.png:logo',
    ]);
    view.unmount();
    expect(revoke).toHaveBeenCalled();
  });

  it('keeps unmatched providers visible and offers matching inside the artwork dialog', async () => {
    server.use(
      http.get(`http://localhost/api/artwork-providers`, ({ request }) =>
        HttpResponse.json(
          new URL(request.url).searchParams.has('titleId')
            ? []
            : [
                provider,
              ],
        ),
      ),
      http.get('http://localhost/api/titles/title1/provider-matches', () =>
        HttpResponse.json([
          {
            providerId: 'igdb',
            providerName: 'IGDB',
            isAvailable: true,
            state: 'NeedsMatch',
            capabilities: [
              'artwork',
            ],
            revision: 'revision',
          },
        ]),
      ),
    );
    show();
    await userEvent.setup().click(
      await screen.findByRole('button', {
        name: 'Find match',
      }),
    );
    expect(
      await screen.findByRole('dialog', {
        name: 'Match IGDB',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('textbox', {
        name: 'Game name',
      }),
    ).toHaveValue('Test game');
  });

  it('hides disabled providers but retains enabled providers needing setup', async () => {
    server.use(
      http.get(`http://localhost/api/artwork-providers`, () =>
        HttpResponse.json([
          provider,
          {
            ...provider,
            id: 'disabled',
            name: 'Disabled provider',
            isAvailable: false,
          },
          {
            ...provider,
            id: 'setup',
            name: 'Setup provider',
            isAvailable: false,
          },
        ]),
      ),
      http.get('http://localhost/api/titles/title1/provider-matches', () =>
        HttpResponse.json([
          {
            providerId: 'igdb',
          },
          {
            providerId: 'setup',
            isAvailable: false,
          },
        ]),
      ),
    );
    show();
    expect(
      await screen.findByRole('button', {
        name: /Setup provider/,
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: /Disabled provider/,
      }),
    ).not.toBeInTheDocument();
  });
  it('collects SteamGridDB logos with capability filters and a reviewable preview', async () => {
    const logoProvider = {
      ...provider,
      id: 'steamgriddb',
      name: 'SteamGridDB',
      mediaTypes: [
        'Logo',
      ],
      roles: [
        {
          role: 'Logo',
          dimensions: [],
          styles: [
            'official',
            'white',
          ],
        },
      ],
    };
    const queries: URLSearchParams[] = [];
    const imported = vi.fn(async ({ request }: { request: Request }) => {
      expect(await request.json()).toEqual({
        candidateReference: 'logo-reference',
      });
      return HttpResponse.json({
        id: 'logo1',
        type: 'Logo',
        sourceId: 'gallery:steamgriddb',
        url: '/media/logo1',
      });
    });
    const apply = vi.fn(() => HttpResponse.json({}));
    server.use(
      http.get(`http://localhost/api/artwork-providers`, () =>
        HttpResponse.json([
          logoProvider,
        ]),
      ),
      http.get('http://localhost/api/titles/title1/provider-matches', () =>
        HttpResponse.json([
          {
            providerId: 'steamgriddb',
            state: 'Confirmed',
          },
        ]),
      ),
      http.get(`${titles}/artwork-candidates`, ({ request }) => {
        queries.push(new URL(request.url).searchParams);
        return HttpResponse.json({
          items: [
            {
              reference: 'logo-reference',
              candidateId: 'logo-candidate',
              providerAssetId: '77',
              role: 'Logo',
              width: 1024,
              height: 400,
              style: 'official',
              attribution: 'Logo artist',
              sourcePageUrl: 'https://www.steamgriddb.com/logo/77',
            },
          ],
          hasNextPage: false,
          nextCursor: null,
        });
      }),
      shownTitle,
      http.post(`${titles}/artwork-candidates/:candidateId/import`, imported),
      http.post(`${titles}/artwork/:role/apply`, apply),
    );
    show();
    const user = userEvent.setup();
    await screen.findByRole('button', {
      name: 'Select image 77',
    });
    expect(screen.getByLabelText('Image type')).toHaveValue('Logo');
    expect(screen.queryByLabelText('Dimensions')).not.toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText('Style'), 'white');
    await waitFor(() => expect(queries.some((query) => query.get('style') === 'white')).toBe(true));
    expect(
      queries.every(
        (query) =>
          query.get('role') === 'Logo' &&
          query.get('mediaType') === 'Logo' &&
          !query.has('dimension'),
      ),
    ).toBe(true);
    await user.click(
      screen.getByRole('button', {
        name: 'Preview image 77',
      }),
    );
    const preview = within(
      await screen.findByRole('dialog', {
        name: 'Preview logo',
      }),
    );
    expect(preview.getByText('1024 × 400 · official · By Logo artist')).toBeInTheDocument();
    expect(
      preview.getByRole('link', {
        name: 'View original on SteamGridDB',
      }),
    ).toHaveAttribute('href', 'https://www.steamgriddb.com/logo/77');
    await user.click(
      preview.getByRole('button', {
        name: 'Select image',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Add 1 image',
      }),
    );
    await waitFor(() => expect(imported).toHaveBeenCalledOnce());
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Select image 77',
        }),
      ).toBeDisabled(),
    );
    expect(apply).not.toHaveBeenCalled();
  });

  it('does not browse a linked provider while it requires configuration', async () => {
    const browse = vi.fn(() =>
      HttpResponse.json({
        items: [],
        hasNextPage: false,
        nextCursor: null,
      }),
    );
    server.use(
      http.get(`http://localhost/api/artwork-providers`, () =>
        HttpResponse.json([
          {
            ...provider,
            isAvailable: false,
          },
        ]),
      ),
      http.get(`${titles}/artwork-candidates`, browse),
    );
    show();
    expect(await screen.findByText(/This provider needs configuration/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Image type')).not.toBeInTheDocument();
    expect(browse).not.toHaveBeenCalled();
  });
});
