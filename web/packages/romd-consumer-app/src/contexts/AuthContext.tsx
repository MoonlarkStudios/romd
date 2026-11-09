import type { CurrentUser } from '@romd/consumer-api-client';
import { getConsumerCurrentUser } from '@romd/consumer-api-client';
import {
  type SessionProfile,
  SessionProfileAccessDeniedError,
  type SessionProfileAdapter,
  useSessionProfile,
} from '@romd/portal-auth';
import { createContext, type ReactNode, useCallback, useContext, useMemo } from 'react';
import { userManager } from '../auth/userManager';

export interface AuthContextValue extends Omit<SessionProfile<CurrentUser>, 'clear'> {
  login: () => Promise<void>;
  logout: () => Promise<void>;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

const profileAdapter: SessionProfileAdapter<CurrentUser> = {
  getSession: async () => {
    const session = await userManager.getUser();
    return session
      ? {
          expired: session.expired,
          subject: session.profile?.sub ?? null,
        }
      : null;
  },
  loadProfile: async () => {
    const response = await getConsumerCurrentUser();
    if (
      [
        401,
        403,
        404,
      ].includes(response.response?.status ?? 0)
    ) {
      throw new SessionProfileAccessDeniedError();
    }
    return response.data;
  },
  subscribe: (onLoaded, onUnloaded) => {
    const onSessionLoaded = (session?: {
      profile?: {
        sub?: string;
      };
    }) => onLoaded(session?.profile?.sub ?? null);
    userManager.events.addUserLoaded(onSessionLoaded);
    userManager.events.addUserUnloaded(onUnloaded);
    return () => {
      userManager.events.removeUserLoaded(onSessionLoaded);
      userManager.events.removeUserUnloaded(onUnloaded);
    };
  },
};

export function AuthProvider({ children }: { children: ReactNode }) {
  const session = useSessionProfile(profileAdapter);
  const login = useCallback(
    () =>
      userManager.signinRedirect({
        state: {
          returnTo: window.location.pathname,
        },
      }),
    [],
  );
  const logout = useCallback(async () => {
    session.clear();
    await userManager.signoutRedirect();
  }, [
    session.clear,
  ]);
  const value = useMemo(
    () => ({
      user: session.user,
      isAuthenticated: session.isAuthenticated,
      isLoading: session.isLoading,
      error: session.error,
      retry: session.retry,
      login,
      logout,
    }),
    [
      session,
      login,
      logout,
    ],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth must be used within an AuthProvider');
  return context;
}
