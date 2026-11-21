import { MantineProvider } from '@mantine/core';
import type { Job, LibraryStats, ServerImportStatus, SystemResource } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render as renderBare, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, Link, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DraftGuardProvider } from '../../components/Administration/DraftGuard';
import { completePage } from '../../test/fixtures/referencePages';
import { render } from '../../test/utils/render';
import { ImportPage } from './index';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    getMe: vi.fn(),
    getRomStats: vi.fn(),
    listJobs: vi.fn(),
    getJob: vi.fn(),
    getTrackedCollectionStats: vi.fn(),
    listAdminSystems: vi.fn(),
    listImportFolders: vi.fn(),
    getServerImport: vi.fn(),
    startServerImport: vi.fn(),
  };
});

// Uploads go through the XHR progress helper, not the generated fetch client.
vi.mock('../../hooks/api/uploadWithProgress', async () => {
  const actual = await vi.importActual('../../hooks/api/uploadWithProgress');
  return {
    ...actual,
    uploadWithProgress: vi.fn(),
  };
});

import {
  getJob,
  getMe,
  getRomStats,
  getServerImport,
  getTrackedCollectionStats,
  listAdminSystems,
  listImportFolders,
  listJobs,
  startServerImport,
} from '@romd/admin-api-client';
import { uploadWithProgress } from '../../hooks/api/uploadWithProgress';

const mockGetCurrentUser = getMe as ReturnType<typeof vi.fn>;
const mockGetJobById = getJob as ReturnType<typeof vi.fn>;
const mockGetLibraryStats = getRomStats as ReturnType<typeof vi.fn>;
const mockListPlatforms = listAdminSystems as ReturnType<typeof vi.fn>;
const mockUpload = uploadWithProgress as ReturnType<typeof vi.fn>;
const permissions = vi.hoisted(() => ({
  canManageTitles: true,
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => permissions,
}));

const stats: LibraryStats = {
  totalRomFiles: '120',
  catalogedCount: '96',
  unroutedCount: '8',
  unidentifiedCount: '4',
  totalSizeBytes: '104857600',
  totalSizeOnDiskBytes: '104857600',
  compressionRatio: '1',
  bytesSaved: '0',
  platformBreakdown: [],
};

const platforms: SystemResource[] = [
  {
    key: 'plat-1',
    name: 'Super Nintendo Entertainment System',
    compactLabel: 'SNES',
    ownership: 'Romd',
    revision: 'a'.repeat(64),
    builtInVersion: 1,
    description: null,
    icon: null,
    manufacturers: [],
    retired: false,
    igdbPlatformId: '19',
  },
  {
    key: 'plat-2',
    name: 'Sega Genesis',
    compactLabel: 'Genesis',
    ownership: 'Romd',
    revision: 'a'.repeat(64),
    builtInVersion: 1,
    description: null,
    icon: null,
    manufacturers: [],
    retired: false,
    igdbPlatformId: '29',
  },
];

const recentJob: Job = {
  jobType: 'upload',
  id: 'job-1',
  correlationId: 'corr-1',
  sourceFilename: 'roms.zip',
  systemKey: null,
  phase: 'Completed',
  progressPercent: '100',
  currentItem: null,
  errors: [],
  hasErrors: false,
  createdAt: '2026-06-22T10:00:00Z',
  startedAt: '2026-06-22T10:00:05Z',
  completedAt: '2026-06-22T10:01:00Z',
  duration: '00:00:55',
  isTerminal: true,
  isArchived: false,
  datsDiscovered: '0',
  romsDiscovered: '100',
  datsProcessed: '0',
  datsSucceeded: '0',
  romsProcessed: '100',
  romsIngested: '98',
  romsDeduplicated: '1',
  romsRejected: '1',
};

const runningJob: Job = {
  ...recentJob,
  id: 'job-auto',
  sourceFilename: 'snes.dat',
  phase: 'IngestingRoms',
  progressPercent: '60',
  isTerminal: false,
  completedAt: null,
  duration: null,
};

const unconfiguredServer: ServerImportStatus = {
  directory: null,
  intervalMinutes: null,
  nextCheckAt: null,
  lastCheckedAt: null,
  lastCheckOutcome: null,
  waitingFiles: 0,
  message: null,
  lastJobId: null,
  lastJobPhase: null,
  lastQueuedFiles: 0,
  isRunning: false,
  canRetry: false,
};

function mockServerStatus(data: ServerImportStatus) {
  vi.mocked(getServerImport).mockResolvedValue({
    data,
  } as Awaited<ReturnType<typeof getServerImport>>);
}

describe('ImportPage', () => {
  beforeEach(() => {
    permissions.canManageTitles = true;
    mockServerStatus(unconfiguredServer);
    mockGetCurrentUser.mockResolvedValue({
      data: null,
      error: {
        status: 401,
      },
    });
    mockGetLibraryStats.mockResolvedValue({
      data: stats,
      error: undefined,
    });
    mockListPlatforms.mockResolvedValue({
      data: completePage(platforms),
      error: undefined,
    });
    vi.mocked(listJobs).mockImplementation(
      async (options) =>
        ({
          data: {
            items: options?.query?.correlationId
              ? []
              : [
                  recentJob,
                ],
            nextCursor: null,
            hasNextPage: false,
          },
        }) as Awaited<ReturnType<typeof listJobs>>,
    );
    mockGetJobById.mockResolvedValue({
      data: runningJob,
      error: undefined,
    });
    mockUpload.mockResolvedValue({
      jobId: 'job-auto',
    });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('renders a focused import workspace with history below staging', async () => {
    render(<ImportPage />);

    expect(
      screen.getByRole('heading', {
        name: 'Import ROMs',
      }),
    ).toBeInTheDocument();
    expect(screen.getByText('Files to import')).toBeInTheDocument();
    expect(screen.getByText('Recent imports')).toBeInTheDocument();

    expect(screen.queryByText('Stored ROMs')).not.toBeInTheDocument();
    expect(await screen.findByText('Import roms.zip')).toBeInTheDocument();
    expect(
      screen.queryByRole('tab', {
        name: 'Server folder',
      }),
    ).not.toBeInTheDocument();
  });

  it('shows the device import when a server deep link has no configured drop folder', async () => {
    vi.mocked(listImportFolders).mockResolvedValue({
      data: {
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
    } as Awaited<ReturnType<typeof listImportFolders>>);
    render(<ImportPage />, {
      routerOptions: {
        initialEntries: [
          '/roms/import?source=server',
        ],
      },
    });

    await waitFor(() => expect(getServerImport).toHaveBeenCalledOnce());
    expect(await screen.findByText('Import roms.zip')).toBeInTheDocument();
    expect(screen.getByText('Files to import')).toBeVisible();
    expect(
      screen.queryByRole('tablist', {
        name: 'Import source',
      }),
    ).not.toBeInTheDocument();
    expect(listImportFolders).not.toHaveBeenCalled();
  });

  it('can retry a failed folder status check before showing the server tab', async () => {
    const user = userEvent.setup();
    mockServerStatus({
      ...unconfiguredServer,
      directory: '/incoming',
    });
    vi.mocked(getServerImport).mockResolvedValueOnce({
      error: {
        title: 'Service unavailable',
        status: 503,
      },
    } as Awaited<ReturnType<typeof getServerImport>>);
    render(<ImportPage />);

    const retry = await screen.findByRole('button', {
      name: 'Retry folder status',
    });
    expect(
      screen.queryByRole('tab', {
        name: 'Server folder',
      }),
    ).not.toBeInTheDocument();
    expect(screen.getByText('Files to import')).toBeVisible();

    await user.click(retry);

    expect(
      await screen.findByRole('tab', {
        name: 'Server folder',
      }),
    ).toBeVisible();
    expect(retry).not.toBeInTheDocument();
  });

  it('does not look up the server drop folder without the Manager role', async () => {
    permissions.canManageTitles = false;
    render(<ImportPage />);

    expect(await screen.findByText('Import roms.zip')).toBeInTheDocument();
    expect(
      screen.queryByRole('tab', {
        name: 'Server folder',
      }),
    ).not.toBeInTheDocument();
    expect(listImportFolders).not.toHaveBeenCalled();
    expect(getServerImport).not.toHaveBeenCalled();
  });

  it('checks the configured folder in one step and preserves the last receipt on an idle result', async () => {
    const user = userEvent.setup();
    const lastJobId = '2fa2e739-af69-404d-83af-43262ab28d46';
    const profile = {
      ...unconfiguredServer,
      directory: '/incoming',
      intervalMinutes: 15,
      nextCheckAt: '2026-11-20T12:15:00Z',
      lastJobId,
      lastJobPhase: 'Completed',
      lastQueuedFiles: 3,
    };
    mockServerStatus(profile);
    vi.mocked(startServerImport).mockResolvedValue({
      data: {
        ...profile,
        lastCheckOutcome: 'NoNewFiles',
        lastCheckedAt: '2026-11-20T12:00:00Z',
      },
    } as Awaited<ReturnType<typeof startServerImport>>);
    render(<ImportPage />, {
      routerOptions: {
        initialEntries: [
          '/roms/import?source=server',
        ],
      },
    });
    expect(
      await screen.findByRole('heading', {
        name: 'Server import folder',
      }),
    ).toBeVisible();

    expect(screen.getByText('/incoming')).toBeVisible();
    expect(screen.getByText(/removes files from this folder after storing them/)).toBeVisible();
    expect(screen.getByText(/Checks every 15 minutes/)).toBeVisible();
    expect(
      screen.queryByRole('button', {
        name: 'Browse server folders',
      }),
    ).not.toBeInTheDocument();
    expect(listImportFolders).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Import from server folder',
      }),
    );

    expect(await screen.findByText('No new files')).toBeInTheDocument();
    expect(startServerImport).toHaveBeenCalledWith(
      expect.objectContaining({
        body: {},
      }),
    );
    expect(
      screen.getByRole('link', {
        name: 'View last import',
      }),
    ).toHaveAttribute('href', `/jobs/${lastJobId}#import-results`);
  });

  it('retries the displayed failed job and opens the resulting import', async () => {
    const user = userEvent.setup();
    const lastJobId = '2fa2e739-af69-404d-83af-43262ab28d46';
    const jobId = '0fb66e70-36b7-4869-9b81-9f7d0db819b8';
    mockServerStatus({
      ...unconfiguredServer,
      directory: '/incoming',
      lastJobId,
      lastJobPhase: 'Failed',
      canRetry: true,
    });
    vi.mocked(startServerImport).mockResolvedValue({
      data: {
        jobId,
      },
    } as Awaited<ReturnType<typeof startServerImport>>);
    render(<ImportPage />);
    await user.click(
      await screen.findByRole('tab', {
        name: 'Server folder',
      }),
    );

    await user.click(
      screen.getByRole('button', {
        name: 'Retry last import',
      }),
    );

    expect(await screen.findByText('Running')).toBeInTheDocument();
    expect(startServerImport).toHaveBeenCalledWith({
      body: {
        retryJobId: lastJobId,
      },
    });
    expect(getJob).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          jobId,
        },
      }),
    );
  });

  it('keeps the device draft while switching to the configured server folder', async () => {
    const user = userEvent.setup();
    mockServerStatus({
      ...unconfiguredServer,
      directory: '/incoming',
    });
    render(<ImportPage />);

    const server = await screen.findByRole('tab', {
      name: 'Server folder',
    });
    await user.upload(
      screen.getByLabelText('file upload'),
      new File(
        [
          'content',
        ],
        'queued.gb',
      ),
    );
    await user.click(
      screen.getByRole('checkbox', {
        name: 'Keep unmatched files',
      }),
    );

    await user.click(server);
    expect(screen.getByText('/incoming')).toBeVisible();
    expect(
      screen.queryByRole('table', {
        name: 'Staged files',
      }),
    ).not.toBeInTheDocument();

    await user.click(
      screen.getByRole('tab', {
        name: 'This device',
      }),
    );
    expect(
      screen.getByRole('table', {
        name: 'Staged files',
      }),
    ).toHaveTextContent('queued.gb');
    expect(
      screen.getByRole('checkbox', {
        name: 'Keep unmatched files',
      }),
    ).toBeChecked();
    expect(mockUpload).not.toHaveBeenCalled();
    expect(startServerImport).not.toHaveBeenCalled();
  });

  it('removes invalid files together and previews a selection that runs as separate jobs', async () => {
    const user = userEvent.setup();
    render(<ImportPage />);

    const fileInput = screen.getByLabelText('file upload');
    const openPicker = vi.spyOn(fileInput, 'click');
    await user.click(
      screen.getByRole('button', {
        name: 'Browse files',
      }),
    );
    expect(openPicker).toHaveBeenCalledOnce();
    openPicker.mockRestore();

    await user.upload(fileInput, [
      new File(
        [
          'content',
        ],
        'games.zip',
      ),
      new File(
        [
          'content',
        ],
        'title.gb',
      ),
      new File([], 'empty.dat'),
    ]);

    const start = await screen.findByRole('button', {
      name: /Start import/,
    });
    expect(start).toBeDisabled();
    expect(screen.getByText('Empty file')).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Remove 1 invalid file',
      }),
    );
    expect(screen.queryByText('empty.dat')).not.toBeInTheDocument();
    expect(screen.getByText(/each file will run as its own import/i)).toHaveTextContent('2 jobs');
    expect(start).toBeEnabled();
    expect(mockUpload).not.toHaveBeenCalled();
  });

  it('stages a DAT, routes it to the chosen system, and starts a single import', async () => {
    const user = userEvent.setup();
    render(<ImportPage />);

    const file = new File(
      [
        '<datafile />',
      ],
      'snes.dat',
      {
        type: 'text/xml',
      },
    );
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [
          file,
        ],
        items: [
          {
            kind: 'file',
            type: file.type,
            getAsFile: () => file,
          },
        ],
        types: [
          'Files',
        ],
      },
    });

    // Staging preview + the conditional system selector (DATs present).
    expect(await screen.findByText('snes.dat')).toBeInTheDocument();
    expect(screen.getByText('DAT catalog')).toBeInTheDocument();

    await user.click(await screen.findByPlaceholderText('Let ROMD route by header'));
    const option = await screen.findByRole('option', {
      name: 'Super Nintendo Entertainment System (SNES)',
      hidden: true,
    });
    fireEvent.click(option);

    await user.click(
      screen.getByRole('button', {
        name: /Start import/,
      }),
    );

    await waitFor(() => {
      expect(mockUpload).toHaveBeenCalledWith(
        '/api/upload',
        file,
        expect.objectContaining({
          query: expect.objectContaining({
            systemKey: 'plat-1',
            allowUnidentified: undefined,
            archiveOnly: 'false',
            trackedOnly: undefined,
          }),
        }),
      );
    });

    // The started job surfaces as a live card.
    expect(await screen.findByText('Running')).toBeInTheDocument();
  });

  it('keeps only tracked-title ROMs without tracking or retaining unmatched files', async () => {
    const user = userEvent.setup();
    render(<ImportPage />);
    const file = new File(
      [
        'rom',
      ],
      'game.sfc',
      {
        type: 'application/octet-stream',
      },
    );
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [
          file,
        ],
        items: [
          {
            kind: 'file',
            type: file.type,
            getAsFile: () => file,
          },
        ],
        types: [
          'Files',
        ],
      },
    });
    await user.click(
      await screen.findByRole('checkbox', {
        name: 'Keep unmatched files',
      }),
    );
    await user.click(
      screen.getByLabelText('ROMs to keep', {
        selector: 'input',
      }),
    );
    await user.click(
      await screen.findByRole('option', {
        name: 'Tracked titles only',
      }),
    );
    expect(
      screen.queryByRole('checkbox', {
        name: 'Track matched titles',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('checkbox', {
        name: 'Keep unmatched files',
      }),
    ).not.toBeInTheDocument();
    vi.mocked(getTrackedCollectionStats).mockResolvedValue({
      data: {
        trackedTitleCount: '1',
      },
    } as Awaited<ReturnType<typeof getTrackedCollectionStats>>);
    await user.click(
      screen.getByRole('button', {
        name: /Start import/,
      }),
    );
    await waitFor(() =>
      expect(mockUpload).toHaveBeenCalledWith(
        '/api/upload',
        file,
        expect.objectContaining({
          query: expect.objectContaining({
            trackedOnly: 'true',
            archiveOnly: 'true',
            allowUnidentified: undefined,
          }),
        }),
      ),
    );
  });

  it('stops a tracked-only upload before transferring bytes when no titles are tracked', async () => {
    const user = userEvent.setup();
    render(<ImportPage />);
    const file = new File(
      [
        'rom',
      ],
      'game.sfc',
      {
        type: 'application/octet-stream',
      },
    );
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [
          file,
        ],
        items: [
          {
            kind: 'file',
            type: file.type,
            getAsFile: () => file,
          },
        ],
        types: [
          'Files',
        ],
      },
    });
    await user.click(
      await screen.findByLabelText('ROMs to keep', {
        selector: 'input',
      }),
    );
    await user.click(
      await screen.findByRole('option', {
        name: 'Tracked titles only',
      }),
    );
    vi.mocked(getTrackedCollectionStats).mockResolvedValue({
      data: {
        trackedTitleCount: '0',
      },
    } as Awaited<ReturnType<typeof getTrackedCollectionStats>>);
    await user.click(
      screen.getByRole('button', {
        name: /Start import/,
      }),
    );
    expect(
      await screen.findByText(/Track at least one title before importing/),
    ).toBeInTheDocument();
    expect(mockUpload).not.toHaveBeenCalled();
  });

  it('imports archive-only when matched-title tracking is unchecked without changing unidentified handling', async () => {
    const user = userEvent.setup();
    render(<ImportPage />);

    const file = new File(
      [
        'rom',
      ],
      'game.sfc',
      {
        type: 'application/octet-stream',
      },
    );
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [
          file,
        ],
        items: [
          {
            kind: 'file',
            type: file.type,
            getAsFile: () => file,
          },
        ],
        types: [
          'Files',
        ],
      },
    });

    const trackingCheckbox = await screen.findByRole('checkbox', {
      name: 'Track matched titles',
    });
    expect(trackingCheckbox).toBeChecked();

    await user.click(trackingCheckbox);
    await user.click(
      screen.getByRole('checkbox', {
        name: 'Keep unmatched files',
      }),
    );
    await user.click(
      screen.getByRole('button', {
        name: /Start import/,
      }),
    );

    await waitFor(() => {
      expect(mockUpload).toHaveBeenCalledWith(
        '/api/upload',
        file,
        expect.objectContaining({
          query: expect.objectContaining({
            systemKey: undefined,
            allowUnidentified: 'true',
            archiveOnly: 'true',
            trackedOnly: undefined,
          }),
        }),
      );
    });

    await waitFor(() => {
      expect(
        screen.queryByRole('checkbox', {
          name: 'Track matched titles',
        }),
      ).not.toBeInTheDocument();
    });

    const nextFile = new File(
      [
        'rom-2',
      ],
      'next-game.sfc',
      {
        type: 'application/octet-stream',
      },
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Import more files',
      }),
    );
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [
          nextFile,
        ],
        items: [
          {
            kind: 'file',
            type: nextFile.type,
            getAsFile: () => nextFile,
          },
        ],
        types: [
          'Files',
        ],
      },
    });

    expect(
      await screen.findByRole('checkbox', {
        name: 'Track matched titles',
      }),
    ).toBeChecked();
  });

  it('restores matched-title tracking after clearing a staged archive-only import', async () => {
    const user = userEvent.setup();
    render(<ImportPage />);

    const firstFile = new File(
      [
        'rom',
      ],
      'first-game.sfc',
      {
        type: 'application/octet-stream',
      },
    );
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [
          firstFile,
        ],
        items: [
          {
            kind: 'file',
            type: firstFile.type,
            getAsFile: () => firstFile,
          },
        ],
        types: [
          'Files',
        ],
      },
    });

    const trackingCheckbox = await screen.findByRole('checkbox', {
      name: 'Track matched titles',
    });
    await user.click(trackingCheckbox);
    expect(trackingCheckbox).not.toBeChecked();
    await user.click(
      screen.getByRole('button', {
        name: 'Clear',
      }),
    );

    const nextFile = new File(
      [
        'rom-2',
      ],
      'next-game.sfc',
      {
        type: 'application/octet-stream',
      },
    );
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [
          nextFile,
        ],
        items: [
          {
            kind: 'file',
            type: nextFile.type,
            getAsFile: () => nextFile,
          },
        ],
        types: [
          'Files',
        ],
      },
    });

    expect(
      await screen.findByRole('checkbox', {
        name: 'Track matched titles',
      }),
    ).toBeChecked();
  });

  it('preserves accepted uploads and retries a failed archive with the same identity', async () => {
    const user = userEvent.setup();
    mockUpload
      .mockResolvedValueOnce({
        jobId: 'first-job',
      })
      .mockRejectedValueOnce(new Error('Connection interrupted'))
      .mockResolvedValueOnce({
        jobId: 'second-job',
      });
    render(<ImportPage />);
    const files = [
      new File(
        [
          'zip',
        ],
        'first.zip',
      ),
      new File(
        [
          'zip',
        ],
        'second.zip',
      ),
    ];
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files,
        items: files.map((file) => ({
          kind: 'file',
          type: file.type,
          getAsFile: () => file,
        })),
        types: [
          'Files',
        ],
      },
    });
    await user.click(
      await screen.findByRole('button', {
        name: /Start import/,
      }),
    );
    expect(await screen.findByText('Upload interrupted')).toBeInTheDocument();
    expect(mockGetJobById).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          jobId: 'first-job',
        },
      }),
    );
    const failedQuery = mockUpload.mock.calls[1][2].query;
    await user.click(
      screen.getByRole('button', {
        name: 'Retry remaining uploads',
      }),
    );
    await waitFor(() => expect(mockUpload).toHaveBeenCalledTimes(3));
    expect(mockUpload.mock.calls[2][2].query).toEqual(failedQuery);
    expect(mockUpload.mock.calls[2][1].name).toBe('second.zip');
  });

  it('recovers accepted imports from the URL after refresh', async () => {
    const batchId = 'ae5f342d-04cc-4672-8cd2-c51650871ddf';
    vi.mocked(listJobs).mockImplementation(
      async (options) =>
        ({
          data: {
            items: options?.query?.correlationId
              ? [
                  runningJob,
                ]
              : [
                  recentJob,
                ],
            nextCursor: null,
            hasNextPage: false,
          },
        }) as Awaited<ReturnType<typeof listJobs>>,
    );
    render(<ImportPage />, {
      routerOptions: {
        initialEntries: [
          `/roms/import?batch=${batchId}`,
        ],
      },
    });
    expect(await screen.findByText('Running')).toBeInTheDocument();
    expect(listJobs).toHaveBeenCalledWith(
      expect.objectContaining({
        query: {
          correlationId: batchId,
          jobType: 'upload',
          archive: 'all',
          limit: 25,
          cursor: undefined,
        },
      }),
    );
  });

  it('protects staged browser files when leaving the import route', async () => {
    const user = userEvent.setup();
    const router = createMemoryRouter(
      [
        {
          path: '/roms/import',
          element: (
            <DraftGuardProvider>
              <Link to="/other">Leave import</Link>
              <ImportPage />
            </DraftGuardProvider>
          ),
        },
        {
          path: '/other',
          element: <h1>Other page</h1>,
        },
      ],
      {
        initialEntries: [
          '/roms/import',
        ],
      },
    );
    const cache = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    renderBare(
      <MantineProvider env="test">
        <QueryClientProvider client={cache}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </MantineProvider>,
    );
    const file = new File(
      [
        'fixture data',
      ],
      'staged.bin',
    );
    await user.upload(screen.getByLabelText('file upload'), file);
    expect(await screen.findByText('staged.bin')).toBeInTheDocument();
    await user.click(
      screen.getByRole('link', {
        name: 'Leave import',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(screen.getByText('staged.bin')).toBeInTheDocument();
    await user.click(
      screen.getByRole('link', {
        name: 'Leave import',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Discard',
      }),
    );
    expect(
      await screen.findByRole('heading', {
        name: 'Other page',
      }),
    ).toBeInTheDocument();
  });
});
