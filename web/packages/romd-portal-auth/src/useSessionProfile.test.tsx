import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { StrictMode } from 'react';
import { afterEach, expect, it, vi } from 'vitest';
import { type SessionProfileAdapter, useSessionProfile } from './useSessionProfile';

afterEach(cleanup);

function createAdapter() {
  const loaded = new Set<(subject: string | null) => void>();
  const unloaded = new Set<() => void>();
  const adapter = {
    getSession: vi
      .fn<
        SessionProfileAdapter<{
          id: string;
        }>['getSession']
      >()
      .mockResolvedValue({
        expired: false,
        subject: 'account',
      }),
    loadProfile: vi
      .fn<
        SessionProfileAdapter<{
          id: string;
        }>['loadProfile']
      >()
      .mockResolvedValue({
        id: 'account',
      }),
    subscribe: (onLoaded: (subject: string | null) => void, onUnloaded: () => void) => {
      loaded.add(onLoaded);
      unloaded.add(onUnloaded);
      return () => {
        loaded.delete(onLoaded);
        unloaded.delete(onUnloaded);
      };
    },
  } satisfies SessionProfileAdapter<{
    id: string;
  }>;
  return {
    adapter,
    loaded,
    unloaded,
  };
}

it('can recover a failed session lookup without treating it as a completed sign-out', async () => {
  const { adapter } = createAdapter();
  adapter.getSession.mockRejectedValueOnce(new Error('Session storage unavailable'));
  const { result } = renderHook(() => useSessionProfile(adapter));
  await waitFor(() => expect(result.current.isLoading).toBe(false));
  expect(result.current.error).toBeTruthy();
  expect(result.current.isAuthenticated).toBe(false);
  expect(adapter.loadProfile).not.toHaveBeenCalled();
  await act(() => result.current.retry());
  expect(result.current.user?.id).toBe('account');
  expect(result.current.error).toBeNull();
});

it('owns only the live subscription and initialization under StrictMode remount', async () => {
  const { adapter, loaded, unloaded } = createAdapter();
  const { result, unmount } = renderHook(() => useSessionProfile(adapter), {
    wrapper: StrictMode,
  });
  await waitFor(() => expect(result.current.user?.id).toBe('account'));
  expect(adapter.loadProfile).toHaveBeenCalledOnce();
  expect(loaded.size).toBe(1);
  expect(unloaded.size).toBe(1);
  unmount();
  expect(loaded.size).toBe(0);
  expect(unloaded.size).toBe(0);
});

it('does not replace a newer successful profile with an older failed request', async () => {
  const { adapter, loaded } = createAdapter();
  let rejectOld!: (error: Error) => void;
  adapter.loadProfile.mockImplementationOnce(
    () =>
      new Promise((_resolve, reject) => {
        rejectOld = reject;
      }),
  );
  const { result } = renderHook(() => useSessionProfile(adapter));
  await waitFor(() => expect(adapter.loadProfile).toHaveBeenCalledOnce());
  await act(async () => {
    for (const listener of loaded) listener('account');
  });
  expect(result.current.user?.id).toBe('account');
  await act(async () => {
    rejectOld(new Error('Old request failed'));
  });
  expect(result.current.error).toBeNull();
  expect(result.current.user?.id).toBe('account');
});

it('rechecks the resolved subject before retaining a profile suggested by a loaded event', async () => {
  const { adapter, loaded } = createAdapter();
  const { result } = renderHook(() => useSessionProfile(adapter));
  await waitFor(() => expect(result.current.user?.id).toBe('account'));
  adapter.getSession.mockResolvedValue({
    expired: false,
    subject: 'different-account',
  });
  let finish!: (value: { id: string }) => void;
  adapter.loadProfile.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  await act(async () => {
    for (const listener of loaded) listener('account');
  });
  expect(result.current.user).toBeNull();
  expect(result.current.isLoading).toBe(true);
  await act(async () => {
    finish({
      id: 'different-account',
    });
  });
  expect(result.current.user?.id).toBe('different-account');
});
