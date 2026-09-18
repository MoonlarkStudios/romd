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
  userStore: new WebStorageStateStore({ store: window.sessionStorage }),
});

let currentAccessToken: string | null = null;

function cacheAccessToken(user: User | null): void {
  currentAccessToken = user && !user.expired ? user.access_token : null;
}

userManager.events.addUserLoaded(cacheAccessToken);
userManager.events.addUserUnloaded(() => cacheAccessToken(null));
void userManager.getUser().then(cacheAccessToken).catch(() => cacheAccessToken(null));

/**
 * Returns the current access token for non-React callers (API client, downloads).
 * Kept fresh by automatic silent renew; null when signed out.
 */
export function getAccessToken(): string | null {
  return currentAccessToken;
}
