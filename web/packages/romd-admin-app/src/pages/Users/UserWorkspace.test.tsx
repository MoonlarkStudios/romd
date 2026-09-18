import { MantineProvider } from '@mantine/core';
import { assignUserLibrary, assignUserRole, getUserById, listLibraries, type UserDto, updateUser } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { UserWorkspace } from './UserWorkspace';

vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 'operator', roles: ['Admin'] } }) }));
vi.mock('@romd/admin-api-client', async () => ({ ...await vi.importActual('@romd/admin-api-client'), getUserById: vi.fn(), listLibraries: vi.fn(), updateUser: vi.fn(), assignUserRole: vi.fn(), assignUserLibrary: vi.fn() }));
const account: UserDto = { id: 'member', email: 'member@example.com', roles: ['User'], libraryId: null, createdAt: '2026-01-01T00:00:00Z', updatedAt: null };
beforeEach(() => { vi.clearAllMocks(); vi.mocked(getUserById).mockResolvedValue({ data: account } as Awaited<ReturnType<typeof getUserById>>); vi.mocked(listLibraries).mockResolvedValue({ data: [] } as Awaited<ReturnType<typeof listLibraries>>); });
function mount() {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const router = createMemoryRouter([{ path: '/users/:userId', element: <UserWorkspace /> }, { path: '/users', element: <div>Directory</div> }], { initialEntries: ['/users/member'] });
  render(<MantineProvider><QueryClientProvider client={cache}><RouterProvider router={router} /></QueryClientProvider></MantineProvider>);
  return { cache, router };
}
it('preserves a dirty email when a background refresh replaces account data', async () => {
  const user = userEvent.setup(); const { cache } = mount();
  const email = await screen.findByRole('textbox', { name: 'Email' });
  await user.clear(email); await user.type(email, 'draft@example.com');
  act(() => cache.setQueryData(['users', 'detail', 'member'], { ...account, email: 'external@example.com' }));
  expect(email).toHaveValue('draft@example.com');
  await user.click(screen.getByRole('link', { name: 'Users' }));
  expect(await screen.findByRole('dialog', { name: 'Discard unsaved changes?' })).toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: 'Keep editing' }));
  expect(email).toHaveValue('draft@example.com');
});
it('saves email independently and preserves the draft on failure', async () => {
  vi.mocked(updateUser).mockResolvedValue({ error: { detail: 'Email is already in use.' } } as Awaited<ReturnType<typeof updateUser>>);
  const user = userEvent.setup(); mount();
  const email = await screen.findByRole('textbox', { name: 'Email' });
  await user.clear(email); await user.type(email, 'draft@example.com');
  await user.click(screen.getByRole('button', { name: 'Save email' }));
  await waitFor(() => expect(updateUser).toHaveBeenCalledWith({ path: { userId: 'member' }, body: { email: 'draft@example.com', newPassword: null } }));
  expect(await screen.findByText('Email is already in use.')).toBeInTheDocument();
  expect(email).toHaveValue('draft@example.com');
  expect(assignUserRole).not.toHaveBeenCalled(); expect(assignUserLibrary).not.toHaveBeenCalled();
});
