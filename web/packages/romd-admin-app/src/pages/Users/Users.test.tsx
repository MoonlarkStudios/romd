import type { LibraryDto, UserDto } from '@romd/admin-api-client';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { Users } from './index';

vi.mock('@romd/admin-api-client', async () => ({ ...await vi.importActual('@romd/admin-api-client'), getUserDirectory: vi.fn(), listLibraries: vi.fn(), assignDefaultLibraryToUsers: vi.fn(), createUser: vi.fn(), getCurrentUser: vi.fn() }));

import { assignDefaultLibraryToUsers, createUser, getCurrentUser, getUserDirectory, listLibraries } from '@romd/admin-api-client';

const users: UserDto[] = [{ id: 'admin', email: 'admin@example.com', roles: ['Admin'], libraryId: 'family', createdAt: '2026-01-01T00:00:00Z', updatedAt: null }, { id: 'member', email: 'member@example.com', roles: ['User'], libraryId: null, createdAt: '2026-01-01T00:00:00Z', updatedAt: null }];
const libraries: LibraryDto[] = [{ id: 'family', name: 'Family library', configurationState: 'Valid', isDefault: true, needsMaterialization: false, itemCount: 12, createdAt: '2026-01-01T00:00:00Z' }];
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(getUserDirectory).mockResolvedValue({ data: { items: users, nextCursor: null } } as Awaited<ReturnType<typeof getUserDirectory>>);
  vi.mocked(listLibraries).mockResolvedValue({ data: libraries } as Awaited<ReturnType<typeof listLibraries>>);
  vi.mocked(getCurrentUser).mockResolvedValue({ data: { id: 'admin', email: 'admin@example.com', roles: ['Admin'], libraryId: 'family' } } as Awaited<ReturnType<typeof getCurrentUser>>);
});
describe('Users directory', () => {
  it('links accounts to durable workspaces and keeps selection actions in the header', async () => {
    const user = userEvent.setup(); render(<Users />);
    expect(await screen.findByRole('link', { name: 'member@example.com' })).toHaveAttribute('href', '/users/member');
    expect(screen.getByRole('button', { name: 'Selection' })).toBeDisabled();
    await user.click(screen.getByRole('checkbox', { name: 'Select member@example.com' }));
    expect(screen.getByRole('button', { name: 'Selection' })).toBeEnabled();
    expect(screen.getByText('1 selected')).toBeInTheDocument();
  });
  it('confirms and submits only selected eligible accounts for default access', async () => {
    vi.mocked(assignDefaultLibraryToUsers).mockResolvedValue({ data: { updatedCount: 1 } } as Awaited<ReturnType<typeof assignDefaultLibraryToUsers>>);
    const user = userEvent.setup(); render(<Users />);
    await user.click(await screen.findByRole('checkbox', { name: 'Select member@example.com' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Selection' })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Selection' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Assign default library (1)' }));
    expect(assignDefaultLibraryToUsers).not.toHaveBeenCalled();
    expect(await screen.findByRole('dialog', { name: 'Assign default library?' })).toHaveTextContent('Grant 1 selected unassigned accounts access to Family library');
    await user.click(screen.getByRole('button', { name: 'Assign library' }));
    await waitFor(() => expect(assignDefaultLibraryToUsers).toHaveBeenCalledWith({ body: { userIds: ['member'] } }));
  });
  it('shows failure without claiming that the directory is empty', async () => {
    vi.mocked(getUserDirectory).mockResolvedValue({ error: { detail: 'Directory unavailable' } } as Awaited<ReturnType<typeof getUserDirectory>>);
    render(<Users />);
    expect(await screen.findByText('Directory unavailable')).toBeInTheDocument();
    expect(screen.queryByText('No human accounts found.')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });
  it('creates pending accounts without asking an administrator to choose a password', async () => {
    vi.mocked(createUser).mockResolvedValue({ data: { ...users[1], requiresActivation: true } } as Awaited<ReturnType<typeof createUser>>);
    const user = userEvent.setup(); render(<Users />);
    await user.click(await screen.findByRole('button', { name: 'Create account' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create account' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Email' }), 'new@example.com');
    expect(screen.queryByLabelText('Initial password')).not.toBeInTheDocument();
    const submit = within(dialog).getByRole('button', { name: 'Create account' });
    await waitFor(() => expect(submit).toBeEnabled());
    await user.click(submit);
    await waitFor(() => expect(createUser).toHaveBeenCalledWith({ body: { email: 'new@example.com', role: 'User', libraryId: null, password: '', requiresActivation: true } }));
  });
});
