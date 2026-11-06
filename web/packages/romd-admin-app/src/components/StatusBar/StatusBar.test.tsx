import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, screen } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import { beforeEach, describe, expect, it } from 'vitest';
import { dashboardStatusBarFixture } from '../../test/msw/fixtures/diagnostics';
import { server } from '../../test/msw/server';
import { render } from '../../test/utils/render';
import { ServerStatus } from './ServerStatus';
import { StatusBar } from './StatusBar';

const ready = {
  status: 'ready',
  checks: [
    {
      name: 'database',
      status: 'healthy',
    },
    {
      name: 'worker',
      status: 'healthy',
    },
    {
      name: 'disk',
      status: 'healthy',
    },
  ],
};
const health = {
  ...dashboardStatusBarFixture.health,
  unidentifiedCount: 0,
  unroutedCount: 0,
};

beforeEach(() => {
  server.use(http.get('*/health/ready', () => HttpResponse.json(ready)));
});

describe('StatusBar', () => {
  it('distinguishes library identification and routing from server readiness', async () => {
    server.use(
      http.get('*/health/ready', () =>
        HttpResponse.json({
          ...ready,
          status: 'degraded',
          checks: [
            {
              name: 'worker',
              status: 'degraded',
            },
          ],
        }),
      ),
    );
    render(
      <StatusBar
        health={health}
        coverage={undefined}
        storage={undefined}
        isLoading={false}
        isError={false}
      />,
    );
    expect(
      screen.getByRole('link', {
        name: 'Library: identified & routed',
      }),
    ).toHaveAttribute('href', '/roms?tab=all');
    expect(
      await screen.findByRole('link', {
        name: 'Server: degraded. Open diagnostics',
      }),
    ).toHaveAttribute('href', '/diagnostics');
    expect(screen.queryByText('Healthy')).not.toBeInTheDocument();
    expect(screen.queryByText('All systems healthy')).not.toBeInTheDocument();
  });

  it.each([
    {
      isLoading: true,
      isError: false,
      health: undefined,
      state: 'loading',
    },
    {
      isLoading: false,
      isError: true,
      health: undefined,
      state: 'unavailable',
    },
    {
      isLoading: false,
      isError: false,
      health: undefined,
      state: 'unavailable',
    },
    {
      isLoading: false,
      isError: true,
      health,
      state: 'stale',
    },
  ])('reports $state without inventing healthy or zero-valued observations', async (props) => {
    render(
      <StatusBar
        {...props}
        coverage={undefined}
        storage={undefined}
      />,
    );
    expect(
      screen.getByRole('link', {
        name: `Library: ${props.state}`,
      }),
    ).toBeInTheDocument();
    expect(screen.queryByText('0.0%')).not.toBeInTheDocument();
    expect(screen.queryByText('0 B')).not.toBeInTheDocument();
    expect(
      await screen.findByRole('link', {
        name: 'Server: ready. Open diagnostics',
      }),
    ).toBeInTheDocument();
  });
});

describe('ServerStatus', () => {
  it('reads degraded readiness from an HTTP 200 body without credentials', async () => {
    let credentials: RequestCredentials | undefined;
    let authorization: string | null = null;
    server.use(
      http.get('*/health/ready', ({ request }) => {
        credentials = request.credentials;
        authorization = request.headers.get('Authorization');
        return HttpResponse.json({
          ...ready,
          status: 'degraded',
          checks: [
            {
              name: 'disk',
              status: 'degraded',
            },
          ],
        });
      }),
    );
    render(<ServerStatus />);
    expect(
      await screen.findByRole('link', {
        name: 'Server: degraded. Open diagnostics',
      }),
    ).toBeInTheDocument();
    expect(credentials).toBe('omit');
    expect(authorization).toBeNull();
  });

  it('reports an HTTP 503 unready body as unready', async () => {
    server.use(
      http.get('*/health/ready', () =>
        HttpResponse.json(
          {
            status: 'unready',
            checks: [
              {
                name: 'database',
                status: 'unready',
              },
            ],
          },
          {
            status: 503,
          },
        ),
      ),
    );
    render(<ServerStatus />);
    expect(
      await screen.findByRole('link', {
        name: 'Server: unready. Open diagnostics',
      }),
    ).toBeInTheDocument();
  });

  it.each([
    {
      body: {
        status: 'Healthy',
      },
      status: 200,
    },
    {
      body: {
        status: 'ready',
        checks: [],
      },
      status: 200,
    },
    {
      body: {
        status: 'ready',
        checks: [
          {
            name: 'disk',
            status: 'degraded',
          },
        ],
      },
      status: 200,
    },
    {
      body: ready,
      status: 500,
    },
  ])('refuses malformed or contradictory readiness: $body', async ({ body, status }) => {
    server.use(
      http.get('*/health/ready', () =>
        HttpResponse.json(body, {
          status,
        }),
      ),
    );
    render(<ServerStatus />);
    expect(
      await screen.findByRole('link', {
        name: 'Server: unavailable. Open diagnostics',
      }),
    ).toBeInTheDocument();
  });

  it('marks a previous ready result stale when a refresh fails', async () => {
    const client = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    const view = render(
      <QueryClientProvider client={client}>
        <ServerStatus />
      </QueryClientProvider>,
    );
    expect(
      await screen.findByRole('link', {
        name: 'Server: ready. Open diagnostics',
      }),
    ).toBeInTheDocument();
    server.use(http.get('*/health/ready', () => HttpResponse.error()));
    await act(() =>
      client.refetchQueries({
        queryKey: [
          'server-readiness',
        ],
      }),
    );
    expect(
      await screen.findByRole('link', {
        name: 'Server: stale. Open diagnostics',
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('link', {
        name: 'Server: ready. Open diagnostics',
      }),
    ).not.toBeInTheDocument();
    view.unmount();
    client.clear();
  });
});
