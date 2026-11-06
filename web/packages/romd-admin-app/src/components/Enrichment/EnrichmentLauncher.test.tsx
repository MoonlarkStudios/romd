import { MantineProvider } from '@mantine/core';
import { type ArtworkCompletionRequest, client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { type ReactElement, useState } from 'react';
import {
  createMemoryRouter,
  MemoryRouter,
  Outlet,
  Route,
  RouterProvider,
  Routes,
  useLocation,
} from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { server } from '../../test/msw/server';
import { DraftGuardProvider } from '../Administration/DraftGuard';
import { UnsavedChanges } from '../Administration/UnsavedChanges';
import { EnrichmentLauncher } from './EnrichmentLauncher';

const previews: ArtworkCompletionRequest[] = [];
const queued: ArtworkCompletionRequest[] = [];
beforeEach(() => {
  previews.length = 0;
  queued.length = 0;
  client.setConfig({
    baseUrl: 'http://localhost',
  });
  server.use(
    http.post('http://localhost/api/artwork/completion/preview', async ({ request }) => {
      previews.push((await request.json()) as ArtworkCompletionRequest);
      return HttpResponse.json({
        titles: 2,
        roles: [
          {
            role: 'Poster',
            status: 'Missing',
            count: 1,
          },
          {
            role: 'Hero',
            status: 'Missing',
            count: 0,
          },
        ],
      });
    }),
    http.post('http://localhost/api/artwork/completion', async ({ request }) => {
      queued.push((await request.json()) as ArtworkCompletionRequest);
      return HttpResponse.json(
        {
          jobId: `run-${queued.length}`,
        },
        {
          status: 202,
        },
      );
    }),
  );
});

function Destination() {
  const location = useLocation();
  return (
    <div>
      Workspace: {location.pathname}
      {location.search}
    </div>
  );
}

function renderLauncher(launcher: ReactElement) {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  return render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <MemoryRouter>
          {launcher}
          <Routes>
            <Route
              path="/"
              element={null}
            />
            <Route
              path="/enrichment"
              element={<Destination />}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const scopeOptions = () =>
  within(
    screen.getByRole('listbox', {
      name: 'Titles to check',
      hidden: true,
    }),
  )
    .getAllByRole('option', {
      hidden: true,
    })
    .map((option) => option.textContent);

it('previews the selection, then opens the new run in the workspace after queueing', async () => {
  const onClose = vi.fn();
  renderLauncher(
    <EnrichmentLauncher
      opened
      onClose={onClose}
      titleIds={[
        'one',
        'two',
      ]}
      initialScope="Selected"
    />,
  );
  expect(await screen.findByText('Poster: 1 missing')).toBeInTheDocument();
  expect(screen.getByText('Banner: 0 missing')).toBeInTheDocument();
  expect(screen.getByText('2 titles in this scope')).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Start filling missing artwork',
    }),
  );
  expect(
    await screen.findByText('Workspace: /enrichment?view=Processing&activity=run-1'),
  ).toBeInTheDocument();
  expect(queued[0]).toMatchObject({
    scope: 'Selected',
    mode: 'Artwork',
    titleIds: [
      'one',
      'two',
    ],
  });
  expect(queued[0]).not.toHaveProperty('filters');
  expect(onClose).toHaveBeenCalled();
});

it('disables start when the preview fails and offers a reload', async () => {
  server.use(
    http.post(
      'http://localhost/api/artwork/completion/preview',
      () =>
        new HttpResponse(null, {
          status: 503,
        }),
    ),
  );
  renderLauncher(
    <EnrichmentLauncher
      opened
      onClose={() => {}}
    />,
  );
  expect(await screen.findByText(/Could not check this scope/)).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Start filling missing artwork',
    }),
  ).toBeDisabled();
  expect(
    screen.getByRole('button', {
      name: 'Reload',
    }),
  ).toBeInTheDocument();
});

it('enriches every tracked title without loaded ids when no narrower scope applies', async () => {
  renderLauncher(
    <EnrichmentLauncher
      opened
      onClose={() => {}}
      initialMode="Enrich"
      initialScope="Selected"
    />,
  );
  await screen.findByText('2 titles in this scope');
  expect(previews.at(-1)).toMatchObject({
    scope: 'AllTracked',
    mode: 'Enrich',
  });
  expect(previews.at(-1)).not.toHaveProperty('titleIds');
  await userEvent.click(
    screen.getByRole('combobox', {
      name: 'Titles to check',
    }),
  );
  expect(scopeOptions()).toEqual([
    'Titles that can be enriched automatically',
    'All tracked titles',
  ]);
});

it('offers only the scopes the entry point supports and drops Automatic outside Enrich mode', async () => {
  const user = userEvent.setup();
  const filters = {
    systemKey: 'snes',
    tracked: 'tracked',
    enrichment: [],
  };
  renderLauncher(
    <EnrichmentLauncher
      opened
      onClose={() => {}}
      titleIds={[
        'one',
      ]}
      filters={filters}
      initialScope="Automatic"
      initialMode="Enrich"
    />,
  );
  await waitFor(() =>
    expect(previews.at(-1)).toMatchObject({
      scope: 'Automatic',
      filters: {
        systemKey: 'snes',
        tracked: 'tracked',
      },
    }),
  );
  expect(previews.at(-1)?.filters).not.toHaveProperty('enrichment');
  await user.click(
    screen.getByRole('combobox', {
      name: 'Titles to check',
    }),
  );
  expect(scopeOptions()).toEqual([
    'Selected titles (1)',
    'Titles matching the current filters',
    'Titles matching the filters that can be enriched automatically',
    'All tracked titles',
  ]);

  await user.click(
    screen.getByRole('combobox', {
      name: 'What to enrich',
    }),
  );
  await user.click(
    screen.getByRole('option', {
      name: 'Missing artwork only',
      hidden: true,
    }),
  );
  await waitFor(() =>
    expect(previews.at(-1)).toMatchObject({
      scope: 'Filtered',
      mode: 'Artwork',
      filters: {
        systemKey: 'snes',
      },
    }),
  );
  expect(
    screen.getByRole('combobox', {
      name: 'Titles to check',
    }),
  ).toHaveValue('Titles matching the current filters');
  await user.click(
    screen.getByRole('combobox', {
      name: 'Titles to check',
    }),
  );
  expect(scopeOptions()).toEqual([
    'Selected titles (1)',
    'Titles matching the current filters',
    'All tracked titles',
  ]);
});

it('launches one title without offering a scope', async () => {
  renderLauncher(
    <EnrichmentLauncher
      opened
      onClose={() => {}}
      titleId="chrono"
      titleName="Chrono Trigger"
      initialMode="Enrich"
    />,
  );
  expect(await screen.findByText('Enrich Chrono Trigger')).toBeInTheDocument();
  expect(await screen.findByText('Poster: 1 missing')).toBeInTheDocument();
  expect(
    screen.queryByRole('combobox', {
      name: 'Titles to check',
    }),
  ).not.toBeInTheDocument();
  expect(screen.queryByText(/in this scope/)).not.toBeInTheDocument();
  expect(
    screen.getByRole('combobox', {
      name: 'What to enrich',
    }),
  ).toHaveValue('Metadata and missing artwork');
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Start enrichment',
    }),
  );
  expect(
    await screen.findByText('Workspace: /enrichment?view=Processing&activity=run-1'),
  ).toBeInTheDocument();
  expect(queued[0]).toEqual({
    requestId: expect.any(String),
    scope: 'Selected',
    mode: 'Enrich',
    titleIds: [
      'chrono',
    ],
  });
});

it('retries a failed start with the same request id', async () => {
  server.use(
    http.post('http://localhost/api/artwork/completion', async ({ request }) => {
      queued.push((await request.json()) as ArtworkCompletionRequest);
      return queued.length === 1
        ? new HttpResponse(null, {
            status: 500,
          })
        : HttpResponse.json(
            {
              jobId: 'run-retried',
            },
            {
              status: 202,
            },
          );
    }),
  );
  renderLauncher(
    <EnrichmentLauncher
      opened
      onClose={() => {}}
      initialMode="Enrich"
    />,
  );
  await screen.findByText('2 titles in this scope');
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Start enrichment',
    }),
  );
  expect(await screen.findByText(/Retry safely with the same scope/)).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Start enrichment',
    }),
  );
  expect(
    await screen.findByText('Workspace: /enrichment?view=Processing&activity=run-retried'),
  ).toBeInTheDocument();
  expect(queued[1].requestId).toBe(queued[0].requestId);
});

function KeptMounted() {
  const [opened, setOpened] = useState(false);
  return (
    <>
      <button
        type="button"
        onClick={() => setOpened(true)}
      >
        Open launcher
      </button>
      <EnrichmentLauncher
        opened={opened}
        onClose={() => setOpened(false)}
        initialMode="Enrich"
      />
    </>
  );
}

it('starts a new request each time a mounted launcher is opened', async () => {
  const user = userEvent.setup();
  renderLauncher(<KeptMounted />);
  for (const run of [
    'run-1',
    'run-2',
  ]) {
    await user.click(
      screen.getByRole('button', {
        name: 'Open launcher',
      }),
    );
    await screen.findByText('2 titles in this scope');
    await user.click(
      screen.getByRole('button', {
        name: 'Start enrichment',
      }),
    );
    expect(
      await screen.findByText(`Workspace: /enrichment?view=Processing&activity=${run}`),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(
        screen.queryByRole('button', {
          name: 'Start enrichment',
        }),
      ).not.toBeInTheDocument(),
    );
  }
  expect(queued).toHaveLength(2);
  expect(queued[1].requestId).not.toBe(queued[0].requestId);
});

function renderWithDraft(launcher: ReactElement) {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const router = createMemoryRouter([
    {
      element: (
        <DraftGuardProvider>
          <Outlet />
        </DraftGuardProvider>
      ),
      children: [
        {
          path: '/',
          element: (
            <>
              <UnsavedChanges
                dirty
                description="Your cropped poster is not saved."
              />
              {launcher}
            </>
          ),
        },
        {
          path: '/enrichment',
          element: <Destination />,
        },
      ],
    },
  ]);
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

it('asks about an open draft before queueing, and Keep editing queues nothing', async () => {
  renderWithDraft(
    <EnrichmentLauncher
      opened
      onClose={() => {}}
      titleId="chrono"
      titleName="Chrono Trigger"
      initialMode="Enrich"
    />,
  );
  await screen.findByText('Poster: 1 missing');
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Start enrichment',
    }),
  );
  expect(await screen.findByText('Discard unsaved changes?')).toBeInTheDocument();
  expect(queued).toHaveLength(0);
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Keep editing',
    }),
  );
  await waitFor(() =>
    expect(screen.queryByText('Discard unsaved changes?')).not.toBeInTheDocument(),
  );
  expect(queued).toHaveLength(0);
  expect(screen.getByText('Enrich Chrono Trigger')).toBeInTheDocument();
});

it('after Discard queues once and opens the run without asking again', async () => {
  renderWithDraft(
    <EnrichmentLauncher
      opened
      onClose={() => {}}
      titleId="chrono"
      titleName="Chrono Trigger"
      initialMode="Enrich"
    />,
  );
  await screen.findByText('Poster: 1 missing');
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Start enrichment',
    }),
  );
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Discard',
    }),
  );
  expect(
    await screen.findByText('Workspace: /enrichment?view=Processing&activity=run-1'),
  ).toBeInTheDocument();
  expect(queued).toHaveLength(1);
  expect(screen.queryByText('Discard unsaved changes?')).not.toBeInTheDocument();
});
