import { listAdminDiscSets } from '@romd/admin-api-client';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useLocation } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { TitleReleaseView } from '../../hooks/api/useTitleDetail';
import { render } from '../../test/utils/render';
import { ReleasesPanel, releaseOwnership, releaseRegions } from './ReleasesPanel';

vi.mock('@romd/admin-api-client', async (original) => ({
  ...(await original<typeof import('@romd/admin-api-client')>()),
  listAdminDiscSets: vi.fn(),
}));

vi.mock('../../hooks/api/useTitleDetail', () => ({
  useTitleSourceReferences: () => ({
    data: [
      {
        catalogSourceId: 'catalog',
        datId: 'secondary',
        name: 'Secondary DAT',
        systemKey: 'snes',
        status: 'Disabled',
        kind: 'Dat',
        entryCount: 2,
      },
    ],
  }),
}));

const mutate = vi.fn();
vi.mock('../../hooks/api/useTitleActions', () => ({
  useSetTitleTracking: () => ({
    mutate,
    isPending: false,
  }),
}));
const hasRole = vi.fn();
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canManageTitles: true,
    hasRole,
  }),
}));
function Location() {
  return <output aria-label="URL">{useLocation().search}</output>;
}

const releases: TitleReleaseView[] = [
  {
    id: 'us',
    catalogReleaseId: 'canonical-us',
    datId: 'main',
    name: 'Shared release',
    region: 'Japan, USA',
    isComplete: true,
    sources: [
      {
        datGameId: 'game',
        datId: 'secondary',
        datName: 'Secondary DAT',
        gameName: 'Original entry',
      },
    ],
  },
  {
    id: 'eu',
    catalogReleaseId: 'canonical-eu',
    datId: 'main',
    name: 'European release',
    region: 'Europe',
    sources: [],
  },
  {
    id: 'us2',
    datId: 'main',
    name: 'US revision',
    region: 'USA',
    sources: [],
    files: [
      {
        id: 'file',
        name: 'disc',
        isOwned: true,
        romFileId: 'rom',
      },
      {
        id: 'file2',
        name: 'disc2',
        isOwned: false,
        sha1: 'ab'.repeat(20),
      },
    ],
  },
];

describe('ReleasesPanel', () => {
  beforeEach(() => {
    localStorage.clear();
    mutate.mockClear();
    hasRole.mockReset().mockReturnValue(false);
    vi.mocked(listAdminDiscSets).mockReset();
  });

  it('reveals a linked release alongside matches and explains a filter exception', async () => {
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
      />,
      {
        routerOptions: {
          initialEntries: [
            '/titles/title?editor=releases&release=game&releaseRegion=Europe',
          ],
        },
      },
    );
    expect(
      await screen.findByRole('button', {
        name: 'Shared release',
        exact: true,
      }),
    ).toHaveAttribute('aria-expanded', 'true');
    expect(await screen.findByText('Linked release is outside your filters')).toBeVisible();
    expect(
      screen.getByRole('complementary', {
        name: 'Shared release',
      }),
    ).toBeVisible();
    expect(
      screen.getByRole('button', {
        name: 'European release',
        exact: true,
      }),
    ).toBeVisible();
    expect(screen.getByLabelText('Release region')).toHaveValue('Europe');
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Dismiss linked release',
      }),
    );
    expect(
      screen.queryByRole('button', {
        name: 'Shared release',
        exact: true,
      }),
    ).not.toBeInTheDocument();
  });

  it('includes shared US releases and keeps ownership counts scoped to the region', async () => {
    const user = userEvent.setup();
    render(
      <>
        <ReleasesPanel
          titleId="title"
          releases={releases}
        />
        <Location />
      </>,
    );
    await user.selectOptions(screen.getByLabelText('Release region'), 'USA');
    expect(
      screen.queryByRole('button', {
        name: 'European release',
        exact: true,
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Shared release',
        exact: true,
      }),
    ).toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText('Ownership'), 'Partial');
    expect(screen.getByText('Showing 1 of 3 releases')).toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'US revision',
        exact: true,
      }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('URL')).toHaveTextContent(
      'releaseRegion=USA&releaseOwnership=Partial',
    );
  });

  it('filters by secondary declarations and retains their source links and status', async () => {
    const user = userEvent.setup();
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
      />,
    );
    await user.selectOptions(screen.getByLabelText('Source'), 'secondary');
    expect(screen.getByText('Showing 1 of 3 releases')).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Shared release',
      }),
    );
    expect(
      await screen.findByRole('link', {
        name: 'Secondary DAT',
      }),
    ).toHaveAttribute('href', '/systems/snes?dat=secondary&sourceTitle=title');
    expect(screen.getByText('Original entry')).toBeInTheDocument();
    expect(
      within(
        screen.getByRole('complementary', {
          name: 'Shared release',
          exact: true,
        }),
      ).getByText('Disabled'),
    ).toBeVisible();
  });

  it('makes an unmatched URL region visible and easy to reset', async () => {
    const user = userEvent.setup();
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
      />,
      {
        routerOptions: {
          initialEntries: [
            '/titles/title?releaseRegion=Canada',
          ],
        },
      },
    );
    expect(screen.getByLabelText('Release region')).toHaveValue('Canada');
    expect(screen.getByText('No releases match this view.')).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Show all releases',
      }),
    );
    expect(screen.getByText('Showing 3 of 3 releases')).toBeInTheDocument();
  });

  it('clears a stale deep link while keeping the rest of the URL', async () => {
    const user = userEvent.setup();
    render(
      <>
        <ReleasesPanel
          titleId="title"
          releases={releases}
        />
        <Location />
      </>,
      {
        routerOptions: {
          initialEntries: [
            '/titles/title/releases?release=gone&releaseRegion=USA',
          ],
        },
      },
    );
    expect(screen.getByText('This release is unavailable')).toBeVisible();
    expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Dismiss linked release',
      }),
    );
    expect(screen.getByLabelText('URL')).toHaveTextContent('?releaseRegion=USA');
  });

  it('normalizes US aliases and distinguishes partial ownership', () => {
    expect(releaseRegions('Japan; US / United States')).toEqual([
      'Japan',
      'USA',
      'USA',
    ]);
    expect(releaseRegions(null)).toEqual([
      'Unspecified',
    ]);
    expect(releases.map(releaseOwnership)).toEqual([
      'Owned',
      'Missing',
      'Partial',
    ]);
  });
  it('puts the preferred release first and pins using its canonical ID', async () => {
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
        preference={{
          isTracked: true,
          preferredCatalogReleaseId: 'canonical-eu',
          releaseRegionPriority: [
            'EUROPE',
            'USA',
          ],
        }}
      />,
    );
    expect(screen.getByText('Preferred · Automatic')).toBeVisible();
    expect(screen.getAllByRole('row')[1]).toHaveTextContent('European release');
    expect(
      screen.queryByRole('button', {
        name: 'Use as preferred',
      }),
    ).not.toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'European release',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Use as preferred',
      }),
    );
    expect(mutate).toHaveBeenCalledWith(
      {
        titleId: 'title',
        tracked: true,
        pinnedCatalogReleaseId: 'canonical-eu',
      },
      expect.any(Object),
    );
  });

  it('can reset an explicit pin without untracking the title', async () => {
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
        preference={{
          isTracked: true,
          pinnedCatalogReleaseId: 'canonical-us',
          preferredCatalogReleaseId: 'canonical-us',
        }}
      />,
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Shared release',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Use automatic preference',
      }),
    );
    expect(mutate).toHaveBeenCalledWith(
      {
        titleId: 'title',
        tracked: true,
        pinnedCatalogReleaseId: null,
      },
      expect.any(Object),
    );
  });

  it('disables pinning if the release has no canonical ID', async () => {
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
      />,
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'US revision',
      }),
    );
    expect(
      screen.getByRole('button', {
        name: 'Use as preferred',
      }),
    ).toBeDisabled();
  });

  it('keeps release files, copyable hashes and downloads in the inspector', async () => {
    const user = userEvent.setup();
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
      />,
    );
    const row = screen.getByRole('button', {
      name: 'US revision',
    });
    await user.click(row);
    expect(row).toHaveAttribute('aria-expanded', 'true');
    const inspector = screen.getByRole('complementary', {
      name: 'US revision',
    });
    expect(await within(inspector).findByText('disc2')).toBeVisible();
    expect(
      within(inspector).getByRole('link', {
        name: 'disc',
      }),
    ).toHaveAttribute('href', '/roms/rom?tab=all');
    expect(
      within(inspector).getByRole('button', {
        name: 'Download for disc',
      }),
    ).toBeEnabled();
    await user.click(within(inspector).getByLabelText('Hashes for disc2'));
    expect(
      within(inspector).getByRole('button', {
        name: 'Copy SHA-1',
      }),
    ).toBeInTheDocument();
    expect(within(inspector).getByText('ab'.repeat(20))).toBeVisible();
    await user.keyboard('{Escape}');
    await waitFor(() => expect(row).toHaveFocus());
    expect(row).toHaveAttribute('aria-expanded', 'false');
    expect(mutate).not.toHaveBeenCalled();
  });

  it('moves between visible release rows with the arrow keys', async () => {
    const user = userEvent.setup();
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
      />,
    );
    const first = screen.getByRole('button', {
      name: 'Shared release',
    });
    const next = screen.getByRole('button', {
      name: 'European release',
    });
    first.focus();
    await user.keyboard('{ArrowDown}');
    expect(next).toHaveFocus();
    expect(next.closest('tr')).toHaveAttribute('aria-current', 'true');
    expect(
      screen.getByRole('complementary', {
        name: 'European release',
      }),
    ).toBeVisible();
  });

  it('keeps disc sets and their API private to administrators', async () => {
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
      />,
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Shared release',
      }),
    );
    expect(screen.queryByText('Disc sets')).not.toBeInTheDocument();
    expect(listAdminDiscSets).not.toHaveBeenCalled();
  });

  it('shows the effective disc membership by canonical release ID and opens its decision editor', async () => {
    const user = userEvent.setup();
    hasRole.mockReturnValue(true);
    const proposal = {
      name: 'Automatic name',
      expectedDiscCount: 2,
      members: [],
      reviewReasons: [],
      ruleVersion: 'v1',
    };
    vi.mocked(listAdminDiscSets).mockResolvedValue({
      data: {
        items: [
          {
            id: 'set',
            catalogSourceId: 'source',
            revision: '1',
            systemKey: 'snes',
            decision: 'corrected',
            decidedBy: null,
            decidedAt: null,
            reason: null,
            proposal,
            correction: {
              ...proposal,
              name: 'Corrected disc set',
              members: [
                {
                  entryKey: 'source-disc',
                  name: 'Disc 2',
                  releaseId: 'canonical-us',
                  ordinal: 2,
                  issue: null,
                },
              ],
            },
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      },
      error: undefined,
      request: new Request('http://localhost'),
      response: new Response(),
    });
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
      />,
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Shared release',
      }),
    );
    const inspector = screen.getByRole('complementary', {
      name: 'Shared release',
    });
    const review = await within(inspector).findByRole('button', {
      name: 'Review disc set Corrected disc set',
    });
    expect(within(inspector).getByText('Disc 2 of 2')).toBeVisible();
    await user.click(review);
    const sets = screen.getByRole('region', {
      name: 'Disc sets',
    });
    const control = within(sets).getByRole('button', {
      name: /Corrected disc set/,
    });
    expect(control).toHaveFocus();
    expect(control).toHaveAttribute('aria-expanded', 'true');
    expect(
      await within(sets).findByRole('button', {
        name: 'Correct membership and order',
      }),
    ).toBeVisible();
  });
});
