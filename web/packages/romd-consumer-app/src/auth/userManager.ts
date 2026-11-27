import { type User, UserManager, WebStorageStateStore } from 'oidc-client-ts';

// Authority is the current origin: in production the consumer host serves the SPA, /connect, and
// discovery; in dev Vite proxies /connect and /.well-known to the consumer host (see vite.config.ts)
// so everything stays same-origin and no CORS is required. Refresh tokens (offline_access) drive
// silent renew, so no hidden-iframe silent-callback page is needed.
export const userManager = new UserManager({
  authority: window.location.origin,
  client_id: 'romd-consumer-spa',
  redirect_uri: `${window.location.origin}/auth/callback`,
  post_logout_redirect_uri: window.location.origin,
  response_type: 'code',
  scope: 'openid profile email roles offline_access',
  automaticSilentRenew: true,
  revokeTokensOnSignout: true,
  // sessionStorage keeps tokens per-tab and clears on close, limiting XSS persistence.
  userStore: new WebStorageStateStore({
    store: window.sessionStorage,
  }),
});

let receivedSessionUpdate = false;
let currentAccessToken: string | null = null;
let currentSession: {
  subject: string;
} | null = null;

export function clearCachedSession(): void {
  receivedSessionUpdate = true;
  currentAccessToken = null;
  currentSession = null;
}

function cacheAccessToken(user: User | null): void {
  receivedSessionUpdate = true;
  if (!user || user.expired || !user.profile.sub) {
    clearCachedSession();
    return;
  }
  currentAccessToken = user.access_token;
  if (currentSession?.subject !== user.profile.sub) {
    currentSession = {
      subject: user.profile.sub,
    };
  }
}

userManager.events.addUserLoaded(cacheAccessToken);
userManager.events.addUserUnloaded(clearCachedSession);
void userManager
  .getUser()
  .then((user) => {
    if (!receivedSessionUpdate) cacheAccessToken(user);
  })
  .catch(() => {
    if (!receivedSessionUpdate) clearCachedSession();
  });

/**
 * Returns the current access token for non-React callers (API client, downloads).
 * Kept fresh by automatic silent renew; null when signed out.
 */
export function getAccessToken(): string | null {
  return currentAccessToken;
}

/** Bind a private request to the active account, including across silent token renewal. */
export function bindAccountRequest(accountId: string) {
  const session = currentSession;
  const isCurrent = () => session?.subject === accountId && session === currentSession;
  const assertCurrent = () => {
    if (!isCurrent()) throw new DOMException('The account session has changed.', 'AbortError');
  };
  const fetch: typeof globalThis.fetch = async (input, init) => {
    // The generated client sets Authorization before calling fetch. Never send a
    // request from an old screen with the new account's bearer token.
    assertCurrent();
    const response = await globalThis.fetch(input, init);
    // Withhold obsolete responses from the client's response interceptors too.
    assertCurrent();
    return response;
  };
  return {
    fetch,
    isCurrent,
  };
}
