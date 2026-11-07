import { readFileSync } from 'node:fs';
import { MantineProvider } from '@mantine/core';
import {
  client,
  type EnrichmentAssessment,
  type EnrichmentRequirement,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { type ComponentProps, createRef, type ReactNode, useState } from 'react';
import { createMemoryRouter, Outlet, RouterProvider, useLocation } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { enrichmentWorkspaceKeys } from '../../hooks/api/enrichmentWorkspaceCache';
import {
  assessmentFixture,
  chronoTriggerRequirements,
  obscureGameBoyRequirements,
  satisfied,
  unsatisfied,
  withRequirements,
  writtenTitleFixture,
} from '../../test/msw/fixtures/enrichment';
import { server } from '../../test/msw/server';
import { DraftGuardProvider } from '../Administration/DraftGuard';
import { UnsavedChanges } from '../Administration/UnsavedChanges';
import { TitleWorkbench, type TitleWorkbenchHandle } from './TitleWorkbench';
import type { TitleEditorOutcome } from './titleWorkbenchModel';

const permissions = vi.hoisted(() => ({
  canManageTitles: true,
  canConfigureIntegrations: true,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => permissions,
}));
vi.mock('./preview/CatalogPresentationPreview', () => ({
  CatalogPresentationPreview: () => <div>Presentation preview</div>,
}));
vi.mock('./MetadataPanel', () => ({
  MetadataPanel: ({
    initialField,
    onResolved,
    onDismissed,
  }: {
    initialField?: string;
  } & TitleEditorOutcome) => {
    const [draft, setDraft] = useState('');
    return (
      <>
        <UnsavedChanges
          dirty={!!draft}
          description="Your metadata draft has not been saved."
        />
        <div>Metadata editor: {initialField ?? 'none'}</div>
        <input
          aria-label="Description draft"
          value={draft}
          onChange={(event) => setDraft(event.currentTarget.value)}
        />
        <button
          type="button"
          onClick={() => {
            setDraft('');
            onResolved?.();
          }}
        >
          Save metadata
        </button>
        <button
          type="button"
          onClick={onDismissed}
        >
          Close metadata
        </button>
      </>
    );
  },
}));
vi.mock('./ProviderMatches', () => ({
  ProviderMatches: ({
    initialProviderId,
    onResolved,
    onDismissed,
  }: {
    initialProviderId?: string;
  } & TitleEditorOutcome) => (
    <>
      <div>Target provider: {initialProviderId ?? 'none'}</div>
      <input
        data-autofocus
        aria-label="Provider search"
      />
      <button
        type="button"
        onClick={onResolved}
      >
        Confirm provider match
      </button>
      <button
        type="button"
        onClick={onDismissed}
      >
        Cancel match
      </button>
    </>
  ),
}));
vi.mock('./ArtworkPanel', () => ({
  ArtworkPanel: ({ initialRole }: { initialRole?: string }) => {
    const [draft, setDraft] = useState('');
    return (
      <>
        <UnsavedChanges
          dirty={!!draft}
          description="Your artwork preview has not been applied."
        />
        <div>Composition review: {initialRole}</div>
        <input
          aria-label="Artwork draft"
          value={draft}
          onChange={(event) => setDraft(event.currentTarget.value)}
        />
      </>
    );
  },
}));

const titleEndpoint = 'http://localhost/api/titles/turok';
const written = writtenTitleFixture({
  id: 'turok',
  name: 'Turok',
  systemKey: 'n64',
});
const identity = unsatisfied('Identity:igdb', 'NeedsReview', 'MatchDecisionRequired', [
  'ReviewProvider',
  'ContinueWithoutProvider',
]);
const steamGridDbReview = unsatisfied('Identity:steamgriddb', 'NeedsReview', 'NoMatchFound', [
  'ReviewProvider',
  'ContinueWithoutProvider',
]);
const metadata = unsatisfied('Metadata:Description', 'NeedsReview', 'NotProvided', [
  'ReviewMetadata',
  'AcceptOmission',
]);
const backdrop = unsatisfied('Artwork:Backdrop', 'NeedsReview', 'ArtworkDecisionRequired', [
  'ReviewArtwork',
]);
function titleWith(
  rows: EnrichmentRequirement[] = [],
  overrides: Partial<EnrichmentAssessment> = {},
) {
  return assessmentFixture(withRequirements(...rows), {
    titleId: 'turok',
    titleName: 'Turok',
    platformName: 'Nintendo 64',
    ...overrides,
  });
}
const chrono = assessmentFixture(chronoTriggerRequirements, {
  titleId: 'turok',
});
const chronoSaved = assessmentFixture(
  chronoTriggerRequirements.map((row) =>
    row.key === metadata.key
      ? satisfied(row.key, 'Present', [
          'ReviewMetadata',
        ])
      : row,
  ),
  {
    titleId: 'turok',
    revision: 'rev-2',
  },
);

let state: EnrichmentAssessment;
let titleName: string;
let titleFails: boolean;
const assessmentReads = vi.fn();
const caches: QueryClient[] = [];
function Location() {
  const location = useLocation();
  return (
    <div>
      Location: {location.pathname}
      {location.search}
    </div>
  );
}

beforeEach(() => {
  permissions.canManageTitles = true;
  permissions.canConfigureIntegrations = true;
  client.setConfig({
    baseUrl: 'http://localhost',
  });
  state = titleWith([
    identity,
  ]);
  titleName = 'Turok';
  titleFails = false;
  assessmentReads.mockReset();
  server.use(
    http.get(`${titleEndpoint}/enrichment-assessment`, () => {
      assessmentReads();
      return HttpResponse.json(state);
    }),
    http.get(titleEndpoint, () =>
      titleFails
        ? new HttpResponse(null, {
            status: 503,
          })
        : HttpResponse.json(
            writtenTitleFixture({
              id: 'turok',
              revision: 'rev-1',
              systemKey: 'n64',
              name: titleName,
              enrichmentStatus: 'Completed',
              isTracked: true,
            }),
          ),
    ),
  );
});
afterEach(() => {
  for (const cache of caches) cache.clear();
  caches.length = 0;
  client.setConfig({
    baseUrl: '',
  });
});

const next = {
  onClick: vi.fn(),
  loading: false,
  disabled: false,
};
function renderWorkbench(
  entry = '/enrichment?title=turok',
  props: Partial<ComponentProps<typeof TitleWorkbench>> = {},
  sibling?: ReactNode,
) {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  caches.push(cache);
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
            path: '/enrichment',
            element: (
              <>
                <TitleWorkbench
                  titleId="turok"
                  frame="workspace"
                  density="compact"
                  next={next}
                  {...props}
                />
                {sibling}
                <Location />
              </>
            ),
          },
          {
            path: '/titles/:titleId',
            element: (
              <>
                <TitleWorkbench
                  titleId="turok"
                  frame="page"
                  density="comfortable"
                  {...props}
                />
                <Location />
              </>
            ),
          },
        ],
      },
    ],
    {
      initialEntries: [
        entry,
      ],
    },
  );
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return {
    cache,
    router,
  };
}
function OtherDraft() {
  const [draft, setDraft] = useState('');
  return (
    <>
      <UnsavedChanges
        dirty={!!draft}
        description="Another draft."
      />
      <input
        aria-label="Other draft"
        value={draft}
        onChange={(event) => setDraft(event.currentTarget.value)}
      />
    </>
  );
}
const rowButton = (row: RegExp, name: string) =>
  within(
    screen.getByRole('listitem', {
      name: row,
    }),
  ).getByRole('button', {
    name,
  });
const findRowButton = async (row: RegExp, name: string) =>
  within(
    await screen.findByRole('listitem', {
      name: row,
    }),
  ).getByRole('button', {
    name,
  });
const liveRegion = () => document.querySelector('[aria-live="polite"]');
const rowNames = () => screen.getAllByRole('listitem').map((row) => row.getAttribute('aria-label'));
const chips = () =>
  within(
    screen.getByRole('group', {
      name: 'Enrichment status',
    }),
  )
    .getAllByText(/./)
    .map((chip) => chip.textContent);
const refreshAssessment = (cache: QueryClient) =>
  act(async () => {
    await cache.invalidateQueries({
      queryKey: [
        'enrichment-workspace',
      ],
    });
  });

describe('identity header and checklist', () => {
  it('presents scenario 8.1 as the complete checklist led by the description', async () => {
    state = chrono;
    renderWorkbench('/enrichment?title=turok', {
      view: 'NeedsReview',
    });
    expect(
      await screen.findByRole('heading', {
        level: 2,
        name: 'Turok',
      }),
    ).toBeInTheDocument();
    expect(chips()).toEqual([
      'Needs review',
      'Blocked',
      'Processing',
    ]);
    expect(screen.getByText('Super Nintendo · Tracked')).toBeInTheDocument();
    expect(screen.getByText('4 outstanding')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Enrichment is running for this title. Results appear here when it finishes.',
      ),
    ).toBeInTheDocument();
    expect(rowNames()).toEqual([
      'IGDB: Matched',
      'SteamGridDB: SteamGridDB is enabled but has no credentials.',
      'Description: Description is missing after enrichment.',
      'Genre: Present',
      'Release date: Present',
      'Refresh: No refresh needed',
      'Poster: Present',
      'Backdrop: Saving your selected image.',
      'Banner: Present',
      'Logo: The artwork provider has no credentials.',
    ]);
    expect(
      within(
        screen.getByRole('region', {
          name: 'Identity',
        }),
      ).getByText('1 outstanding'),
    ).toBeInTheDocument();
    expect(
      within(
        screen.getByRole('region', {
          name: 'Metadata',
        }),
      ).getByText('1 outstanding'),
    ).toBeInTheDocument();
    expect(
      within(
        screen.getByRole('region', {
          name: 'Artwork',
        }),
      ).getByText('2 outstanding'),
    ).toBeInTheDocument();
    const description = screen.getByRole('listitem', {
      name: /^Description:/,
    });
    expect(description).toHaveAttribute('data-current', 'true');
    expect(
      within(description).getByRole('button', {
        name: 'Review description',
      }),
    ).toHaveAttribute('data-variant', 'filled');
    expect(
      within(description).getByRole('button', {
        name: 'Keep description empty',
      }),
    ).toHaveAttribute('data-variant', 'default');
    expect(
      screen.getAllByRole('link', {
        name: 'Provider settings',
      }),
    ).toHaveLength(2);
    expect(
      within(
        screen.getByRole('listitem', {
          name: /^Genre:/,
        }),
      ).getByRole('button', {
        name: 'Edit genre',
      }),
    ).toBeInTheDocument();
    // The current requirement selects its editor but never opens the field inspector on its own.
    expect(screen.getByText('Metadata editor: none')).toBeInTheDocument();
    expect(
      screen.getByRole('tab', {
        name: 'Metadata',
      }),
    ).toHaveAttribute('aria-selected', 'true');
    expect(
      screen.getByRole('button', {
        name: 'Next title',
      }),
    ).toHaveAttribute('data-variant', 'subtle');
  });

  it('presents scenario 8.2 as settled with Next title as the primary action', async () => {
    state = assessmentFixture(obscureGameBoyRequirements, {
      titleId: 'turok',
    });
    renderWorkbench('/enrichment?title=turok', {
      view: 'All',
    });
    expect(
      await screen.findByText('Nothing outstanding · 2 optional assets unavailable'),
    ).toBeInTheDocument();
    expect(chips()).toEqual([
      'Settled',
    ]);
    // Settled is explained once, on its chip, so "Nothing outstanding" is not repeated under the summary.
    const settled = within(
      screen.getByRole('group', {
        name: 'Enrichment status',
      }),
    )
      .getByText('Settled')
      .closest('[aria-describedby]');
    expect(settled).toHaveAccessibleDescription(
      'Nothing outstanding. Some values may be intentionally empty or unavailable.',
    );
    expect(
      screen.getAllByText(/^Nothing outstanding/, {
        selector: 'p',
      }),
    ).toHaveLength(1);
    expect(
      screen.getByRole('listitem', {
        name: 'Logo: Nothing usable found, text title shown',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('listitem', {
        name: 'Backdrop: Nothing usable found, details page shows no backdrop',
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(/outstanding$/, {
        selector: 'p',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Next title',
      }),
    ).toHaveAttribute('data-variant', 'filled');
  });

  it('never lets satisfied or operation rows drive review, showing processing as a banner', async () => {
    state = titleWith([
      unsatisfied('Operation:Processing', 'Processing', 'Working'),
    ]);
    renderWorkbench('/enrichment?title=turok&requirement=Identity%3Aigdb', {
      view: 'Processing',
    });
    expect(await screen.findByText('No checklist actions · Processing')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Enrichment is running for this title. Results appear here when it finishes.',
      ),
    ).toBeInTheDocument();
    expect(screen.getByText('Metadata editor: none')).toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: /^Review /,
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('listitem', {
        name: /^Processing:/,
      }),
    ).not.toBeInTheDocument();
    expect(document.querySelector('[data-current]')).toBeNull();
    expect(screen.queryByText(/intentionally empty or unavailable/)).not.toBeInTheDocument();
  });

  it('names the matched game when provider matches are already loaded', async () => {
    state = titleWith();
    const { cache } = renderWorkbench();
    cache.setQueryData(
      [
        'provider-matches',
        'turok',
      ],
      [
        {
          providerId: 'igdb',
          providerName: 'IGDB',
          isAvailable: true,
          capabilities: [
            'metadata',
          ],
          revision: 'r',
          state: 'Confirmed',
          externalId: '1',
          game: {
            id: '1',
            name: 'Turok',
            url: 'https://igdb.example/turok',
            year: 1997,
          },
          metadataNeedsRefresh: false,
        },
      ],
    );
    expect(
      await screen.findByRole('listitem', {
        name: 'IGDB: Matched to Turok (1997)',
      }),
    ).toBeInTheDocument();
  });

  it('keeps a title that leaves the view selected and says so until the curator navigates', async () => {
    state = titleWith([
      metadata,
    ]);
    const { cache } = renderWorkbench('/enrichment?title=turok', {
      view: 'NeedsReview',
    });
    await screen.findByRole('listitem', {
      name: 'Description: Description is missing after enrichment.',
    });
    expect(screen.queryByText('No longer in this view')).not.toBeInTheDocument();
    state = titleWith([], {
      revision: 'rev-2',
    });
    await refreshAssessment(cache);
    expect(await screen.findByText('No longer in this view')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', {
        level: 2,
        name: 'Turok',
      }),
    ).toBeInTheDocument();
  });

  it('does not claim a deep-linked title left a view it was never in', async () => {
    state = titleWith();
    renderWorkbench('/enrichment?title=turok', {
      view: 'NeedsReview',
    });
    await screen.findByText('Nothing outstanding');
    expect(screen.queryByText('No longer in this view')).not.toBeInTheDocument();
  });
});

describe('decisions', () => {
  it('rejects an omission decided on a revision the curator never saw and reloads the decisions', async () => {
    state = titleWith([
      metadata,
    ]);
    const accept = vi.fn(({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"rev-1"');
      return HttpResponse.json(
        {
          status: 412,
          errorCode: 'Titles.PreconditionFailed',
        },
        {
          status: 412,
        },
      );
    });
    server.use(http.put(`${titleEndpoint}/enrichment-omissions/Description`, accept));
    renderWorkbench();
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Keep description empty',
      }),
    );
    // Another curator changes the title before this one confirms; the decision still targets what was shown.
    state = titleWith(
      [
        metadata,
      ],
      {
        revision: 'rev-2',
      },
    );
    const reads = assessmentReads.mock.calls.length;
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Keep description empty?',
        }),
      ).getByRole('button', {
        name: 'Keep description empty',
      }),
    );
    expect(
      await screen.findByText(
        'This title changed while you were reviewing it. Refresh the decision before trying again.',
      ),
    ).toBeInTheDocument();
    expect(accept).toHaveBeenCalledOnce();
    await waitFor(() => expect(assessmentReads.mock.calls.length).toBeGreaterThan(reads));
  });

  it('accepts an intentional metadata omission only after confirmation, then focuses Next title', async () => {
    const omitted = titleWith(
      [
        satisfied('Metadata:Description', 'AcceptedOmission', [
          'ReviewMetadata',
        ]),
      ],
      {
        revision: 'rev-2',
        acceptedOmissions: [
          'Description',
        ],
      },
    );
    const accept = vi.fn(async ({ request }: { request: Request }) => {
      expect(await request.json()).toEqual({
        accepted: true,
      });
      expect(request.headers.get('If-Match')).toBe('"rev-1"');
      state = omitted;
      return HttpResponse.json(written);
    });
    state = titleWith([
      metadata,
    ]);
    server.use(http.put(`${titleEndpoint}/enrichment-omissions/Description`, accept));
    renderWorkbench();
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Keep description empty',
      }),
    );
    const dialog = await screen.findByRole('dialog', {
      name: 'Keep description empty?',
    });
    expect(
      within(dialog).getByText(
        'The consumer app shows no description for this title. You can revisit this later.',
      ),
    ).toBeInTheDocument();
    await userEvent.click(
      within(dialog).getByRole('button', {
        name: 'Cancel',
      }),
    );
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(accept).not.toHaveBeenCalled();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Keep description empty',
      }),
    );
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Keep description empty?',
        }),
      ).getByRole('button', {
        name: 'Keep description empty',
      }),
    );
    expect(
      await screen.findByText('Nothing outstanding · Description intentionally empty'),
    ).toBeInTheDocument();
    expect(accept).toHaveBeenCalledOnce();
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Next title',
        }),
      ).toHaveFocus(),
    );
    expect(
      screen.getByText(
        'Nothing outstanding. Some values may be intentionally empty or unavailable.',
      ),
    ).toBeInTheDocument();
  });

  it('revisits a durable accepted omission from its satisfied row', async () => {
    state = titleWith(
      [
        satisfied('Metadata:Description', 'AcceptedOmission', [
          'ReviewMetadata',
        ]),
      ],
      {
        acceptedOmissions: [
          'Description',
        ],
      },
    );
    const reverse = vi.fn(async ({ request }: { request: Request }) => {
      expect(await request.json()).toEqual({
        accepted: false,
      });
      expect(request.headers.get('If-Match')).toBe('"rev-1"');
      state = titleWith([
        metadata,
      ]);
      return HttpResponse.json(written);
    });
    server.use(http.put(`${titleEndpoint}/enrichment-omissions/Description`, reverse));
    renderWorkbench();
    const row = await screen.findByRole('listitem', {
      name: 'Description: Intentionally empty',
    });
    expect(
      within(row).getByRole('button', {
        name: 'Edit description',
      }),
    ).toBeInTheDocument();
    await userEvent.click(
      within(row).getByRole('button', {
        name: 'Revisit description',
      }),
    );
    await waitFor(() => expect(reverse).toHaveBeenCalledOnce());
  });

  it('continues without a provider only after confirmation, under the match revision the title shows', async () => {
    const unlink = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"shown-rev"');
      state = titleWith([
        satisfied('Identity:igdb', 'ProviderSuppressed', [
          'ReviewProvider',
        ]),
      ]);
      return HttpResponse.json({
        providerId: 'igdb',
        providerName: 'IGDB',
        isAvailable: true,
        capabilities: [
          'metadata',
        ],
        revision: 'unlinked-rev',
        state: 'Unlinked',
        metadataNeedsRefresh: false,
      });
    });
    server.use(
      http.get('http://localhost/api/titles/turok/provider-matches', () =>
        HttpResponse.json([
          {
            providerId: 'igdb',
            providerName: 'IGDB',
            isAvailable: true,
            capabilities: [
              'metadata',
            ],
            revision: 'match-rev',
            state: 'NeedsReview',
            metadataNeedsRefresh: false,
          },
        ]),
      ),
      http.delete('http://localhost/api/titles/turok/provider-matches/igdb', unlink),
    );
    const { cache } = renderWorkbench();
    cache.setQueryData(
      [
        'provider-matches',
        'turok',
      ],
      [
        {
          providerId: 'igdb',
          providerName: 'IGDB',
          isAvailable: true,
          capabilities: [
            'metadata',
          ],
          revision: 'shown-rev',
          state: 'NeedsReview',
          metadataNeedsRefresh: false,
        },
      ],
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Continue without IGDB',
      }),
    );
    const dialog = await screen.findByRole('dialog', {
      name: 'Continue without IGDB?',
    });
    expect(
      within(dialog).getByText(
        'Automatic matching with IGDB stops for this title. You can find a match later.',
      ),
    ).toBeInTheDocument();
    await userEvent.click(
      within(dialog).getByRole('button', {
        name: 'Cancel',
      }),
    );
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(unlink).not.toHaveBeenCalled();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Continue without IGDB',
      }),
    );
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Continue without IGDB?',
        }),
      ).getByRole('button', {
        name: 'Continue without IGDB',
      }),
    );
    expect(
      await screen.findByText('Nothing outstanding · Continuing without IGDB'),
    ).toBeInTheDocument();
    expect(unlink).toHaveBeenCalledOnce();
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Next title',
        }),
      ).toHaveFocus(),
    );
    await userEvent.click(
      within(
        screen.getByRole('listitem', {
          name: 'IGDB: Continuing without IGDB',
        }),
      ).getByRole('button', {
        name: 'Find IGDB match',
      }),
    );
    expect(await screen.findByText('Target provider: igdb')).toBeInTheDocument();
  });

  it('leaves an artwork role empty only after confirmation, under the role revision the title shows, and can return it to automatic', async () => {
    const nothingUsable = unsatisfied('Artwork:Hero', 'NeedsReview', 'NothingUsable', [
      'ReviewArtwork',
      'LeaveEmpty',
    ]);
    const leave = vi.fn(async ({ request }: { request: Request }) => {
      expect(request.headers.get('If-Match')).toBe('"shown-rev"');
      expect(await request.json()).toEqual({
        mode: 'Empty',
      });
      state = titleWith([
        satisfied('Artwork:Hero', 'LeftEmpty', [
          'ReviewArtwork',
        ]),
      ]);
      return HttpResponse.json({
        role: 'Hero',
        mode: 'Empty',
        revision: 'hero-rev-2',
        pinnedAssetId: null,
        pendingJobId: null,
      });
    });
    const automatic = vi.fn((request: Request) => {
      // The leave answered the role with its new revision, which the next write sends.
      expect(request.headers.get('If-Match')).toBe('"hero-rev-2"');
      state = titleWith([
        nothingUsable,
      ]);
      return HttpResponse.json({
        role: 'Hero',
        mode: 'Automatic',
        revision: 'hero-rev-3',
        pinnedAssetId: null,
        pendingJobId: null,
      });
    });
    state = titleWith([
      nothingUsable,
    ]);
    server.use(
      http.get('http://localhost/api/titles/turok/artwork', () =>
        HttpResponse.json({
          revision: 'family-rev',
          roles: [
            {
              role: 'Hero',
              mode: leave.mock.calls.length > 0 ? 'Empty' : 'Automatic',
              // The role as the title shows it: the decisions read it when they mount.
              revision: leave.mock.calls.length > 0 ? 'hero-rev-2' : 'shown-rev',
              pinnedAssetId: null,
              pendingJobId: null,
            },
          ],
        }),
      ),
      http.put('http://localhost/api/titles/turok/artwork/Hero', async ({ request }) => {
        const { mode } = (await request.clone().json()) as {
          mode: string;
        };
        return mode === 'Empty'
          ? leave({
              request,
            })
          : automatic(request);
      }),
    );
    renderWorkbench();
    expect(await screen.findByText('Composition review: Hero')).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Leave banner empty',
      }),
    );
    const dialog = await screen.findByRole('dialog', {
      name: 'Leave banner empty?',
    });
    expect(
      within(dialog).getByText(
        'The consumer app shows no banner for this title and automatic runs will not fill it. Use Return to automatic to undo.',
      ),
    ).toBeInTheDocument();
    await userEvent.click(
      within(dialog).getByRole('button', {
        name: 'Cancel',
      }),
    );
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(leave).not.toHaveBeenCalled();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Leave banner empty',
      }),
    );
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Leave banner empty?',
        }),
      ).getByRole('button', {
        name: 'Leave banner empty',
      }),
    );
    expect(await screen.findByText('Nothing outstanding · Banner left empty')).toBeInTheDocument();
    expect(leave).toHaveBeenCalledOnce();
    await waitFor(() =>
      expect(
        screen.getByRole('button', {
          name: 'Next title',
        }),
      ).toHaveFocus(),
    );
    await userEvent.click(
      within(
        screen.getByRole('listitem', {
          name: 'Banner: Left empty by you',
        }),
      ).getByRole('button', {
        name: 'Return banner to automatic',
      }),
    );
    await waitFor(() => expect(automatic).toHaveBeenCalledOnce());
    expect(
      await screen.findByRole('listitem', {
        name: /^Banner: No usable banner was found/,
      }),
    ).toBeInTheDocument();
  });

  it('reads the role revisions when the decisions mount, so a leave-empty reads nothing when it opens or confirms', async () => {
    const requests: string[] = [];
    state = titleWith([
      unsatisfied('Artwork:Hero', 'NeedsReview', 'NothingUsable', [
        'ReviewArtwork',
        'LeaveEmpty',
      ]),
    ]);
    server.use(
      http.get('http://localhost/api/titles/turok/artwork', () => {
        requests.push('read');
        return HttpResponse.json({
          revision: 'family-rev',
          roles: [
            {
              role: 'Hero',
              mode: 'Automatic',
              revision: 'hero-rev',
              pinnedAssetId: null,
              pendingJobId: null,
            },
          ],
        });
      }),
      http.put('http://localhost/api/titles/turok/artwork/Hero', ({ request }) => {
        requests.push(`write ${request.headers.get('If-Match')}`);
        return HttpResponse.json({
          role: 'Hero',
          mode: 'Empty',
          revision: 'hero-rev-2',
          pinnedAssetId: null,
          pendingJobId: null,
        });
      }),
    );
    renderWorkbench();
    const decide = await screen.findByRole('button', {
      name: 'Leave banner empty',
    });
    await waitFor(() =>
      expect(requests).toEqual([
        'read',
      ]),
    );
    await userEvent.click(decide);
    const dialog = await screen.findByRole('dialog', {
      name: 'Leave banner empty?',
    });
    await userEvent.click(
      within(dialog).getByRole('button', {
        name: 'Leave banner empty',
      }),
    );
    await waitFor(() =>
      expect(requests.slice(0, 2)).toEqual([
        'read',
        'write "hero-rev"',
      ]),
    );
  });

  it('returns a role to automatic under the revision read when the decisions mounted, reading nothing at click time', async () => {
    const requests: string[] = [];
    state = titleWith([
      satisfied('Artwork:Hero', 'LeftEmpty', [
        'ReviewArtwork',
      ]),
    ]);
    server.use(
      http.get('http://localhost/api/titles/turok/artwork', () => {
        requests.push('read');
        return HttpResponse.json({
          revision: 'family-rev',
          roles: [
            {
              role: 'Hero',
              mode: 'Empty',
              revision: 'hero-rev',
              pinnedAssetId: null,
              pendingJobId: null,
            },
          ],
        });
      }),
      http.put('http://localhost/api/titles/turok/artwork/Hero', ({ request }) => {
        requests.push(`write ${request.headers.get('If-Match')}`);
        return HttpResponse.json({
          role: 'Hero',
          mode: 'Automatic',
          revision: 'hero-rev-2',
          pinnedAssetId: null,
          pendingJobId: null,
        });
      }),
    );
    renderWorkbench();
    const revert = await screen.findByRole('button', {
      name: 'Return banner to automatic',
    });
    await waitFor(() =>
      expect(requests).toEqual([
        'read',
      ]),
    );

    await userEvent.click(revert);

    await waitFor(() =>
      expect(requests.slice(0, 2)).toEqual([
        'read',
        'write "hero-rev"',
      ]),
    );
  });

  it('reads the match revision a continue-without sends when its confirmation opens, never when the curator confirms', async () => {
    const requests: string[] = [];
    const match = {
      providerId: 'igdb',
      providerName: 'IGDB',
      isAvailable: true,
      capabilities: [
        'metadata',
      ],
      revision: 'match-rev',
      state: 'NeedsReview',
      metadataNeedsRefresh: false,
    };
    server.use(
      http.get('http://localhost/api/titles/turok/provider-matches', () => {
        requests.push('read');
        return HttpResponse.json([
          match,
        ]);
      }),
      http.delete('http://localhost/api/titles/turok/provider-matches/igdb', ({ request }) => {
        requests.push(`write ${request.headers.get('If-Match')}`);
        return HttpResponse.json({
          ...match,
          revision: 'unlinked-rev',
          state: 'Unlinked',
        });
      }),
    );
    renderWorkbench();
    const decide = await screen.findByRole('button', {
      name: 'Continue without IGDB',
    });
    expect(requests).toEqual([]);
    await userEvent.click(decide);
    const dialog = await screen.findByRole('dialog', {
      name: 'Continue without IGDB?',
    });
    await waitFor(() =>
      expect(requests).toEqual([
        'read',
      ]),
    );
    await userEvent.click(
      within(dialog).getByRole('button', {
        name: 'Continue without IGDB',
      }),
    );
    await waitFor(() =>
      expect(requests.slice(0, 2)).toEqual([
        'read',
        'write "match-rev"',
      ]),
    );
  });

  it.each([
    [
      true,
      'ProviderUnconfigured' as const,
      null,
    ],
    [
      false,
      'ProviderUnconfigured' as const,
      'An administrator can add SteamGridDB credentials',
    ],
    [
      false,
      'ProviderDisabled' as const,
      'An administrator can enable SteamGridDB',
    ],
  ])('offers provider settings only to Admins (Admin: %s, %s)', async (admin, cause, hint) => {
    permissions.canConfigureIntegrations = admin;
    state = titleWith([
      unsatisfied('Identity:steamgriddb', 'Blocked', cause, [
        'ConfigureProvider',
      ]),
    ]);
    renderWorkbench('/enrichment?title=turok', {
      view: 'Blocked',
    });
    await screen.findByText('Target provider: none');
    if (hint) {
      expect(screen.getByText(hint)).toBeInTheDocument();
      expect(
        screen.queryByRole('link', {
          name: 'Provider settings',
        }),
      ).not.toBeInTheDocument();
    } else
      expect(
        screen.getByRole('link', {
          name: 'Provider settings',
        }),
      ).toHaveAttribute('href', '/integrations');
  });

  it('opens the single-title launcher for Enrich and Retry', async () => {
    const bodies: unknown[] = [];
    server.use(
      http.post('http://localhost/api/artwork/completion/preview', async ({ request }) => {
        bodies.push(await request.json());
        return HttpResponse.json({
          titles: 1,
          roles: [],
        });
      }),
    );
    state = titleWith([
      unsatisfied('Artwork:Poster', 'Blocked', 'AcquisitionFailed', [
        'Retry',
      ]),
    ]);
    renderWorkbench('/enrichment?title=turok', {
      view: 'Blocked',
    });
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Retry enrichment',
      }),
    );
    expect(
      await screen.findByRole('dialog', {
        name: 'Enrich Turok',
      }),
    ).toBeInTheDocument();
    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({
      scope: 'Selected',
      mode: 'Enrich',
      titleIds: [
        'turok',
      ],
    });
  });
});

describe('editor targeting', () => {
  it('opens the provider belonging to an explicitly chosen requirement', async () => {
    state = titleWith([
      identity,
      steamGridDbReview,
    ]);
    renderWorkbench();
    expect(await screen.findByText('Target provider: igdb')).toBeInTheDocument();
    await userEvent.click(
      within(
        screen.getByRole('listitem', {
          name: /^SteamGridDB:/,
        }),
      ).getByRole('button', {
        name: 'Review SteamGridDB match',
      }),
    );
    expect(await screen.findByText('Target provider: steamgriddb')).toBeInTheDocument();
    expect(screen.getByText(/^Location:/).textContent).toContain(
      'editor=match&requirement=Identity%3Asteamgriddb',
    );
    expect(
      within(
        screen.getByRole('region', {
          name: 'Identity',
        }),
      ).getByText('2 outstanding'),
    ).toBeInTheDocument();
  });

  it('targets the provider behind a metadata row, never an identity reviewed earlier', async () => {
    state = titleWith([
      satisfied('Identity:igdb', 'ProviderSuppressed', [
        'ReviewProvider',
      ]),
      steamGridDbReview,
      unsatisfied('Metadata:Genre', 'NeedsReview', 'ProviderSuppressed', [
        'ReviewMetadata',
        'AcceptOmission',
        'ReviewProvider',
      ]),
    ]);
    renderWorkbench();
    expect(await screen.findByText('Target provider: steamgriddb')).toBeInTheDocument();
    await userEvent.click(
      within(
        screen.getByRole('listitem', {
          name: /^Genre:/,
        }),
      ).getByRole('button', {
        name: 'Review IGDB match',
      }),
    );
    expect(await screen.findByText('Target provider: igdb')).toBeInTheDocument();
    expect(screen.getByText(/^Location:/).textContent).toBe(
      'Location: /enrichment?title=turok&editor=match&requirement=Metadata%3AGenre',
    );
  });

  it('keeps the provider target stable on assessment refresh but follows explicit requirement navigation and Back', async () => {
    state = titleWith([
      identity,
      steamGridDbReview,
    ]);
    const { cache, router } = renderWorkbench();
    expect(await screen.findByText('Target provider: igdb')).toBeInTheDocument();
    state = titleWith(
      [
        satisfied('Identity:igdb', 'Trusted', [
          'ReviewProvider',
        ]),
        steamGridDbReview,
      ],
      {
        revision: 'rev-2',
      },
    );
    await refreshAssessment(cache);
    await screen.findByRole('listitem', {
      name: 'IGDB: Matched',
    });
    expect(screen.getByText('Target provider: igdb')).toBeInTheDocument();
    await act(async () => {
      await router.navigate(
        '/enrichment?title=turok&requirement=Identity%3Asteamgriddb&editor=match',
      );
    });
    expect(screen.getByText('Target provider: steamgriddb')).toBeInTheDocument();
    await act(async () => {
      await router.navigate(-1);
    });
    expect(screen.getByText('Target provider: igdb')).toBeInTheDocument();
  });

  it('preserves an artwork draft when a refreshed assessment suggests a different next role', async () => {
    state = titleWith([
      backdrop,
    ]);
    const { cache } = renderWorkbench();
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Artwork draft',
      }),
      'Keep my crop',
    );
    state = titleWith(
      [
        satisfied('Artwork:Backdrop', 'Present', [
          'ReviewArtwork',
        ]),
        unsatisfied('Artwork:Poster', 'NeedsReview', 'ArtworkDecisionRequired', [
          'ReviewArtwork',
        ]),
      ],
      {
        revision: 'rev-2',
      },
    );
    await refreshAssessment(cache);
    await screen.findByRole('listitem', {
      name: 'Backdrop: Present',
    });
    expect(
      screen.getByRole('textbox', {
        name: 'Artwork draft',
      }),
    ).toHaveValue('Keep my crop');
    expect(screen.getByText('Composition review: Backdrop')).toBeInTheDocument();
  });

  it('keeps the cached title and artwork draft mounted after a background title refresh fails', async () => {
    state = titleWith([
      backdrop,
    ]);
    const { cache } = renderWorkbench();
    const draft = await screen.findByRole('textbox', {
      name: 'Artwork draft',
    });
    await userEvent.type(draft, 'Keep my crop');
    titleFails = true;
    await act(async () => {
      await cache.invalidateQueries({
        queryKey: [
          'titleDetail',
          'turok',
        ],
      });
    });
    expect(await screen.findByText('Could not refresh this title')).toBeInTheDocument();
    expect(
      screen.getByRole('textbox', {
        name: 'Artwork draft',
      }),
    ).toBe(draft);
    expect(draft).toHaveValue('Keep my crop');
    titleFails = false;
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Retry title refresh',
      }),
    );
    await waitFor(() =>
      expect(screen.queryByText('Could not refresh this title')).not.toBeInTheDocument(),
    );
    expect(
      screen.getByRole('textbox', {
        name: 'Artwork draft',
      }),
    ).toBe(draft);
    expect(draft).toHaveValue('Keep my crop');
  });

  it('guards editor tab changes with the shared discard dialog and keeps the draft when leaving is canceled', async () => {
    state = titleWith([
      metadata,
    ]);
    renderWorkbench();
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Description draft',
      }),
      'A deliberate edit',
    );
    await userEvent.click(
      screen.getByRole('tab', {
        name: 'Artwork',
      }),
    );
    expect(
      await screen.findByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(
      screen.getByRole('textbox', {
        name: 'Description draft',
      }),
    ).toHaveValue('A deliberate edit');
    expect(
      screen.getByRole('tab', {
        name: 'Metadata',
      }),
    ).toHaveAttribute('aria-selected', 'true');
    await userEvent.click(
      screen.getByRole('tab', {
        name: 'Artwork',
      }),
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Discard',
      }),
    );
    expect(await screen.findByText(/^Composition review/)).toBeInTheDocument();
  });

  it('names the editors Match, Metadata and Artwork in that order', async () => {
    renderWorkbench();
    const editors = await screen.findByRole('tablist', {
      name: 'Title enrichment editors',
    });
    expect(
      within(editors)
        .getAllByRole('tab')
        .map((tab) => tab.textContent),
    ).toEqual([
      'Match',
      'Metadata',
      'Artwork',
    ]);
  });
});

describe('focus and population', () => {
  it('focuses the heading when the title detail arrives after an already loaded assessment', async () => {
    let releaseTitle!: () => void;
    const titleArrives = new Promise<void>((resolve) => {
      releaseTitle = resolve;
    });
    server.use(
      http.get(titleEndpoint, async () => {
        await titleArrives;
        return HttpResponse.json(
          writtenTitleFixture({
            id: 'turok',
            revision: 'rev-1',
            systemKey: 'n64',
            name: 'Turok',
            enrichmentStatus: 'Completed',
            isTracked: true,
          }),
        );
      }),
    );
    const { cache } = renderWorkbench('/enrichment?title=turok', {
      focusHeading: true,
    });
    await waitFor(() =>
      expect(
        cache.getQueryState([
          'enrichment-workspace',
          'title',
          'turok',
        ])?.status,
      ).toBe('success'),
    );
    releaseTitle();
    await waitFor(() =>
      expect(
        screen.getByRole('heading', {
          level: 2,
          name: 'Turok',
        }),
      ).toHaveFocus(),
    );
  });

  it('focuses the title heading once it renders when the curator chose the title', async () => {
    renderWorkbench('/enrichment?title=turok', {
      focusHeading: true,
    });
    await waitFor(() =>
      expect(
        screen.getByRole('heading', {
          level: 2,
          name: 'Turok',
        }),
      ).toHaveFocus(),
    );
  });

  it('focuses the next open row after the 8.1 description is saved, drops Needs review and reports 3 outstanding', async () => {
    state = chrono;
    renderWorkbench('/enrichment?title=turok', {
      view: 'NeedsReview',
    });
    await userEvent.click(await findRowButton(/^Description:/, 'Review description'));
    await screen.findByText('Metadata editor: Description');
    state = chronoSaved;
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Save metadata',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('listitem', {
          name: 'SteamGridDB: SteamGridDB is enabled but has no credentials.',
        }),
      ).toHaveFocus(),
    );
    expect(screen.getByText('3 outstanding')).toBeInTheDocument();
    expect(chips()).toEqual([
      'Blocked',
      'Processing',
    ]);
    expect(screen.getByText('No longer in this view')).toBeInTheDocument();
  });

  it('leaves focus alone after a resolution while another draft is open', async () => {
    state = chrono;
    renderWorkbench(
      '/enrichment?title=turok',
      {
        view: 'NeedsReview',
      },
      <OtherDraft />,
    );
    await userEvent.click(await findRowButton(/^Description:/, 'Review description'));
    await screen.findByText('Metadata editor: Description');
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Other draft',
      }),
      'Unsaved',
    );
    const save = screen.getByRole('button', {
      name: 'Save metadata',
    });
    save.focus();
    state = chronoSaved;
    await userEvent.click(save);
    expect(await screen.findByText('3 outstanding')).toBeInTheDocument();
    expect(
      screen.getByRole('listitem', {
        name: /^SteamGridDB:/,
      }),
    ).not.toHaveFocus();
    expect(save).toHaveFocus();
  });

  it('focuses the editor’s first control when opened from a row and returns to the row when the editor closes', async () => {
    state = titleWith([
      identity,
      metadata,
    ]);
    renderWorkbench('/enrichment?title=turok&requirement=Metadata%3ADescription');
    await screen.findByText('Metadata editor: Description');
    const igdb = screen.getByRole('listitem', {
      name: /^IGDB:/,
    });
    await userEvent.click(
      within(igdb).getByRole('button', {
        name: 'Review IGDB match',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('textbox', {
          name: 'Provider search',
        }),
      ).toHaveFocus(),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Cancel match',
      }),
    );
    expect(igdb).toHaveFocus();
  });

  it('announces rows added after a user-initiated match confirmation and focuses the first open one', async () => {
    const waiting = titleWith([
      identity,
    ]).requirements.filter(
      (row) =>
        ![
          'Description',
          'Genre',
          'ReleaseDate',
        ].includes(row.subject),
    );
    state = assessmentFixture(waiting, {
      titleId: 'turok',
      titleName: 'Turok',
    });
    renderWorkbench('/enrichment?title=turok', {
      view: 'NeedsReview',
    });
    await screen.findByText('Target provider: igdb');
    expect(
      screen.queryByRole('region', {
        name: 'Metadata',
      }),
    ).not.toHaveTextContent('Description');
    await userEvent.click(rowButton(/^IGDB:/, 'Review IGDB match'));
    state = titleWith(
      [
        satisfied('Identity:igdb', 'Trusted', [
          'ReviewProvider',
        ]),
        metadata,
      ],
      {
        revision: 'rev-2',
      },
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Confirm provider match',
      }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('listitem', {
          name: 'Description: Description is missing after enrichment.',
        }),
      ).toHaveFocus(),
    );
    expect(await screen.findByText('3 requirements added')).toBeInTheDocument();
  });

  it('announces rows added by a background refresh without moving focus', async () => {
    const waiting = titleWith([
      identity,
    ]).requirements.filter(
      (row) =>
        ![
          'Description',
          'Genre',
          'ReleaseDate',
        ].includes(row.subject),
    );
    state = assessmentFixture(waiting, {
      titleId: 'turok',
      titleName: 'Turok',
    });
    const { cache } = renderWorkbench();
    await screen.findByText('Target provider: igdb');
    const search = screen.getByRole('textbox', {
      name: 'Provider search',
    });
    search.focus();
    state = titleWith(
      [
        satisfied('Identity:igdb', 'Trusted', [
          'ReviewProvider',
        ]),
        metadata,
      ],
      {
        revision: 'rev-2',
      },
    );
    await refreshAssessment(cache);
    expect(await screen.findByText('3 requirements added')).toBeInTheDocument();
    expect(search).toHaveFocus();
  });
});

describe('title page frame', () => {
  it('keeps the checklist within Enrichment and collapses completed requirements', async () => {
    state = assessmentFixture(obscureGameBoyRequirements, {
      titleId: 'turok',
    });
    renderWorkbench('/titles/turok?editor=releases', {
      extraSections: [
        {
          value: 'releases',
          label: 'Releases',
          content: <div>Releases panel</div>,
        },
        {
          value: 'ratings',
          label: 'Ratings',
          content: <div>Ratings panel</div>,
        },
      ],
    });
    expect(await screen.findByText('Releases panel')).toBeInTheDocument();
    expect(
      screen.queryByText('Nothing outstanding · 2 optional assets unavailable'),
    ).not.toBeInTheDocument();
    const sections = screen.getByRole('tablist', {
      name: 'Title sections',
    });
    expect(
      within(sections)
        .getAllByRole('tab')
        .map((tab) => tab.textContent),
    ).toEqual([
      'Metadata',
      'Artwork',
      'Releases',
      'Ratings',
      'Enrichment',
    ]);
    await userEvent.click(
      within(sections).getByRole('tab', {
        name: 'Enrichment',
      }),
    );
    expect(
      await screen.findByText('Nothing outstanding · 2 optional assets unavailable'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('link', {
        name: 'Open enrichment workspace',
      }),
    ).toHaveAttribute('href', '/enrichment?title=turok');
    expect(
      screen.queryByRole('heading', {
        name: 'Identity',
      }),
    ).not.toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: /Completed/,
      }),
    );
    expect(
      await screen.findByRole('heading', {
        name: 'Identity',
      }),
    ).toBeVisible();
    await userEvent.click(
      within(sections).getByRole('tab', {
        name: 'Metadata',
      }),
    );
    expect(await screen.findByText('Metadata editor: none')).toBeInTheDocument();
    expect(screen.getByText(/^Location:/).textContent).toBe(
      'Location: /titles/turok?editor=metadata',
    );
    expect(screen.queryByText('Presentation preview')).not.toBeInTheDocument();
  });

  it('starts on Metadata without opening a requirement editor', async () => {
    state = titleWith([
      metadata,
    ]);
    renderWorkbench('/titles/turok');
    expect(await screen.findByText('Metadata editor: none')).toBeInTheDocument();
    expect(
      screen.getByRole('tab', {
        name: 'Metadata',
      }),
    ).toHaveAttribute('aria-selected', 'true');
  });

  it('shows other roles the editors without the Manager-only assessment or Match', async () => {
    permissions.canManageTitles = false;
    renderWorkbench('/titles/turok?editor=match');
    expect(await screen.findByText('Metadata editor: none')).toBeInTheDocument();
    expect(
      screen.queryByRole('tab', {
        name: 'Match',
      }),
    ).not.toBeInTheDocument();
    expect(screen.queryByRole('list')).not.toBeInTheDocument();
    expect(assessmentReads).not.toHaveBeenCalled();
  });
});

describe('container layout and density', () => {
  it.each([
    [
      '/enrichment?title=turok',
      'Title enrichment editors',
      'compact',
    ],
    [
      '/titles/turok',
      'Title sections',
      'comfortable',
    ],
  ])('marks the host density on the workbench container (%s)', async (entry, editors, density) => {
    renderWorkbench(entry);
    expect(
      (
        await screen.findByRole('tablist', {
          name: editors,
        })
      ).closest('[data-density]'),
    ).toHaveAttribute('data-density', density);
  });

  it('tiers checklist spacing through custom properties, with a narrow workbench forced compact', () => {
    const styles = readFileSync(`${import.meta.dirname}/TitleWorkbench.module.css`, 'utf8');
    expect(styles).not.toMatch(/@media/);
    expect(styles).toMatch(/\.workbench \{\s*container: title-workbench \/ inline-size;/);
    const compact = styles.slice(styles.indexOf(".workbench[data-density='compact'] {"));
    const forced = styles.slice(styles.indexOf('@container title-workbench (max-width: 36em) {'));
    for (const tier of [
      compact,
      forced,
    ]) {
      expect(tier).toMatch(
        /--checklist-row-padding: 6px var\(--mantine-spacing-xs\);[^}]*--checklist-satisfied-row-padding: 2px var\(--mantine-spacing-xs\);/,
      );
    }
    expect(styles).toMatch(/\.row \{ padding: var\(--checklist-row-padding\);/);
    expect(styles).toContain(
      '.row[data-satisfied] { padding: var(--checklist-satisfied-row-padding); }',
    );
  });

  it('lays out the header and checklist for their own widths, never the viewport', () => {
    const styles = readFileSync(`${import.meta.dirname}/TitleWorkbench.module.css`, 'utf8');
    expect(styles).toMatch(/\.header \{ container: workbench-header \/ inline-size;/);
    expect(styles).toContain(
      '@container workbench-header (max-width: 30em) { .headerBar { flex-direction: column; } }',
    );
    expect(styles).toMatch(/\.requirements \{ container: requirement-checklist \/ inline-size;/);
    const block = (start: string) => {
      const from = styles.indexOf(start);
      const end = styles.indexOf('@container', from + 1);
      return styles.slice(from, end < 0 ? undefined : end);
    };
    const wide = block('@container requirement-checklist (min-width: 900px) {');
    expect(wide).toContain('.groups { grid-template-columns: repeat(2, minmax(0, 1fr)); }');
    expect(wide).toContain(".group[data-kind='Artwork'] { grid-column: 2; grid-row: 1 / span 2; }");
    expect(block('@container requirement-checklist (min-width: 1200px) {')).toContain(
      '.groups { grid-template-columns: repeat(3, minmax(0, 1fr)); }',
    );
  });

  it('keeps every group in reading order with its kind for the column layout', async () => {
    state = chrono;
    renderWorkbench('/enrichment?title=turok', {
      view: 'NeedsReview',
    });
    await screen.findByRole('listitem', {
      name: /^Description:/,
    });
    expect(screen.getAllByRole('region').map((group) => group.getAttribute('data-kind'))).toEqual([
      'Identity',
      'Metadata',
      'Artwork',
    ]);
    expect(
      screen.getByRole('region', {
        name: 'Artwork',
      }),
    ).toHaveAttribute('data-kind', 'Artwork');
    expect(rowNames()).toHaveLength(10);
  });

  it('renders satisfied rows compact, never truncating their copy, with small actions that stay keyboard reachable', async () => {
    state = chrono;
    renderWorkbench('/enrichment?title=turok', {
      view: 'NeedsReview',
    });
    const genre = await screen.findByRole('listitem', {
      name: 'Genre: Present',
    });
    expect(genre).toHaveAttribute('data-satisfied', 'true');
    expect(within(genre).getByText('Present').closest('[data-truncate]')).toBeNull();
    const styles = readFileSync(`${import.meta.dirname}/TitleWorkbench.module.css`, 'utf8');
    expect(styles).not.toMatch(/text-overflow|white-space: nowrap/);
    expect(styles).toContain(
      '.row[data-satisfied] .rowLine { flex-wrap: nowrap; align-items: flex-start; }',
    );
    const edit = within(genre).getByRole('button', {
      name: 'Edit genre',
    });
    expect(edit).toHaveAttribute('data-size', 'compact-xs');
    expect(edit).toHaveAttribute('data-variant', 'subtle');
    expect(edit).not.toHaveAttribute('tabindex', '-1');
    const description = screen.getByRole('listitem', {
      name: /^Description:/,
    });
    expect(description).not.toHaveAttribute('data-satisfied');
    expect(
      within(description)
        .getByText('Description is missing after enrichment.')
        .closest('[data-truncate]'),
    ).toBeNull();
    expect(
      within(description).getByRole('button', {
        name: 'Review description',
      }),
    ).toHaveAttribute('data-size', 'compact-sm');
    act(() => edit.focus());
    await userEvent.keyboard('{Enter}');
    expect(await screen.findByText('Metadata editor: Genre')).toBeInTheDocument();
  });

  it('keeps reversal actions on satisfied rows as small reachable buttons', async () => {
    state = titleWith(
      [
        satisfied('Metadata:Description', 'AcceptedOmission', [
          'ReviewMetadata',
        ]),
        satisfied('Artwork:Hero', 'LeftEmpty', [
          'ReviewArtwork',
        ]),
      ],
      {
        acceptedOmissions: [
          'Description',
        ],
      },
    );
    renderWorkbench();
    const description = await screen.findByRole('listitem', {
      name: 'Description: Intentionally empty',
    });
    const banner = screen.getByRole('listitem', {
      name: 'Banner: Left empty by you',
    });
    for (const button of [
      within(description).getByRole('button', {
        name: 'Revisit description',
      }),
      within(banner).getByRole('button', {
        name: 'Return banner to automatic',
      }),
    ]) {
      expect(button).toHaveAttribute('data-size', 'compact-xs');
      expect(button).not.toHaveAttribute('tabindex', '-1');
    }
  });
});

describe('keyboard review handle', () => {
  it('reviews the focused row, else the current requirement, and does nothing for a row without a review action', async () => {
    state = chrono;
    const handle = createRef<TitleWorkbenchHandle>();
    const { router } = renderWorkbench('/enrichment?title=turok', {
      view: 'NeedsReview',
      ref: handle,
    });
    const steamGridDb = await screen.findByRole('listitem', {
      name: /^SteamGridDB:/,
    });
    act(() => steamGridDb.focus());
    act(() => handle.current?.reviewRequirement());
    expect(router.state.location.search).toBe('?title=turok');
    act(() =>
      screen
        .getByRole('heading', {
          level: 2,
          name: 'Turok',
        })
        .focus(),
    );
    act(() => handle.current?.reviewRequirement());
    await waitFor(() =>
      expect(router.state.location.search).toBe(
        '?title=turok&editor=metadata&requirement=Metadata%3ADescription',
      ),
    );
    expect(await screen.findByText('Metadata editor: Description')).toBeInTheDocument();
    // Like a click on its button, the review starts from the row, so closing the editor returns there.
    expect(
      screen.getByRole('listitem', {
        name: /^Description:/,
      }),
    ).toHaveFocus();
    act(() =>
      screen
        .getByRole('listitem', {
          name: /^Poster:/,
        })
        .focus(),
    );
    act(() => handle.current?.reviewRequirement());
    await waitFor(() =>
      expect(router.state.location.search).toBe(
        '?title=turok&editor=artwork&requirement=Artwork%3APoster&role=Poster',
      ),
    );
    expect(await screen.findByText('Composition review: Poster')).toBeInTheDocument();
  });

  it('goes through the discard dialog like the review button when a draft is open', async () => {
    state = titleWith([
      identity,
      metadata,
    ]);
    const handle = createRef<TitleWorkbenchHandle>();
    const { router } = renderWorkbench(
      '/enrichment?title=turok&requirement=Metadata%3ADescription',
      {
        ref: handle,
      },
    );
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Description draft',
      }),
      'Deliberate',
    );
    act(() =>
      screen
        .getByRole('listitem', {
          name: /^IGDB:/,
        })
        .focus(),
    );
    act(() => handle.current?.reviewRequirement());
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard unsaved changes?',
        }),
      ).getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(router.state.location.search).toBe('?title=turok&requirement=Metadata%3ADescription');
    expect(
      screen.getByRole('textbox', {
        name: 'Description draft',
      }),
    ).toHaveValue('Deliberate');
    await waitFor(() =>
      expect(
        screen.getByRole('listitem', {
          name: /^IGDB:/,
        }),
      ).toHaveFocus(),
    );
  });
});

describe('keyboard review guards', () => {
  it('does nothing when the target requirement is already open in its editor, so a draft there is never asked to discard', async () => {
    state = titleWith([
      identity,
      metadata,
    ]);
    const handle = createRef<TitleWorkbenchHandle>();
    const { router } = renderWorkbench(
      '/enrichment?title=turok&editor=metadata&requirement=Metadata%3ADescription',
      {
        ref: handle,
      },
    );
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Description draft',
      }),
      'Deliberate',
    );
    const location = router.state.location.key;
    for (const target of [
      screen.getByRole('listitem', {
        name: /^Description:/,
      }),
      screen.getByRole('heading', {
        level: 2,
        name: 'Turok',
      }),
    ]) {
      act(() => target.focus());
      act(() => handle.current?.reviewRequirement());
    }
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(
      screen.queryByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).not.toBeInTheDocument();
    expect(router.state.location.key).toBe(location);
    expect(
      screen.getByRole('textbox', {
        name: 'Description draft',
      }),
    ).toHaveValue('Deliberate');
  });
});

describe('focus after a reversal', () => {
  const omitted = satisfied('Metadata:Description', 'AcceptedOmission', [
    'ReviewMetadata',
  ]);
  const leftEmpty = satisfied('Artwork:Hero', 'LeftEmpty', [
    'ReviewArtwork',
  ]);
  const nothingUsable = unsatisfied('Artwork:Hero', 'NeedsReview', 'NothingUsable', [
    'ReviewArtwork',
    'LeaveEmpty',
  ]);

  it('stays on the reversed row when Revisit and Return to automatic replace the button that was pressed', async () => {
    state = titleWith(
      [
        omitted,
        leftEmpty,
      ],
      {
        acceptedOmissions: [
          'Description',
        ],
      },
    );
    server.use(
      http.put(`${titleEndpoint}/enrichment-omissions/Description`, () => {
        state = titleWith(
          [
            metadata,
            leftEmpty,
          ],
          {
            revision: 'rev-2',
          },
        );
        return HttpResponse.json(written);
      }),
      http.get('http://localhost/api/titles/turok/artwork', () =>
        HttpResponse.json({
          revision: 'family-rev',
          roles: [
            {
              role: 'Hero',
              mode: 'Empty',
              revision: 'hero-rev',
              pinnedAssetId: null,
              pendingJobId: null,
            },
          ],
        }),
      ),
      http.put('http://localhost/api/titles/turok/artwork/Hero', () => {
        state = titleWith(
          [
            metadata,
            nothingUsable,
          ],
          {
            revision: 'rev-3',
          },
        );
        return HttpResponse.json({
          role: 'Hero',
          mode: 'Automatic',
          revision: 'hero-rev-2',
          pinnedAssetId: null,
          pendingJobId: null,
        });
      }),
    );
    renderWorkbench();
    await userEvent.click(
      await findRowButton(/^Description: Intentionally empty/, 'Revisit description'),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('listitem', {
          name: 'Description: Description is missing after enrichment.',
        }),
      ).toHaveFocus(),
    );
    await userEvent.click(rowButton(/^Banner: Left empty by you/, 'Return banner to automatic'));
    await waitFor(() =>
      expect(
        screen.getByRole('listitem', {
          name: /^Banner: No usable banner was found/,
        }),
      ).toHaveFocus(),
    );
    expect(document.activeElement).not.toBe(document.body);
  });

  it('leaves focus where the curator moved it while the reversal was saving', async () => {
    let release!: () => void;
    const saved = new Promise<void>((resolve) => {
      release = resolve;
    });
    state = titleWith(
      [
        omitted,
      ],
      {
        acceptedOmissions: [
          'Description',
        ],
      },
    );
    server.use(
      http.put(`${titleEndpoint}/enrichment-omissions/Description`, async () => {
        await saved;
        state = titleWith(
          [
            metadata,
          ],
          {
            revision: 'rev-2',
          },
        );
        return HttpResponse.json(written);
      }),
    );
    renderWorkbench();
    await userEvent.click(
      await findRowButton(/^Description: Intentionally empty/, 'Revisit description'),
    );
    const heading = screen.getByRole('heading', {
      level: 2,
      name: 'Turok',
    });
    act(() => heading.focus());
    release();
    await screen.findByRole('listitem', {
      name: 'Description: Description is missing after enrichment.',
    });
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(heading).toHaveFocus();
  });
});

describe('review hardening', () => {
  it('opens the field inspector only for a first-load deep link or a row review, never on returning to the tab', async () => {
    state = titleWith([
      metadata,
    ]);
    renderWorkbench('/enrichment?title=turok&editor=metadata&requirement=Metadata%3ADescription');
    expect(await screen.findByText('Metadata editor: Description')).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('tab', {
        name: 'Artwork',
      }),
    );
    await userEvent.click(
      await screen.findByRole('tab', {
        name: 'Metadata',
      }),
    );
    expect(await screen.findByText('Metadata editor: none')).toBeInTheDocument();
    await userEvent.click(rowButton(/^Description:/, 'Review description'));
    expect(await screen.findByText('Metadata editor: Description')).toBeInTheDocument();
  });

  it('leaves no editor request or origin behind when the curator keeps editing', async () => {
    state = titleWith([
      identity,
      metadata,
    ]);
    renderWorkbench('/enrichment?title=turok&requirement=Metadata%3ADescription');
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Description draft',
      }),
      'Deliberate',
    );
    const igdb = screen.getByRole('listitem', {
      name: /^IGDB:/,
    });
    await userEvent.click(
      within(igdb).getByRole('button', {
        name: 'Review IGDB match',
      }),
    );
    await userEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard unsaved changes?',
        }),
      ).getByRole('button', {
        name: 'Keep editing',
      }),
    );
    await userEvent.clear(
      screen.getByRole('textbox', {
        name: 'Description draft',
      }),
    );
    await userEvent.click(
      screen.getByRole('tab', {
        name: 'Match',
      }),
    );
    const search = await screen.findByRole('textbox', {
      name: 'Provider search',
    });
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(search).not.toHaveFocus();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Cancel match',
      }),
    );
    expect(igdb).not.toHaveFocus();
  });

  it('never moves focus after editing a value that was already satisfied', async () => {
    state = chrono;
    renderWorkbench('/enrichment?title=turok', {
      view: 'NeedsReview',
    });
    await userEvent.click(await findRowButton(/^Genre:/, 'Edit genre'));
    await screen.findByText('Metadata editor: Genre');
    const save = screen.getByRole('button', {
      name: 'Save metadata',
    });
    save.focus();
    const reads = assessmentReads.mock.calls.length;
    await userEvent.click(save);
    await waitFor(() => expect(assessmentReads.mock.calls.length).toBeGreaterThan(reads));
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(save).toHaveFocus();
  });

  it('drops an artwork draft when a navigation that keeps the editor mounted is discarded, with no second dialog', async () => {
    state = titleWith([
      backdrop,
    ]);
    const { router } = renderWorkbench();
    await userEvent.type(
      await screen.findByRole('textbox', {
        name: 'Artwork draft',
      }),
      'Keep my crop',
    );
    await act(async () => {
      await router.navigate('/enrichment?title=turok&activity=run-1');
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
    await waitFor(() => expect(router.state.location.search).toBe('?title=turok&activity=run-1'));
    await waitFor(() =>
      expect(
        screen.getByRole('textbox', {
          name: 'Artwork draft',
        }),
      ).toHaveValue(''),
    );
    await act(async () => {
      await router.navigate('/enrichment?title=turok');
    });
    expect(router.state.location.search).toBe('?title=turok');
    expect(
      screen.queryByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).not.toBeInTheDocument();
  });

  it('re-announces identical population text and clears the live region once read', async () => {
    const { cache } = renderWorkbench();
    await screen.findByText('Target provider: igdb');
    // Hold time still at each observable clear boundary; auto-advancing timers
    // can run the announcement before the assertion under full-suite load.
    vi.useFakeTimers();
    const publish = (assessment: EnrichmentAssessment) =>
      act(async () => {
        cache.setQueryData(enrichmentWorkspaceKeys.title('turok'), assessment);
        await vi.advanceTimersByTimeAsync(0);
      });
    try {
      await publish(
        titleWith(
          [
            identity,
            unsatisfied('Identity:screenscraper', 'Automatic', 'Discover', [
              'Enrich',
            ]),
          ],
          {
            revision: 'rev-2',
          },
        ),
      );
      expect(liveRegion()).toBeEmptyDOMElement();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(150);
      });
      expect(liveRegion()).toHaveTextContent('1 requirement added');
      await publish(
        titleWith(
          [
            identity,
            unsatisfied('Identity:screenscraper', 'Automatic', 'Discover', [
              'Enrich',
            ]),
            unsatisfied('Identity:mobygames', 'Automatic', 'Discover', [
              'Enrich',
            ]),
          ],
          {
            revision: 'rev-3',
          },
        ),
      );
      expect(
        screen.getByRole('listitem', {
          name: /^Mobygames:/,
        }),
      ).toBeInTheDocument();
      expect(liveRegion()).toBeEmptyDOMElement();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(150);
      });
      expect(liveRegion()).toHaveTextContent('1 requirement added');
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5000);
      });
      expect(liveRegion()).toBeEmptyDOMElement();
    } finally {
      vi.useRealTimers();
    }
  });
});
