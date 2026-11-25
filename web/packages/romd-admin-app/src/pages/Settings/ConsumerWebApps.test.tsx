import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { ConsumerWebApps } from './ConsumerWebApps';

const api = vi.hoisted(() => ({
  list: vi.fn(),
  register: vi.fn(),
  disable: vi.fn(),
}));
vi.mock('@romd/admin-api-client', () => ({
  listConsumerWebApps: api.list,
  registerConsumerWebApp: api.register,
  disableConsumerWebApp: api.disable,
}));
const app = {
  clientId: 'romd-web-app',
  displayName: 'Family games',
  callbackUri: 'https://games.example/auth/callback',
  browserOrigin: 'https://games.example',
  status: 'Active',
};
const failed = {
  ...app,
  displayName: 'Another app',
  clientId: 'romd-web-failed',
  status: 'ProvisioningFailed',
};

beforeEach(() => {
  vi.resetAllMocks();
});

function mount() {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <ConsumerWebApps />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

it('registers the exact deployment details and shows the assigned client ID', async () => {
  api.list.mockResolvedValue({
    data: {
      items: [],
      hasNextPage: false,
    },
  });
  api.register.mockResolvedValue({
    data: {
      ...app,
      status: 'Pending',
    },
  });
  mount();
  const user = userEvent.setup();
  await user.type(screen.getByLabelText(/Display name/), app.displayName);
  await user.type(screen.getByLabelText(/HTTPS callback URL/), app.callbackUri);
  await user.type(screen.getByLabelText(/HTTPS browser origin/), app.browserOrigin);
  await user.click(
    screen.getByRole('button', {
      name: 'Register web app',
    }),
  );
  expect(await screen.findByText(app.clientId)).toBeVisible();
  expect(api.register).toHaveBeenCalledWith({
    body: {
      displayName: app.displayName,
      callbackUri: app.callbackUri,
      browserOrigin: app.browserOrigin,
    },
  });
});

it('pages registrations, explains a setup failure, and confirms disabling the selected app', async () => {
  let status = failed.status;
  api.list.mockImplementation(({ query }) =>
    Promise.resolve({
      data: query.cursor
        ? {
            items: [
              {
                ...failed,
                status,
              },
            ],
            hasNextPage: false,
          }
        : {
            items: [
              app,
            ],
            hasNextPage: true,
            nextCursor: 'next',
          },
    }),
  );
  api.disable.mockImplementation(() => {
    status = 'Disabled';
    return Promise.resolve({});
  });

  mount();
  const user = userEvent.setup();
  expect(await screen.findByText(app.displayName)).toBeVisible();
  await user.click(
    screen.getByRole('button', {
      name: 'Next',
    }),
  );
  expect(await screen.findByText('Setup failed')).toBeVisible();
  expect(screen.getByText(/will retry automatically/)).toBeVisible();

  await user.click(
    screen.getByRole('button', {
      name: `Disable ${failed.displayName}`,
    }),
  );
  const modal = within(screen.getByRole('dialog'));
  expect(modal.getByText(/end all of its ROMD sign-ins/)).toBeVisible();
  expect(api.disable).not.toHaveBeenCalled();
  await user.click(
    modal.getByRole('button', {
      name: 'Disable web app',
    }),
  );
  expect(await screen.findByText('Disabled')).toBeVisible();
  expect(api.disable).toHaveBeenCalledWith({
    path: {
      clientId: failed.clientId,
    },
  });

  await user.click(
    screen.getByRole('button', {
      name: 'Previous',
    }),
  );
  expect(await screen.findByText(app.displayName)).toBeVisible();
  expect(screen.queryByText(failed.displayName)).not.toBeInTheDocument();
});
