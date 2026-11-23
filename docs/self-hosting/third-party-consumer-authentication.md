# Connect a third-party ROMD client

Use the public HTTPS address of the consumer host. Members sign in on the ROMD
host with their existing account. Applications can browse and download only
from the member's currently assigned library.

## Pair a console or native app

1. In the app, enter the ROMD consumer address and start pairing.
2. Open the ROMD verification link on a phone or another browser. Enter the
   code if the link did not fill it in.
3. Compare the displayed code with the device in front of you, read the
   requested permissions, and select **Link device** after entering your ROMD
   credentials. The app finishes pairing when it next polls the server.

The ROMD Console pairing screen includes permission to sync play sessions.
You can view and clear synced play history from the ROMD consumer portal.

ROMD shows the app and device names supplied by the client. Use the code to
confirm the pairing. In the ROMD consumer portal, open the account menu and
choose **Sign-in sessions** to identify and revoke a device. Revocation ends
that pairing immediately, including refresh. Other sessions stay signed in.

## Approve a separately hosted web app

1. Open **Admin → Settings → Consumer web apps**.
2. Enter the display name, exact HTTPS callback URL and HTTPS browser origin.
   For example, enter `https://games.example.org/auth/callback` and
   `https://games.example.org`. The origin contains the scheme, hostname and
   non-default port, when used.
3. Select **Register web app**. Wait for the registration to show **Active**,
   then copy its client ID into the app's deployment configuration. Use the
   ROMD consumer address as the issuer. If the registration shows **Setup failed**,
   the worker will retry. Check the worker logs using the client ID if setup keeps
   failing.
4. Open the app and sign in. ROMD shows the registered app name and origin on
   the sign-in page.

Select **Disable** in Admin Settings to end the registration for everyone.
Disablement prevents new authorization, pending code exchange, refresh and
authenticated API access. Members can also revoke their individual sessions.
Register a new client ID if the deployment changes. Registrations and sessions
are stored in PostgreSQL and survive normal restarts.

## Integrate a client

- **Native apps:** use public client ID `romd-community-device`. Request a
  device code at `POST /connect/device`. Optional form fields `app_name` and
  `device_name` supply the approval and session label. Display the
  `verification_uri` and `user_code`, and poll `POST /connect/token` with the
  returned device code according to the device response's interval and expiry.
  Request `offline_access` for refresh. Keep refresh tokens in OS secure
  storage and rotate them on refresh. [OAuth device authorization](https://www.rfc-editor.org/rfc/rfc8628.html)
- **Web apps:** discover the consumer issuer's OIDC configuration at
  `/.well-known/openid-configuration`. Use the assigned public client ID,
  authorization code with S256 PKCE, and a state value verified on callback.
  The browser exchanges the code and refreshes tokens from its registered
  origin. Send API calls with the bearer token and use credential-free
  cross-origin requests. Use `openid` when you need an ID token and
  `offline_access` when you need refresh. [OAuth security best current practice](https://www.rfc-editor.org/rfc/rfc9700.html)
- **Sign-out:** POST the token and client ID to `/connect/revocation` to end the
  session, then clear local credentials. A web app sends this request from
  its registered origin.

Request the scopes required by your client. The consumer's normal library
authorization applies on every request.

| Scope | Access |
| --- | --- |
| `romd:discovery` | Basic signed-in identity and the published reference catalog snapshot |
| `romd:library` | Assigned library, catalog, title details, artwork metadata, and collections |
| `romd:releases` | Title releases, release access, and disc-set discovery and access previews |
| `romd:delivery` | Release manifests, disc-set downloads, and other consumer content grants |

Admin APIs and first-party account management require first-party browser
sign-in. Public server identity, public media, and download redemption follow
their existing authorization rules. A previously issued delivery URL retains
its own short expiry. [Consumer delivery](../architecture/consumer-delivery.md)

See the [API contract policy](../decisions/api-contract-policy.md) for route
and compatibility rules.
