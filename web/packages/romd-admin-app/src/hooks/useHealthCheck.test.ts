import { healthCheck } from '@romd/admin-api-client';
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, type Mock, vi } from 'vitest';
import { useHealthCheck } from './useHealthCheck';

vi.mock('@romd/admin-api-client', () => ({
  healthCheck: vi.fn(),
}));

const mockHealthCheck = healthCheck as Mock;

describe('useHealthCheck', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('starts in loading state', () => {
    mockHealthCheck.mockImplementation(() => new Promise(() => {})); // Never resolves

    const { result } = renderHook(() => useHealthCheck());

    expect(result.current.status).toBe('loading');
    expect(result.current.data).toBeNull();
    expect(result.current.error).toBeNull();
  });

  it('returns healthy status on successful fetch', async () => {
    const mockData = {
      status: 'healthy',
      timestamp: '2024-01-01T00:00:00Z',
    };

    mockHealthCheck.mockResolvedValue({
      data: mockData,
      error: null,
    });

    const { result } = renderHook(() => useHealthCheck());

    await waitFor(() => {
      expect(result.current.status).toBe('healthy');
    });

    expect(result.current.data).toEqual(mockData);
    expect(result.current.error).toBeNull();
  });

  it('returns error status on failed fetch', async () => {
    mockHealthCheck.mockRejectedValue(new Error('Network error'));

    const { result } = renderHook(() => useHealthCheck());

    await waitFor(() => {
      expect(result.current.status).toBe('error');
    });

    expect(result.current.data).toBeNull();
    expect(result.current.error).toBe('Network error');
  });

  it('returns error status on error response', async () => {
    mockHealthCheck.mockResolvedValue({
      data: null,
      error: { status: 500 },
    });

    const { result } = renderHook(() => useHealthCheck());

    await waitFor(() => {
      expect(result.current.status).toBe('error');
    });

    expect(result.current.error).toBe('Health check failed');
  });

  it('refresh function refetches data', async () => {
    const mockData = {
      status: 'healthy',
      timestamp: '2024-01-01T00:00:00Z',
    };

    mockHealthCheck.mockResolvedValue({
      data: mockData,
      error: null,
    });

    const { result } = renderHook(() => useHealthCheck());

    await waitFor(() => {
      expect(result.current.status).toBe('healthy');
    });

    expect(mockHealthCheck).toHaveBeenCalledTimes(1);

    act(() => {
      result.current.refresh();
    });

    await waitFor(() => {
      expect(mockHealthCheck).toHaveBeenCalledTimes(2);
    });
  });
});
