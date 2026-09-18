import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { Navbar } from './Navbar';

const permissions = vi.hoisted(() => ({ canUploadRoms: true, canManageTitles: true, canManageUsers: true }));
const counts = vi.hoisted(() => ({ unidentified: 2, dats: 1, success: true }));
vi.mock('../../hooks/usePermissions', () => ({ usePermissions: () => permissions }));
vi.mock('../../hooks/api/useDats', () => ({ useUnroutedDats: () => ({ isSuccess: counts.success, data: Array(counts.dats).fill({}) }) }));
vi.mock('../../hooks/api/useRoms', () => ({ useLibraryStats: () => ({ isSuccess: counts.success, data: { unidentifiedCount: counts.unidentified } }) }));
beforeEach(() => { Object.assign(permissions, { canUploadRoms: true, canManageTitles: true, canManageUsers: true }); Object.assign(counts, { unidentified: 2, dats: 1, success: true }); });

it('groups the admin workflow in the requested order and keeps ROM import selected', async () => {
  const onNavigate = vi.fn();
  render(<Navbar onNavigate={onNavigate} />, { routerOptions: { initialEntries: ['/roms/import'] } });
  expect(screen.getAllByRole('link').map((link) => link.textContent)).toEqual(['Dashboard', 'Systems', 'ROMs3', 'Catalog', 'Collections', 'Libraries', 'Jobs', 'Storage', 'Diagnostics', 'Users', 'Integrations', 'Reference data', 'Settings', 'Audit log']);
  for (const section of ['Sources & Files', 'Curation', 'Operations', 'Administration']) expect(screen.getByText(section)).toBeInTheDocument();
  expect(screen.getByRole('link', { name: /ROMs/ })).toHaveAttribute('aria-current', 'page');
  await userEvent.click(screen.getByRole('link', { name: 'Systems' }));
  expect(onNavigate).toHaveBeenCalledOnce();
});
it('does not expose privileged navigation to a read-only user', () => {
  Object.assign(permissions, { canUploadRoms: false, canManageTitles: false, canManageUsers: false });
  render(<Navbar />);
  expect(screen.queryByRole('link', { name: 'Users' })).not.toBeInTheDocument();
  expect(screen.queryByRole('link', { name: 'Diagnostics' })).not.toBeInTheDocument();
  expect(screen.queryByRole('link', { name: 'Libraries' })).not.toBeInTheDocument();
  expect(screen.getByRole('link', { name: /ROMs/ })).toBeInTheDocument();
  for (const name of ['Dashboard', 'Systems', 'Catalog', 'Jobs', 'Storage']) expect(screen.getByRole('link', { name })).toBeInTheDocument();
});
it('shows Collections to contributors but reserves Reference data for managers and Libraries for admins', () => {
  Object.assign(permissions, { canManageTitles: false, canManageUsers: false });
  render(<Navbar />);
  expect(screen.getByRole('link', { name: 'Collections' })).toBeInTheDocument();
  expect(screen.queryByRole('link', { name: 'Reference data' })).not.toBeInTheDocument();
  expect(screen.queryByRole('link', { name: 'Libraries' })).not.toBeInTheDocument();
});
it('shows Reference data but not Users or Libraries to managers', () => {
  permissions.canManageUsers = false;
  render(<Navbar />);
  expect(screen.getByRole('link', { name: 'Reference data' })).toBeInTheDocument();
  expect(screen.queryByRole('link', { name: 'Users' })).not.toBeInTheDocument();
  expect(screen.queryByRole('link', { name: 'Libraries' })).not.toBeInTheDocument();
});
it.each(['Systems', 'ROMs', 'Diagnostics', 'Reference data', 'Storage'])('links %s to its root workspace', (name) => {
  render(<Navbar />);
  expect(screen.getByRole('link', { name: new RegExp(name) })).toHaveAttribute('href', `/${name.toLowerCase().replace(' ', '-')}`);
});
it('hides zero attention counts', () => {
  counts.unidentified = 0; counts.dats = 0;
  render(<Navbar />);
  expect(screen.queryByText('0')).not.toBeInTheDocument();
});
it('does not present an incomplete attention count after a query failure', () => {
  counts.success = false;
  render(<Navbar />);
  expect(screen.queryByText('3')).not.toBeInTheDocument();
});
