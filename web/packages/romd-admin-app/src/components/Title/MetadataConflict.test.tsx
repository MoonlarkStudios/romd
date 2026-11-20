import { MantineProvider } from '@mantine/core';
import { client, type MetadataLayer, type TitleDetail } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useEnrichmentAssessment } from '../../hooks/api/useEnrichmentWorkspace';
import { titleDetailKeys, useTitleDetail } from '../../hooks/api/useTitleDetail';
import { assessmentFixture, writtenTitleFixture } from '../../test/msw/fixtures/enrichment';
import { server } from '../../test/msw/server';
import { MetadataPanel } from './MetadataPanel';

vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canManageTitles: true,
  }),
}));

const api = 'http://localhost/api';
const title: TitleDetail = writtenTitleFixture({
  id: 't1',
  name: 'Test Game',
  systemKey: 'dreamcast',
  enrichmentStatus: 'Completed',
  publisher: 'Sega',
});
const userLayer = (
  publisher: string | null,
  name: string | null = null,
): MetadataLayer & {
  name?: string | null;
} => ({
  sourceId: 'user',
  sourceType: 'User',
  description: null,
  publisher,
  developer: null,
  genre: null,
  releaseDate: null,
  players: null,
  rating: null,
  updatedAt: '2026-09-22T00:00:00Z',
  name,
});

let revision: number;
let publisher: string | null;
let titleName: string;
let assessmentAvailable: boolean;
const saves: Record<string, unknown>[] = [];
/** Another curator's change: a new revision and new values, seen here only after a refresh. */
const otherCuratorChange = () => {
  revision += 1;
  publisher = 'Other curator';
  titleName = 'Renamed elsewhere';
};

beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
  revision = 1;
  publisher = null;
  titleName = 'Test Game';
  assessmentAvailable = true;
  saves.length = 0;
  server.use(
    http.get(`${api}/systems/dreamcast`, () =>
      HttpResponse.json({
        name: 'Sega Dreamcast',
      }),
    ),
    http.get(`${api}/titles/t1/enrichment-assessment`, () =>
      assessmentAvailable
        ? HttpResponse.json(
            assessmentFixture([], {
              titleId: 't1',
              revision: `rev-${revision}`,
            }),
          )
        : new HttpResponse(null, {
            status: 500,
          }),
    ),
    http.get(`${api}/titles/t1/enrichment`, () =>
      HttpResponse.json({
        titleId: 't1',
        name: titleName,
        enrichmentStatus: 'Completed',
        fieldProvenance: {
          Publisher: publisher ? 'user' : 'igdb',
        },
        fieldSourceOverrides: {},
        layers: [
          userLayer(publisher, titleName),
        ],
        externalIds: [],
      }),
    ),
    http.get(`${api}/titles/t1`, () =>
      HttpResponse.json({
        ...title,
        revision: `rev-${revision}`,
        name: titleName,
        publisher: publisher ?? 'Sega',
      }),
    ),
    http.get(`${api}/catalog/filters`, () =>
      HttpResponse.json({
        genres: [],
        years: [],
        manufacturers: [],
        regions: [],
        languages: [],
        contentRatings: [],
        platforms: [],
        enrichmentStatuses: [],
      }),
    ),
    http.patch(`${api}/titles/t1`, async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown> & {
        publisher?: string | null;
        name?: string | null;
      };
      const ifMatch = request.headers.get('If-Match');
      saves.push({
        ...body,
        ifMatch,
      });
      if (ifMatch !== `"rev-${revision}"`) {
        return HttpResponse.json(
          {
            status: 412,
            title: 'Precondition Failed',
            errorCode: 'Titles.PreconditionFailed',
          },
          {
            status: 412,
            headers: {
              'Content-Type': 'application/problem+json',
            },
          },
        );
      }
      revision += 1;
      publisher = body.publisher ?? null;
      if (body.name) titleName = body.name;
      return HttpResponse.json(
        {
          ...title,
          revision: `rev-${revision}`,
          name: titleName,
          publisher,
        },
        {
          headers: {
            ETag: `"rev-${revision}"`,
          },
        },
      );
    }),
  );
});
afterEach(() => {
  client.setConfig({
    baseUrl: '',
  });
});

/** The workbench keeps the assessment loaded; the editors no longer read their revision from it. */
function AssessmentObserver({ children }: { children: ReactNode }) {
  useEnrichmentAssessment('t1');
  return children;
}
function renderWith(element: ReactNode) {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <MemoryRouter>
          <AssessmentObserver>{element}</AssessmentObserver>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
  return cache;
}
/** Hosts the metadata form with the live title and its revision. */
function MetadataPanelHost() {
  const detail = useTitleDetail('t1');
  return detail.data ? (
    <MetadataPanel
      title={detail.data}
      titleId="t1"
    />
  ) : null;
}
const assessmentLoaded = (cache: QueryClient) =>
  waitFor(() =>
    expect(
      cache.getQueryState([
        'enrichment-workspace',
        'title',
        't1',
      ])?.status,
    ).toBe(assessmentAvailable ? 'success' : 'error'),
  );
async function draftPublisher(user: ReturnType<typeof userEvent.setup>, value: string) {
  const form = await screen.findByRole('form', {
    name: 'Title metadata',
  });
  const input = within(form).getByRole('textbox', {
    name: 'Publisher',
  });
  await user.clear(input);
  await user.type(input, value);
  return form;
}

describe('metadata draft conflicts', () => {
  it('keeps the draft on a conflict and reloads the latest values only after confirmation', async () => {
    const user = userEvent.setup();
    const cache = renderWith(<MetadataPanelHost />);
    await assessmentLoaded(cache);
    const form = await draftPublisher(user, 'My publisher');
    otherCuratorChange();
    await act(async () => {
      await cache.invalidateQueries({
        queryKey: titleDetailKeys.detail('t1'),
      });
    });
    expect(
      within(form).getByRole('textbox', {
        name: 'Publisher',
      }),
    ).toHaveValue('My publisher');

    await user.click(
      within(form).getByRole('button', {
        name: 'Save changes',
      }),
    );
    expect(
      await within(form).findByText('This title changed since you started editing.'),
    ).toBeInTheDocument();
    expect(saves).toEqual([
      expect.objectContaining({
        publisher: 'My publisher',
        ifMatch: '"rev-1"',
      }),
    ]);
    expect(
      within(form).getByRole('textbox', {
        name: 'Publisher',
      }),
    ).toHaveValue('My publisher');
    expect(
      within(form).getByRole('button', {
        name: 'Save changes',
      }),
    ).toBeDisabled();

    await user.click(
      within(form).getByRole('button', {
        name: 'Reload latest',
      }),
    );
    expect(
      within(form).getByText('Reloading discards your unsaved metadata changes.'),
    ).toBeInTheDocument();
    await user.click(
      within(form).getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(
      within(form).getByRole('textbox', {
        name: 'Publisher',
      }),
    ).toHaveValue('My publisher');
    await user.click(
      within(form).getByRole('button', {
        name: 'Reload latest',
      }),
    );
    await user.click(
      within(form).getByRole('button', {
        name: 'Reload latest',
      }),
    );

    const reopened = await screen.findByRole('form', {
      name: 'Title metadata',
    });
    await waitFor(() =>
      expect(
        within(reopened).getByRole('textbox', {
          name: 'Publisher',
        }),
      ).toHaveValue('Other curator'),
    );
    expect(
      within(reopened).queryByText('This title changed since you started editing.'),
    ).not.toBeInTheDocument();
    const input = within(reopened).getByRole('textbox', {
      name: 'Publisher',
    });
    await user.clear(input);
    await user.type(input, 'Final publisher');
    await user.click(
      within(reopened).getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeDisabled(),
    );
    expect(saves[1]).toEqual(
      expect.objectContaining({
        publisher: 'Final publisher',
        ifMatch: '"rev-2"',
      }),
    );
  });

  it('starts the next draft from the refreshed revision, so consecutive saves never conflict with themselves', async () => {
    const user = userEvent.setup();
    const cache = renderWith(<MetadataPanelHost />);
    await assessmentLoaded(cache);
    const first = await draftPublisher(user, 'First');
    await user.click(
      within(first).getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeDisabled(),
    );
    const second = await draftPublisher(user, 'Second');
    await user.click(
      within(second).getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeDisabled(),
    );
    expect(saves).toEqual([
      expect.objectContaining({
        publisher: 'First',
        ifMatch: '"rev-1"',
      }),
      expect.objectContaining({
        publisher: 'Second',
        ifMatch: '"rev-2"',
      }),
    ]);
  });

  it('retries a partly applied save from its last applied write, not from its starting revision', async () => {
    let overrides: Record<string, string> = {
      Publisher: 'igdb',
    };
    let failMetadataOnce = true;
    server.use(
      http.get(`${api}/titles/t1/enrichment`, () =>
        HttpResponse.json({
          titleId: 't1',
          name: titleName,
          enrichmentStatus: 'Completed',
          fieldProvenance: {
            Publisher: 'igdb',
          },
          fieldSourceOverrides: overrides,
          layers: [
            userLayer(publisher, titleName),
          ],
          externalIds: [],
        }),
      ),
      http.patch(`${api}/titles/t1/enrichment`, ({ request }) => {
        if (request.headers.get('If-Match') !== `"rev-${revision}"`)
          return new HttpResponse(null, {
            status: 412,
          });
        overrides = {};
        revision += 1;
        return HttpResponse.json(
          {
            ...title,
            revision: `rev-${revision}`,
          },
          {
            headers: {
              ETag: `"rev-${revision}"`,
            },
          },
        );
      }),
      http.patch(`${api}/titles/t1`, () => {
        if (!failMetadataOnce) return undefined;
        failMetadataOnce = false;
        return new HttpResponse(null, {
          status: 500,
        });
      }),
    );
    const user = userEvent.setup();
    const cache = renderWith(<MetadataPanelHost />);
    await assessmentLoaded(cache);
    const form = await draftPublisher(user, 'Mine');

    // The override clear applies; the custom value fails.
    await user.click(
      within(form).getByRole('button', {
        name: 'Save changes',
      }),
    );
    expect(await within(form).findByText(/Some changes were saved/)).toBeInTheDocument();
    await user.click(
      within(form).getByRole('button', {
        name: 'Save changes',
      }),
    );

    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeDisabled(),
    );
    expect(saves).toEqual([
      expect.objectContaining({
        publisher: 'Mine',
        ifMatch: '"rev-2"',
      }),
    ]);
  });

  it('sends the shown title revision even when the assessment is unavailable', async () => {
    assessmentAvailable = false;
    const user = userEvent.setup();
    const cache = renderWith(<MetadataPanelHost />);
    await assessmentLoaded(cache);
    const form = await draftPublisher(user, 'Unchecked');
    await user.click(
      within(form).getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toBeDisabled(),
    );
    expect(saves).toHaveLength(1);
    expect(saves[0]).toMatchObject({
      publisher: 'Unchecked',
      ifMatch: '"rev-1"',
    });
    expect(saves[0]).not.toHaveProperty('expectedRevision');
  });
});

it('shows provider IDs with their exact match links and the catalog sources for the title', async () => {
  server.use(
    http.get(`${api}/titles/t1/enrichment`, () =>
      HttpResponse.json({
        titleId: 't1',
        name: titleName,
        enrichmentStatus: 'Completed',
        layers: [],
        externalIds: [
          {
            provider: 'igdb',
            externalId: '42',
            isConfirmed: true,
          },
          {
            provider: 'steamgriddb',
            externalId: '99',
            isConfirmed: false,
          },
        ],
      }),
    ),
    http.get(`${api}/titles/t1/provider-matches`, () =>
      HttpResponse.json([
        {
          providerId: 'igdb',
          providerName: 'IGDB',
          game: {
            id: '42',
            url: 'https://www.igdb.com/games/test-game',
          },
        },
        {
          providerId: 'steamgriddb',
          providerName: 'SteamGridDB',
          game: {
            id: '100',
            url: 'https://www.steamgriddb.com/game/100',
          },
        },
      ]),
    ),
    http.get(`${api}/titles/t1/source-references`, () =>
      HttpResponse.json({
        items: [
          {
            catalogSourceId: 's1',
            kind: 'Dat',
            name: 'Dreamcast catalog',
            status: 'Active',
            entryCount: 1,
            datId: 'dat1',
            systemKey: 'dreamcast',
            hasActiveDefinition: true,
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      }),
    ),
  );
  renderWith(<MetadataPanelHost />);
  const providerLink = await screen.findByRole('link', {
    name: /Open IGDB ID 42/,
  });
  expect(providerLink).toHaveAttribute('href', 'https://www.igdb.com/games/test-game');
  expect(providerLink).toHaveAttribute('rel', 'noopener noreferrer');
  expect(screen.getByText('Confirmed')).toBeInTheDocument();
  expect(screen.getByText('Unconfirmed')).toBeInTheDocument();
  expect(
    screen.queryByRole('link', {
      name: /ID 99/,
    }),
  ).not.toBeInTheDocument();
  expect(
    await screen.findByRole('link', {
      name: 'Dreamcast catalog',
    }),
  ).toHaveAttribute('href', '/systems/dreamcast?dat=dat1&sourceTitle=t1');
});

describe('title name conflicts', () => {
  it('keeps the rename on a conflict and resets it to the latest name only after confirmation', async () => {
    const user = userEvent.setup();
    const cache = renderWith(<MetadataPanelHost />);
    await assessmentLoaded(cache);
    const input = await screen.findByRole('textbox', {
      name: 'Title name',
    });
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Save changes',
        }),
      ).toHaveProperty('disabled', true),
    );
    await user.clear(input);
    await user.type(input, 'My name');
    otherCuratorChange();
    await act(async () => {
      await cache.invalidateQueries({
        queryKey: titleDetailKeys.detail('t1'),
      });
    });
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    expect(
      await screen.findByText('This title changed since you started editing.'),
    ).toBeInTheDocument();
    expect(saves).toEqual([
      expect.objectContaining({
        name: 'My name',
        ifMatch: '"rev-1"',
      }),
    ]);
    expect(input).toHaveValue('My name');
    await user.click(
      screen.getByRole('button', {
        name: 'Reload latest',
      }),
    );
    expect(
      screen.getByText('Reloading discards your unsaved metadata changes.'),
    ).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Reload latest',
      }),
    );
    await waitFor(() => expect(input).toHaveValue('Renamed elsewhere'));
    expect(
      screen.queryByText('This title changed since you started editing.'),
    ).not.toBeInTheDocument();
  });
});
