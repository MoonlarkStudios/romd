import type { JobDto, LibraryStats, SystemResourceDto } from '@romd/admin-api-client';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { ImportPage } from './index';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    getCurrentUser: vi.fn(),
    getLibraryStats: vi.fn(),
    getRecentJobs: vi.fn(),
    getJobById: vi.fn(),
    getImportBatch: vi.fn(),
    getTrackedCollectionStats: vi.fn(),
    listAdminSystems: vi.fn(),
  };
});

// Uploads go through the XHR progress helper, not the generated fetch client.
vi.mock('../../hooks/api/uploadWithProgress', async () => {
  const actual = await vi.importActual('../../hooks/api/uploadWithProgress');
  return { ...actual, uploadWithProgress: vi.fn() };
});

import {
  getCurrentUser,
  getImportBatch,
  getJobById,
  getLibraryStats,
  getRecentJobs,
  getTrackedCollectionStats,
  listAdminSystems,
} from '@romd/admin-api-client';
import { uploadWithProgress } from '../../hooks/api/uploadWithProgress';

const mockGetCurrentUser = getCurrentUser as ReturnType<typeof vi.fn>;
const mockGetJobById = getJobById as ReturnType<typeof vi.fn>;
const mockGetLibraryStats = getLibraryStats as ReturnType<typeof vi.fn>;
const mockGetRecentJobs = getRecentJobs as ReturnType<typeof vi.fn>;
const mockListPlatforms = listAdminSystems as ReturnType<typeof vi.fn>;
const mockUpload = uploadWithProgress as ReturnType<typeof vi.fn>;
vi.mock('../../hooks/usePermissions', () => ({ usePermissions: () => ({ canManageTitles: true }) }));

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

const platforms: SystemResourceDto[] = [
  {
    key: 'plat-1',
    name: 'Super Nintendo Entertainment System',
    compactLabel: 'SNES',
    ownership: 'Romd', builtInVersion: 1, description: null, icon: null, manufacturers: [], retired: false,
    igdbPlatformId: '19',
  },
  {
    key: 'plat-2',
    name: 'Sega Genesis',
    compactLabel: 'Genesis',
    ownership: 'Romd', builtInVersion: 1, description: null, icon: null, manufacturers: [], retired: false,
    igdbPlatformId: '29',
  },
];

const recentJob: JobDto = {
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

const runningJob: JobDto = {
  ...recentJob,
  id: 'job-auto',
  sourceFilename: 'snes.dat',
  phase: 'IngestingRoms',
  progressPercent: '60',
  isTerminal: false,
  completedAt: null,
  duration: null,
};

describe('ImportPage', () => {
  beforeEach(() => {
    vi.mocked(getImportBatch).mockResolvedValue({ data: [] } as never);
    mockGetCurrentUser.mockResolvedValue({ data: null, error: { status: 401 } });
    mockGetLibraryStats.mockResolvedValue({ data: stats, error: undefined });
    mockListPlatforms.mockResolvedValue({ data: platforms, error: undefined });
    mockGetRecentJobs.mockResolvedValue({ data: [recentJob], error: undefined });
    mockGetJobById.mockResolvedValue({ data: runningJob, error: undefined });
    mockUpload.mockResolvedValue({
      jobId: 'job-auto',
      backgroundJobId: 'bg-auto',
      statusUrl: '/api/jobs/job-auto',
    });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('renders a focused import workspace with history below staging', async () => {
    render(<ImportPage />);

    expect(screen.getByRole('heading', { name: 'Import ROMs' })).toBeInTheDocument();
    expect(screen.getByText('Files to import')).toBeInTheDocument();
    expect(screen.getByText('Recent imports')).toBeInTheDocument();

    expect(screen.queryByText('Stored ROMs')).not.toBeInTheDocument();
    expect(await screen.findByText('Import roms.zip')).toBeInTheDocument();
  });

  it('stages a DAT, routes it to the chosen system, and starts a single import', async () => {
    const user = userEvent.setup();
    render(<ImportPage />);

    const file = new File(['<datafile />'], 'snes.dat', { type: 'text/xml' });
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [file],
        items: [{ kind: 'file', type: file.type, getAsFile: () => file }],
        types: ['Files'],
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

    await user.click(screen.getByRole('button', { name: /Start import/ }));

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
    const file = new File(['rom'], 'game.sfc', { type: 'application/octet-stream' });
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: { files: [file], items: [{ kind: 'file', type: file.type, getAsFile: () => file }], types: ['Files'] },
    });
    await user.click(await screen.findByRole('checkbox', { name: 'Keep unmatched files' }));
    await user.click(screen.getByLabelText('ROMs to keep', { selector: 'input' }));
    await user.click(await screen.findByRole('option', { name: 'Tracked titles only' }));
    expect(screen.getByRole('checkbox', { name: 'Track matched titles' })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: 'Keep unmatched files' })).not.toBeChecked();
    vi.mocked(getTrackedCollectionStats).mockResolvedValue({ data: { trackedTitleCount: '1' } } as Awaited<ReturnType<typeof getTrackedCollectionStats>>);
    await user.click(screen.getByRole('button', { name: /Start import/ }));
    await waitFor(() => expect(mockUpload).toHaveBeenCalledWith('/api/upload', file,
      expect.objectContaining({ query: expect.objectContaining({ trackedOnly: 'true', archiveOnly: 'true', allowUnidentified: undefined }) })));
  });

  it('stops a tracked-only upload before transferring bytes when no titles are tracked', async () => {
    const user = userEvent.setup();
    render(<ImportPage />);
    const file = new File(['rom'], 'game.sfc', { type: 'application/octet-stream' });
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: { files: [file], items: [{ kind: 'file', type: file.type, getAsFile: () => file }], types: ['Files'] },
    });
    await user.click(await screen.findByLabelText('ROMs to keep', { selector: 'input' }));
    await user.click(await screen.findByRole('option', { name: 'Tracked titles only' }));
    vi.mocked(getTrackedCollectionStats).mockResolvedValue({ data: { trackedTitleCount: '0' } } as Awaited<ReturnType<typeof getTrackedCollectionStats>>);
    await user.click(screen.getByRole('button', { name: /Start import/ }));
    expect(await screen.findByText(/Track at least one title before importing/)).toBeInTheDocument();
    expect(mockUpload).not.toHaveBeenCalled();
  });

  it('imports archive-only when matched-title tracking is unchecked without changing unidentified handling', async () => {
    const user = userEvent.setup();
    render(<ImportPage />);

    const file = new File(['rom'], 'game.sfc', { type: 'application/octet-stream' });
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [file],
        items: [{ kind: 'file', type: file.type, getAsFile: () => file }],
        types: ['Files'],
      },
    });

    const trackingCheckbox = await screen.findByRole('checkbox', {
      name: 'Track matched titles',
    });
    expect(trackingCheckbox).toBeChecked();

    await user.click(trackingCheckbox);
    await user.click(screen.getByRole('checkbox', { name: 'Keep unmatched files' }));
    await user.click(screen.getByRole('button', { name: /Start import/ }));

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
        screen.queryByRole('checkbox', { name: 'Track matched titles' }),
      ).not.toBeInTheDocument();
    });

    const nextFile = new File(['rom-2'], 'next-game.sfc', {
      type: 'application/octet-stream',
    });
    await user.click(screen.getByRole('button', { name: 'Import more files' }));
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [nextFile],
        items: [{ kind: 'file', type: nextFile.type, getAsFile: () => nextFile }],
        types: ['Files'],
      },
    });

    expect(
      await screen.findByRole('checkbox', { name: 'Track matched titles' }),
    ).toBeChecked();
  });

  it('restores matched-title tracking after clearing a staged archive-only import', async () => {
    const user = userEvent.setup();
    render(<ImportPage />);

    const firstFile = new File(['rom'], 'first-game.sfc', {
      type: 'application/octet-stream',
    });
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [firstFile],
        items: [{ kind: 'file', type: firstFile.type, getAsFile: () => firstFile }],
        types: ['Files'],
      },
    });

    const trackingCheckbox = await screen.findByRole('checkbox', {
      name: 'Track matched titles',
    });
    await user.click(trackingCheckbox);
    expect(trackingCheckbox).not.toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Clear' }));

    const nextFile = new File(['rom-2'], 'next-game.sfc', {
      type: 'application/octet-stream',
    });
    fireEvent.drop(screen.getByTestId('import-dropzone'), {
      dataTransfer: {
        files: [nextFile],
        items: [{ kind: 'file', type: nextFile.type, getAsFile: () => nextFile }],
        types: ['Files'],
      },
    });

    expect(
      await screen.findByRole('checkbox', { name: 'Track matched titles' }),
    ).toBeChecked();
  });

  it('preserves accepted uploads and retries a failed archive with the same identity', async () => {
    const user = userEvent.setup();
    mockUpload.mockResolvedValueOnce({ jobId: 'first-job' }).mockRejectedValueOnce(new Error('Connection interrupted'))
      .mockResolvedValueOnce({ jobId: 'second-job' });
    render(<ImportPage />);
    const files = [new File(['zip'], 'first.zip'), new File(['zip'], 'second.zip')];
    fireEvent.drop(screen.getByTestId('import-dropzone'), { dataTransfer: { files, items: files.map((file) => ({ kind: 'file', type: file.type, getAsFile: () => file })), types: ['Files'] } });
    await user.click(await screen.findByRole('button', { name: /Start import/ }));
    expect(await screen.findByText('Upload interrupted')).toBeInTheDocument();
    expect(mockGetJobById).toHaveBeenCalledWith(expect.objectContaining({ path: { id: 'first-job' } }));
    const failedQuery = mockUpload.mock.calls[1][2].query;
    await user.click(screen.getByRole('button', { name: 'Retry remaining uploads' }));
    await waitFor(() => expect(mockUpload).toHaveBeenCalledTimes(3));
    expect(mockUpload.mock.calls[2][2].query).toEqual(failedQuery);
    expect(mockUpload.mock.calls[2][1].name).toBe('second.zip');
  });

  it('recovers accepted imports from the URL after refresh', async () => {
    const batchId = 'ae5f342d-04cc-4672-8cd2-c51650871ddf';
    vi.mocked(getImportBatch).mockResolvedValue({ data: [runningJob] } as never);
    render(<ImportPage />, { routerOptions: { initialEntries: [`/roms/import?batch=${batchId}`] } });
    expect(await screen.findByText('Running')).toBeInTheDocument();
    expect(getImportBatch).toHaveBeenCalledWith(expect.objectContaining({ path: { batchId } }));
  });
});
