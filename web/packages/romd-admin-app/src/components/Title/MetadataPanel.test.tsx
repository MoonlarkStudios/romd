import { MantineProvider } from '@mantine/core';
import type { TitleEnrichmentStateResponse } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, Link, RouterProvider } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { TitleDetailView } from '../../hooks/api/useTitleDetail';
import { MetadataPanel } from './MetadataPanel';

const mocks = vi.hoisted(() => ({
  update: vi.fn(),
  overrides: vi.fn(),
  sources: vi.fn(),
  enrichment: {} as TitleEnrichmentStateResponse,
  canManageTitles: true,
}));

vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canManageTitles: mocks.canManageTitles,
  }),
}));

vi.mock('../../hooks/api/useLibraryManagement', () => ({
  useCatalogFilterFacets: () => ({
    data: {
      genres: [],
    },
  }),
}));

vi.mock('../../hooks/api/usePlatforms', () => ({
  usePlatform: () => ({
    data: {
      name: 'Sega Dreamcast',
    },
  }),
}));

vi.mock('../../hooks/api/useTitleActions', () => ({
  useUpdateTitleMetadata: () => ({
    mutateAsync: mocks.update,
  }),
}));

vi.mock('../../hooks/api/useTitleEnrichment', () => ({
  useTitleEnrichment: (id?: string) => ({
    data: id ? mocks.enrichment : undefined,
  }),
  useSetFieldOverrides: () => ({
    mutateAsync: mocks.overrides,
  }),
}));

vi.mock('../../hooks/api/useTitleDetail', () => ({
  useTitleSourceReferences: (titleId: string) => {
    mocks.sources(titleId);
    return {
      data: [],
    };
  },
}));

const title: TitleDetailView = {
  id: 'title1',
  revision: 'rev-1',
  name: 'Test Game',
  systemKey: 'dreamcast',
  enrichmentStatus: 'Completed',
  publisher: 'Sega',
  developer: 'Capcom',
  releases: [],
  media: [],
};

function renderPanel(initialField?: string) {
  const router = createMemoryRouter([
    {
      path: '/',
      element: (
        <>
          <MetadataPanel
            title={title}
            titleId={title.id}
            initialField={initialField}
          />
          <Link to="/away">Next section</Link>
          <Link to="?field=description">Another field</Link>
        </>
      ),
    },
    {
      path: '/away',
      element: <h1>Other section</h1>,
    },
  ]);
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={new QueryClient()}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return router;
}

function publisher() {
  return screen.getByRole('textbox', {
    name: 'Publisher',
  });
}

function saveChanges() {
  return screen.getByRole('button', {
    name: 'Save changes',
  });
}

async function changePublisher(user: ReturnType<typeof userEvent.setup>, value: string) {
  await user.clear(publisher());
  await user.type(publisher(), value);
}

describe('Metadata form', () => {
  beforeEach(() => {
    mocks.update.mockReset().mockResolvedValue({
      revision: 'rev-2',
    });
    mocks.overrides.mockReset().mockResolvedValue({
      revision: 'rev-2',
    });
    mocks.sources.mockReset();
    mocks.canManageTitles = true;
    mocks.enrichment = {
      titleId: title.id,
      revision: title.revision,
      name: title.name,
      enrichmentStatus: 'Completed',
      fieldProvenance: {
        Publisher: 'igdb',
        Developer: 'user',
      },
      layers: [
        {
          sourceId: 'igdb',
          sourceType: 'Provider',
          publisher: 'Sega',
          developer: 'Sega AM2',
        },
        {
          sourceId: 'user',
          sourceType: 'User',
          developer: 'Capcom',
        },
      ],
      externalIds: [],
    };
  });

  it('batches edited fields and the title name into one revision-checked write', async () => {
    const user = userEvent.setup();
    renderPanel();
    expect(
      screen.getByRole('textbox', {
        name: 'Platform',
      }),
    ).toHaveAttribute('readonly');
    expect(
      screen.getByRole('textbox', {
        name: 'Platform',
      }),
    ).toHaveValue('Sega Dreamcast');
    expect(mocks.sources).toHaveBeenCalledWith('title1');
    expect(saveChanges()).toBeDisabled();

    await changePublisher(user, 'My publisher');
    const name = screen.getByRole('textbox', {
      name: 'Title name',
    });
    await user.clear(name);
    await user.type(name, 'Updated name');
    expect(
      screen.getByRole('button', {
        name: 'Publisher source: Edited',
      }),
    ).toBeInTheDocument();
    await user.click(saveChanges());

    await waitFor(() => expect(saveChanges()).toBeDisabled());
    expect(mocks.update).toHaveBeenCalledExactlyOnceWith({
      titleId: title.id,
      revision: 'rev-1',
      metadata: {
        name: 'Updated name',
        publisher: 'My publisher',
      },
    });
    expect(mocks.overrides).not.toHaveBeenCalled();
  });

  it('previews a locked source selected with the keyboard and saves it using the existing override contract', async () => {
    const user = userEvent.setup();
    renderPanel();
    const marker = screen.getByRole('button', {
      name: 'Developer source: Edited',
    });
    marker.focus();
    await user.keyboard('{Enter}');
    const source = screen.getByRole('radio', {
      name: 'Use IGDB',
    });
    source.focus();
    await user.keyboard(' ');
    expect(
      screen.getByRole('textbox', {
        name: 'Developer',
      }),
    ).toHaveValue('Sega AM2');
    await user.keyboard('{Escape}');
    expect(marker).toHaveFocus();
    expect(marker).toHaveAccessibleName('Developer source: IGDB · Locked');
    await user.click(saveChanges());

    await waitFor(() => expect(saveChanges()).toBeDisabled());
    expect(mocks.overrides).toHaveBeenCalledExactlyOnceWith({
      titleId: title.id,
      revision: 'rev-1',
      overrides: {
        Developer: 'igdb',
      },
    });
    expect(mocks.update).not.toHaveBeenCalled();
  });

  it('removes a saved edit and its lock when returning a field to automatic resolution', async () => {
    const user = userEvent.setup();
    mocks.enrichment.fieldSourceOverrides = {
      Developer: 'user',
    };
    renderPanel();
    await user.click(
      screen.getByRole('button', {
        name: 'Developer source: Edited',
      }),
    );
    await user.click(
      screen.getByRole('radio', {
        name: 'Use source automatically',
      }),
    );
    expect(
      screen.getByRole('textbox', {
        name: 'Developer',
      }),
    ).toHaveAttribute('placeholder', 'Resolved on save');
    await user.keyboard('{Escape}');
    await user.click(saveChanges());

    await waitFor(() => expect(saveChanges()).toBeDisabled());
    expect(mocks.update).toHaveBeenCalledExactlyOnceWith({
      titleId: title.id,
      revision: 'rev-1',
      metadata: {
        developer: null,
      },
    });
    expect(mocks.overrides).toHaveBeenCalledExactlyOnceWith({
      titleId: title.id,
      revision: 'rev-2',
      overrides: {
        Developer: '',
      },
    });
  });

  it('restores the saved value without writing when reverting the current draft', async () => {
    const user = userEvent.setup();
    renderPanel();
    await changePublisher(user, 'My publisher');
    await user.click(
      screen.getByRole('button', {
        name: 'Publisher source: Edited',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Restore saved field',
      }),
    );
    expect(publisher()).toHaveValue('Sega');
    expect(saveChanges()).toBeDisabled();
    expect(mocks.update).not.toHaveBeenCalled();
  });

  it('keeps a draft when route navigation is canceled, then discards it on confirmed navigation', async () => {
    const user = userEvent.setup();
    renderPanel();
    await changePublisher(user, 'My publisher');
    await user.click(
      screen.getByRole('link', {
        name: 'Next section',
      }),
    );
    expect(await screen.findByText('Your metadata draft has not been saved.')).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(publisher()).toHaveValue('My publisher');

    await user.click(
      screen.getByRole('link', {
        name: 'Next section',
      }),
    );
    const dialog = screen.getByRole('dialog', {
      name: 'Discard unsaved changes?',
    });
    await user.click(
      within(dialog).getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(
      await screen.findByRole('heading', {
        name: 'Other section',
      }),
    ).toBeInTheDocument();
  });

  it('drops a draft after confirmed navigation that leaves the metadata section mounted', async () => {
    const user = userEvent.setup();
    const router = renderPanel();
    await changePublisher(user, 'My publisher');
    await user.click(
      screen.getByRole('link', {
        name: 'Another field',
      }),
    );
    const dialog = screen.getByRole('dialog', {
      name: 'Discard unsaved changes?',
    });
    await user.click(
      within(dialog).getByRole('button', {
        name: 'Discard',
      }),
    );
    await waitFor(() => expect(router.state.location.search).toBe('?field=description'));
    expect(publisher()).toHaveValue('Sega');
    expect(saveChanges()).toBeDisabled();
  });

  it('keeps the form protected during a save and prompts no longer once it succeeds', async () => {
    let finish = (_data: { revision: string }) => {};
    mocks.update.mockImplementationOnce(
      () =>
        new Promise<{
          revision: string;
        }>((resolve) => {
          finish = resolve;
        }),
    );
    const user = userEvent.setup();
    renderPanel();
    await changePublisher(user, 'My publisher');
    await user.click(saveChanges());
    expect(publisher()).toBeDisabled();
    expect(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    ).toBeDisabled();
    await act(async () =>
      finish({
        revision: 'rev-2',
      }),
    );
    await user.click(
      screen.getByRole('link', {
        name: 'Next section',
      }),
    );
    expect(
      await screen.findByRole('heading', {
        name: 'Other section',
      }),
    ).toBeInTheDocument();
  });

  it('ignores a late write failure from a draft the curator already discarded', async () => {
    let fail = (_reason: Error) => {};
    mocks.update.mockImplementationOnce(
      () =>
        new Promise((_resolve, reject) => {
          fail = reject;
        }),
    );
    const user = userEvent.setup();
    const router = renderPanel();
    await changePublisher(user, 'My publisher');
    await user.click(saveChanges());
    await user.click(
      screen.getByRole('link', {
        name: 'Another field',
      }),
    );
    const dialog = screen.getByRole('dialog', {
      name: 'Discard unsaved changes?',
    });
    await user.click(
      within(dialog).getByRole('button', {
        name: 'Discard',
      }),
    );
    await waitFor(() => expect(router.state.location.search).toBe('?field=description'));
    await act(async () => fail(new Error('Write failed')));
    expect(publisher()).toHaveValue('Sega');
    expect(saveChanges()).toBeDisabled();
    expect(screen.queryByText(/Could not save your draft/)).not.toBeInTheDocument();
  });

  it('adds provider values to the same draft and keeps fields the curator already edited', async () => {
    const user = userEvent.setup();
    mocks.enrichment.layers[0].genre = 'Action';
    renderPanel();
    await changePublisher(user, 'My publisher');
    await user.click(
      screen.getByRole('button', {
        name: 'Apply provider values',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Add to draft (1)',
      }),
    );
    expect(publisher()).toHaveValue('My publisher');
    expect(
      screen.getByRole('button', {
        name: 'Genre source: IGDB · Locked',
      }),
    ).toBeInTheDocument();
    await user.click(saveChanges());

    await waitFor(() => expect(saveChanges()).toBeDisabled());
    expect(mocks.update).toHaveBeenCalledExactlyOnceWith({
      titleId: title.id,
      revision: 'rev-1',
      metadata: {
        publisher: 'My publisher',
      },
    });
    expect(mocks.overrides).toHaveBeenCalledExactlyOnceWith({
      titleId: title.id,
      revision: 'rev-2',
      overrides: {
        Genre: 'igdb',
      },
    });
  });

  it('focuses the input specified by a requirement deep link', () => {
    renderPanel('publisher');
    expect(publisher()).toHaveFocus();
    expect(publisher().closest('[data-highlighted]')).toBeInTheDocument();
  });

  it('shows metadata values and identifiers to read-only users', () => {
    mocks.canManageTitles = false;
    renderPanel();
    expect(publisher()).toHaveAttribute('readonly');
    expect(
      screen.getByRole('heading', {
        name: 'Identifiers and sources',
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: 'Save changes',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: /^Publisher source:/,
      }),
    ).not.toBeInTheDocument();
  });
});
