import type { TitleRelease } from '@romd/admin-api-client';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { ReleasesPanel, releaseOwnership, releaseRegions } from './ReleasesPanel';

vi.mock('../../hooks/api/useTitleDetail', () => ({
  useTitleSourceReferences: () => ({ data: [{ catalogSourceId: 'catalog', datId: 'secondary', name: 'Secondary DAT', systemKey: 'snes', status: 'Disabled', kind: 'Dat', entryCount: 2 }] }),
}));

const releases: TitleRelease[] = [
  { id: 'us', datId: 'main', name: 'Shared release', region: 'Japan, USA', isComplete: true, sources: [{ datGameId: 'game', datId: 'secondary', datName: 'Secondary DAT', gameName: 'Original entry' }] },
  { id: 'eu', datId: 'main', name: 'European release', region: 'Europe' },
  { id: 'us2', datId: 'main', name: 'US revision', region: 'USA', files: [{ id: 'file', name: 'disc', isOwned: true }, { id: 'file2', name: 'disc2', isOwned: false }] },
];

describe('ReleasesPanel', () => {
  beforeEach(() => localStorage.clear());

  it('opens a linked secondary declaration despite remembered region filters', async () => {
    localStorage.setItem('romd-admin-release-region', JSON.stringify('Canada'));
    render(<ReleasesPanel titleId="title" releases={releases} />, { routerOptions: { initialEntries: ['/titles/title?tab=releases&release=game'] } });
    await waitFor(() => expect(screen.getByText('Original entry')).toBeVisible());
    expect(screen.getByRole('button', { name: /Shared release/ })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByLabelText('Release region')).toHaveValue('Canada');
    await userEvent.click(screen.getByRole('button', { name: 'Show filtered releases' }));
    expect(screen.getByText('No releases match this view.')).toBeInTheDocument();
  });

  it('includes shared US releases and keeps ownership counts scoped to the region', async () => {
    const user = userEvent.setup();
    render(<ReleasesPanel titleId="title" releases={releases} />);
    await user.selectOptions(screen.getByLabelText('Release region'), 'USA');
    expect(screen.getByText('USA: 1 owned · 2 known')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /European release/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Shared release/ })).toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText('Ownership'), 'Partial');
    expect(screen.getByText('Showing 1 of 3 catalog releases')).toBeInTheDocument();
    expect(screen.getByText('USA: 1 owned · 2 known')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /US revision/ })).toBeInTheDocument();
    expect(JSON.parse(localStorage.getItem('romd-admin-release-region')!)).toBe('USA');
  });

  it('filters by secondary declarations and retains their source links and status', async () => {
    const user = userEvent.setup();
    render(<ReleasesPanel titleId="title" releases={releases} />);
    await user.selectOptions(screen.getByLabelText('Source'), 'secondary');
    expect(screen.getByText('Showing 1 of 3 catalog releases')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /Shared release/ }));
    expect(await screen.findByRole('link', { name: 'Secondary DAT' })).toHaveAttribute('href', '/systems/snes?dat=secondary&sourceTitle=title');
    expect(screen.getByText('Original entry')).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: /Shared release/ })).getByText('Disabled')).toBeVisible();
  });

  it('makes an unmatched remembered region visible and easy to reset', async () => {
    localStorage.setItem('romd-admin-release-region', JSON.stringify('Canada'));
    const user = userEvent.setup();
    render(<ReleasesPanel titleId="title" releases={releases} />);
    expect(screen.getByLabelText('Release region')).toHaveValue('Canada');
    expect(screen.getByText('No releases match this view.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Show all releases' }));
    expect(screen.getByText('Showing 3 of 3 catalog releases')).toBeInTheDocument();
  });

  it('retains dormant catalog sources when there are no releases', async () => {
    const user = userEvent.setup();
    render(<ReleasesPanel titleId="title" releases={[]} />);
    expect(screen.getByText('No releases found for this title.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Catalog sources (1)' }));
    expect(await screen.findByRole('link', { name: 'Secondary DAT' })).toBeInTheDocument();
    expect(screen.getByText('Disabled')).toBeInTheDocument();
  });

  it('normalizes US aliases and distinguishes partial ownership', () => {
    expect(releaseRegions('Japan; US / United States')).toEqual(['Japan', 'USA', 'USA']);
    expect(releaseRegions(null)).toEqual(['Unspecified']);
    expect(releases.map(releaseOwnership)).toEqual(['Owned', 'Missing', 'Partial']);
  });
});
