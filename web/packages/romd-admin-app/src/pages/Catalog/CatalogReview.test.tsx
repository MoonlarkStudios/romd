import {
  type CatalogTitle,
  client,
  type EnrichmentWorkbenchCounts,
  type SetEnrichmentOmissionRequest,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { catalogSearchKeys } from '../../hooks/api/useCatalogSearch';
import { authFixtures } from '../../test/msw/fixtures/auth';
import {
  assessmentFixture,
  satisfied,
  unsatisfied,
  withRequirements,
  writtenTitleFixture,
} from '../../test/msw/fixtures/enrichment';
import { server } from '../../test/msw/server';
import { render } from '../../test/utils/render';
import { finalPage } from '../../test/workspacePages';
import { Catalog } from './index';

vi.mock('@mantine/hooks', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@mantine/hooks')>()),
  useElementSize: () => ({
    ref: () => {},
    width: 1200,
    height: 45,
  }),
}));

beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
});
afterEach(() => {
  client.setConfig({
    baseUrl: '',
  });
});

it('updates the checklist, queue, count and Catalog row after a Needs review decision', async () => {
  const api = 'http://localhost/api';
  const title = writtenTitleFixture({
    revision: 'rev-1',
  });
  const titleUrl = `${api}/titles/${title.id}`;
  const description = 'Metadata:Description';
  let assessment = assessmentFixture(
    withRequirements(
      unsatisfied(description, 'NeedsReview', 'NotProvided', [
        'ReviewMetadata',
        'AcceptOmission',
      ]),
    ),
  );
  const saveDecision = vi.fn();
  server.use(
    http.get(`${api}/system-setup`, () => HttpResponse.json(finalPage([]))),
    http.get(`${api}/catalog/stats`, () =>
      HttpResponse.json({
        totalCount: 1,
      }),
    ),
    http.get(`${api}/catalog`, () =>
      HttpResponse.json(
        finalPage<CatalogTitle>([
          {
            id: title.id,
            name: title.name,
            systemKey: title.systemKey,
            isTracked: true,
            enrichmentAssessment: assessment,
          },
        ]),
      ),
    ),
    http.get(`${api}/enrichment/workbench/stats`, () =>
      HttpResponse.json<EnrichmentWorkbenchCounts>({
        total: 1,
        needsReview: Number(assessment.needsReview),
        automatic: 0,
        processing: 0,
        blocked: 0,
      }),
    ),
    http.get(`${api}/enrichment/workbench`, () =>
      HttpResponse.json(
        finalPage(
          assessment.needsReview
            ? [
                assessment,
              ]
            : [],
        ),
      ),
    ),
    http.get(titleUrl, () =>
      HttpResponse.json({
        ...title,
        revision: assessment.revision,
      }),
    ),
    http.get(`${titleUrl}/artwork`, () =>
      HttpResponse.json({
        roles: [],
        revision: 'art-1',
      }),
    ),
    http.get(`${titleUrl}/enrichment-assessment`, () => HttpResponse.json(assessment)),
    http.put<never, SetEnrichmentOmissionRequest>(
      `${titleUrl}/enrichment-omissions/Description`,
      async ({ request }) => {
        const validator = request.headers.get('If-Match');
        const decision = await request.json();
        saveDecision(validator, decision);
        if (validator !== `"${assessment.revision}"` || !decision.accepted)
          return new HttpResponse(null, {
            status: 412,
          });

        assessment = assessmentFixture(
          withRequirements(satisfied(description, 'AcceptedOmission')),
          {
            revision: 'rev-2',
            acceptedOmissions: [
              'Description',
            ],
          },
        );
        return HttpResponse.json({
          ...title,
          revision: assessment.revision,
        });
      },
    ),
  );

  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
      },
    },
  });
  // Keep All titles fresh across view switches so remounting cannot hide missed invalidation.
  cache.setQueryDefaults(catalogSearchKeys.all, {
    staleTime: Infinity,
    gcTime: Infinity,
  });
  const user = userEvent.setup();
  render(
    <QueryClientProvider client={cache}>
      <Catalog />
    </QueryClientProvider>,
    {
      auth: {
        user: authFixtures.currentUser,
        isAuthenticated: true,
        isLoading: false,
        error: null,
        retry: async () => {},
        login: async () => {},
        logout: async () => {},
      },
      routerOptions: {
        initialEntries: [
          '/catalog?view=all',
        ],
      },
    },
  );

  const catalogStatus = async (status: string) => {
    const table = await screen.findByRole('table', {
      name: 'Catalog titles',
    });
    const row = await within(table).findByRole('row', {
      name: /Chrono Trigger/,
    });
    return within(row).findByText(status);
  };
  expect(await catalogStatus('Needs review')).toBeVisible();
  const views = within(
    screen.getByRole('navigation', {
      name: 'Catalog views',
    }),
  );
  await user.click(
    await views.findByRole('link', {
      name: 'Needs review 1',
    }),
  );
  const queue = within(
    await screen.findByRole('table', {
      name: 'Titles in the review queue',
    }),
  );
  await user.click(
    queue.getByRole('button', {
      name: 'Preview Chrono Trigger',
    }),
  );
  const inspector = within(
    await screen.findByRole('complementary', {
      name: title.name,
    }),
  );
  expect(
    await inspector.findByRole('link', {
      name: 'Description',
    }),
  ).toBeVisible();
  expect(inspector.getByText('1 outstanding')).toBeVisible();

  await user.click(
    inspector.getByRole('button', {
      name: 'Keep description empty',
    }),
  );
  const confirmation = within(
    await screen.findByRole('dialog', {
      name: 'Keep description empty?',
    }),
  );
  await user.click(
    confirmation.getByRole('button', {
      name: 'Keep description empty',
    }),
  );

  expect(await inspector.findByText('No outstanding requirements')).toBeVisible();
  expect(inspector.getByText('Settled')).toBeVisible();
  expect(
    inspector.queryByRole('link', {
      name: 'Description',
    }),
  ).not.toBeInTheDocument();
  expect(saveDecision).toHaveBeenCalledExactlyOnceWith('"rev-1"', {
    accepted: true,
  });
  expect(
    await views.findByRole('link', {
      name: 'Needs review 0',
    }),
  ).toBeVisible();
  expect(await queue.findByText('No longer in this view')).toBeVisible();
  await user.click(
    inspector.getByRole('button', {
      name: 'Close inspector',
    }),
  );
  expect(await screen.findByText('Nothing needs review')).toBeVisible();

  await user.click(
    views.getByRole('link', {
      name: 'All titles',
    }),
  );
  expect(await catalogStatus('Settled')).toBeVisible();
});
