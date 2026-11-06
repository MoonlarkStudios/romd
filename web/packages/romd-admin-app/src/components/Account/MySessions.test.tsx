import { listMySessions } from '@romd/admin-api-client';
import { fireEvent, screen } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { MySessions } from './MySessions';

vi.mock('@romd/admin-api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@romd/admin-api-client')>()),
  listMySessions: vi.fn(),
}));

const session = {
  id: 'self',
  client: 'Current browser',
  device: 'Desktop',
  status: 'Current',
  isCurrent: true,
  createdAt: null,
  expiresAt: null,
  lastUsedAt: null,
  revokedAt: null,
  revokedBy: null,
  revocationReason: null,
};
const reply = <T,>(data: T) => ({
  data,
  error: undefined,
  request: new Request('http://localhost'),
  response: new Response(),
});

beforeEach(() => vi.resetAllMocks());

it('loads ended sessions beyond an empty intermediate page before showing the complete list', async () => {
  vi.mocked(listMySessions)
    .mockResolvedValueOnce(
      reply({
        items: [
          session,
        ],
        nextCursor: 'second',
        hasNextPage: true,
      }),
    )
    .mockResolvedValueOnce(
      reply({
        items: [],
        nextCursor: 'third',
        hasNextPage: true,
      }),
    )
    .mockResolvedValueOnce(
      reply({
        items: [
          {
            ...session,
            id: 'ended',
            client: 'Earlier console',
            status: 'Revoked',
            isCurrent: false,
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      }),
    );
  render(<MySessions />);
  fireEvent.click(
    await screen.findByRole('tab', {
      name: 'History (1)',
    }),
  );
  expect(await screen.findByText('Earlier console')).toBeInTheDocument();
  expect(listMySessions).toHaveBeenNthCalledWith(
    2,
    expect.objectContaining({
      query: {
        cursor: 'second',
        limit: 100,
      },
    }),
  );
  expect(listMySessions).toHaveBeenNthCalledWith(
    3,
    expect.objectContaining({
      query: {
        cursor: 'third',
        limit: 100,
      },
    }),
  );
});

it('reports a later-page failure instead of presenting the first page as complete', async () => {
  vi.mocked(listMySessions)
    .mockResolvedValueOnce(
      reply({
        items: [
          session,
        ],
        nextCursor: 'second',
        hasNextPage: true,
      }),
    )
    .mockResolvedValueOnce({
      data: undefined,
      error: {
        detail: 'History unavailable',
      },
      request: new Request('http://localhost'),
      response: new Response(null, {
        status: 500,
      }),
    });
  render(<MySessions />);
  expect(await screen.findByText('History unavailable')).toBeInTheDocument();
  expect(screen.queryByText('Current browser')).not.toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Retry',
    }),
  ).toBeInTheDocument();
});
