# Configuration

ROMD uses the standard .NET configuration hierarchy. Settings can come from a
host's tracked `appsettings.json`, a local-only environment-specific file such
as `appsettings.Development.json`, environment variables, or command-line
arguments. Later providers override earlier ones. In environment variables,
replace each JSON `:` separator with `__`; for example,
`Romd:DataDirectory` becomes `Romd__DataDirectory`.

The tracked `src/Romd.Admin.Host/appsettings.json` is the safe, non-secret
reference for shared option defaults. Admin project-local development settings
belong in the ignored `src/Romd.Admin.Host/appsettings.Development.json`;
consumer- and worker-specific files belong under their respective project
roots. Container deployments set shared and secret values through
`compose.yaml` and `.env`.

`Romd.Host` was retired before v1. Source deployments upgrading from it must
move every required value from `src/Romd.Host/appsettings.json`,
`appsettings.Development.json`, or any other project-local `appsettings*` file
to the corresponding canonical host file or to environment/shared
configuration. Project-local files are not discovered across project roots.
Relocate secrets, provider credentials, allowed import roots, host URLs, and
all other overrides explicitly; only environment/shared configuration follows
the process change automatically.

## Core settings

| JSON path | Environment variable | Default | Description |
| --- | --- | --- | --- |
| `Romd:DataDirectory` | `Romd__DataDirectory` | Platform user-data directory | Shared root for SQLite, CAS content, media, logs, data-protection keys, and the OpenIddict signing key. All hosts in one instance must use the same durable directory. |
| `Romd:JwtSecret` | `Romd__JwtSecret` | Insecure development value | Derives token-encryption key material. Set a unique value of at least 32 characters outside local development. |
| `Romd:DefaultAdminEmail` | `Romd__DefaultAdminEmail` | `admin@localhost` | Email used when bootstrapping the initial administrator. |
| `Romd:DefaultAdminPassword` | `Romd__DefaultAdminPassword` | Insecure development value | Initial administrator password. Production refuses to start when this is missing or left at its default. |
| `Romd:MaxUploadBytes` | `Romd__MaxUploadBytes` | `10737418240` | Maximum HTTP upload and content-addressable storage object size, in bytes. |
| `Romd:ReadinessMarkerPath` | `Romd__ReadinessMarkerPath` | Unset | Optional ephemeral marker written after startup services complete. Used by the production Compose readiness gate. |

## Host and authentication settings

| JSON path | Default | Description |
| --- | --- | --- |
| `Romd:AdminHost:JwtAudience` | `romd-admin` | Audience accepted by the admin surface. |
| `Romd:ConsumerHost:JwtAudience` | `romd-consumer` | Audience accepted by the consumer surface. |
| `Romd:AdminHost:PublicUrl` | Request origin | Absolute public admin origin/issuer when hosted behind a reverse proxy. |
| `Romd:ConsumerHost:PublicUrl` | Request origin | Absolute public consumer origin/issuer when hosted behind a reverse proxy. |
| `Romd:AdminHost:CorsOrigins` | Development Vite origins in Development; empty otherwise | Allowed admin browser origins. Use array indexes in environment variables, such as `Romd__AdminHost__CorsOrigins__0`. |
| `Romd:ConsumerHost:CorsOrigins` | Development Vite origins in Development; empty otherwise | Allowed consumer browser origins. |
| `Romd:AdminHost:AllowCorsCredentials` | `true` | Whether the admin CORS policy permits credentials. |
| `Romd:ConsumerHost:AllowCorsCredentials` | `true` | Whether the consumer CORS policy permits credentials. |
| `Romd:Auth:AdminSpa:RedirectUris` | `http://localhost:5137/auth/callback` | Registered admin SPA authorization-code callback URIs. |
| `Romd:Auth:AdminSpa:PostLogoutRedirectUris` | `http://localhost:5137` | Registered admin SPA post-logout origins. |
| `Romd:Auth:ConsumerSpa:RedirectUris` | `http://localhost:5174/auth/callback` | Registered consumer SPA authorization-code callback URIs. |
| `Romd:Auth:ConsumerSpa:PostLogoutRedirectUris` | `http://localhost:5174` | Registered consumer SPA post-logout origins. |

Array values use numeric environment-variable segments. For example:

```sh
export Romd__Auth__AdminSpa__RedirectUris__0=https://admin.example.com/auth/callback
export Romd__Auth__AdminSpa__PostLogoutRedirectUris__0=https://admin.example.com
```

## Consumer delivery

| JSON path | Environment variable | Default | Description |
| --- | --- | --- | --- |
| `Romd:ConsumerDelivery:SigningKeyId` | `Romd__ConsumerDelivery__SigningKeyId` | Empty | Identifier included in signed content grants. |
| `Romd:ConsumerDelivery:SigningSecret` | `Romd__ConsumerDelivery__SigningSecret` | Empty | Secret used to sign consumer download URLs; use at least 32 bytes. |
| `Romd:ConsumerDelivery:SignedUrlTtlMinutes` | `Romd__ConsumerDelivery__SignedUrlTtlMinutes` | `10` | Lifetime of a signed content URL. |

## Metadata enrichment

The non-secret enrichment defaults are advertised in
`src/Romd.Admin.Host/appsettings.json`.

| JSON path | Default | Description |
| --- | --- | --- |
| `Enrichment:GlobalSourcePriority` | `["igdb"]` | Provider priority used when resolving metadata and primary media. |
| `Enrichment:MinimumAutoEnrichConfidence` | `0.6` | Minimum confidence from `0.0` to `1.0` for automatic application. |
| `Enrichment:DownloadMedia` | `true` | Download selected provider media into local storage. |
| `Enrichment:MediaTypesToDownload` | Cover, Screenshot, Background | Media types retained during enrichment. |
| `Enrichment:MaxMediaDownloadConcurrency` | `8` | Maximum concurrent media downloads. |
| `Enrichment:PersistenceBatchSize` | `25` | Titles persisted per bulk-enrichment batch. |
| `Enrichment:RegionPriority` | USA, WORLD, EUROPE, JAPAN | Preferred regions when choosing a representative release. |
| `Enrichment:Providers:<id>:Enabled` | `true` | Enables or disables a provider; for IGDB this applies only when credentials are managed by deployment. |
| `Enrichment:Providers:<id>:RateLimit` | `4` | Provider request limit per second. |
| `Providers:Igdb:ClientId` | Empty | Deployment override for the Twitch/IGDB application client ID. |
| `Providers:Igdb:ClientSecret` | Empty | Deployment override for the Twitch/IGDB application client secret. |

IGDB is optional. Administrators can configure it under **Settings → Metadata
Providers** using a Twitch Client ID and Client Secret. Save the settings, then
use **Test connection** to check the saved credentials. ROMD obtains access
tokens itself; do not enter an access token as the client secret.

The client secret is write-only: reads return only whether one is stored. Leaving
the secret input empty retains it; changing the client ID requires replacing or
explicitly removing the saved secret. Disable IGDB before removing credentials. New enrichment
runs use the current database settings without restarting the admin or worker.
An active run retains its initial configuration. Disabling IGDB preserves existing
metadata; an unavailable provider is a configuration failure, not a title search
that returned no match.

For deployment-managed installations, configure both credentials on **both the
admin and worker hosts**:

```sh
export Providers__Igdb__ClientId=replace-me
export Providers__Igdb__ClientSecret=replace-me
```

Compose exposes the same pair as `IGDB_CLIENT_ID` and `IGDB_CLIENT_SECRET` in
`.env` and maps them to the .NET configuration names on the admin and worker
only. If either credential is nonempty, deployment configuration takes precedence
and Admin UI editing is locked. A partial pair is reported as a configuration
error; it is never combined with a database credential. To return to UI management,
remove both deployment values and restart both hosts. Previously saved database
settings remain available. Keep overrides identical across the two hosts.

Database secrets are encrypted with the shared ASP.NET Core Data Protection
keyring in `Romd:DataDirectory/dp-keys`, using the stable `romd` application name.
The admin and worker must share that directory. The consumer database role is
denied access to provider settings. Protect filesystem access to the data directory:
the keyring is outside PostgreSQL, but is not itself encrypted by this configuration.
Back up and restore the database and data directory together using the supported
backup scripts. Losing the matching keyring makes saved credentials unreadable;
restore the original keys or replace the credentials through Admin Settings.
Connection test results describe the credentials tested at that time and do not
promise future provider availability.

## Path import

Server-side path import (`POST /upload/from-path`) is disabled until at least
one allowed root is configured.

| JSON path | Environment variable | Default | Description |
| --- | --- | --- | --- |
| `Romd:AllowedImportPaths` | `Romd__AllowedImportPaths__0`, `__1`, … | Empty (feature disabled) | Absolute directories that server-side imports may read from. Every imported path must be canonically contained under one of these roots; symbolic links anywhere on the path are rejected. |

In a split-host deployment the admin host stages an import and the worker host
executes it, including move-mode deletion of source originals. Both hosts must
mount each allowed import root at an identical absolute path. If a source is
not visible from the executing host it is never counted as moved: the original
is retained with a warning and the deletion is retried by the periodic sweep
until its manifest ages out.

## Reverse proxies

Forwarded headers are disabled by default. Enabling them without limiting which
proxy peers are trusted would allow clients to spoof the public scheme or host,
so proxied deployments must configure an allowlist.

| JSON path | Default | Description |
| --- | --- | --- |
| `Romd:ForwardedHeaders:Enabled` | `false` | Process `X-Forwarded-*` headers. |
| `Romd:ForwardedHeaders:KnownProxies` | Empty | Trusted individual proxy IP addresses. |
| `Romd:ForwardedHeaders:KnownNetworks` | Empty | Trusted proxy networks in CIDR notation. |
| `Romd:ForwardedHeaders:ForwardLimit` | `1` | Maximum trusted proxy hops. |
| `Romd:ForwardedHeaders:TrustAllProxies` | `false` | Trust any immediate peer. Use only when the hosts cannot be reached except through the proxy. |

See [`production-deployment.md`](production-deployment.md) for the supported
nginx and Cloudflare deployment topology.

## Secret handling

Never put real credentials in tracked `appsettings.json`. For source-based
development, use environment variables or the ignored
`src/Romd.Admin.Host/appsettings.Development.json`. For Compose, copy `.env.example`
to the ignored `.env` file and replace its local placeholders before any shared
or production deployment.

If a credential has ever appeared in a tracked file, removing the file from Git
does not invalidate the credential. Rotate it at its issuer.
