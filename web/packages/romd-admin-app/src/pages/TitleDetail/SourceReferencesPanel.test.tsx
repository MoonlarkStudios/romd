import type { TitleSourceReference } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { SourceReferencesPanel } from './SourceReferencesPanel';

vi.mock('@romd/admin-api-client', async () => {
  const actual = await vi.importActual('@romd/admin-api-client');
  return {
    ...actual,
    getTitleSourceReferences: vi.fn(),
  };
});

import { getTitleSourceReferences } from '@romd/admin-api-client';

const mockGetTitleSourceReferences = getTitleSourceReferences as ReturnType<typeof vi.fn>;

const references: TitleSourceReference[] = [
  {
    catalogSourceId: 'catsrc-1',
    kind: 'Dat',
    name: 'No-Intro SNES',
    status: 'Active',
    entryCount: '3',
    hasActiveDefinition: true,
    datId: 'dat-1',
    systemKey: 'plat-1',
  },
  {
    catalogSourceId: 'catsrc-2',
    kind: 'Import',
    name: null,
    status: 'Disabled',
    entryCount: '1',
  },
];

describe('SourceReferencesPanel', () => {
  beforeEach(() => {
    mockGetTitleSourceReferences.mockResolvedValue({
      data: references,
      error: undefined,
    });
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('lists each backing source with kind, status, and entry count', async () => {
    render(<SourceReferencesPanel titleId="title-1" />);

    expect(
      await screen.findByRole('link', {
        name: 'No-Intro SNES',
      }),
    ).toHaveAttribute('href', '/systems/plat-1?dat=dat-1&sourceTitle=title-1');
    expect(mockGetTitleSourceReferences).toHaveBeenCalledWith({
      path: {
        titleId: 'title-1',
      },
    });

    expect(screen.getByText('Dat')).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText('3 entries')).toBeInTheDocument();

    // A dormant reference still backs the title and stays visible here.
    expect(screen.getByText('Import')).toBeInTheDocument();
    expect(screen.getByText('Disabled')).toBeInTheDocument();
    expect(screen.getByText('1 entries')).toBeInTheDocument();

    // A nameless source falls back to an em-dash.
    expect(screen.getByText('—')).toBeInTheDocument();

    expect(screen.getByText(/Inactive sources preserve their entries/)).toBeInTheDocument();
  });

  it('explains a title with no source backing', async () => {
    mockGetTitleSourceReferences.mockResolvedValue({
      data: [],
      error: undefined,
    });
    render(<SourceReferencesPanel titleId="title-1" />);

    expect(
      await screen.findByText(
        'No active catalog definition. This title and its personal state remain saved.',
      ),
    ).toBeInTheDocument();
  });

  it('shows an error state when the references cannot be loaded', async () => {
    mockGetTitleSourceReferences.mockResolvedValue({
      data: undefined,
      error: {
        status: 500,
      },
    });
    render(<SourceReferencesPanel titleId="title-1" />);

    expect(
      await screen.findByText('Failed to load the sources backing this title.'),
    ).toBeInTheDocument();
  });
});
