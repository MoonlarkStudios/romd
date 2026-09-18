import {
  createSystemHubConnection,
  type HubConnection,
  type LibraryUpdatedRealtimePayload,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { libraryKeys } from '../api/useUsers';
import { useSystemSocket } from './useSystemSocket';

const { getAuthTokenMock } = vi.hoisted(() => ({
  getAuthTokenMock: vi.fn(() => 'admin-token'),
}));

vi.mock('@romd/admin-api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@romd/admin-api-client')>()),
  createSystemHubConnection: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../api/client')>()),
  getAuthToken: getAuthTokenMock,
}));

type RealtimeHandler = (...arguments_: unknown[]) => void;

describe('useSystemSocket', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getAuthTokenMock.mockReturnValue('admin-token');
  });

  it('accepts the opaque LibraryUpdated v2 payload and invalidates library queries', async () => {
    const handlers = new Map<string, RealtimeHandler>();
    const connection = {
      on: vi.fn((eventName: string, handler: RealtimeHandler) => {
        handlers.set(eventName, handler);
      }),
      onreconnecting: vi.fn(),
      onreconnected: vi.fn(),
      onclose: vi.fn(),
      start: vi.fn().mockResolvedValue(undefined),
      stop: vi.fn().mockResolvedValue(undefined),
      invoke: vi.fn().mockResolvedValue(undefined),
    } as unknown as HubConnection;
    vi.mocked(createSystemHubConnection).mockResolvedValue(connection);
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const invalidateQueries = vi.spyOn(queryClient, 'invalidateQueries');
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );

    const { result } = renderHook(() => useSystemSocket(true), { wrapper });
    await waitFor(() => expect(result.current.status).toBe('connected'));
    invalidateQueries.mockClear();

    const payload: LibraryUpdatedRealtimePayload = {
      libraryId: 'opaque-library',
      name: 'Curated',
      needsMaterialization: false,
      itemCount: 12,
      configurationState: 'Valid',
    };
    act(() => handlers.get('LibraryUpdated')?.(payload, 2));

    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: libraryKeys.all });
  });
});
