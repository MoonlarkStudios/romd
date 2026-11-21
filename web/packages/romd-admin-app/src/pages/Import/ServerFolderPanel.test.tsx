import type { ImportFolderListing } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { ServerFolderPanel } from './ServerFolderPanel';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    listImportFolders: vi.fn(),
    importFromPath: vi.fn(),
    getTrackedCollectionStats: vi.fn(),
  };
});

const permissions = vi.hoisted(() => ({
  canManageTitles: true,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => permissions,
}));

import { importFromPath, listImportFolders } from '@romd/admin-api-client';

type Listed = Awaited<ReturnType<typeof listImportFolders>>;
type Started = Awaited<ReturnType<typeof importFromPath>>;

const folders: Record<string, ImportFolderListing> = {
  '': {
    enabled: true,
    folders: [
      {
        name: '/library',
        path: '/library',
      },
      {
        name: '/archive',
        path: '/archive',
      },
    ],
    fileCount: 0,
    truncated: false,
  },
  '/library': {
    enabled: true,
    path: '/library',
    parentPath: null,
    folders: [
      {
        name: 'psx',
        path: '/library/psx',
      },
    ],
    fileCount: 0,
    truncated: false,
  },
  '/library/psx': {
    enabled: true,
    path: '/library/psx',
    parentPath: '/library',
    folders: [],
    fileCount: 12,
    truncated: false,
  },
};

function serve(listings: Record<string, ImportFolderListing>) {
  vi.mocked(listImportFolders).mockImplementation(
    async (options) =>
      ({
        data: listings[options?.query?.path ?? ''],
        error: undefined,
      }) as Listed,
  );
}

describe('ServerFolderPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    permissions.canManageTitles = true;
    serve(folders);
  });

  it('stays hidden when the operator has not enabled path import', async () => {
    serve({
      '': {
        enabled: false,
        folders: [],
        fileCount: 0,
        truncated: false,
      },
    });

    render(
      <ServerFolderPanel
        disabled={false}
        onStarted={vi.fn()}
      />,
    );

    await waitFor(() => expect(listImportFolders).toHaveBeenCalledTimes(1));
    expect(
      screen.queryByRole('heading', {
        name: 'Import from a server folder',
      }),
    ).not.toBeInTheDocument();
  });

  it('never asks the server for folders without the Manager role', () => {
    permissions.canManageTitles = false;

    render(
      <ServerFolderPanel
        disabled={false}
        onStarted={vi.fn()}
      />,
    );

    expect(
      screen.queryByRole('heading', {
        name: 'Import from a server folder',
      }),
    ).not.toBeInTheDocument();
    expect(listImportFolders).not.toHaveBeenCalled();
  });

  it('browses from the allowed roots and copies the chosen folder without moving it', async () => {
    const user = userEvent.setup();
    const onStarted = vi.fn();
    vi.mocked(importFromPath).mockResolvedValue({
      data: {
        jobId: 'job-9',
      },
      error: undefined,
    } as Started);
    vi.mocked(importFromPath).mockResolvedValueOnce({
      error: {
        detail: 'The response was interrupted.',
      },
    } as never);
    render(
      <ServerFolderPanel
        disabled={false}
        onStarted={onStarted}
      />,
    );

    expect(await screen.findByText('/archive')).toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: 'Import this folder',
      }),
    ).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    await user.click(screen.getByText('/library'));
    await user.click(await screen.findByText('psx'));

    expect(await screen.findByText(/12 files directly in this folder/)).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Import this folder',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Try again',
      }),
    );

    await waitFor(() => expect(onStarted).toHaveBeenCalledWith('job-9', 'psx'));
    expect(importFromPath).toHaveBeenCalledTimes(2);
    expect(vi.mocked(importFromPath).mock.calls[0][0]?.body.requestId).toBe(
      vi.mocked(importFromPath).mock.calls[1][0]?.body.requestId,
    );
    expect(importFromPath).toHaveBeenCalledWith({
      body: {
        path: '/library/psx',
        requestId: expect.any(String),
        move: false,
        allowUnidentified: false,
        archiveOnly: false,
        trackedOnly: false,
      },
    });
  });

  it('opens a single allowed root directly and returns up through parents', async () => {
    const user = userEvent.setup();
    serve({
      ...folders,
      '': {
        enabled: true,
        folders: [
          {
            name: '/library',
            path: '/library',
          },
        ],
        fileCount: 0,
        truncated: false,
      },
    });
    render(
      <ServerFolderPanel
        disabled={false}
        onStarted={vi.fn()}
      />,
    );

    await user.click(await screen.findByText('psx'));
    expect(await screen.findByText(/12 files/)).toBeInTheDocument();
    const up = screen.getByRole('button', {
      name: 'Up one folder',
    });
    await user.click(up);

    expect(await screen.findByText('psx')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Up one folder',
      }),
    ).toBeDisabled();
  });

  it('shows the server refusal instead of listing a folder', async () => {
    const user = userEvent.setup();
    vi.mocked(listImportFolders).mockImplementation(async (options) =>
      options?.query?.path
        ? ({
            data: undefined,
            error: {
              detail: "Import path contains a symbolic link: '/library'",
            },
          } as Listed)
        : ({
            data: folders[''],
            error: undefined,
          } as Listed),
    );
    render(
      <ServerFolderPanel
        disabled={false}
        onStarted={vi.fn()}
      />,
    );

    await user.click(await screen.findByText('/library'));

    expect(await screen.findByText(/contains a symbolic link/)).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Back to allowed folders',
      }),
    );
    expect(await screen.findByText('/archive')).toBeInTheDocument();
  });
});
