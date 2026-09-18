import type { CurrentUserDto } from '@romd/consumer-api-client';
import { getConsumerCurrentUser } from '@romd/consumer-api-client';
import { createContext, type ReactNode, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { userManager } from '../auth/userManager';

interface AuthContextValue {
  user: CurrentUserDto | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login: () => Promise<void>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

interface AuthProviderProps {
  children: ReactNode;
}

export function AuthProvider({ children }: AuthProviderProps) {
  const [user, setUser] = useState<CurrentUserDto | null>(null);
  // hasSession tracks the OIDC token independently of the profile fetch so the post-callback
  // window (token present, profile pending) is treated as loading rather than unauthenticated.
  const [hasSession, setHasSession] = useState(false);
  const [isInitialized, setIsInitialized] = useState(false);

  useEffect(() => {
    let active = true;

    const loadProfile = async () => {
      try {
        const oidcUser = await userManager.getUser();
        if (!active) {
          return;
        }

        if (oidcUser && !oidcUser.expired) {
          setHasSession(true);
          const response = await getConsumerCurrentUser();
          if (active && response.data) {
            setUser(response.data);
          }
        } else {
          setHasSession(false);
          setUser(null);
        }
      } catch {
        // Treat any failure resolving the session or profile as signed out.
        if (active) {
          setHasSession(false);
          setUser(null);
        }
      } finally {
        if (active) {
          setIsInitialized(true);
        }
      }
    };

    void loadProfile();

    const onUserLoaded = () => {
      void loadProfile();
    };
    const onUserUnloaded = () => {
      setHasSession(false);
      setUser(null);
    };

    userManager.events.addUserLoaded(onUserLoaded);
    userManager.events.addUserUnloaded(onUserUnloaded);

    return () => {
      active = false;
      userManager.events.removeUserLoaded(onUserLoaded);
      userManager.events.removeUserUnloaded(onUserUnloaded);
    };
  }, []);

  const login = useCallback(
    () => userManager.signinRedirect({ state: { returnTo: window.location.pathname } }),
    [],
  );

  const logout = useCallback(async () => {
    setUser(null);
    setHasSession(false);
    await userManager.signoutRedirect();
  }, []);

  const value = useMemo(
    () => ({
      user,
      isAuthenticated: hasSession,
      // While a session exists but the profile is still loading, stay in the loading state so
      // protected routes show a spinner instead of bouncing to a fresh sign-in.
      isLoading: !isInitialized || (hasSession && user === null),
      login,
      logout,
    }),
    [user, hasSession, isInitialized, login, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth must be used within an AuthProvider');
  }

  return context;
}
