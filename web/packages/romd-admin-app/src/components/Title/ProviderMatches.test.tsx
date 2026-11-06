import { readFileSync } from 'node:fs';
import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { server } from '../../test/msw/server';
import { ProviderMatches, ProviderMatchPicker } from './ProviderMatches';
import classes from './ProviderMatches.module.css';

const matches = 'http://localhost/api/titles/title1/provider-matches';
const providers = 'http://localhost/api/metadata-providers';
const row = {
  providerId: 'example',
  providerName: 'Example provider',
  isAvailable: true,
  capabilities: [
    'search',
    'resolve',
  ],
  revision: '00000000-0000-0000-0000-000000000001',
  state: 'NeedsMatch',
  metadataNeedsRefresh: false,
};
const game = {
  id: '42',
  name: 'The matching game',
  url: 'https://example.com/games/42',
  year: 1994,
  platforms: [
    'SNES',
  ],
};
function searchResult(
  items = [
    game,
  ],
  completeness: 'Complete' | 'ProviderLimited' | 'BudgetLimited' = 'Complete',
  limit = 500,
) {
  return {
    items,
    completeness,
    limit,
  };
}

beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        row,
      ]),
    ),
  );
});
afterEach(() =>
  client.setConfig({
    baseUrl: '',
  }),
);
function show(density?: 'compact' | 'comfortable') {
  return render(
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
        <ProviderMatches
          titleId="title1"
          titleName="Game"
          density={density}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

it('marks the host density on its own size container and tiers row spacing in CSS', async () => {
  const { unmount } = show();
  const list = (await screen.findByText('Example provider')).closest('[data-density]');
  expect(list).toHaveAttribute('data-density', 'comfortable');
  unmount();
  show('compact');
  expect((await screen.findByText('Example provider')).closest('[data-density]')).toHaveAttribute(
    'data-density',
    'compact',
  );
  const styles = readFileSync(`${import.meta.dirname}/ProviderMatches.module.css`, 'utf8');
  expect(styles).not.toMatch(/@media/);
  expect(styles).toMatch(/\.matches \{\s*container: provider-matches \/ inline-size;/);
  expect(styles).toMatch(
    /\.matches\[data-density='compact'\] \{[^}]*--matches-row-padding: var\(--mantine-spacing-xs\)/,
  );
  expect(styles).toMatch(
    /@container provider-matches \(max-width: 30em\) \{ \.match \{ padding-block: var\(--mantine-spacing-xs\); \} \}/,
  );
});

it('renders server-supplied provider names and external game links', async () => {
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        {
          ...row,
          state: 'Confirmed',
          externalId: '42',
          game,
        },
      ]),
    ),
  );
  show();
  expect(await screen.findByText('Example provider')).toBeInTheDocument();
  const link = screen.getByRole('link', {
    name: /The matching game/,
  });
  expect(link).toHaveAttribute('href', game.url);
  expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  expect(screen.queryByText('IGDB')).not.toBeInTheDocument();
});

it('requires a preview selection before confirming a match and sends its revision', async () => {
  const save = vi.fn(async ({ request }: { request: Request }) => {
    expect(request.headers.get('If-Match')).toBe(`"${row.revision}"`);
    expect(await request.json()).toEqual({
      externalId: '42',
    });
    return HttpResponse.json(row);
  });
  // A search is a safe read with a bounded-suggestion result.
  const search = vi.fn(({ request }: { request: Request }) => {
    const url = new URL(request.url);
    expect(url.searchParams.get('query')).toBe('Game');
    expect(url.searchParams.has('resolve')).toBe(false);
    return HttpResponse.json(searchResult());
  });
  server.use(http.get(`${providers}/example/games`, search), http.put(`${matches}/example`, save));
  show();
  const user = userEvent.setup();
  await user.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Confirm match',
    }),
  ).toBeDisabled();
  await user.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  await user.click(
    await screen.findByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  expect(save).not.toHaveBeenCalled();
  await user.click(
    screen.getByRole('button', {
      name: 'Confirm The matching game',
    }),
  );
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
  expect(search).toHaveBeenCalledOnce();
});

it('requires a reload and renewed selection after a match conflict', async () => {
  const latest = {
    ...row,
    revision: '00000000-0000-0000-0000-000000000002',
    state: 'Confirmed',
    externalId: '99',
    game: {
      ...game,
      id: '99',
      name: 'Another current match',
    },
  };
  let changed = false;
  const save = vi.fn(async ({ request }: { request: Request }) => {
    const body = await request.json();
    if (!changed) {
      expect(request.headers.get('If-Match')).toBe(`"${row.revision}"`);
      expect(body).toEqual({
        externalId: '42',
      });
      changed = true;
      return HttpResponse.json(
        {
          detail: 'The provider match changed.',
        },
        {
          status: 412,
        },
      );
    }
    expect(request.headers.get('If-Match')).toBe(`"${latest.revision}"`);
    expect(body).toEqual({
      externalId: '42',
    });
    return HttpResponse.json(row);
  });
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        changed ? latest : row,
      ]),
    ),
    http.get(`${providers}/example/games`, () => HttpResponse.json(searchResult())),
    http.put(`${matches}/example`, save),
  );
  show();
  const user = userEvent.setup();
  await user.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  await user.click(
    await screen.findByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Confirm The matching game',
    }),
  );
  await user.click(
    await screen.findByRole('button', {
      name: 'Reload latest',
    }),
  );
  const reloadDialog = await screen.findByRole('dialog', {
    name: 'Reload the latest match?',
  });
  await user.click(
    within(reloadDialog).getByRole('button', {
      name: 'Keep editing',
    }),
  );
  expect(screen.getByText('Selected: The matching game')).toBeInTheDocument();
  await user.click(
    screen.getByRole('button', {
      name: 'Reload latest',
    }),
  );
  await user.click(
    within(
      await screen.findByRole('dialog', {
        name: 'Reload the latest match?',
      }),
    ).getByRole('button', {
      name: 'Reload latest',
    }),
  );
  expect(await screen.findByText(/Current match: Another current match/)).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Confirm match',
    }),
  ).toBeDisabled();
  expect(save).toHaveBeenCalledTimes(1);
  await user.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  await user.click(
    await screen.findByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Confirm The matching game',
    }),
  );
  await waitFor(() => expect(save).toHaveBeenCalledTimes(2));
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
});

it('tells the workbench when a match is decided and when an inline picker is cancelled', async () => {
  const onResolved = vi.fn();
  const onDismissed = vi.fn();
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        {
          ...row,
          state: 'AutoMatched',
          externalId: '42',
          game,
        },
      ]),
    ),
    http.post(`${matches}/example/confirm`, () => HttpResponse.json(row)),
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
        <ProviderMatches
          titleId="title1"
          titleName="Game"
          inline
          onResolved={onResolved}
          onDismissed={onDismissed}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
  const user = userEvent.setup();
  await user.click(
    await screen.findByRole('button', {
      name: 'Change',
    }),
  );
  expect(
    screen.getByRole('textbox', {
      name: 'Game name',
    }),
  ).toHaveAttribute('data-autofocus', 'true');
  await user.click(
    screen.getByRole('button', {
      name: 'Cancel',
    }),
  );
  expect(onDismissed).toHaveBeenCalledOnce();
  await user.click(
    await screen.findByRole('button', {
      name: 'Confirm match',
    }),
  );
  await waitFor(() => expect(onResolved).toHaveBeenCalledOnce());
  expect(onDismissed).toHaveBeenCalledOnce();
});

it('shows persisted candidates and independent evidence without repeating provider search', async () => {
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        {
          ...row,
          state: 'NeedsReview',
          decision: {
            state: 'NeedsReview',
            explanation: 'Two plausible editions.',
            provenance: 'Local catalog evidence.',
            inputFingerprint: 'evidence-v2',
            localEvidence: {
              name: 'Game',
              names: [
                'Game',
              ],
              years: [
                1994,
              ],
              platforms: [
                'SNES',
              ],
            },
            candidates: [
              {
                game,
                score: 120,
                eligible: false,
                evidence: [
                  'Platform agrees.',
                ],
                conflicts: [
                  'Release year differs.',
                ],
              },
            ],
          },
        },
      ]),
    ),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  expect(screen.getByText('Two plausible editions.')).toBeInTheDocument();
  expect(screen.getByText('Local title: Game · SNES · 1994')).toBeInTheDocument();
  expect(screen.getByText('Conflict: Release year differs.')).toBeInTheDocument();
  expect(
    screen.getByRole('radio', {
      name: 'Select The matching game',
    }),
  ).toBeEnabled();
});

it('offers confirmation directly and invalidates the permanent workbench after saving', async () => {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  cache.setQueryData(
    [
      'enrichment-workspace',
      'list',
      'NeedsReview',
    ],
    {
      needsReview: 1,
    },
  );
  const confirm = vi.fn(async ({ request }: { request: Request }) => {
    expect(request.headers.get('If-Match')).toBe(`"${row.revision}"`);
    return HttpResponse.json(row);
  });
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        {
          ...row,
          state: 'AutoMatched',
          externalId: '42',
          game,
        },
      ]),
    ),
    http.post(`${matches}/example/confirm`, confirm),
  );
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <ProviderMatches
          titleId="title1"
          titleName="Game"
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Confirm match',
    }),
  );
  await waitFor(() => expect(confirm).toHaveBeenCalledOnce());
  await waitFor(() =>
    expect(
      cache.getQueryState([
        'enrichment-workspace',
        'list',
        'NeedsReview',
      ])?.isInvalidated,
    ).toBe(true),
  );
  cache.clear();
});

it('reloads the matches after a stale confirmation so the curator decides on the current one', async () => {
  let reads = 0;
  const revisions: (string | null)[] = [];
  server.use(
    http.get(matches, () => {
      reads += 1;
      return HttpResponse.json([
        {
          ...row,
          revision: reads > 1 ? '00000000-0000-0000-0000-000000000002' : row.revision,
          state: 'AutoMatched',
          externalId: '42',
          game,
        },
      ]);
    }),
    http.post(`${matches}/example/confirm`, ({ request }) => {
      revisions.push(request.headers.get('If-Match'));
      return revisions.length === 1
        ? HttpResponse.json(
            {
              errorCode: 'ProviderMatch.PreconditionFailed',
            },
            {
              status: 412,
            },
          )
        : HttpResponse.json(row);
    }),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Confirm match',
    }),
  );
  expect(
    await screen.findByText(
      'The provider match changed. Review the reloaded match, then decide again.',
    ),
  ).toBeInTheDocument();
  await waitFor(() => expect(reads).toBe(2));
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Confirm match',
    }),
  );
  await waitFor(() =>
    expect(revisions).toEqual([
      `"${row.revision}"`,
      '"00000000-0000-0000-0000-000000000002"',
    ]),
  );
});

it.each([
  'NoMatchFound',
  'NeedsReview',
  'NeedsMatch',
])('can resolve an unlinked %s decision by continuing without that provider', async (state) => {
  let suppressed = false;
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  cache.setQueryData(
    [
      'enrichment-workspace',
      'list',
    ],
    {
      needsReview: 1,
    },
  );
  const unlink = vi.fn(async ({ request }: { request: Request }) => {
    expect(request.headers.get('If-Match')).toBe(`"${row.revision}"`);
    suppressed = true;
    return new HttpResponse(null, {
      status: 204,
    });
  });
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        {
          ...row,
          state: suppressed ? 'Unlinked' : state,
        },
      ]),
    ),
    http.delete(`${matches}/example`, unlink),
  );
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <ProviderMatches
          titleId="title1"
          titleName="Game"
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
  expect(
    await screen.findByText(
      'You can continue without this provider. Automatic matching will stay off until you choose a match.',
    ),
  ).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Continue without this provider',
    }),
  );
  expect(
    await screen.findByText('Automatic matching is off. Find a match to use this provider again.'),
  ).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Find match',
    }),
  ).toBeEnabled();
  expect(
    screen.queryByRole('button', {
      name: 'Continue without this provider',
    }),
  ).not.toBeInTheDocument();
  expect(unlink).toHaveBeenCalledOnce();
  expect(
    cache.getQueryState([
      'enrichment-workspace',
      'list',
    ])?.isInvalidated,
  ).toBe(true);
  cache.clear();
});

it('preserves the review decision when continuing without a provider fails', async () => {
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        {
          ...row,
          state: 'NoMatchFound',
        },
      ]),
    ),
    http.delete(`${matches}/example`, () =>
      HttpResponse.json(
        {
          detail: 'The provider is unavailable.',
        },
        {
          status: 503,
        },
      ),
    ),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Continue without this provider',
    }),
  );
  expect(
    await screen.findByText('The match could not be updated. Reload matches and try again.'),
  ).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Continue without this provider',
    }),
  ).toBeEnabled();
  expect(
    screen.queryByText('Automatic matching is off. Find a match to use this provider again.'),
  ).not.toBeInTheDocument();
});

it('does not preselect an uncertain suggestion and supports keyboard selection', async () => {
  const other = {
    ...game,
    id: '43',
    name: 'Another edition',
    year: null,
    platforms: [],
  };
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        {
          ...row,
          state: 'NeedsReview',
          externalId: game.id,
          game,
          decision: {
            explanation: 'Insufficient corroboration.',
            provenance: 'Catalog',
            candidates: [
              game,
              other,
            ].map((game) => ({
              game,
              evidence: [],
              conflicts: [],
            })),
          },
        },
      ]),
    ),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Change',
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Confirm match',
    }),
  ).toBeDisabled();
  const first = screen.getByRole('radio', {
    name: 'Select The matching game',
  });
  expect(first).not.toBeChecked();
  await userEvent.click(first);
  await userEvent.keyboard('{ArrowDown}');
  expect(
    screen.getByRole('radio', {
      name: 'Select Another edition',
    }),
  ).toBeChecked();
  expect(
    screen.getByRole('button', {
      name: 'Confirm Another edition',
    }),
  ).toBeEnabled();
  expect(
    screen.getByText('Provider evidence: platform unknown · year unknown'),
  ).toBeInTheDocument();
});

it('guards dismissal of a selection and preserves it when editing continues', async () => {
  server.use(http.get(`${providers}/example/games`, () => HttpResponse.json(searchResult())));
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  await userEvent.click(
    await screen.findByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  await userEvent.keyboard('{Escape}');
  expect(await screen.findByText('Discard provider match draft?')).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Keep editing',
    }),
  );
  expect(
    screen.getByRole('radio', {
      name: 'Select The matching game',
    }),
  ).toBeChecked();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Cancel',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Discard draft',
    }),
  );
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
});

it('titles the inline picker with the scheme text colour rather than the dark-only card title colour', async () => {
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
        <ProviderMatches
          titleId="title1"
          titleName="Game"
          inline
          initialProviderId="example"
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
  const heading = await screen.findByText('Match Example provider');
  expect(heading).toHaveClass('romd-card-title', classes.pickerHeading);
  const styles = readFileSync(`${import.meta.dirname}/ProviderMatches.module.css`, 'utf8');
  expect(styles).toContain(
    '.pickerHeading:global(.romd-card-title) { color: light-dark(var(--mantine-color-text), var(--romd-text-strong)); }',
  );
});

it('opens a requested provider once and preserves its draft through background updates', async () => {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  server.use(http.get(`${providers}/example/games`, () => HttpResponse.json(searchResult())));
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <ProviderMatches
          titleId="title1"
          titleName="Game"
          inline
          initialProviderId="example"
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Search provider games',
    }),
  );
  await userEvent.click(
    await screen.findByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  await cache.invalidateQueries({
    queryKey: [
      'provider-matches',
      'title1',
    ],
  });
  expect(
    screen.getByRole('radio', {
      name: 'Select The matching game',
    }),
  ).toBeChecked();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Cancel',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Discard draft',
    }),
  );
  expect(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  ).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Reload matches',
    }),
  );
  expect(screen.queryByRole('radio')).not.toBeInTheDocument();
});

it('locks the selected game and dismissal until confirmation completes', async () => {
  let finishSave: () => void = () => {};
  const pending = new Promise<void>((resolve) => {
    finishSave = resolve;
  });
  server.use(
    http.get(`${providers}/example/games`, () => HttpResponse.json(searchResult())),
    http.put(`${matches}/example`, async () => {
      await pending;
      return HttpResponse.json(row);
    }),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  await userEvent.click(
    await screen.findByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Confirm The matching game',
    }),
  );
  expect(
    screen.getByRole('radio', {
      name: 'Select The matching game',
    }),
  ).toBeDisabled();
  expect(
    screen.getByRole('button', {
      name: 'Cancel',
    }),
  ).toBeDisabled();
  expect(
    screen.getByRole('textbox', {
      name: 'Game name',
    }),
  ).toBeDisabled();
  await userEvent.keyboard('{Escape}');
  expect(
    screen.getByRole('dialog', {
      name: 'Match Example provider',
    }),
  ).toBeInTheDocument();
  finishSave();
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
});

it('keeps provider settings guidance available when a requested provider is unavailable', async () => {
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        {
          ...row,
          isAvailable: false,
        },
      ]),
    ),
  );
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
        <ProviderMatches
          titleId="title1"
          titleName="Game"
          inline
          initialProviderId="example"
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
  expect(await screen.findByText('Provider configuration required')).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Find match',
    }),
  ).toBeDisabled();
  expect(
    screen.queryByRole('button', {
      name: 'Search provider games',
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Reload matches',
    }),
  ).toBeEnabled();
});

it('reopens a dismissed provider only for a new explicit review request', async () => {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const view = (request: string) => (
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <ProviderMatches
          titleId="title1"
          titleName="Game"
          inline
          initialProviderId="example"
          providerRequestKey={request}
        />
      </QueryClientProvider>
    </MantineProvider>
  );
  const { rerender } = render(view('first'));
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Cancel',
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Find match',
    }),
  ).toBeInTheDocument();
  rerender(view('first'));
  expect(
    screen.queryByRole('button', {
      name: 'Search provider games',
    }),
  ).not.toBeInTheDocument();
  rerender(view('second'));
  expect(
    await screen.findByRole('button', {
      name: 'Search provider games',
    }),
  ).toBeInTheDocument();
  rerender(view('third'));
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Cancel',
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Find match',
    }),
  ).toBeInTheDocument();
});

it('switches to a newly requested provider while a clean picker is open', async () => {
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        row,
        {
          ...row,
          providerId: 'second',
          providerName: 'Second provider',
        },
      ]),
    ),
  );
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const view = (providerId: string) => (
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <ProviderMatches
          titleId="title1"
          titleName="Game"
          inline
          initialProviderId={providerId}
          providerRequestKey={providerId}
        />
      </QueryClientProvider>
    </MantineProvider>
  );
  const { rerender } = render(view('example'));
  expect(await screen.findByText('Match Example provider')).toBeInTheDocument();
  rerender(view('second'));
  expect(await screen.findByText('Match Second provider')).toBeInTheDocument();
  expect(screen.queryByText('Match Example provider')).not.toBeInTheDocument();
});

it('does not close a new provider picker when the previous save finishes late', async () => {
  let finishSave: () => void = () => {};
  const pending = new Promise<void>((resolve) => {
    finishSave = resolve;
  });
  const save = vi.fn(async () => {
    await pending;
    return HttpResponse.json(row);
  });
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        row,
        {
          ...row,
          providerId: 'second',
          providerName: 'Second provider',
        },
      ]),
    ),
    http.get(`${providers}/example/games`, () => HttpResponse.json(searchResult())),
    http.put(`${matches}/example`, save),
  );
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const view = (providerId: string) => (
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <ProviderMatches
          titleId="title1"
          titleName="Game"
          inline
          initialProviderId={providerId}
          providerRequestKey={providerId}
        />
      </QueryClientProvider>
    </MantineProvider>
  );
  const { rerender } = render(view('example'));
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Search provider games',
    }),
  );
  await userEvent.click(
    await screen.findByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Confirm The matching game',
    }),
  );
  await waitFor(() => expect(save).toHaveBeenCalledOnce());
  rerender(view('second'));
  expect(await screen.findByText('Match Second provider')).toBeInTheDocument();
  const invalidation = vi.spyOn(cache, 'invalidateQueries');
  finishSave();
  await waitFor(() =>
    expect(invalidation).toHaveBeenCalledWith({
      queryKey: [
        'artwork-enrichment',
        'title',
        'title1',
      ],
    }),
  );
  await waitFor(() => expect(cache.isMutating()).toBe(0));
  expect(screen.getByText('Match Second provider')).toBeInTheDocument();
});

it('resets a same-provider draft after a new accepted navigation request', async () => {
  server.use(http.get(`${providers}/example/games`, () => HttpResponse.json(searchResult())));
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const view = (request: string) => (
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <ProviderMatches
          titleId="title1"
          titleName="Game"
          inline
          initialProviderId="example"
          providerRequestKey={request}
        />
      </QueryClientProvider>
    </MantineProvider>
  );
  const { rerender } = render(view('initial'));
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Search provider games',
    }),
  );
  await userEvent.click(
    await screen.findByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Confirm The matching game',
    }),
  ).toBeEnabled();
  // The router changes location.key only after its unsaved-changes blocker allows navigation.
  rerender(view('accepted-navigation'));
  expect(
    screen.getByRole('button', {
      name: 'Confirm match',
    }),
  ).toBeDisabled();
  expect(
    screen.queryByRole('radio', {
      name: 'Select The matching game',
    }),
  ).not.toBeInTheDocument();
  expect(screen.getByText('Select a provider game to confirm this match.')).toBeInTheDocument();
});

it.each([
  [
    'ProviderLimited',
    100,
    'Provider search is limited to 100 suggestions and may be incomplete.',
  ],
  [
    'BudgetLimited',
    500,
    'The 500-game search limit was reached. More matches may exist.',
  ],
] as const)('shows %s search limits without treating the suggestions as exhaustive', async (completeness, limit, message) => {
  server.use(
    http.get(`${providers}/example/games`, () =>
      HttpResponse.json(
        searchResult(
          [
            game,
          ],
          completeness,
          limit,
        ),
      ),
    ),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  expect(
    await screen.findByText(message, {
      exact: false,
    }),
  ).toBeInTheDocument();
  expect(screen.getByText(/Refine your search or enter a game ID or URL\./)).toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: /Load more/,
    }),
  ).not.toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Confirm The matching game',
    }),
  ).toBeEnabled();
});

it('does not claim that an empty provider-limited search proves there is no match', async () => {
  server.use(
    http.get(`${providers}/example/games`, () =>
      HttpResponse.json(searchResult([], 'ProviderLimited', 100)),
    ),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  expect(
    await screen.findByText('No suggestions returned; more matches may exist.'),
  ).toBeInTheDocument();
  expect(screen.getByText(/Refine your search or enter a game ID or URL\./)).toBeInTheDocument();
  expect(screen.queryByText('No matching games found.')).not.toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Confirm match',
    }),
  ).toBeDisabled();
});

it('describes a complete empty search as the submitted search result', async () => {
  server.use(http.get(`${providers}/example/games`, () => HttpResponse.json(searchResult([]))));
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  expect(await screen.findByText('No suggestions returned for this search.')).toBeInTheDocument();
  expect(screen.queryByText(/more matches may exist/i)).not.toBeInTheDocument();
});

it('clears suggestions and the selected game immediately when the search input changes', async () => {
  server.use(http.get(`${providers}/example/games`, () => HttpResponse.json(searchResult())));
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  await userEvent.click(
    await screen.findByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  await userEvent.type(
    screen.getByRole('textbox', {
      name: 'Game name',
    }),
    ' sequel',
  );
  expect(
    screen.queryByRole('radio', {
      name: 'Select The matching game',
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Confirm match',
    }),
  ).toBeDisabled();
});

it('aborts a pending search when its query changes and ignores its late response', async () => {
  let finishSearch = () => {};
  const pending = new Promise<void>((resolve) => {
    finishSearch = resolve;
  });
  const started = vi.fn();
  const aborted = vi.fn();
  const finished = vi.fn();
  server.use(
    http.get(`${providers}/example/games`, async ({ request }) => {
      started();
      request.signal.addEventListener('abort', aborted);
      await pending;
      finished();
      return HttpResponse.json(searchResult());
    }),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  await waitFor(() => expect(started).toHaveBeenCalledOnce());
  await userEvent.type(
    screen.getByRole('textbox', {
      name: 'Game name',
    }),
    ' sequel',
  );
  await waitFor(() => expect(aborted).toHaveBeenCalledOnce());
  finishSearch();
  await waitFor(() => expect(finished).toHaveBeenCalledOnce());
  expect(
    screen.queryByRole('radio', {
      name: 'Select The matching game',
    }),
  ).not.toBeInTheDocument();
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Confirm match',
    }),
  ).toBeDisabled();
});

it('resolves a game ID or URL through the separate safe lookup resource', async () => {
  const lookup = vi.fn(({ request }: { request: Request }) => {
    const url = new URL(request.url);
    expect(url.searchParams.get('input')).toBe(game.url);
    expect(url.searchParams.has('query')).toBe(false);
    expect(url.searchParams.has('resolve')).toBe(false);
    return HttpResponse.json(game);
  });
  const search = vi.fn(() => HttpResponse.json(searchResult()));
  server.use(
    http.get(`${providers}/example/games`, search),
    http.get(`${providers}/example/game-resolution`, lookup),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await userEvent.click(
    screen.getByRole('radio', {
      name: 'ID or URL',
    }),
  );
  await userEvent.type(
    screen.getByRole('textbox', {
      name: 'Provider ID or page URL',
    }),
    game.url,
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Look up provider game',
    }),
  );
  await userEvent.click(
    await screen.findByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Confirm The matching game',
    }),
  ).toBeEnabled();
  expect(lookup).toHaveBeenCalledOnce();
  expect(search).not.toHaveBeenCalled();
  expect(screen.queryByText(/search limit|search is limited/i)).not.toBeInTheDocument();
});

it('clears the previous mode results and cancels a search when switching to ID lookup', async () => {
  let finishSearch = () => {};
  const pending = new Promise<void>((resolve) => {
    finishSearch = resolve;
  });
  const started = vi.fn();
  const aborted = vi.fn();
  server.use(
    http.get(`${providers}/example/games`, async ({ request }) => {
      started();
      request.signal.addEventListener('abort', aborted);
      await pending;
      return HttpResponse.json(searchResult());
    }),
    http.get(`${providers}/example/game-resolution`, () =>
      HttpResponse.json({
        ...game,
        id: '99',
        name: 'Resolved game',
      }),
    ),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  await waitFor(() => expect(started).toHaveBeenCalledOnce());
  await userEvent.click(
    screen.getByRole('radio', {
      name: 'ID or URL',
    }),
  );
  await waitFor(() => expect(aborted).toHaveBeenCalledOnce());
  await userEvent.type(
    screen.getByRole('textbox', {
      name: 'Provider ID or page URL',
    }),
    '99',
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Look up provider game',
    }),
  );
  expect(
    await screen.findByRole('radio', {
      name: 'Select Resolved game',
    }),
  ).toBeInTheDocument();
  finishSearch();
  expect(
    screen.queryByRole('radio', {
      name: 'Select The matching game',
    }),
  ).not.toBeInTheDocument();
});

it('cancels an old provider search and clears its candidates on a provider change', async () => {
  let finishSearch = () => {};
  const pending = new Promise<void>((resolve) => {
    finishSearch = resolve;
  });
  const started = vi.fn();
  const aborted = vi.fn();
  server.use(
    http.get(matches, () =>
      HttpResponse.json([
        row,
        {
          ...row,
          providerId: 'second',
          providerName: 'Second provider',
        },
      ]),
    ),
    http.get(`${providers}/example/games`, async ({ request }) => {
      started();
      request.signal.addEventListener('abort', aborted);
      await pending;
      return HttpResponse.json(searchResult());
    }),
    http.get(`${providers}/second/games`, () =>
      HttpResponse.json(
        searchResult([
          {
            ...game,
            id: '99',
            name: 'Second provider game',
          },
        ]),
      ),
    ),
  );
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const view = (providerId: string) => (
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <ProviderMatches
          titleId="title1"
          titleName="Game"
          inline
          initialProviderId={providerId}
          providerRequestKey={providerId}
        />
      </QueryClientProvider>
    </MantineProvider>
  );
  const { rerender } = render(view('example'));
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Search provider games',
    }),
  );
  await waitFor(() => expect(started).toHaveBeenCalledOnce());
  rerender(view('second'));
  expect(await screen.findByText('Match Second provider')).toBeInTheDocument();
  await waitFor(() => expect(aborted).toHaveBeenCalledOnce());
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  expect(
    await screen.findByRole('radio', {
      name: 'Select Second provider game',
    }),
  ).toBeInTheDocument();
  finishSearch();
  expect(
    screen.queryByRole('radio', {
      name: 'Select The matching game',
    }),
  ).not.toBeInTheDocument();
  cache.clear();
});

it('rejects a response without search completeness instead of claiming no suggestions exist', async () => {
  server.use(
    http.get(`${providers}/example/games`, () =>
      HttpResponse.json({
        items: [],
        limit: 100,
      }),
    ),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Search provider games',
    }),
  );
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'The provider returned an invalid search result. Try again.',
  );
  expect(screen.queryByText(/No suggestions returned/)).not.toBeInTheDocument();
});

it('keeps an unsuccessful ID lookup distinct from a completed search', async () => {
  server.use(
    http.get(`${providers}/example/game-resolution`, () =>
      HttpResponse.json(
        {
          detail: 'Game not found.',
        },
        {
          status: 404,
        },
      ),
    ),
  );
  show();
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Find match',
    }),
  );
  await userEvent.click(
    screen.getByRole('radio', {
      name: 'ID or URL',
    }),
  );
  await userEvent.type(
    screen.getByRole('textbox', {
      name: 'Provider ID or page URL',
    }),
    'missing',
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Look up provider game',
    }),
  );
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'Could not find that provider game. Check the ID or URL, or try again.',
  );
  expect(screen.queryByText(/No suggestions returned/)).not.toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Confirm match',
    }),
  ).toBeDisabled();
});

it.each([
  'provider',
  'title',
] as const)('clears a shared picker draft when its %s changes', async (change) => {
  server.use(http.get(`${providers}/example/games`, () => HttpResponse.json(searchResult())));
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const view = (changed: boolean) => (
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <ProviderMatchPicker
          titleId={changed && change === 'title' ? 'title2' : 'title1'}
          titleName="Game"
          row={{
            ...row,
            providerId: changed && change === 'provider' ? 'second' : row.providerId,
            externalId: null,
            game: null,
            confidence: null,
            lastError: null,
          }}
          onClose={() => {}}
        />
      </QueryClientProvider>
    </MantineProvider>
  );
  const { rerender } = render(view(false));
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Search provider games',
    }),
  );
  await userEvent.click(
    await screen.findByRole('radio', {
      name: 'Select The matching game',
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Confirm The matching game',
    }),
  ).toBeEnabled();
  rerender(view(true));
  expect(
    screen.getByRole('button', {
      name: 'Confirm match',
    }),
  ).toBeDisabled();
  expect(
    screen.queryByRole('radio', {
      name: 'Select The matching game',
    }),
  ).not.toBeInTheDocument();
  cache.clear();
});
