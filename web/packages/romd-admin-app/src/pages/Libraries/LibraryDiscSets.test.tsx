import { getLibraryDiscSetOptions, updateLibrary } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { useState } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useUpdateLibrary } from '../../hooks/api/useLibraryManagement';
import { render } from '../../test/utils/render';
import { discSetPage } from '../../test/workspacePages';
import { LibraryDiscSets } from './LibraryDiscSets';
import { toConfiguration, toFormState } from './libraryForm';
import { defaultLibraryConfiguration } from './libraryOptions';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  getLibraryDiscSetOptions: vi.fn(),
  updateLibrary: vi.fn(),
}));
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(updateLibrary).mockResolvedValue({
    data: {
      id: 'lib',
      name: 'Saved Library',
      configurationState: 'Valid',
      isDefault: false,
      needsMaterialization: true,
      itemCount: 0,
      createdAt: '2026-10-15T00:00:00Z',
    },
    error: undefined,
    request: new Request('http://localhost'),
    response: new Response(),
  });
  vi.mocked(getLibraryDiscSetOptions).mockResolvedValue({
    data: discSetPage({
      selectedDiscSetIds: [
        'old',
      ],
      selectedItems: [
        {
          id: 'old',
          name: 'Old edition',
          titleId: null,
          systemKey: 'psx',
          memberCount: 2,
          expectedDiscCount: null,
          isEligible: false,
          issue: 'Source unavailable',
        },
      ],
      items: [
        {
          id: 'new',
          name: 'Game (USA)',
          titleId: 't1',
          systemKey: 'psx',
          memberCount: 2,
          expectedDiscCount: null,
          isEligible: true,
          issue: null,
        },
      ],
      nextAfter: null,
    }),
    error: undefined,
    request: new Request('http://localhost'),
    response: new Response(),
  });
});
function SavedPolicyHarness() {
  const [selection, setSelection] = useState([
    'old',
    'dirty',
  ]);
  const update = useUpdateLibrary();
  return (
    <>
      <LibraryDiscSets
        libraryId="lib"
        value={selection}
        onChange={setSelection}
      />
      <button
        type="button"
        onClick={() =>
          update.mutate({
            libraryId: 'lib',
            request: {
              name: 'Saved Library',
              configuration: defaultLibraryConfiguration,
            },
          })
        }
      >
        Save policy
      </button>
      <output aria-label="Draft selections">{JSON.stringify(selection)}</output>
    </>
  );
}

describe('LibraryDiscSets', () => {
  it('loads a later Page in the same library scope without losing unresolved selections', async () => {
    vi.mocked(getLibraryDiscSetOptions).mockResolvedValueOnce({
      data: {
        selectedIds: [
          'old',
        ],
        selected: [],
        page: {
          items: [],
          nextCursor: 'later',
          hasNextPage: true,
        },
      },
      error: undefined,
      request: new Request('http://localhost'),
      response: new Response(),
    });
    render(
      <LibraryDiscSets
        libraryId="lib"
        value={[
          'old',
        ]}
        onChange={vi.fn()}
      />,
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Choose disc sets',
      }),
    );
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'More disc sets',
      }),
    );

    expect(
      await screen.findByRole('checkbox', {
        name: 'Game (USA) · psx',
      }),
    ).toBeEnabled();
    expect(getLibraryDiscSetOptions).toHaveBeenLastCalledWith({
      path: {
        libraryId: 'lib',
      },
      query: {
        cursor: 'later',
      },
    });
    expect(
      screen.getByRole('checkbox', {
        name: 'Old edition · psx',
      }),
    ).toBeChecked();
  });

  it('reloads eligible choices after saving policy and preserves unsaved selections', async () => {
    vi.mocked(getLibraryDiscSetOptions).mockResolvedValueOnce({
      data: discSetPage({
        selectedDiscSetIds: [
          'old',
        ],
        selectedItems: [],
        items: [],
        nextAfter: null,
      }),
      error: undefined,
      request: new Request('http://localhost'),
      response: new Response(),
    });
    const client = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
          staleTime: Infinity,
        },
      },
    });
    render(
      <QueryClientProvider client={client}>
        <SavedPolicyHarness />
      </QueryClientProvider>,
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Choose disc sets',
      }),
    );
    await waitFor(() => expect(getLibraryDiscSetOptions).toHaveBeenCalledTimes(1));
    expect(
      screen.queryByRole('checkbox', {
        name: 'Game (USA) · psx',
      }),
    ).not.toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Save policy',
      }),
    );
    expect(
      await screen.findByRole('checkbox', {
        name: 'Game (USA) · psx',
      }),
    ).toBeEnabled();
    expect(getLibraryDiscSetOptions).toHaveBeenCalledTimes(2);
    expect(
      screen.getByRole('checkbox', {
        name: 'Old edition · psx',
      }),
    ).toBeChecked();
    expect(screen.getByLabelText('Draft selections')).toHaveTextContent('["old","dirty"]');
    fireEvent.click(
      screen.getByRole('checkbox', {
        name: 'Game (USA) · psx',
      }),
    );
    expect(screen.getByLabelText('Draft selections')).toHaveTextContent('["old","dirty","new"]');
  });

  it('does not refresh choices or alter draft selections when policy saving fails', async () => {
    vi.mocked(updateLibrary).mockResolvedValueOnce({
      data: undefined,
      error: {
        title: 'Invalid policy',
        status: 400,
      },
      request: new Request('http://localhost'),
      response: new Response(null, {
        status: 400,
      }),
    });
    const client = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
          staleTime: Infinity,
        },
      },
    });
    render(
      <QueryClientProvider client={client}>
        <SavedPolicyHarness />
      </QueryClientProvider>,
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Choose disc sets',
      }),
    );
    await screen.findByRole('checkbox', {
      name: 'Game (USA) · psx',
    });
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Save policy',
      }),
    );
    await waitFor(() => expect(updateLibrary).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(client.isMutating()).toBe(0));
    expect(getLibraryDiscSetOptions).toHaveBeenCalledTimes(1);
    expect(screen.getByLabelText('Draft selections')).toHaveTextContent('["old","dirty"]');
  });

  it('loads choices on demand and preserves a selected unresolved set when adding another', async () => {
    const change = vi.fn();
    render(
      <LibraryDiscSets
        libraryId="lib"
        value={[
          'old',
        ]}
        onChange={change}
      />,
    );
    expect(getLibraryDiscSetOptions).not.toHaveBeenCalled();
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Choose disc sets',
      }),
    );
    expect(await screen.findByText(/Source unavailable/)).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('checkbox', {
        name: 'Game (USA) · psx',
      }),
    );
    expect(change).toHaveBeenCalledWith([
      'old',
      'new',
    ]);
    expect(
      screen.getByRole('checkbox', {
        name: 'Old edition · psx',
      }),
    ).toBeChecked();
  });
  it('allows explicitly removing an ineligible selection', async () => {
    const change = vi.fn();
    render(
      <LibraryDiscSets
        libraryId="lib"
        value={[
          'old',
        ]}
        onChange={change}
      />,
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Choose disc sets',
      }),
    );
    fireEvent.click(
      await screen.findByRole('checkbox', {
        name: 'Old edition · psx',
      }),
    );
    await waitFor(() => expect(change).toHaveBeenCalledWith([]));
  });
  it('preserves group choices when editing rules in either title-selection mode', () => {
    const form = toFormState(null);
    form.selectedDiscSetIds = [
      'set1',
      'set2',
    ];
    expect(toConfiguration(form).selectedDiscSetIds).toEqual([
      'set1',
      'set2',
    ]);
    form.titleSelectionMode = 'IncludeOnly';
    expect(toConfiguration(form).selectedDiscSetIds).toEqual([
      'set1',
      'set2',
    ]);
  });
});
