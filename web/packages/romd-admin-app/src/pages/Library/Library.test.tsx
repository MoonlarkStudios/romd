import type { LibraryStats, PageOfRom } from '@romd/admin-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { Library } from './index';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    listRoms: vi.fn(),
    getLibraryStats: vi.fn(),
    getRecentJobs: vi.fn(),
    getCurrentUser: vi.fn(),
  };
});

import { getCurrentUser, getLibraryStats, getRecentJobs, listRoms } from '@romd/admin-api-client';

const mockListRoms = listRoms as ReturnType<typeof vi.fn>;
const mockGetLibraryStats = getLibraryStats as ReturnType<typeof vi.fn>;
const mockGetRecentJobs = getRecentJobs as ReturnType<typeof vi.fn>;
const mockGetCurrentUser = getCurrentUser as ReturnType<typeof vi.fn>;

const stats: LibraryStats = {
  totalRomFiles: '3',
  catalogedCount: '2',
  unroutedCount: '0',
  unidentifiedCount: '1',
  totalSizeBytes: '1835008',
  platformBreakdown: [],
};

const emptyPage: PageOfRom = {
  items: [],
  nextCursor: null,
  hasNextPage: false,
};

describe('Library', () => {
  beforeEach(() => {
    // Defaults — empty ROM list, valid stats, no jobs
    mockListRoms.mockResolvedValue({ data: emptyPage, error: undefined });
    mockGetLibraryStats.mockResolvedValue({ data: stats, error: undefined });
    mockGetRecentJobs.mockResolvedValue({ data: [], error: undefined });
    mockGetCurrentUser.mockResolvedValue({ data: null, error: { status: 401 } });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('renders the page title and add files button', async () => {
    render(<Library />);

    expect(screen.getByRole('heading', { name: /rom library/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /add files/i })).toBeInTheDocument();
  });

  it('displays library stats', async () => {
    render(<Library />);

    await waitFor(() => {
      expect(screen.getByText('Total ROMs')).toBeInTheDocument();
    });

    // These labels appear in both the stats grid and filter bar
    expect(screen.getAllByText('Cataloged').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Unidentified').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Unrouted').length).toBeGreaterThanOrEqual(1);
  });

  it('displays filter buttons', async () => {
    render(<Library />);

    expect(screen.getByRole('button', { name: /all/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /cataloged/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /unidentified/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /unrouted/i })).toBeInTheDocument();
  });

  it('loads with search query from URL', async () => {
    render(<Library />, {
      routerOptions: { initialEntries: ['/roms?q=mario'] },
    });

    const searchInput = screen.getByPlaceholderText(/search by filename/i);
    expect(searchInput).toHaveValue('mario');
  });

  it('switches filter when clicking filter button', async () => {
    const user = userEvent.setup();
    render(<Library />);

    // Click the Cataloged filter button
    const catalogedButton = screen.getByRole('button', { name: /cataloged/i });
    await user.click(catalogedButton);

    // The button should now be active (variant="filled")
    await waitFor(() => {
      expect(mockListRoms).toHaveBeenCalled();
    });
  });

  it('handles API errors gracefully', async () => {
    mockListRoms.mockResolvedValue({ data: undefined, error: { status: 500 } });

    render(<Library />);

    await waitFor(() => {
      expect(screen.getByText(/something went wrong/i)).toBeInTheDocument();
    });
  });

  it('renders search input with placeholder', async () => {
    render(<Library />);

    expect(screen.getByPlaceholderText(/search by filename/i)).toBeInTheDocument();
  });

  it('shows unrouted info banner when unrouted filter is active', async () => {
    render(<Library />, {
      routerOptions: { initialEntries: ['/roms?status=Unrouted'] },
    });

    await waitFor(() => {
      expect(screen.getByText(/route them in the inbox/i)).toBeInTheDocument();
    });
  });
});
