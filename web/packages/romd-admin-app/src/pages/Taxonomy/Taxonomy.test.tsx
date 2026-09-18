import type { LanguageDto, RegionDto } from '@romd/admin-api-client';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Permissions, Role } from '../../hooks/usePermissions';
import { render } from '../../test/utils/render';
import { Taxonomy } from './index';

let permissions: Permissions;

vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => permissions,
}));

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    getTaxonomyMergeImpact: vi.fn(),
    listRegions: vi.fn(),
    listLanguages: vi.fn(),
    addRegionAlias: vi.fn(),
    removeRegionAlias: vi.fn(),
    mergeRegions: vi.fn(),
    addLanguageAlias: vi.fn(),
    removeLanguageAlias: vi.fn(),
    mergeLanguages: vi.fn(),
    getCurrentUser: vi.fn(),
  };
});

import {
  addRegionAlias,
  getCurrentUser,
  getTaxonomyMergeImpact,
  listLanguages,
  listRegions,
  mergeRegions,
} from '@romd/admin-api-client';

const mockListRegions = listRegions as ReturnType<typeof vi.fn>;
const mockListLanguages = listLanguages as ReturnType<typeof vi.fn>;
const mockAddRegionAlias = addRegionAlias as ReturnType<typeof vi.fn>;
const mockMergeRegions = mergeRegions as ReturnType<typeof vi.fn>;
const mockGetCurrentUser = getCurrentUser as ReturnType<typeof vi.fn>;

function makePermissions(role: Role, canManageUsers: boolean): Permissions {
  return {
    canUploadRoms: true,
    canManageTitles: true,
    canTriggerEnrichment: true,
    canManageUsers,
    canEditReferenceData: canManageUsers,
    canConfigureIntegrations: canManageUsers,
    canEditMetadataPolicy: role === 'Admin' || role === 'Manager',
    role,
    hasRole: () => true,
  };
}

const regions: RegionDto[] = [
  {
    id: 'usa',
    name: 'USA',
    sortOrder: '0',
    isAutoCreated: false, canMerge: true,
    aliases: [{ id: '1', alias: 'United States', ownership: 'Installation' }],
  },
  { id: 'eur', name: 'Europe', sortOrder: '1', isAutoCreated: true, canMerge: true, aliases: [] },
];

const languages: LanguageDto[] = [
  {
    id: 'en',
    name: 'English',
    code: 'En',
    sortOrder: '0',
    isAutoCreated: false, canMerge: true,
    aliases: [{ id: '5', alias: 'Eng', ownership: 'Installation' }],
  },
];

describe('Taxonomy', () => {
  beforeEach(() => {
    vi.mocked(getTaxonomyMergeImpact).mockResolvedValue({ data: { aliases: 2, sourceGames: 4, catalogReleases: 3 } } as Awaited<ReturnType<typeof getTaxonomyMergeImpact>>);
    permissions = makePermissions('Admin', true);
    mockListRegions.mockResolvedValue({ data: regions, error: undefined });
    mockListLanguages.mockResolvedValue({ data: languages, error: undefined });
    mockAddRegionAlias.mockResolvedValue({ data: undefined, error: undefined });
    mockMergeRegions.mockResolvedValue({ data: undefined, error: undefined });
    mockGetCurrentUser.mockResolvedValue({ data: null, error: undefined });
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it('renders regions with their aliases and an auto-created badge', async () => {
    render(<Taxonomy />);

    expect(await screen.findByText('USA')).toBeInTheDocument();
    expect(screen.getByText('United States · Local')).toBeInTheDocument();
    expect(screen.getByText('Europe')).toBeInTheDocument();
    expect(screen.getByText('Auto')).toBeInTheDocument();
  });

  it('hides invalid managed actions and distinguishes alias ownership', async () => {
    mockListRegions.mockResolvedValue({ data: [{ ...regions[0], canMerge: false, aliases: [
      { id: '1', alias: 'Managed alias', ownership: 'Romd' },
      { id: '2', alias: 'Local alias', ownership: 'Installation' },
      { id: '3', alias: 'Legacy alias', ownership: 'Unresolved' },
    ] }, regions[1]], error: undefined });
    render(<Taxonomy />);
    await screen.findByText('USA');
    expect(screen.queryByRole('button', { name: 'Merge USA into another value' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Merge Europe into another value' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove alias Managed alias from USA' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Remove alias Local alias from USA' })).toBeInTheDocument();
    expect(screen.getByText('Managed alias · ROMD')).toBeInTheDocument();
    expect(screen.getByText('Legacy alias · Ownership unknown')).toBeInTheDocument();
    expect(screen.getByText(/matching aliases may reappear/)).toBeInTheDocument();
  });

  it('adds an alias to a region as an admin', async () => {
    const user = userEvent.setup();
    render(<Taxonomy />);

    await screen.findByText('USA');
    // First row after sortOrder sort is USA.
    await user.click(screen.getAllByRole('button', { name: /Add alias to/i })[0]);
    await user.type(await screen.findByLabelText('New alias'), 'Canada{Enter}');

    await waitFor(() => {
      expect(mockAddRegionAlias).toHaveBeenCalledWith({
        path: { regionId: 'usa' },
        body: { alias: 'Canada' },
      });
    });
  });

  it('merges a region into another, excluding itself as a target', async () => {
    const user = userEvent.setup();
    render(<Taxonomy />);

    await screen.findByText('USA');
    // Open the merge modal for USA (first row).
    await user.click(screen.getAllByRole('button', { name: /Merge .* into/i })[0]);

    const dialog = await screen.findByRole('dialog');
    // Open the target combobox. Mantine renders options into the DOM but keeps
    // the dropdown display:none under jsdom (no layout), so query with
    // { hidden: true } and fire the option click directly.
    await user.click(within(dialog).getByRole('textbox'));

    // The source (USA) must not be offered as its own merge target.
    expect(screen.queryByRole('option', { name: 'USA', hidden: true })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('option', { name: 'Europe', hidden: true }));
    await user.click(within(dialog).getByRole('button', { name: /^merge$/i }));

    await waitFor(() => {
      expect(mockMergeRegions).toHaveBeenCalledWith({
        path: { sourceId: 'usa', targetId: 'eur' },
      });
    });
  });

  it('preserves the server explanation when a merge is rejected', async () => {
    const user = userEvent.setup();
    const detail = 'A registered reference identity cannot be merged into another identity.';
    mockMergeRegions.mockResolvedValue({ error: { detail }, response: { status: 409 } });
    render(<Taxonomy />);
    await screen.findByText('USA');
    await user.click(screen.getByRole('button', { name: 'Merge USA into another value' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('textbox'));
    fireEvent.click(screen.getByRole('option', { name: 'Europe', hidden: true }));
    await user.click(within(dialog).getByRole('button', { name: /^merge$/i }));
    expect(await screen.findByText(detail)).toBeInTheDocument();
    expect(screen.queryByText('Failed to merge regions.')).not.toBeInTheDocument();
  });

  it('is read-only without admin rights', async () => {
    permissions = makePermissions('Manager', false);
    render(<Taxonomy />);

    await screen.findByText('USA');
    expect(screen.queryByRole('button', { name: /Add alias to/i })).not.toBeInTheDocument();
    expect(screen.getByText(/editing requires an administrator/i)).toBeInTheDocument();
  });

  it('shows languages with their code on the Languages tab', async () => {
    const user = userEvent.setup();
    render(<Taxonomy />);

    await user.click(screen.getByRole('tab', { name: /languages/i }));

    expect(await screen.findByText('English')).toBeInTheDocument();
    expect(screen.getByText('En')).toBeInTheDocument();
    expect(screen.getByText('Eng · Local')).toBeInTheDocument();
  });
});
