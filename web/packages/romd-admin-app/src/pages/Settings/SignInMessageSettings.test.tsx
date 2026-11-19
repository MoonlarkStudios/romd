import { client, type SignInMessageSettings as SavedSignInMessage } from '@romd/admin-api-client';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { AuthContextValue } from '../../contexts/AuthContext';
import { server } from '../../test/msw/server';
import { render } from '../../test/utils/render';
import { SignInMessageSettings } from './SignInMessageSettings';

const endpoint = 'http://localhost/api/server/sign-in-message';
const adminAuth: AuthContextValue = {
  user: {
    id: 'admin-1',
    email: 'admin@localhost',
    roles: [
      'Admin',
    ],
    libraryId: null,
  },
  isAuthenticated: true,
  isLoading: false,
  error: null,
  retry: async () => {},
  login: async () => {},
  logout: async () => {},
};

function settingsResponse(settings: SavedSignInMessage) {
  return HttpResponse.json(settings, {
    headers: {
      ETag: `"${settings.revision}"`,
    },
  });
}

beforeEach(() => {
  client.setConfig({
    baseUrl: 'http://localhost',
  });
});
afterEach(() => {
  client.setConfig({
    baseUrl: '',
  });
  vi.restoreAllMocks();
});

it('saves an optional message and submits an empty value when it is cleared', async () => {
  const requests: {
    tag: string | null;
    body: unknown;
  }[] = [];
  server.use(
    http.get(endpoint, () =>
      settingsResponse({
        revision: 'initial',
        message: '',
      }),
    ),
    http.put(endpoint, async ({ request }) => {
      requests.push({
        tag: request.headers.get('If-Match'),
        body: await request.json(),
      });
      return requests.length === 1
        ? settingsResponse({
            revision: 'saved',
            message: 'Welcome home',
          })
        : settingsResponse({
            revision: 'cleared',
            message: '',
          });
    }),
  );
  const user = userEvent.setup();
  render(<SignInMessageSettings />, {
    auth: adminAuth,
  });

  const input = await screen.findByRole('textbox', {
    name: 'Sign-in message',
  });
  const save = screen.getByRole('button', {
    name: 'Save sign-in message',
  });
  expect(input).toHaveValue('');
  expect(input).toHaveAccessibleDescription(
    'Visible to anyone who opens a ROMD sign-in page. Leave blank to hide it.',
  );
  expect(save).toBeDisabled();
  await user.type(input, 'Welcome home');
  await user.click(save);
  expect(await screen.findByText('Sign-in message saved.')).toBeVisible();

  await user.clear(input);
  await user.click(save);
  expect(await screen.findByText('Sign-in message saved.')).toBeVisible();
  expect(input).toHaveValue('');
  expect(save).toBeDisabled();
  expect(requests).toEqual([
    {
      tag: '"initial"',
      body: {
        message: 'Welcome home',
      },
    },
    {
      tag: '"saved"',
      body: {
        message: '',
      },
    },
  ]);
});

it('preserves the draft on a stale save and reloads the current saved message on request', async () => {
  let saved = {
    revision: 'initial',
    message: 'Original',
  };
  server.use(
    http.get(endpoint, () => settingsResponse(saved)),
    http.put(endpoint, () => {
      saved = {
        revision: 'updated',
        message: 'Other administrator’s text',
      };
      return HttpResponse.json(
        {
          detail: 'The sign-in message changed.',
        },
        {
          status: 412,
        },
      );
    }),
  );
  vi.spyOn(window, 'confirm').mockReturnValue(true);
  const user = userEvent.setup();
  render(<SignInMessageSettings />, {
    auth: adminAuth,
  });

  const input = await screen.findByRole('textbox', {
    name: 'Sign-in message',
  });
  await user.clear(input);
  await user.type(input, 'My draft');
  await user.click(
    screen.getByRole('button', {
      name: 'Save sign-in message',
    }),
  );

  expect(await screen.findByRole('alert')).toHaveTextContent('The sign-in message changed.');
  expect(input).toHaveValue('My draft');
  await user.click(
    screen.getByRole('button', {
      name: 'Reload saved message',
    }),
  );
  expect(await screen.findByDisplayValue(saved.message)).toBeVisible();
  expect(
    screen.getByRole('button', {
      name: 'Save sign-in message',
    }),
  ).toBeDisabled();
});
