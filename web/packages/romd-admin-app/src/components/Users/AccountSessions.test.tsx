import { MantineProvider } from '@mantine/core';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import { AccountSessions } from './AccountSessions';

it('separates ended sessions and preserves the current session when revoking others', async () => {
  const onRevoke = vi.fn();
  const onRevokeOthers = vi.fn();
  const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
  render(<MantineProvider><AccountSessions pending={false} onRevoke={onRevoke} onRevokeOthers={onRevokeOthers} sessions={[
    { id: 'this', client: 'ROMD Admin', device: 'Firefox on Linux', status: 'Current', isCurrent: true },
    { id: 'other', client: 'ROMD Console', device: 'Living room', status: 'Current' },
    { id: 'ended', client: 'ROMD Admin', device: 'Old browser', status: 'Revoked' },
  ]} /></MantineProvider>);
  expect(screen.getByText('This session')).toBeInTheDocument();
  expect(screen.queryByText('Old browser')).not.toBeInTheDocument();
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Revoke other sessions' }));
  expect(onRevokeOthers).toHaveBeenCalledOnce();
  expect(onRevoke).not.toHaveBeenCalled();
  await user.click(screen.getByRole('button', { name: 'Sign out' }));
  expect(onRevoke).toHaveBeenCalledWith('this');
  await user.click(screen.getByRole('tab', { name: 'History (1)' }));
  expect(screen.getByText('Old browser')).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Revoke', exact: true })).not.toBeInTheDocument();
  confirm.mockRestore();
});
