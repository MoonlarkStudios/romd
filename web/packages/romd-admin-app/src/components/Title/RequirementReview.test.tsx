import { MantineProvider } from '@mantine/core';
import {
  type ArtworkSelectionState,
  client,
  type EnrichmentAssessment,
  type EnrichmentRequirement,
  type TitleProviderMatch,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { enrichmentWorkspaceKeys } from '../../hooks/api/enrichmentWorkspaceCache';
import { providerMatchKeys, titleEnrichmentKeys } from '../../hooks/api/titleCache';
import { artworkKeys } from '../../hooks/api/useArtwork';
import { useEnrichmentAssessment } from '../../hooks/api/useEnrichmentWorkspace';
import {
  artworkPage,
  assessmentFixture,
  satisfied,
  unsatisfied,
  writtenTitleFixture,
} from '../../test/msw/fixtures/enrichment';
import { server } from '../../test/msw/server';
import { RequirementReview } from './RequirementReview';

vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canManageTitles: true,
    canConfigureIntegrations: true,
  }),
}));

const titleId = 'chrono-trigger';
const endpoint = `http://localhost/api/titles/${titleId}`;
const description = unsatisfied('Metadata:Description', 'NeedsReview', 'NotProvided', [
  'ReviewMetadata',
  'AcceptOmission',
]);
const identity = unsatisfied('Identity:igdb', 'NeedsReview', 'MatchDecisionRequired', [
  'ReviewProvider',
  'ContinueWithoutProvider',
]);
const banner = unsatisfied('Artwork:Hero', 'NeedsReview', 'NothingUsable', [
  'ReviewArtwork',
  'LeaveEmpty',
]);
const game = {
  id: '42',
  name: 'Chrono Trigger',
  url: 'https://www.igdb.com/games/chrono-trigger',
  year: 1995,
};
const match: TitleProviderMatch = {
  providerId: 'igdb',
  providerName: 'IGDB',
  isAvailable: true,
  capabilities: [
    'metadata',
  ],
  revision: 'match-1',
  state: 'NeedsReview',
  externalId: game.id,
  game,
  confidence: 0.95,
  lastError: null,
  metadataNeedsRefresh: false,
};
const initialRole: ArtworkSelectionState = {
  role: 'Hero',
  mode: 'Automatic',
  revision: 'hero-1',
  pinnedAssetId: null,
  pendingJobId: null,
};

let assessment: EnrichmentAssessment;
let role: ArtworkSelectionState;
let cache: QueryClient;

function assess(rows: EnrichmentRequirement[], revision = 'rev-1') {
  assessment = assessmentFixture(rows, {
    revision,
  });
}

function ReviewHost() {
  const query = useEnrichmentAssessment(titleId);
  return query.data ? (
    <RequirementReview
      assessment={query.data}
      onRefresh={() => void query.refetch()}
    />
  ) : null;
}

function renderReview() {
  const user = userEvent.setup();
  cache = new QueryClient({
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
          <ReviewHost />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
  return user;
}

function respondWithTitle() {
  return HttpResponse.json(
    writtenTitleFixture({
      revision: assessment.revision,
    }),
    {
      headers: {
        ETag: `"${assessment.revision}"`,
      },
    },
  );
}

const conflicts = () =>
  new HttpResponse(null, {
    status: 412,
  });

beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
  assess([
    description,
  ]);
  role = initialRole;
  server.use(
    http.get(endpoint, respondWithTitle),
    http.get(`${endpoint}/enrichment-assessment`, () => HttpResponse.json(assessment)),
    http.get(`${endpoint}/artwork`, () =>
      HttpResponse.json({
        revision: 'family-1',
        roles: [
          role,
        ],
      }),
    ),
  );
});

afterEach(() => {
  cache.clear();
  client.setConfig({
    baseUrl: '',
  });
});

async function openDecision(
  user: ReturnType<typeof userEvent.setup>,
  action: string,
  heading: string,
) {
  await user.click(
    await screen.findByRole('button', {
      name: action,
    }),
  );
  return within(
    await screen.findByRole('dialog', {
      name: heading,
    }),
  );
}

describe('requirement confirmations', () => {
  it('keeps the title revision from when the omission confirmation opened through a background refresh', async () => {
    const accept = vi.fn();
    server.use(
      http.put(`${endpoint}/enrichment-omissions/Description`, ({ request }) => {
        accept(request.headers.get('If-Match'));
        return conflicts();
      }),
    );
    const user = renderReview();
    const dialog = await openDecision(user, 'Keep description empty', 'Keep description empty?');

    assess(
      [
        description,
        unsatisfied('Metadata:Genre', 'NeedsReview', 'NotProvided'),
      ],
      'rev-2',
    );
    await act(async () =>
      cache.invalidateQueries({
        queryKey: enrichmentWorkspaceKeys.title(titleId),
      }),
    );
    expect(
      await screen.findByRole('link', {
        name: 'Genre',
      }),
    ).toBeInTheDocument();
    expect(accept).not.toHaveBeenCalled();

    await user.click(
      dialog.getByRole('button', {
        name: 'Keep description empty',
      }),
    );
    expect(
      await dialog.findByText(/This title changed while you were reviewing it/),
    ).toBeInTheDocument();
    expect(accept).toHaveBeenCalledExactlyOnceWith('"rev-1"');
  });

  it('keeps the provider revision from when the continue-without confirmation opened', async () => {
    assess([
      identity,
    ]);
    const writes = vi.fn();
    let shownMatch = match;
    server.use(
      http.get(`${endpoint}/provider-matches`, () =>
        HttpResponse.json([
          shownMatch,
        ]),
      ),
      http.delete(`${endpoint}/provider-matches/igdb`, ({ request }) => {
        writes(request.headers.get('If-Match'));
        return conflicts();
      }),
    );
    const user = renderReview();
    const dialog = await openDecision(user, 'Continue without IGDB', 'Continue without IGDB?');
    const queryKey = providerMatchKeys.title(titleId);
    await waitFor(() =>
      expect(cache.getQueryData(queryKey)).toEqual([
        match,
      ]),
    );

    shownMatch = {
      ...match,
      revision: 'match-2',
    };
    await act(async () =>
      cache.invalidateQueries({
        queryKey,
        refetchType: 'all',
      }),
    );
    expect(cache.getQueryData(queryKey)).toEqual([
      shownMatch,
    ]);
    await user.click(
      dialog.getByRole('button', {
        name: 'Continue without IGDB',
      }),
    );

    expect(await dialog.findByText(/provider match changed/i)).toBeInTheDocument();
    expect(writes).toHaveBeenCalledExactlyOnceWith('"match-1"');
  });

  it('keeps the banner role revision from when its leave-empty confirmation opened', async () => {
    assess([
      banner,
    ]);
    const writes = vi.fn();
    server.use(
      http.put(`${endpoint}/artwork/Hero`, ({ request }) => {
        writes(request.headers.get('If-Match'));
        return conflicts();
      }),
    );
    const user = renderReview();
    const queryKey = artworkKeys.selections(titleId);
    await waitFor(() =>
      expect(cache.getQueryData(queryKey)).toMatchObject({
        roles: [
          initialRole,
        ],
      }),
    );
    const dialog = await openDecision(user, 'Leave banner empty', 'Leave banner empty?');

    role = {
      ...initialRole,
      mode: 'Pinned',
      revision: 'hero-2',
      pinnedAssetId: 'new-asset',
    };
    await act(async () =>
      cache.invalidateQueries({
        queryKey,
      }),
    );
    expect(cache.getQueryData(queryKey)).toMatchObject({
      roles: [
        role,
      ],
    });
    await user.click(
      dialog.getByRole('button', {
        name: 'Leave banner empty',
      }),
    );

    expect(await dialog.findByText(/artwork changed/i)).toBeInTheDocument();
    expect(writes).toHaveBeenCalledExactlyOnceWith('"hero-1"');
  });

  it('applies an omission only after confirmation and reverses it using the accepted revision', async () => {
    const writes = vi.fn();
    server.use(
      http.put(`${endpoint}/enrichment-omissions/Description`, async ({ request }) => {
        const { accepted } = (await request.json()) as {
          accepted: boolean;
        };
        writes(accepted, request.headers.get('If-Match'));
        assess(
          accepted
            ? [
                satisfied(description.key, 'AcceptedOmission', [
                  'ReviewMetadata',
                ]),
              ]
            : [
                description,
              ],
          accepted ? 'rev-2' : 'rev-3',
        );
        return respondWithTitle();
      }),
    );
    const user = renderReview();
    const canceled = await openDecision(user, 'Keep description empty', 'Keep description empty?');
    await user.click(
      canceled.getByRole('button', {
        name: 'Cancel',
      }),
    );
    expect(writes).not.toHaveBeenCalled();

    const confirmation = await openDecision(
      user,
      'Keep description empty',
      'Keep description empty?',
    );
    await user.click(
      confirmation.getByRole('button', {
        name: 'Keep description empty',
      }),
    );
    await user.click(await screen.findByText('Previous decisions (1)'));
    await user.click(
      screen.getByRole('button', {
        name: 'Revisit description',
      }),
    );

    await screen.findByRole('link', {
      name: 'Description',
    });
    expect(writes.mock.calls).toEqual([
      [
        true,
        '"rev-1"',
      ],
      [
        false,
        '"rev-2"',
      ],
    ]);
    expect(screen.queryByText('Previous decisions (1)')).not.toBeInTheDocument();
  });

  it('reverses an empty banner using its role revision through the previous decisions list', async () => {
    assess([
      banner,
    ]);
    const writes = vi.fn();
    server.use(
      http.put(`${endpoint}/artwork/Hero`, async ({ request }) => {
        const { mode } = (await request.json()) as {
          mode: ArtworkSelectionState['mode'];
        };
        writes(mode, request.headers.get('If-Match'));
        role = {
          ...initialRole,
          mode,
          revision: mode === 'Empty' ? 'hero-2' : 'hero-3',
        };
        assess(
          mode === 'Empty'
            ? [
                satisfied(banner.key, 'LeftEmpty', [
                  'ReviewArtwork',
                ]),
              ]
            : [
                banner,
              ],
        );
        return HttpResponse.json(role);
      }),
    );
    const user = renderReview();
    const confirmation = await openDecision(user, 'Leave banner empty', 'Leave banner empty?');
    expect(writes).not.toHaveBeenCalled();
    await user.click(
      confirmation.getByRole('button', {
        name: 'Leave banner empty',
      }),
    );
    await user.click(await screen.findByText('Previous decisions (1)'));
    await user.click(
      screen.getByRole('button', {
        name: 'Return banner to automatic',
      }),
    );

    await screen.findByRole('link', {
      name: 'Banner',
    });
    expect(writes.mock.calls).toEqual([
      [
        'Empty',
        '"hero-1"',
      ],
      [
        'Automatic',
        '"hero-2"',
      ],
    ]);
  });
});

describe('quick requirement decisions', () => {
  it('accepts an unambiguous proposed match and refreshes the assessment', async () => {
    assess([
      identity,
    ]);
    const writes = vi.fn();
    server.use(
      http.get(`${endpoint}/provider-matches`, () =>
        HttpResponse.json([
          match,
        ]),
      ),
      http.post(`${endpoint}/provider-matches/igdb/confirm`, ({ request }) => {
        writes(request.headers.get('If-Match'));
        assess(
          [
            satisfied(identity.key, 'Trusted', [
              'ReviewProvider',
            ]),
          ],
          'rev-2',
        );
        return HttpResponse.json({
          ...match,
          state: 'Confirmed',
          revision: 'match-2',
        });
      }),
    );
    const user = renderReview();
    const dialog = await openDecision(user, 'Review IGDB match', 'IGDB');
    expect(await dialog.findByText('Chrono Trigger')).toBeInTheDocument();
    expect(writes).not.toHaveBeenCalled();
    await user.click(
      dialog.getByRole('button', {
        name: 'Accept match',
      }),
    );

    expect(await screen.findByText('No outstanding requirements')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(writes).toHaveBeenCalledExactlyOnceWith('"match-1"');
  });

  it('links to the title match editor when candidates need comparison', async () => {
    assess([
      identity,
    ]);
    const candidate = (id: string) => ({
      game: {
        ...game,
        id,
      },
      score: 0.95,
      eligible: true,
      evidence: [],
      conflicts: [],
    });
    server.use(
      http.get(`${endpoint}/provider-matches`, () =>
        HttpResponse.json([
          {
            ...match,
            decision: {
              state: 'NeedsReview',
              explanation: 'Multiple candidates',
              provenance: 'Matcher',
              inputFingerprint: 'fingerprint',
              candidates: [
                candidate('42'),
                candidate('43'),
              ],
            },
          },
        ]),
      ),
    );
    const user = renderReview();
    const dialog = await openDecision(user, 'Review IGDB match', 'IGDB');

    expect(
      await dialog.findByRole('link', {
        name: 'Open title editor',
      }),
    ).toHaveAttribute('href', `/titles/${titleId}/metadata?requirement=Identity%3Aigdb&match=1`);
    expect(
      dialog.queryByRole('button', {
        name: 'Accept match',
      }),
    ).not.toBeInTheDocument();
  });

  it.each([
    'saved',
    'conflicted',
  ] as const)('uses the revision observed when the metadata source was chosen even when the request is %s', async (outcome) => {
    const writes = vi.fn();
    server.use(
      http.get(`${endpoint}/enrichment`, () =>
        HttpResponse.json({
          titleId,
          name: assessment.titleName,
          revision: assessment.revision,
          enrichmentStatus: 'Completed',
          layers: [
            {
              sourceId: 'igdb',
              sourceType: 'Provider',
              description: 'Time travelers',
            },
          ],
          externalIds: [],
        }),
      ),
      http.patch(`${endpoint}/enrichment`, async ({ request }) => {
        writes(await request.json(), request.headers.get('If-Match'));
        if (outcome === 'conflicted') return conflicts();
        assess(
          [
            satisfied(description.key, 'Present', [
              'ReviewMetadata',
            ]),
          ],
          'rev-2',
        );
        return respondWithTitle();
      }),
    );
    const user = renderReview();
    const dialog = await openDecision(user, 'Review description', 'Description');
    expect(await dialog.findByText('Time travelers')).toBeInTheDocument();
    expect(
      dialog.getByRole('button', {
        name: 'Use source',
      }),
    ).toBeDisabled();
    await user.click(
      dialog.getByRole('radio', {
        name: 'IGDB',
      }),
    );
    if (outcome === 'conflicted') {
      assess(
        [
          description,
        ],
        'rev-2',
      );
      const queryKey = titleEnrichmentKeys.detail(titleId);
      await act(async () =>
        cache.invalidateQueries({
          queryKey,
        }),
      );
      expect(cache.getQueryData(queryKey)).toMatchObject({
        revision: 'rev-2',
      });
    }
    await user.click(
      dialog.getByRole('button', {
        name: 'Use source',
      }),
    );

    if (outcome === 'conflicted') {
      expect(await dialog.findByText(/title changed/i)).toBeInTheDocument();
      expect(
        dialog.getByRole('button', {
          name: 'Use source',
        }),
      ).toBeDisabled();
      expect(
        screen.getByRole('link', {
          name: 'Description',
        }),
      ).toBeInTheDocument();
    } else {
      expect(await screen.findByText('No outstanding requirements')).toBeInTheDocument();
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    }
    expect(writes).toHaveBeenCalledExactlyOnceWith(
      {
        overrides: {
          Description: 'igdb',
        },
      },
      '"rev-1"',
    );
  });

  it('reloads the metadata source after an unseen conflict before the curator can retry', async () => {
    const writes = vi.fn();
    server.use(
      http.get(`${endpoint}/enrichment`, () =>
        HttpResponse.json({
          titleId,
          name: assessment.titleName,
          revision: assessment.revision,
          enrichmentStatus: 'Completed',
          layers: [
            {
              sourceId: 'igdb',
              sourceType: 'Provider',
              description:
                assessment.revision === 'rev-1' ? 'Earlier description' : 'Updated description',
            },
          ],
          externalIds: [],
        }),
      ),
      http.patch(`${endpoint}/enrichment`, ({ request }) => {
        const validator = request.headers.get('If-Match');
        writes(validator);
        if (validator === '"rev-1"') {
          assess(
            [
              description,
            ],
            'rev-2',
          );
          return conflicts();
        }
        assess(
          [
            satisfied(description.key, 'Present', [
              'ReviewMetadata',
            ]),
          ],
          'rev-3',
        );
        return respondWithTitle();
      }),
    );
    const user = renderReview();
    const dialog = await openDecision(user, 'Review description', 'Description');
    const useSource = () =>
      dialog.getByRole('button', {
        name: 'Use source',
      });
    const chooseIgdb = async () =>
      user.click(
        await dialog.findByRole('radio', {
          name: 'IGDB',
        }),
      );

    await chooseIgdb();
    await user.click(useSource());
    expect(await dialog.findByText('Updated description')).toBeInTheDocument();
    expect(useSource()).toBeDisabled();
    expect(writes).toHaveBeenCalledExactlyOnceWith('"rev-1"');

    await chooseIgdb();
    await user.click(useSource());
    expect(await screen.findByText('No outstanding requirements')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(writes.mock.calls).toEqual([
      [
        '"rev-1"',
      ],
      [
        '"rev-2"',
      ],
    ]);
  });

  it.each([
    'saved',
    'conflicted',
  ] as const)('pins a saved image with the role revision observed when selected, then handles it as %s', async (outcome) => {
    assess([
      banner,
    ]);
    const writes = vi.fn();
    server.use(
      http.get(endpoint, () =>
        HttpResponse.json(
          writtenTitleFixture({
            revision: assessment.revision,
            media: [
              {
                id: 'media-1',
                url: '/sample-cover.jpg',
                type: 'cover',
                sourceId: 'user',
                isPrimary: false,
              },
            ],
          }),
        ),
      ),
      http.get(`${endpoint}/artwork-assets`, () => HttpResponse.json(artworkPage([]))),
      http.put(`${endpoint}/artwork/Hero`, async ({ request }) => {
        writes(await request.json(), request.headers.get('If-Match'));
        if (outcome === 'conflicted') return conflicts();
        role = {
          ...initialRole,
          mode: 'Pinned',
          revision: 'hero-2',
        };
        assess(
          [
            satisfied(banner.key, 'Present', [
              'ReviewArtwork',
            ]),
          ],
          'rev-2',
        );
        return HttpResponse.json(role);
      }),
    );
    const user = renderReview();
    const dialog = await openDecision(user, 'Review banner', 'Banner');
    const image = await dialog.findByRole('radio', {
      name: /cover 1/,
    });
    expect(
      dialog.getByRole('button', {
        name: 'Use artwork',
      }),
    ).toBeDisabled();
    await user.click(image);
    if (outcome === 'conflicted') {
      role = {
        ...initialRole,
        mode: 'Pinned',
        revision: 'hero-2',
        pinnedAssetId: 'new-asset',
      };
      const queryKey = artworkKeys.selections(titleId);
      await act(async () =>
        cache.invalidateQueries({
          queryKey,
        }),
      );
      expect(cache.getQueryData(queryKey)).toMatchObject({
        roles: [
          role,
        ],
      });
    }
    await user.click(
      dialog.getByRole('button', {
        name: 'Use artwork',
      }),
    );

    if (outcome === 'conflicted') {
      expect(await dialog.findByText(/artwork changed/i)).toBeInTheDocument();
      expect(
        dialog.getByRole('button', {
          name: 'Use artwork',
        }),
      ).toBeDisabled();
    } else {
      expect(await screen.findByText('No outstanding requirements')).toBeInTheDocument();
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    }
    expect(writes).toHaveBeenCalledExactlyOnceWith(
      {
        mode: 'Pinned',
        mediaId: 'media-1',
      },
      '"hero-1"',
    );
  });
});
