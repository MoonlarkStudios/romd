import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

/** Each portal maps its session transport and events to a stable account subject. */
export interface SessionProfileAdapter<TUser> {
  getSession: () => Promise<{
    expired?: boolean;
    subject: string | null;
  } | null>;
  loadProfile: () => Promise<TUser | null | undefined>;
  subscribe: (onLoaded: (subject: string | null) => void, onUnloaded: () => void) => () => void;
}

/** A transport adapter uses this when retaining an earlier profile would be unsafe. */
export class SessionProfileAccessDeniedError extends Error {}

export interface SessionProfile<TUser> {
  user: TUser | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  error: string | null;
  retry: () => Promise<void>;
  clear: () => void;
}

interface ReadyProfile<TUser> {
  subject: string | null;
  user: TUser;
}

type ProfileState<TUser> =
  | {
      status: 'checking-session';
    }
  | {
      status: 'loading-profile';
    }
  | {
      status: 'signed-out';
    }
  | {
      status: 'ready';
      profile: ReadyProfile<TUser>;
      failedRefresh: boolean;
    }
  | {
      status: 'error';
      hasSession: boolean;
    };

/** Owns request ordering and subscription lifetime independently of OIDC or an API client. */
export function useSessionProfile<TUser>(
  adapter: SessionProfileAdapter<TUser>,
): SessionProfile<TUser> {
  const [state, setState] = useState<ProfileState<TUser>>({
    status: 'checking-session',
  });
  const profile = useRef<ReadyProfile<TUser> | null>(null);
  const mounted = useRef(false);
  const request = useRef(0);

  const load = useCallback(
    async (loadedSubject?: string | null) => {
      const generation = ++request.current;
      const isCurrent = () => mounted.current && request.current === generation;
      const previous = profile.current;
      let retained =
        previous?.subject != null &&
        (loadedSubject === undefined || loadedSubject === previous.subject)
          ? previous
          : null;
      let hasSession = false;
      if (retained)
        setState({
          status: 'ready',
          profile: retained,
          failedRefresh: false,
        });
      else {
        profile.current = null;
        setState({
          status: 'checking-session',
        });
      }
      try {
        const session = await adapter.getSession();
        if (!isCurrent()) return;
        if (!session || session.expired) {
          profile.current = null;
          setState({
            status: 'signed-out',
          });
          return;
        }
        hasSession = true;
        if (!retained || retained.subject !== session.subject) {
          retained = null;
          profile.current = null;
          setState({
            status: 'loading-profile',
          });
        }
        const user = await adapter.loadProfile();
        if (!isCurrent()) return;
        if (user == null) throw new Error('Account profile was not returned');
        const loaded = {
          subject: session.subject,
          user,
        };
        profile.current = loaded;
        setState({
          status: 'ready',
          profile: loaded,
          failedRefresh: false,
        });
      } catch (error) {
        if (!isCurrent()) return;
        if (retained && !(error instanceof SessionProfileAccessDeniedError)) {
          setState({
            status: 'ready',
            profile: retained,
            failedRefresh: true,
          });
        } else {
          profile.current = null;
          setState({
            status: 'error',
            hasSession,
          });
        }
      }
    },
    [
      adapter,
    ],
  );

  const clear = useCallback(() => {
    ++request.current;
    profile.current = null;
    setState({
      status: 'signed-out',
    });
  }, []);

  useEffect(() => {
    mounted.current = true;
    void load();
    const unsubscribe = adapter.subscribe((subject) => void load(subject), clear);
    return () => {
      mounted.current = false;
      ++request.current;
      unsubscribe();
    };
  }, [
    adapter,
    load,
    clear,
  ]);

  return useMemo(
    () => ({
      user: state.status === 'ready' ? state.profile.user : null,
      isAuthenticated:
        state.status === 'ready' ||
        state.status === 'loading-profile' ||
        (state.status === 'error' && state.hasSession),
      isLoading: state.status === 'checking-session' || state.status === 'loading-profile',
      error:
        state.status === 'error' || (state.status === 'ready' && state.failedRefresh)
          ? 'Unable to load your account. Check your connection and try again.'
          : null,
      retry: load,
      clear,
    }),
    [
      state,
      load,
      clear,
    ],
  );
}
