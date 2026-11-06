import { MantineProvider } from '@mantine/core';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useEffect, useState } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { ProtectedRoute } from '../components/auth/ProtectedRoute';
import { AuthProvider, useAuth } from './AuthContext';

const session = vi.hoisted(() => ({
  getUser: vi.fn(),
  getProfile: vi.fn(),
  signout: vi.fn(),
  loaded: new Set<
    (session?: {
      profile: {
        sub: string;
      };
    }) => void
  >(),
  unloaded: new Set<() => void>(),
}));

vi.mock('../auth/userManager', () => ({
  userManager: {
    getUser: session.getUser,
    signinRedirect: vi.fn(),
    signoutRedirect: session.signout,
    events: {
      addUserLoaded: (
        listener: (session?: {
          profile: {
            sub: string;
          };
        }) => void,
      ) => session.loaded.add(listener),
      removeUserLoaded: (
        listener: (session?: {
          profile: {
            sub: string;
          };
        }) => void,
      ) => session.loaded.delete(listener),
      addUserUnloaded: (listener: () => void) => session.unloaded.add(listener),
      removeUserUnloaded: (listener: () => void) => session.unloaded.delete(listener),
    },
  },
}));
vi.mock('@romd/admin-api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@romd/admin-api-client')>()),
  getMe: session.getProfile,
}));

function SessionState() {
  const auth = useAuth();
  return (
    <>
      <button
        type="button"
        onClick={() => void auth.logout()}
      >
        Sign out
      </button>
      <output>
        {JSON.stringify({
          loading: auth.isLoading,
          authenticated: auth.isAuthenticated,
          userId: auth.user?.id ?? null,
          error: auth.error,
        })}
      </output>
    </>
  );
}

beforeEach(() => {
  session.getUser.mockReset().mockResolvedValue(null);
  session.getProfile.mockReset();
  session.signout.mockReset().mockResolvedValue(undefined);
  session.loaded.clear();
  session.unloaded.clear();
});

it('initializes an absent session without loading a profile', async () => {
  render(
    <AuthProvider>
      <SessionState />
    </AuthProvider>,
  );
  expect(screen.getByRole('status')).toHaveTextContent('"loading":true');
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('"loading":false'));
  expect(screen.getByRole('status')).toHaveTextContent('"authenticated":false');
  expect(session.getProfile).not.toHaveBeenCalled();
});

it('keeps an authenticated session loading until its profile resolves and handles sign-out', async () => {
  let resolveProfile: (value: {
    data: {
      id: string;
    };
  }) => void = () => {
    throw new Error('Profile request has not started');
  };
  session.getUser.mockResolvedValue({
    expired: false,
  });
  session.getProfile.mockImplementation(
    () =>
      new Promise((resolve) => {
        resolveProfile = resolve;
      }),
  );
  render(
    <AuthProvider>
      <SessionState />
    </AuthProvider>,
  );
  await waitFor(() => expect(session.getProfile).toHaveBeenCalledOnce());
  expect(screen.getByRole('status')).toHaveTextContent('"authenticated":true');
  expect(screen.getByRole('status')).toHaveTextContent('"loading":true');
  await act(async () => {
    resolveProfile({
      data: {
        id: 'account',
      },
    });
  });
  expect(screen.getByRole('status')).toHaveTextContent('"loading":false');
  expect(screen.getByRole('status')).toHaveTextContent('"userId":"account"');
  act(() => {
    for (const listener of session.unloaded) listener();
  });
  expect(screen.getByRole('status')).toHaveTextContent('"authenticated":false');
  expect(screen.getByRole('status')).toHaveTextContent('"userId":null');
});

it('reloads the profile on a session event and removes listeners on unmount', async () => {
  const { unmount } = render(
    <AuthProvider>
      <SessionState />
    </AuthProvider>,
  );
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('"loading":false'));
  session.getUser.mockResolvedValue({
    expired: false,
  });
  session.getProfile.mockResolvedValue({
    data: {
      id: 'signed-in',
    },
  });
  await act(async () => {
    for (const listener of session.loaded) listener();
  });
  expect(screen.getByRole('status')).toHaveTextContent('"userId":"signed-in"');
  unmount();
  expect(session.loaded.size).toBe(0);
  expect(session.unloaded.size).toBe(0);
});

it.each([
  [
    'unavailable',
    () =>
      Promise.resolve({
        error: {
          status: 503,
        },
      }),
  ],
  [
    'forbidden',
    () =>
      Promise.resolve({
        error: {
          status: 403,
        },
      }),
  ],
  [
    'offline',
    () => Promise.reject(new TypeError('Network unavailable')),
  ],
])('settles a %s profile failure and can retry without signing in again', async (_name, failure) => {
  session.getUser.mockResolvedValue({
    expired: false,
  });
  session.getProfile.mockImplementationOnce(failure).mockResolvedValue({
    data: {
      id: 'recovered',
    },
  });
  render(
    <MantineProvider env="test">
      <MemoryRouter>
        <AuthProvider>
          <ProtectedRoute>
            <div>Account ready</div>
          </ProtectedRoute>
        </AuthProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
  expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load your account');
  expect(screen.queryByText('Account ready')).not.toBeInTheDocument();
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Try again',
    }),
  );
  expect(await screen.findByText('Account ready')).toBeInTheDocument();
  expect(session.getProfile).toHaveBeenCalledTimes(2);
});

it('does not restore a profile that resolves after the session is unloaded', async () => {
  let resolveProfile!: (value: {
    data: {
      id: string;
    };
  }) => void;
  session.getUser.mockResolvedValue({
    expired: false,
  });
  session.getProfile.mockImplementation(
    () =>
      new Promise((resolve) => {
        resolveProfile = resolve;
      }),
  );
  render(
    <AuthProvider>
      <SessionState />
    </AuthProvider>,
  );
  await waitFor(() => expect(session.getProfile).toHaveBeenCalledOnce());
  act(() => {
    for (const listener of session.unloaded) listener();
  });
  await act(async () => {
    resolveProfile({
      data: {
        id: 'stale',
      },
    });
  });
  expect(screen.getByRole('status')).toHaveTextContent('"loading":false');
  expect(screen.getByRole('status')).toHaveTextContent('"authenticated":false');
  expect(screen.getByRole('status')).toHaveTextContent('"userId":null');
});

it('ignores a superseded profile request when a newer session finishes first', async () => {
  let resolveOld!: (value: {
    data: {
      id: string;
    };
  }) => void;
  session.getUser.mockResolvedValue({
    expired: false,
  });
  session.getProfile
    .mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveOld = resolve;
        }),
    )
    .mockResolvedValue({
      data: {
        id: 'current',
      },
    });
  render(
    <AuthProvider>
      <SessionState />
    </AuthProvider>,
  );
  await waitFor(() => expect(session.getProfile).toHaveBeenCalledOnce());
  await act(async () => {
    for (const listener of session.loaded) listener();
  });
  expect(screen.getByRole('status')).toHaveTextContent('"userId":"current"');
  await act(async () => {
    resolveOld({
      data: {
        id: 'stale',
      },
    });
  });
  expect(screen.getByRole('status')).toHaveTextContent('"userId":"current"');
});

it('invalidates a pending profile before starting the sign-out redirect', async () => {
  let resolveProfile!: (value: {
    data: {
      id: string;
    };
  }) => void;
  session.getUser.mockResolvedValue({
    expired: false,
  });
  session.getProfile.mockImplementation(
    () =>
      new Promise((resolve) => {
        resolveProfile = resolve;
      }),
  );
  render(
    <AuthProvider>
      <SessionState />
    </AuthProvider>,
  );
  await waitFor(() => expect(session.getProfile).toHaveBeenCalledOnce());
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Sign out',
    }),
  );
  await act(async () => {
    resolveProfile({
      data: {
        id: 'stale',
      },
    });
  });
  expect(screen.getByRole('status')).toHaveTextContent('"authenticated":false');
  expect(screen.getByRole('status')).toHaveTextContent('"userId":null');
});

function Draft({ mounted, unmounted }: { mounted: () => void; unmounted: () => void }) {
  const [value, setValue] = useState('initial');
  const { user } = useAuth();
  useEffect(() => {
    mounted();
    return unmounted;
  }, [
    mounted,
    unmounted,
  ]);
  return (
    <>
      <input
        aria-label="Unsaved draft"
        value={value}
        onChange={(event) => setValue(event.currentTarget.value)}
      />
      <output aria-label="Account identity">{user?.id}</output>
    </>
  );
}

async function renderEditedDraft() {
  session.getUser.mockResolvedValue({
    expired: false,
    profile: {
      sub: 'subject-a',
    },
  });
  session.getProfile.mockResolvedValue({
    data: {
      id: 'account-a',
    },
  });
  const mounted = vi.fn();
  const unmounted = vi.fn();
  render(
    <MantineProvider env="test">
      <MemoryRouter>
        <AuthProvider>
          <ProtectedRoute>
            <Draft
              mounted={mounted}
              unmounted={unmounted}
            />
          </ProtectedRoute>
        </AuthProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
  const input = await screen.findByLabelText('Unsaved draft');
  fireEvent.change(input, {
    target: {
      value: 'unsaved edit',
    },
  });
  return {
    mounted,
    unmounted,
  };
}

it('preserves an edited protected child while the same OIDC subject silently renews', async () => {
  const { mounted, unmounted } = await renderEditedDraft();
  let finish!: (value: {
    data: {
      id: string;
    };
  }) => void;
  session.getProfile.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  await act(async () => {
    for (const listener of session.loaded)
      listener({
        profile: {
          sub: 'subject-a',
        },
      });
  });
  expect(screen.getByLabelText('Unsaved draft')).toHaveValue('unsaved edit');
  expect(unmounted).not.toHaveBeenCalled();
  await act(async () => {
    finish({
      data: {
        id: 'account-a',
      },
    });
  });
  expect(screen.getByLabelText('Unsaved draft')).toHaveValue('unsaved edit');
  expect(mounted).toHaveBeenCalledOnce();
  expect(unmounted).not.toHaveBeenCalled();
});

it('preserves same-subject content through a failed background refresh and retry', async () => {
  const { mounted, unmounted } = await renderEditedDraft();
  session.getProfile.mockResolvedValueOnce({
    error: {
      status: 503,
    },
    response: new Response(null, {
      status: 503,
    }),
  });
  await act(async () => {
    for (const listener of session.loaded)
      listener({
        profile: {
          sub: 'subject-a',
        },
      });
  });
  expect(screen.getByRole('alert')).toHaveTextContent('Account refresh failed');
  expect(screen.getByLabelText('Unsaved draft')).toHaveValue('unsaved edit');
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Try again',
    }),
  );
  await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  expect(screen.getByLabelText('Unsaved draft')).toHaveValue('unsaved edit');
  expect(mounted).toHaveBeenCalledOnce();
  expect(unmounted).not.toHaveBeenCalled();
});

it('clears old subject content before fetching a switched account profile', async () => {
  const { mounted, unmounted } = await renderEditedDraft();
  session.getUser.mockResolvedValue({
    expired: false,
    profile: {
      sub: 'subject-b',
    },
  });
  let finish!: (value: {
    data: {
      id: string;
    };
  }) => void;
  session.getProfile.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  await act(async () => {
    for (const listener of session.loaded)
      listener({
        profile: {
          sub: 'subject-b',
        },
      });
  });
  expect(screen.queryByLabelText('Unsaved draft')).not.toBeInTheDocument();
  expect(screen.queryByText('account-a')).not.toBeInTheDocument();
  expect(unmounted).toHaveBeenCalledOnce();
  await act(async () => {
    finish({
      data: {
        id: 'account-b',
      },
    });
  });
  expect(await screen.findByLabelText('Unsaved draft')).toHaveValue('initial');
  expect(screen.getByLabelText('Account identity')).toHaveTextContent('account-b');
  expect(mounted).toHaveBeenCalledTimes(2);
});

it('discards a cached profile when a background read is explicitly forbidden', async () => {
  const { unmounted } = await renderEditedDraft();
  session.getProfile.mockResolvedValueOnce({
    error: {
      status: 403,
    },
    response: new Response(null, {
      status: 403,
    }),
  });
  await act(async () => {
    for (const listener of session.loaded)
      listener({
        profile: {
          sub: 'subject-a',
        },
      });
  });
  expect(screen.queryByLabelText('Unsaved draft')).not.toBeInTheDocument();
  expect(screen.getByRole('alert')).toHaveTextContent('Unable to load your account');
  expect(unmounted).toHaveBeenCalledOnce();
});

it('lets a rejected account sign out from the full profile-error view', async () => {
  session.getUser.mockResolvedValue({
    expired: false,
    profile: {
      sub: 'subject-a',
    },
  });
  session.getProfile.mockResolvedValue({
    error: {
      status: 403,
    },
    response: new Response(null, {
      status: 403,
    }),
  });
  render(
    <MantineProvider env="test">
      <MemoryRouter>
        <AuthProvider>
          <Routes>
            <Route
              path="/"
              element={
                <ProtectedRoute>
                  <div>Account ready</div>
                </ProtectedRoute>
              }
            />
            <Route
              path="/login"
              element={<div>Sign in again</div>}
            />
          </Routes>
        </AuthProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
  expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load your account');
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Sign out',
    }),
  );
  expect(await screen.findByText('Sign in again')).toBeInTheDocument();
  expect(session.signout).toHaveBeenCalledOnce();
  expect(screen.queryByText('Account ready')).not.toBeInTheDocument();
});
