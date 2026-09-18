# Account sessions — September 2026

ROMD now persists a session for each browser or console sign-in. The session ID survives access/refresh token rotation. OAuth grants are no longer presented as devices or sessions.

## Behavior and ownership

- Shared application interface, PostgreSQL implementation, and common host authentication handlers own the session lifecycle. Both API hosts validate the session and current account security stamp on authenticated requests and token refreshes.
- A session has a 30-day absolute lifetime. Activity cannot extend it; access and refresh token expiry are bounded by it. Last-used writes are conditional and throttled to once per five minutes across hosts.
- Browser authorization reuses its session-bound login cookie. A revoked or invalidated cookie requires credentials instead of silently creating another session.
- Sign-out and protocol token revocation end the session. Account security changes invalidate prior tokens and sessions. Requests already accepted may finish.
- My account identifies the current session and supports revoking one, all others, or all sessions. User administration uses the same presentation. All current sessions remain visible; history includes the latest 200 ended sessions.
- Browser/OS descriptions and console-supplied device labels aid recognition; they are unverified labels. Last used is activity, not online presence. No IP addresses or raw user agents are retained.
- Console sign-out attempts remote revocation and always clears local credentials. If remote revocation fails, it asks the user to have an administrator revoke the session. Existing console storage keys remain unchanged.

## Deployment and migration

Schema version 22 introduces `AccountSessions`. Stop both API hosts before the worker applies `ExplicitAccountSessions`, then start matching admin and consumer images. The migration revokes existing valid OpenIddict tokens and authorizations: all existing clients must sign in again. It deliberately does not infer session identity from legacy grants. Rolling back the table does not restore revoked credentials.

The consumer runtime receives session SELECT/INSERT/UPDATE permissions through the worker's schema provisioner; management account links and audit tables remain unavailable to it. The session record stores revocation time, actor, and reason separately from general administration audit events.

API clients: admin — regenerated session metadata and the revoke-other-sessions query. Consumer generated API contract unchanged; browser OIDC configuration and console protocol calls updated.

## Verification

- `mise run test`: 2,698 passed after adding the new fixture dependencies.
- `mise run test:integration`: initial run 526 passed; final run with the added rotation/sign-out edge case had 526 passed and one backup-restore worker startup failure. That restore test passed in isolation (1 test); details are in `docs/known-issues.md`.
- Web: production build and lint passed; 634 tests passed. Existing bundle-size and React test `act` warnings remain.
- Console: analyze passed; full suite 1,225 passed, followed by 29 focused tests including the added protocol revocation test and in-flight refresh sign-out assertion.
- Generated API clients were regenerated from backend OpenAPI. No generated files were hand-edited.
- Docker: all three review images built; migration reached schema 22 with zero session rows and zero remaining legacy valid tokens. Both API readiness endpoints returned healthy. Browser reload correctly required fresh login. Automatic approval review blocked submitting the stored test credential, so the signed-in visual smoke check remains for the user.
