import type { JobItemPage } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { ProvenanceLedger } from './ProvenanceLedger';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return { ...actual, getCurrentUser: vi.fn(), getJobItems: vi.fn(), deleteRom: vi.fn() };
});

import { deleteRom, getCurrentUser, getJobItems } from '@romd/admin-api-client';

const mockGetCurrentUser = getCurrentUser as ReturnType<typeof vi.fn>;
const mockGetJobItems = getJobItems as ReturnType<typeof vi.fn>;
const mockDeleteRom = deleteRom as ReturnType<typeof vi.fn>;

const page: JobItemPage = {
  hasMore: false,
  items: [
    {
      id: '1',
      kind: 'rom',
      fileName: 'nhl94.bin',
      sizeBytes: '1024',
      outcome: 'ingested',
      matchedTitles: [{ id: 't1', name: 'NHL 94 (USA)' }],
      platformName: 'Genesis',
      romFileId: 'rom-1',
      archiveOnly: false,
    },
    {
      id: '2',
      kind: 'rom',
      fileName: 'mystery.bin',
      sizeBytes: '512',
      outcome: 'rejected',
      matchedTitles: [],
      archiveOnly: true,
    },
    {
      id: '3',
      kind: 'rom',
      fileName: 'scph5500.bin',
      sizeBytes: '524288',
      outcome: 'ingested',
      matchedTitles: [],
      platformName: 'psx',
      romFileId: 'rom-2',
    },
  ],
};

describe('ProvenanceLedger', () => {
  beforeEach(() => {
    mockGetCurrentUser.mockResolvedValue({ data: null, error: { status: 401 } });
    mockGetJobItems.mockResolvedValue({ data: page, error: undefined });
    mockDeleteRom.mockResolvedValue({ data: undefined, error: undefined });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('renders each file with its resolved destination', async () => {
    render(<ProvenanceLedger jobId="job-1" />);

    expect(await screen.findByText('nhl94.bin')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'NHL 94 (USA)' })).toHaveAttribute('href', '/titles/t1');
    expect(screen.getByRole('link', { name: 'nhl94.bin' })).toHaveAttribute('href', '/roms/rom-1?tab=all');
    expect(screen.getByText('mystery.bin')).toBeInTheDocument();
    expect(screen.getByText('No catalog match')).toBeInTheDocument();
    expect(screen.getByText('scph5500.bin')).toBeInTheDocument();
    expect(screen.getByText('psx')).toBeInTheDocument();
  });

  it('re-queries with the chosen outcome filter', async () => {
    const user = userEvent.setup();
    render(<ProvenanceLedger jobId="job-1" />);

    await screen.findByText('nhl94.bin');
    await user.click(screen.getByRole('textbox', { name: 'Filter outcomes' }));
    await user.click(await screen.findByRole('option', { name: 'Not stored', hidden: true }));

    await waitFor(() => {
      expect(mockGetJobItems).toHaveBeenCalledWith(
        expect.objectContaining({ query: expect.objectContaining({ outcome: 'rejected' }) }),
      );
    });
  });

  it('paginates results and resets the cursor when searching paths', async () => {
    const user = userEvent.setup();
    mockGetJobItems.mockResolvedValue({ data: { ...page, hasMore: true, nextCursor: 'cursor-1' } });
    render(<ProvenanceLedger jobId="job-1" />);

    await screen.findByText('nhl94.bin');
    await user.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() => {
      expect(mockGetJobItems).toHaveBeenCalledWith(expect.objectContaining({ query: expect.objectContaining({ cursor: 'cursor-1' }) }));
    });
    await user.type(screen.getByRole('textbox', { name: 'Search imported files' }), 'folder');
    await waitFor(() => expect(mockGetJobItems).toHaveBeenCalledWith(expect.objectContaining({ query: expect.objectContaining({ search: 'folder', cursor: undefined }) })));
  });
});
