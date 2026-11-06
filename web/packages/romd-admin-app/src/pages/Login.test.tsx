import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import { render } from '../test/utils/render';
import { Login } from './Login';

it('explains a failed sign-in redirect and lets the person retry', async () => {
  const user = userEvent.setup();
  const login = vi
    .fn()
    .mockRejectedValueOnce(new Error('Network unavailable'))
    .mockResolvedValue(undefined);
  render(<Login />, {
    auth: {
      user: null,
      isAuthenticated: false,
      isLoading: false,
      login,
      logout: async () => {},
    },
  });

  await user.click(
    screen.getByRole('button', {
      name: 'Sign in',
    }),
  );
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'The sign-in service could not be reached. Try again.',
  );
  const retry = screen.getByRole('button', {
    name: 'Sign in',
  });
  expect(retry).toBeEnabled();
  await user.click(retry);
  expect(login).toHaveBeenCalledTimes(2);
});
