import type { DiscSet } from '@romd/admin-api-client';
import {
  getLibraryDiscSetOptions,
  listAdminDiscSets,
  updateAdminDiscSet,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { libraryManagementKeys } from '../../hooks/api/useLibraryManagement';
import { render } from '../../test/utils/render';
import { LibraryDiscSets } from '../Libraries/LibraryDiscSets';
import { DiscSetsPanel } from './DiscSetsPanel';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  listAdminDiscSets: vi.fn(),
  updateAdminDiscSet: vi.fn(),
  getLibraryDiscSetOptions: vi.fn(),
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    hasRole: () => true,
  }),
}));
const page = <T,>(items: T[]) => ({
  items,
  nextCursor: null,
  hasNextPage: false,
});
const set: DiscSet = {
  id: 'set-1',
  catalogSourceId: 'source-1',
  systemKey: 'psx',
  revision: 'revision-1',
  decision: 'proposed',
  correction: null,
  decidedBy: null,
  decidedAt: null,
  reason: null,
  proposal: {
    name: 'Game (USA)',
    expectedDiscCount: null,
    reviewReasons: [],
    ruleVersion: 'v1',
    members: [
      {
        name: 'Game (USA) (Disc 1)',
        entryKey: 'disc-1',
        ordinal: 1,
        releaseId: 'r1',
        issue: null,
      },
      {
        name: 'Game (USA) (Disc 2)',
        entryKey: 'disc-2',
        ordinal: 2,
        releaseId: 'r2',
        issue: null,
      },
    ],
  },
};
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(listAdminDiscSets).mockResolvedValue({
    data: page([
      set,
    ]),
    error: undefined,
    request: new Request('http://localhost'),
    response: new Response(),
  });
  vi.mocked(updateAdminDiscSet).mockResolvedValue({
    data: set,
    error: undefined,
    request: new Request('http://localhost'),
    response: new Response(),
  });
});
describe('DiscSetsPanel', () => {
  it('continues an empty first page with the server cursor before declaring that no proposals exist', async () => {
    vi.mocked(listAdminDiscSets).mockResolvedValueOnce({
      data: {
        items: [],
        nextCursor: 'continued',
        hasNextPage: true,
      },
      error: undefined,
      request: new Request('http://localhost'),
      response: new Response(),
    });
    render(<DiscSetsPanel titleId="title" />);
    expect(await screen.findByText(/Expected: unknown/)).toBeInTheDocument();
    expect(listAdminDiscSets).toHaveBeenNthCalledWith(
      2,
      expect.objectContaining({
        query: {
          titleId: 'title',
          cursor: 'continued',
          limit: 100,
        },
      }),
    );
    expect(screen.queryByText(/No disc proposals/)).not.toBeInTheDocument();
  });

  it('refreshes Library choices after a grouping decision without changing draft choices', async () => {
    const client = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
          staleTime: Infinity,
        },
      },
    });
    const stale = {
      selectedIds: [],
      selected: [],
      page: page([]),
    };
    client.setQueryData(libraryManagementKeys.discSetOptionsFor('other'), stale);
    vi.mocked(getLibraryDiscSetOptions)
      .mockResolvedValueOnce({
        data: stale,
        error: undefined,
        request: new Request('http://localhost'),
        response: new Response(),
      })
      .mockResolvedValue({
        data: {
          ...stale,
          page: page([
            {
              id: set.id,
              name: 'Game (USA)',
              titleId: 'title',
              systemKey: 'psx',
              memberCount: 2,
              expectedDiscCount: null,
              isEligible: true,
              issue: null,
            },
          ]),
        },
        error: undefined,
        request: new Request('http://localhost'),
        response: new Response(),
      });
    const change = vi.fn();
    render(
      <QueryClientProvider client={client}>
        <LibraryDiscSets
          libraryId="lib"
          value={[
            'dirty',
          ]}
          onChange={change}
        />
        <DiscSetsPanel titleId="title" />
      </QueryClientProvider>,
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Choose disc sets',
      }),
    );
    await waitFor(() => expect(getLibraryDiscSetOptions).toHaveBeenCalledTimes(1));
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Accept proposal',
      }),
    );
    expect(
      await screen.findByRole('checkbox', {
        name: 'Game (USA) · psx',
      }),
    ).toBeEnabled();
    expect(getLibraryDiscSetOptions).toHaveBeenCalledTimes(2);
    expect(
      client.getQueryState(libraryManagementKeys.discSetOptionsFor('other'))?.isInvalidated,
    ).toBe(true);
    expect(
      screen.getByText('Selected set dirty: details not loaded. This choice will be preserved.'),
    ).toBeInTheDocument();
    expect(change).not.toHaveBeenCalled();
  });

  it('shows unknown total and accepts a proposal with its revision', async () => {
    render(<DiscSetsPanel titleId="title" />);
    expect(await screen.findByText(/Expected: unknown/)).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Accept proposal',
      }),
    );
    await waitFor(() =>
      expect(updateAdminDiscSet).toHaveBeenCalledWith(
        expect.objectContaining({
          headers: {
            'If-Match': '"revision-1"',
          },
          body: expect.objectContaining({
            decision: 'accept',
            expectedDiscCount: null,
          }),
        }),
      ),
    );
  });
  it('persists reordered membership as a correction', async () => {
    render(<DiscSetsPanel titleId="title" />);
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Correct membership and order',
      }),
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Move member 2 up',
      }),
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Save correction',
      }),
    );
    await waitFor(() =>
      expect(updateAdminDiscSet).toHaveBeenCalledWith(
        expect.objectContaining({
          body: expect.objectContaining({
            decision: 'correct',
            members: [
              {
                entryKey: 'disc-2',
                ordinal: 1,
              },
              {
                entryKey: 'disc-1',
                ordinal: 2,
              },
            ],
          }),
        }),
      ),
    );
  });
  it('keeps review issues visible and requires correction before acceptance', async () => {
    vi.mocked(listAdminDiscSets).mockResolvedValue({
      data: page([
        {
          ...set,
          proposal: {
            ...set.proposal,
            reviewReasons: [
              'DuplicateDiscNumber',
            ],
          },
        },
      ]),
      error: undefined,
      request: new Request('http://localhost'),
      response: new Response(),
    });
    render(<DiscSetsPanel titleId="title" />);
    expect(await screen.findByText('Duplicate disc number')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Accept proposal',
      }),
    ).toBeDisabled();
  });
  it('reports stale correction conflicts without discarding the editor', async () => {
    vi.mocked(updateAdminDiscSet).mockResolvedValue({
      data: undefined,
      error: {
        title: 'Conflict',
      },
      request: new Request('http://localhost'),
      response: new Response(null, {
        status: 412,
      }),
    });
    render(<DiscSetsPanel titleId="title" />);
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Reject set',
      }),
    );
    expect(
      await screen.findByText('This set changed. Reload before saving your correction.'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Reload',
      }),
    ).toBeInTheDocument();
  });
});
