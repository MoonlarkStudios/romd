import type { TitleRelease } from '@romd/admin-api-client';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useLocation } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { ReleasesPanel, releaseOwnership, releaseRegions } from './ReleasesPanel';

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
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canManageTitles: true,
  }),
}));
function Location() {
  return <output aria-label="URL">{useLocation().search}</output>;
}

const releases: TitleRelease[] = [
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
  },
  {
    id: 'us2',
    datId: 'main',
    name: 'US revision',
    region: 'USA',
    files: [
      {
        id: 'file',
        name: 'disc',
        isOwned: true,
      },
      {
        id: 'file2',
        name: 'disc2',
        isOwned: false,
      },
    ],
  },
];

describe('ReleasesPanel', () => {
  beforeEach(() => {
    localStorage.clear();
    mutate.mockClear();
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
        name: 'Sources for Shared release',
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
        screen.getByRole('dialog', {
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

  it('retains dormant catalog sources when there are no releases', async () => {
    const user = userEvent.setup();
    render(
      <ReleasesPanel
        titleId="title"
        releases={[]}
      />,
    );
    expect(screen.getByText('No releases found for this title.')).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Sources (1)',
      }),
    );
    expect(
      await screen.findByRole('link', {
        name: 'Secondary DAT',
      }),
    ).toBeInTheDocument();
    expect(screen.getByText('Disabled')).toBeInTheDocument();
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
          preferredReleaseName: 'European release',
          releaseRegionPriority: [
            'EUROPE',
            'USA',
          ],
        }}
      />,
    );
    expect(screen.getByText('Preferred · Automatic')).toBeVisible();
    expect(screen.getAllByRole('row')[1]).toHaveTextContent('European release');
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Set as preferred: European release',
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
    expect(
      screen.queryByRole('button', {
        name: 'Use automatic preference',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('button', {
        name: 'Unpin release: Shared release',
      }),
    ).toHaveAttribute('aria-pressed', 'true');
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Unpin release: Shared release',
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

  it('allows unpinning the current pin and disables releases without canonical IDs', () => {
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
        preference={{
          pinnedCatalogReleaseId: 'canonical-us',
          preferredCatalogReleaseId: 'canonical-us',
        }}
      />,
    );
    expect(
      screen.getByRole('button', {
        name: 'Unpin release: Shared release',
      }),
    ).toBeEnabled();
    expect(
      screen.getByRole('button', {
        name: 'Set as preferred: US revision',
      }),
    ).toBeDisabled();
    expect(
      screen.getByRole('button', {
        name: 'Set as preferred: European release',
      }),
    ).toBeEnabled();
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
  });

  it('expands a nested file table without changing preference', async () => {
    render(
      <ReleasesPanel
        titleId="title"
        releases={releases}
      />,
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'US revision',
        exact: true,
      }),
    );
    expect(
      screen.getByRole('button', {
        name: 'US revision',
        exact: true,
      }),
    ).toHaveAttribute('aria-expanded', 'true');
    await waitFor(() => expect(screen.getByText('disc2')).toBeVisible());
    expect(screen.getAllByRole('table')).toHaveLength(2);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(mutate).not.toHaveBeenCalled();
  });
});
