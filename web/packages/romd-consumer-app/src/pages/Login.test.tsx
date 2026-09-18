import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { userManager } from '../auth/userManager';
import { render } from '../test/utils/render';
import { Login } from './Login';

vi.mock('../auth/userManager', () => ({
  userManager: {
    signinRedirect: vi.fn().mockResolvedValue(undefined),
    getUser: vi.fn().mockResolvedValue(null),
    events: {
      addUserLoaded: vi.fn(),
      addUserUnloaded: vi.fn(),
      removeUserLoaded: vi.fn(),
      removeUserUnloaded: vi.fn(),
    },
  },
  getAccessToken: () => null,
}));

describe('Login', () => {
  it('starts the OpenIddict sign-in redirect when the button is clicked', async () => {
    const user = userEvent.setup();
    render(<Login />);

    await user.click(await screen.findByRole('button', { name: /sign in/i }));

    await waitFor(() => {
      expect(vi.mocked(userManager.signinRedirect)).toHaveBeenCalled();
    });
  });
});
