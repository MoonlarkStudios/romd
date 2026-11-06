import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import { Connections } from './Connections';

const api = vi.hoisted(() => ({
  list: vi.fn(),
  create: vi.fn(),
  rename: vi.fn(),
  rotate: vi.fn(),
  revoke: vi.fn(),
}));
vi.mock('@romd/consumer-api-client', () => ({
  listConsumerConnections: api.list,
  createConsumerConnection: api.create,
  updateConsumerConnection: api.rename,
  rotateConsumerConnection: api.rotate,
  deleteConsumerConnection: api.revoke,
}));
const connection = {
  id: 'id-1',
  name: 'Steam Deck',
  username: 'romd-user',
  status: 'Active',
  createdAt: '2026-09-28T00:00:00Z',
  credentialUpdatedAt: '2026-09-28T00:00:00Z',
  lastUsedAt: null,
  format: 'stored',
};
const credential = {
  connection,
  password: 'one-time-secret',
  endpoint: 'https://romd.test/vfs/library/',
  rcloneConfig: '[romd]\npass = obscured',
};

beforeEach(() => {
  vi.resetAllMocks();
  api.list.mockResolvedValue({
    data: {
      items: [
        connection,
      ],
      nextCursor: null,
      hasNextPage: false,
    },
  });
  api.create.mockResolvedValue({
    data: credential,
  });
  api.rotate.mockResolvedValue({
    data: credential,
  });
  api.rename.mockResolvedValue({
    data: connection,
  });
  api.revoke.mockResolvedValue({});
});
function mount() {
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  const rendered = render(
    <QueryClientProvider client={client}>
      <MantineProvider env="test">
        <Connections />
      </MantineProvider>
    </QueryClientProvider>,
  );
  return {
    client,
    ...rendered,
  };
}

it('reveals issuance once and keeps credentials out of the query cache', async () => {
  const { client } = mount();
  await screen.findByText('Steam Deck');
  expect(
    screen.getByRole('heading', {
      name: 'Steam Deck',
    }),
  ).toBeInTheDocument();
  fireEvent.click(
    screen.getByRole('button', {
      name: 'New connection',
    }),
  );
  fireEvent.change(screen.getByLabelText('Connection name'), {
    target: {
      value: 'New device',
    },
  });
  fireEvent.click(
    within(screen.getByRole('dialog')).getByRole('button', {
      name: 'New connection',
    }),
  );
  expect(await screen.findByLabelText('App password')).toHaveValue('one-time-secret');
  expect(api.create).toHaveBeenCalledWith({
    body: {
      name: 'New device',
      format: 'stored',
    },
  });
  expect(
    JSON.stringify(
      client
        .getQueryCache()
        .getAll()
        .map((query) => query.state.data),
    ),
  ).not.toContain('one-time-secret');
  expect(client.getMutationCache().getAll()).toHaveLength(0);
  fireEvent.click(
    screen.getByRole('button', {
      name: 'I have saved my connection',
    }),
  );
  await waitFor(() => expect(screen.queryByLabelText('App password')).not.toBeInTheDocument());
});

it('requires confirmation before rotation and explains that old configs stop working', async () => {
  mount();
  fireEvent.click(
    await screen.findByRole('button', {
      name: 'Replace password for Steam Deck',
    }),
  );
  expect(
    screen.getByText(/All existing copies of its password and config will stop working/),
  ).toBeInTheDocument();
  expect(api.rotate).not.toHaveBeenCalled();
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Replace password',
      exact: true,
    }),
  );
  await screen.findByLabelText('App password');
  expect(api.rotate).toHaveBeenCalledWith({
    path: {
      connectionId: 'id-1',
    },
  });
});

it('keeps failed revocation open for retry without rotating the password', async () => {
  api.list.mockResolvedValue({
    data: {
      items: [
        {
          ...connection,
          format: 'original',
        },
      ],
      nextCursor: null,
      hasNextPage: false,
    },
  });
  api.revoke
    .mockResolvedValueOnce({
      error: {
        detail: 'Please try again.',
      },
    })
    .mockResolvedValueOnce({});
  mount();
  fireEvent.click(
    await screen.findByRole('button', {
      name: 'Revoke Steam Deck',
    }),
  );
  expect(api.revoke).not.toHaveBeenCalled();
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Revoke connection',
    }),
  );
  await screen.findByText('Please try again.');
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Revoke connection',
    }),
  );
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(api.revoke).toHaveBeenCalledTimes(2);
  expect(api.revoke).toHaveBeenNthCalledWith(2, {
    path: {
      connectionId: 'id-1',
    },
  });
  expect(api.rename).not.toHaveBeenCalled();
  expect(api.create).not.toHaveBeenCalled();
  expect(api.rotate).not.toHaveBeenCalled();
  expect(screen.getByText('Media format: Original')).toBeInTheDocument();
});

it.each([
  {
    format: undefined,
    label: 'Stored',
  },
  {
    format: 'stored',
    label: 'Stored',
  },
  {
    format: 'original',
    label: 'Original',
  },
])('displays the saved media format $format, with stored as the legacy default', async ({
  format,
  label,
}) => {
  api.list.mockResolvedValue({
    data: {
      items: [
        {
          ...connection,
          format,
        },
      ],
      nextCursor: null,
      hasNextPage: false,
    },
  });
  mount();
  expect(await screen.findByText(`Media format: ${label}`)).toBeInTheDocument();
});

it('preserves an original-format creation choice through an API failure and retry', async () => {
  api.create
    .mockResolvedValueOnce({
      error: {
        detail: 'Please retry creation.',
      },
    })
    .mockResolvedValueOnce({
      data: {
        ...credential,
        connection: {
          ...connection,
          format: 'original',
        },
      },
    });
  mount();
  await screen.findByRole('heading', {
    name: 'Steam Deck',
  });
  fireEvent.click(
    screen.getByRole('button', {
      name: 'New connection',
    }),
  );
  fireEvent.change(screen.getByLabelText('Connection name'), {
    target: {
      value: 'New device',
    },
  });
  fireEvent.change(screen.getByLabelText('Media format'), {
    target: {
      value: 'original',
    },
  });
  fireEvent.click(
    within(screen.getByRole('dialog')).getByRole('button', {
      name: 'New connection',
    }),
  );
  await screen.findByText('Please retry creation.');
  expect(screen.getByLabelText('Connection name')).toHaveValue('New device');
  expect(screen.getByLabelText('Media format')).toHaveValue('original');
  expect(screen.queryByLabelText('App password')).not.toBeInTheDocument();
  fireEvent.click(
    within(screen.getByRole('dialog')).getByRole('button', {
      name: 'New connection',
    }),
  );
  await screen.findByLabelText('App password');
  expect(api.create).toHaveBeenNthCalledWith(1, {
    body: {
      name: 'New device',
      format: 'original',
    },
  });
  expect(api.create).toHaveBeenNthCalledWith(2, {
    body: {
      name: 'New device',
      format: 'original',
    },
  });
  expect(api.rename).not.toHaveBeenCalled();
  expect(api.rotate).not.toHaveBeenCalled();
});

it.each([
  'stored',
  'original',
] as const)('keeps the saved %s format when editing only the connection name', async (format) => {
  api.list.mockResolvedValue({
    data: {
      items: [
        {
          ...connection,
          format,
        },
      ],
      nextCursor: null,
      hasNextPage: false,
    },
  });
  mount();
  fireEvent.click(
    await screen.findByRole('button', {
      name: 'Edit Steam Deck',
    }),
  );
  expect(screen.getByLabelText('Media format')).toHaveValue(format);
  fireEvent.change(screen.getByLabelText('Connection name'), {
    target: {
      value: 'Renamed device',
    },
  });
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Save connection',
    }),
  );
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(api.rename).toHaveBeenCalledWith({
    path: {
      connectionId: 'id-1',
    },
    body: {
      name: 'Renamed device',
      format,
    },
  });
  expect(api.rotate).not.toHaveBeenCalled();
  expect(screen.queryByLabelText('App password')).not.toBeInTheDocument();
});

it('refreshes the saved format after editing without creating or rotating credentials', async () => {
  api.list
    .mockResolvedValueOnce({
      data: {
        items: [
          connection,
        ],
        nextCursor: null,
        hasNextPage: false,
      },
    })
    .mockResolvedValue({
      data: {
        items: [
          {
            ...connection,
            format: 'original',
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      },
    });
  mount();
  fireEvent.click(
    await screen.findByRole('button', {
      name: 'Edit Steam Deck',
    }),
  );
  fireEvent.change(screen.getByLabelText('Media format'), {
    target: {
      value: 'original',
    },
  });
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Save connection',
    }),
  );
  expect(await screen.findByText('Media format: Original')).toBeInTheDocument();
  expect(api.rename).toHaveBeenCalledWith({
    path: {
      connectionId: 'id-1',
    },
    body: {
      name: 'Steam Deck',
      format: 'original',
    },
  });
  expect(api.list).toHaveBeenCalledTimes(2);
  expect(api.rotate).not.toHaveBeenCalled();
  expect(api.create).not.toHaveBeenCalled();
});

it('keeps failed format edits open with their selected name and format for retry', async () => {
  api.rename
    .mockResolvedValueOnce({
      error: {
        detail: 'Format update unavailable.',
      },
    })
    .mockResolvedValueOnce({
      data: connection,
    });
  mount();
  fireEvent.click(
    await screen.findByRole('button', {
      name: 'Edit Steam Deck',
    }),
  );
  fireEvent.change(screen.getByLabelText('Connection name'), {
    target: {
      value: 'Edited device',
    },
  });
  fireEvent.change(screen.getByLabelText('Media format'), {
    target: {
      value: 'original',
    },
  });
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Save connection',
    }),
  );
  await screen.findByText('Format update unavailable.');
  expect(screen.getByLabelText('Connection name')).toHaveValue('Edited device');
  expect(screen.getByLabelText('Media format')).toHaveValue('original');
  expect(screen.queryByLabelText('App password')).not.toBeInTheDocument();
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Save connection',
    }),
  );
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  expect(api.rename).toHaveBeenNthCalledWith(2, {
    path: {
      connectionId: 'id-1',
    },
    body: {
      name: 'Edited device',
      format: 'original',
    },
  });
  expect(api.rotate).not.toHaveBeenCalled();
  expect(api.create).not.toHaveBeenCalled();
});

it('rotates an original-format connection without an extra format update', async () => {
  api.list.mockResolvedValue({
    data: {
      items: [
        {
          ...connection,
          format: 'original',
        },
      ],
      nextCursor: null,
      hasNextPage: false,
    },
  });
  api.rotate.mockResolvedValue({
    data: {
      ...credential,
      connection: {
        ...connection,
        format: 'original',
      },
    },
  });
  mount();
  fireEvent.click(
    await screen.findByRole('button', {
      name: 'Replace password for Steam Deck',
    }),
  );
  expect(screen.queryByLabelText('Media format')).not.toBeInTheDocument();
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Replace password',
      exact: true,
    }),
  );
  await screen.findByLabelText('App password');
  expect(api.rotate).toHaveBeenCalledWith({
    path: {
      connectionId: 'id-1',
    },
  });
  expect(api.rename).not.toHaveBeenCalled();
  expect(api.create).not.toHaveBeenCalled();
  expect(screen.getByText('Media format: Original')).toBeInTheDocument();
});
