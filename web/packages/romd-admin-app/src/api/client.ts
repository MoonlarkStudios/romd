import { client } from '@romd/admin-api-client';
import { getAccessToken, userManager } from '../auth/userManager';

// Request interceptor: inject the current OIDC access token.
client.interceptors.request.use((request) => {
  const token = getAccessToken();
  if (token) {
    request.headers.set('Authorization', `Bearer ${token}`);
  }
  return request;
});

// Response interceptor: on 401 drop the local session and surface the login page. We do NOT
// auto-redirect to the IdP here: the interactive login cookie can still be valid, so an automatic
// signinRedirect would mint a fresh code and loop. The login page waits for an explicit click.
client.interceptors.response.use((response) => {
  if (
    response.status === 401 &&
    !window.location.pathname.startsWith('/auth/') &&
    !window.location.pathname.startsWith('/login')
  ) {
    void userManager.removeUser();
    window.location.href = '/login';
  }
  return response;
});

/**
 * Back-compat accessor for callers that attach the bearer token themselves
 * (authenticated downloads, SignalR access-token factory).
 */
export function getAuthToken(): string | null {
  return getAccessToken();
}
