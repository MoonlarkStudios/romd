import { readFileSync } from 'node:fs';
import { MantineProvider } from '@mantine/core';
import type { TitleDetail, TitleEnrichmentStateResponse } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import {
  createMemoryRouter,
  Outlet,
  RouterProvider,
  useLocation,
  useSearchParams,
} from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DraftGuardProvider } from '../Administration/DraftGuard';
import { MetadataPanel } from './MetadataPanel';
import layout from './MetadataPanel.module.css';
import type { TitleEditorOutcome } from './titleWorkbenchModel';

const mocks = vi.hoisted(() => ({
  update: vi.fn(),
  overrides: vi.fn(),
  enrichment: {} as TitleEnrichmentStateResponse,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canManageTitles: true,
  }),
}));
vi.mock('../../hooks/api/useLibraryManagement', () => ({
  useCatalogFilterFacets: () => ({
    data: {
      genres: [],
    },
  }),
}));
vi.mock('../../hooks/api/useTitleActions', () => ({
  useUpdateTitleMetadata: () => ({
    mutateAsync: mocks.update,
  }),
}));
vi.mock('../../hooks/api/useTitleEnrichment', () => ({
  useTitleEnrichment: () => ({
    data: mocks.enrichment,
  }),
  useSetFieldOverrides: () => ({
    mutateAsync: mocks.overrides,
  }),
}));
const title: TitleDetail = {
  id: 'title1',
  name: 'Test Game',
  systemKey: 'dreamcast',
  publisher: 'Sega',
};
function renderPanel(initialField?: string, outcome: TitleEditorOutcome = {}) {
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
        <MetadataPanel
          title={title}
          titleId={title.id}
          initialField={initialField}
          {...outcome}
        />
      ),
    },
    {
      path: '/away',
      element: <h1>Other page</h1>,
    },
  ]);
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return router;
}
async function draft() {
  const user = userEvent.setup();
  await user.click(
    screen.getByRole('button', {
      name: 'Publisher: Sega',
    }),
  );
  await user.click(
    screen.getByRole('radio', {
      name: 'Custom',
      exact: true,
    }),
  );
  await user.type(
    screen.getByRole('textbox', {
      name: 'Publisher user override',
    }),
    'My publisher',
  );
  return user;
}

describe('Metadata draft safety', () => {
  beforeEach(() => {
    mocks.update.mockReset().mockResolvedValue(undefined);
    mocks.overrides.mockReset().mockResolvedValue(undefined);
    mocks.enrichment = {
      titleId: 'title1',
      layers: [],
      fieldProvenance: {
        Publisher: 'igdb',
      },
    };
  });
  it.each([
    'Escape',
    'close button',
    'Cancel',
    'backdrop',
  ])('protects dirty drafts dismissed with %s', async (method) => {
    renderPanel();
    const user = await draft();
    if (method === 'Escape') await user.keyboard('{Escape}');
    else if (method === 'close button')
      await user.click(
        screen.getByRole('button', {
          name: 'Close Publisher inspector',
        }),
      );
    else if (method === 'Cancel')
      await user.click(
        screen.getByRole('button', {
          name: 'Cancel',
        }),
      );
    else {
      const overlay = document.querySelector('.mantine-Modal-overlay');
      expect(overlay).not.toBeNull();
      fireEvent.click(overlay as Element);
    }
    expect(
      screen.getByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(
      screen.getByRole('textbox', {
        name: 'Publisher user override',
      }),
    ).toHaveValue('My publisher');
    expect(mocks.update).not.toHaveBeenCalled();
  });
  it('discards only after confirmation and opens a clean editor next time', async () => {
    renderPanel();
    const user = await draft();
    await user.click(
      screen.getByRole('button', {
        name: 'Cancel',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Publisher: Sega',
      }),
    );
    await user.click(
      screen.getByRole('radio', {
        name: 'Custom',
        exact: true,
      }),
    );
    expect(
      screen.getByRole('textbox', {
        name: 'Publisher user override',
      }),
    ).toHaveValue('');
  });
  it('dismisses an unchanged editor without confirmation', async () => {
    renderPanel();
    const user = userEvent.setup();
    await user.click(
      screen.getByRole('button', {
        name: 'Publisher: Sega',
      }),
    );
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
  it('keeps saving drafts open and closes successfully saved drafts without a discard prompt', async () => {
    let finish: () => void = () => {};
    mocks.update.mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          finish = resolve;
        }),
    );
    renderPanel();
    const user = await draft();
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    expect(
      screen.getByRole('button', {
        name: 'Cancel',
      }),
    ).toBeDisabled();
    expect(
      screen.getByRole('button', {
        name: 'Close Publisher inspector',
      }),
    ).toBeDisabled();
    expect(
      screen.getByRole('textbox', {
        name: 'Publisher user override',
      }),
    ).toBeDisabled();
    await user.keyboard('{Escape}');
    expect(
      screen.getByRole('dialog', {
        name: 'Edit Publisher',
      }),
    ).toBeInTheDocument();
    await act(async () => finish());
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(mocks.update).toHaveBeenCalledWith(
      expect.objectContaining({
        titleId: 'title1',
        metadata: expect.objectContaining({
          publisher: 'My publisher',
        }),
      }),
    );
  });
  it('preserves the draft after a save failure', async () => {
    mocks.update.mockRejectedValue(new Error('Unavailable'));
    renderPanel();
    const user = await draft();
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    expect(await screen.findByRole('alert')).toHaveTextContent('Your draft is still here');
    expect(
      screen.getByRole('textbox', {
        name: 'Publisher user override',
      }),
    ).toHaveValue('My publisher');
    await user.keyboard('{Escape}');
    expect(
      screen.getByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).toBeInTheDocument();
  });
  it('tells the workbench which field drafts were saved and which were dismissed', async () => {
    const onResolved = vi.fn();
    const onDismissed = vi.fn();
    renderPanel(undefined, {
      onResolved,
      onDismissed,
    });
    const user = userEvent.setup();
    await user.click(
      screen.getByRole('button', {
        name: 'Publisher: Sega',
      }),
    );
    await user.keyboard('{Escape}');
    expect(onDismissed).toHaveBeenCalledTimes(1);
    await draft();
    await user.click(
      screen.getByRole('button', {
        name: 'Cancel',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(onDismissed).toHaveBeenCalledTimes(2);
    await draft();
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await waitFor(() => expect(onResolved).toHaveBeenCalledOnce());
    expect(onDismissed).toHaveBeenCalledTimes(2);
  });
  it('closes the inspector and drops its draft when a navigation that keeps the panel mounted is discarded', async () => {
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
              <MetadataPanel
                title={title}
                titleId={title.id}
              />
            ),
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
    await draft();
    await act(async () => {
      await router.navigate('/?view=All');
    });
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    await waitFor(() => expect(router.state.location.search).toBe('?view=All'));
    await waitFor(() =>
      expect(
        screen.queryByRole('dialog', {
          name: 'Edit Publisher',
        }),
      ).not.toBeInTheDocument(),
    );
    await act(async () => {
      await router.navigate('/?view=Blocked');
    });
    expect(router.state.location.search).toBe('?view=Blocked');
    expect(
      screen.queryByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).not.toBeInTheDocument();
    expect(mocks.update).not.toHaveBeenCalled();
  });
  it('continues protecting dirty drafts during route navigation', async () => {
    const router = renderPanel();
    const user = await draft();
    await act(async () => {
      await router.navigate('/away');
    });
    await user.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(
      screen.getByRole('textbox', {
        name: 'Publisher user override',
      }),
    ).toHaveValue('My publisher');
    await act(async () => {
      await router.navigate('/away');
    });
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(
      screen.getByRole('heading', {
        name: 'Other page',
      }),
    ).toBeInTheDocument();
  });
  it('opens the requested requirement once and stays closed after dismissal', async () => {
    renderPanel('Publisher');
    const user = userEvent.setup();
    expect(
      screen.getByRole('dialog', {
        name: 'Edit Publisher',
      }),
    ).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Cancel',
      }),
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Developer: No value',
      }),
    );
    expect(
      await screen.findByRole('dialog', {
        name: 'Edit Developer',
      }),
    ).toBeInTheDocument();
  });
  it('preserves refreshes and canceled history navigation, then opens the accepted history target', async () => {
    const router = renderHistory([
      '/?field=Developer',
      '/?field=Publisher',
    ]);
    const user = userEvent.setup();
    await user.click(
      screen.getByRole('radio', {
        name: 'Custom',
        exact: true,
      }),
    );
    await user.type(
      screen.getByRole('textbox', {
        name: 'Publisher user override',
      }),
      'Keep this draft',
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Refresh assessment',
        hidden: true,
      }),
    );
    expect(
      screen.getByRole('textbox', {
        name: 'Publisher user override',
      }),
    ).toHaveValue('Keep this draft');
    await act(async () => {
      await router.navigate(-1);
    });
    await user.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(router.state.location.search).toBe('?field=Publisher');
    expect(
      screen.getByRole('textbox', {
        name: 'Publisher user override',
      }),
    ).toHaveValue('Keep this draft');
    await act(async () => {
      await router.navigate(-1);
    });
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(
      await screen.findByRole('dialog', {
        name: 'Edit Developer',
      }),
    ).toBeInTheDocument();
    await act(async () => {
      await router.navigate(1);
    });
    expect(
      screen.getByRole('dialog', {
        name: 'Edit Publisher',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('radio', {
        name: 'Automatic',
        exact: true,
      }),
    ).toBeChecked();
    await act(async () => {
      await router.navigate('/');
    });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await act(async () => {
      await router.navigate('/?field=Publisher');
    });
    await user.click(
      screen.getByRole('button', {
        name: 'Cancel',
      }),
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Refresh assessment',
      }),
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('does not let an old save close a new editor session for the same field', async () => {
    let finish: () => void = () => {};
    mocks.update.mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          finish = resolve;
        }),
    );
    const router = renderHistory([
      '/?field=Publisher&request=earlier',
      '/?field=Publisher',
    ]);
    const user = userEvent.setup();
    await user.click(
      screen.getByRole('radio', {
        name: 'Custom',
        exact: true,
      }),
    );
    await user.type(
      screen.getByRole('textbox', {
        name: 'Publisher user override',
      }),
      'Saving draft',
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Save changes',
      }),
    );
    await act(async () => {
      await router.navigate(-1);
    });
    await user.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    await waitFor(() =>
      expect(router.state.location.search).toBe('?field=Publisher&request=earlier'),
    );
    await waitFor(() =>
      expect(
        screen.getByRole('radio', {
          name: 'Automatic',
          exact: true,
        }),
      ).toBeChecked(),
    );
    await act(async () => finish());
    expect(
      screen.getByRole('dialog', {
        name: 'Edit Publisher',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Cancel',
      }),
    ).toBeEnabled();
  });
});

describe('Metadata editor layout', () => {
  beforeEach(() => {
    mocks.enrichment = {
      titleId: 'title1',
      layers: [],
      fieldProvenance: {
        Publisher: 'igdb',
      },
    };
  });
  it.each([
    'comfortable',
    'compact',
  ] as const)('marks the %s host density on its own size container', (density) => {
    renderPanel(undefined, {
      density,
    });
    const publisher = screen.getByRole('button', {
      name: 'Publisher: Sega',
    });
    expect(publisher.closest('[data-density]')).toHaveAttribute('data-density', density);
    expect(publisher).toHaveClass(layout.field);
    expect(publisher.closest(`.${layout.fields}`)).not.toBeNull();
    expect(
      screen.getByRole('button', {
        name: /^Description:/,
      }).parentElement,
    ).toHaveClass(layout.wide);
  });
  it('lays out columns by its own width and tiers field spacing through custom properties', () => {
    const styles = readFileSync(`${import.meta.dirname}/MetadataPanel.module.css`, 'utf8');
    expect(styles).not.toMatch(/@media/);
    expect(styles).toMatch(/\.panel \{\s*container: metadata-editor \/ inline-size;/);
    const compact = styles.slice(styles.indexOf(".panel[data-density='compact'] {"));
    const forced = styles.slice(styles.indexOf('@container metadata-editor (max-width: 30em) {'));
    for (const tier of [
      compact,
      forced,
    ]) {
      expect(tier).toMatch(
        /--metadata-field-gap: var\(--mantine-spacing-xs\);[^}]*--metadata-field-min-height: 0;/,
      );
    }
    expect(styles).toContain(
      '.fields { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--metadata-field-gap); }',
    );
    expect(styles).toContain(
      '@container metadata-editor (max-width: 32em) { .fields { grid-template-columns: minmax(0, 1fr); } }',
    );
    expect(styles).toContain(
      '.field { padding: var(--metadata-field-padding); min-height: var(--metadata-field-min-height); }',
    );
    // The shared title stylesheet no longer sizes the field button, so the density tier alone decides it.
    expect(readFileSync(`${import.meta.dirname}/TitleDetail.module.css`, 'utf8')).toMatch(
      /\.field \{ width: 100%; border-bottom: [^}]*\}/,
    );
  });
});

function renderHistory(initialEntries: string[]) {
  function Workspace() {
    const [, refresh] = useState(0);
    const location = useLocation();
    const [search] = useSearchParams();
    return (
      <>
        <button
          type="button"
          onClick={() => {
            mocks.enrichment = {
              ...mocks.enrichment,
            };
            refresh((value) => value + 1);
          }}
        >
          Refresh assessment
        </button>
        <MetadataPanel
          title={title}
          titleId="title1"
          initialField={search.get('field') ?? undefined}
          fieldRequestKey={location.key}
        />
      </>
    );
  }
  const cache = new QueryClient();
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <Workspace />,
      },
    ],
    {
      initialEntries,
      initialIndex: initialEntries.length - 1,
    },
  );
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return router;
}
