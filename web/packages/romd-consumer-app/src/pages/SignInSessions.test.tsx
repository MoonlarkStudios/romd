import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import { renderWithProviders } from '../test/utils/render';
import { SignInSessions } from './SignInSessions';

const api = vi.hoisted(() => ({
  list: vi.fn(),
  revoke: vi.fn(),
}));
vi.mock('@romd/consumer-api-client', () => ({
  listConsumerSignInSessions: api.list,
  revokeConsumerSignInSession: api.revoke,
}));

it('confirms revoking one device and leaves the other sessions active', async () => {
  const first = {
    id: 'device-1',
    device: 'Living room',
    lastUsedAt: '2026-11-23T15:00:00Z',
    status: 'Current',
  };
  const second = {
    ...first,
    id: 'device-2',
    device: 'Handheld',
  };
  const browser = {
    ...first,
    id: 'browser',
    device: 'Web browser',
    isCurrent: true,
  };
  api.list
    .mockResolvedValueOnce({
      data: {
        items: [
          first,
          second,
          browser,
        ],
        hasNextPage: false,
        nextCursor: null,
      },
    })
    .mockResolvedValue({
      data: {
        items: [
          {
            ...first,
            status: 'Revoked',
          },
          second,
          browser,
        ],
        hasNextPage: false,
        nextCursor: null,
      },
    });
  api.revoke.mockResolvedValue({});
  renderWithProviders(<SignInSessions />);
  const user = userEvent.setup();

  await user.click(
    await screen.findByRole('button', {
      name: 'Revoke Living room',
    }),
  );
  const modal = within(screen.getByRole('dialog'));
  expect(modal.getByText(/Living room/)).toBeVisible();
  expect(api.revoke).not.toHaveBeenCalled();
  await user.click(
    modal.getByRole('button', {
      name: 'Revoke session',
    }),
  );

  expect(await screen.findByText('Revoked')).toBeVisible();
  expect(api.revoke).toHaveBeenCalledWith({
    path: {
      sessionId: first.id,
    },
  });
  expect(
    screen.getByRole('button', {
      name: 'Revoke Handheld',
    }),
  ).toBeVisible();
  expect(screen.getByText('This browser')).toBeVisible();
});
