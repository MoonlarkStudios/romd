# Configuration

ROMD uses the standard .NET configuration hierarchy. Settings can come from a
host's tracked `appsettings.json`, a local-only environment-specific file such
as `appsettings.Development.json`, environment variables, or command-line
arguments. Later providers override earlier ones. In environment variables,
replace each JSON `:` separator with `__`; for example,
`Romd:DataDirectory` becomes `Romd__DataDirectory`.

This page covers operator-configurable settings. Defaults come from the option
types, host settings and image environment; Compose supplies installation values.
The tracked `src/Romd.Admin.Host/appsettings.json` contains the non-secret
enrichment defaults. Admin project-local development settings
belong in the ignored `src/Romd.Admin.Host/appsettings.Development.json`;
consumer- and worker-specific files belong under their respective project
roots. Container deployments set shared and secret values through
`compose.yaml` and `.env`.

Project-local settings are not discovered across host project roots. Configure
each owning host explicitly. The supported topology and credentials are described
in [production deployment](production-deployment.md); source development and its
local-only passwords are in [development sign-in](../development/getting-started.md#development-sign-in).

## Database and installation boundary

The server uses PostgreSQL 18. The current application schema starts with
`20261031050000_FreshMediaBaseline` and includes the additive
`20261102000000_PspUmdObservations` migration (readiness version 73).
Earlier migration chains, unversioned populated databases and baseline downgrades
are unsupported. The worker refuses them before schema writes. Use a new empty
database and data volumes for an unsupported starting version; preserve the old
installation and backups for recovery. See [upgrade](production-deployment.md#upgrade).

Compose fills these connection strings from its independent role passwords;
directly hosted processes must supply them themselves:

| JSON path | Owning host | Purpose |
| --- | --- | --- |
| `ConnectionStrings:Romd` | All three hosts | Application database, using that host's runtime role |
| `ConnectionStrings:Hangfire` | All three hosts | Shared Hangfire database, using that host's runtime role |
| `ConnectionStrings:RomdProvisioning` | Worker only | Application schema provisioning credential |
| `ConnectionStrings:HangfireProvisioning` | Worker only | Hangfire schema provisioning credential |

These have no connection-string default. Environment names use `__`, for
example `ConnectionStrings__Romd`. API hosts must never receive provisioning
credentials. See [host ownership](../architecture/split-host-topology.md).

## Core settings

| JSON path | Environment variable | Default | Description |
| --- | --- | --- | --- |
| `Romd:DataDirectory` | `Romd__DataDirectory` | Platform user-data directory | Shared root for CAS content, media, logs, Data Protection keys and the OpenIddict signing key. PostgreSQL stores server database state separately. All hosts in one instance must use the same durable directory. |
| `Romd:JwtSecret` | `Romd__JwtSecret` | Insecure development value | Derives token-encryption key material. Set a unique value of at least 32 characters outside local development. |
| `Romd:DefaultAdminEmail` | `Romd__DefaultAdminEmail` | `admin@localhost` | Email used when bootstrapping the initial administrator. |
| `Romd:DefaultAdminPassword` | `Romd__DefaultAdminPassword` | `ChangeMe123!` bootstrap fallback in Development | Initial administrator password. Container development supplies `LocalDev123` instead. Production requires an explicit secure password. Existing passwords are not changed by this setting. |
| `Romd:MaxUploadBytes` | `Romd__MaxUploadBytes` | `68719476736` | Maximum ROM upload file size, in bytes. It must not exceed `Romd:Storage:MaxContentSize`; hosts refuse to start otherwise. ROM upload requests allow an additional 2 MiB for multipart overhead (capped at `long.MaxValue`); the file itself remains bounded by this setting. Other routes keep ASP.NET Core's default body limit; DAT and artwork routes allow 34 MiB. |
| `Romd:Storage:WritePolicy` | `Romd__Storage__WritePolicy` | `SeekableZstandard` | Representation of new content-addressable storage objects: `SeekableZstandard`, which falls back to raw when it saves less than 5%, or `Raw`. Reads never depend on it. |
| `Romd:Storage:SeekableFrameSize` | `Romd__Storage__SeekableFrameSize` | `262144` | Uncompressed bytes per frame for new seekable objects, from `65536` to `1048576`. Existing objects keep their own frame size. |
| `Romd:Storage:SeekIndexCacheBytes` | `Romd__Storage__SeekIndexCacheBytes` | `67108864` | Memory budget for seek tables shared by positional readers, which the VFS opens once per request. A 64 GiB object at 256 KiB frames has a 3 MiB table. `0` disables sharing. |
| `Romd:Storage:MaxContentSize` | `Romd__Storage__MaxContentSize` | `68719476736` | Largest content-addressable storage object a write accepts, in bytes, whatever the source: upload, path import, or artwork. Reads never depend on it. |
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
| `Romd:Auth:Attempts:IpLimit` | `30` | Combined interactive login/device-verification attempts per client IP per window, per API process. IPv6 addresses share a /64 budget. |
| `Romd:Auth:Attempts:AccountLimit` | `10` | Combined attempts per resolved account per window, per API process; username/email aliases and casing share a budget. Unknown login names are normalized and hashed. |
| `Romd:Auth:Attempts:WindowSeconds` | `60` | Attempt window, from the first admitted attempt; rejected requests do not prolong it. |
| `Romd:Auth:Attempts:MaxIpKeys` | `4096` | Maximum live IP buckets per process. |
| `Romd:Auth:Attempts:MaxAccountKeys` | `8192` | Maximum live account buckets per process. |
| `Romd:Auth:Attempts:MaxConcurrentVerifications` | `2` | Account lookups and password verifications admitted concurrently per process; excess attempts receive 429 without waiting. |

Both API hosts enforce these limits before password hashing, with the IP gate
before account lookup. Every submitted attempt counts, including successful
sign-ins. Responses exceeding a rate budget return HTTP 429 and `Retry-After`
seconds until that budget expires. Full bucket maps reject new keys until the
earliest live key expires; flooding new names cannot evict an existing limit.
Account-lookup/password-verification saturation returns 429 with a one-second retry suggestion.
Expired buckets are reclaimed on subsequent attempts, with memory bounded by
the configured capacities. Throttling never sets account lockout fields or
revokes existing sessions.

Budgets are ephemeral and independent in the admin and consumer API processes:
the supported single-admin/single-consumer deployment admits at most 60 attempts
per IP and 20 per account across both hosts in their respective 60-second
windows at the defaults. Restarting a host clears its budgets. Additional
replicas multiply the allowance; use ingress throttling or shared coordination
before scaling beyond this topology. Configure both hosts consistently.

Client IPs come exclusively from the connection after trusted-proxy processing;
raw `X-Forwarded-For` values never select a throttle key. Set
`Romd:ForwardedHeaders:KnownProxies`/`KnownNetworks` to the actual ingress and
prevent direct access to the API containers. With forwarding disabled, clients
behind the same proxy share its IP budget. See the
[split-host runbook](../architecture/split-host-topology.md) for proxy trust configuration.

Administrator bootstrap runs only while the database has no human accounts.
Renaming or deleting the original administrator, suspending accounts, or changing
the configured bootstrap email does not create a replacement administrator.
Use an existing administrator's account recovery flow to restore access. If the
sole administrator cannot sign in, use the host-local
[administrator recovery command](production-deployment.md#administrator-recovery).
The initial account and its administrator role are created in one transaction;
invalid bootstrap credentials fail worker startup without leaving a partial account.

Interactive `/connect/login` and `/connect/verify` POSTs require the antiforgery
cookie and hidden form token issued by their GET forms. Browsers submit these
naturally; automation must GET the form and preserve its cookie first. OAuth
protocol token/device/revocation requests and bearer APIs do not require these
browser tokens. Login forms accept at most 64 KiB, allowing for percent-encoded
Unicode passwords; login names accept at most 320 characters and passwords at
most 4096 UTF-16 code units. The same password maximum applies to account
creation, activation, recovery and password changes. An invalid new password
does not replace existing credentials or consume an account link.

Successful sign-in upgrades older password hashes when the configured hasher
requests it. The upgrade preserves existing sessions and cannot overwrite a
password reset or account change committed during verification.

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

## Media processing

Limits for media inspection and encoding (`Romd:Media`) and for decoding
stored media on read (`Romd:MediaRead`), plus the native CHD binary locations
(`Romd:Native`; see [CHD media binaries](production-deployment.md#chd-media-binaries)).
Inspection and encoding belong to the worker;
their admission leases are shared by worker processes using the same data
directory on one host. Read limits apply separately in each delivering host.
Invalid values stop the owning host at startup. The
[media process guide](media-processing.md) explains
child packaging, containment and failure recovery. The
[media pipeline decision](../decisions/media-pipeline.md#isolation-and-resource-bounds) defines the rules these
settings bound.

| JSON path | Environment variable | Default | Description |
| --- | --- | --- | --- |
| `Romd:Media:MaxConcurrent` | `Romd__Media__MaxConcurrent` | `2` | Concurrent inspections and encodings across workers sharing the host-local data directory, from `1` to `16`. Live or unresolved runs prevent conflicting limits. |
| `Romd:Media:AdmissionTimeoutSeconds` | `Romd__Media__AdmissionTimeoutSeconds` | `5` | Maximum wait for a verification slot before deferring the file, and for the accounting lock during recovery, from `1` to `60` seconds. |
| `Romd:Media:WallTimeoutSeconds` | `Romd__Media__WallTimeoutSeconds` | `600` | Deadline for one admitted inspection, extraction or RVZ encoding, from `1` to `14400`. Exceeding it fails that file. |
| `Romd:Media:MaxArtifactBytes` | `Romd__Media__MaxArtifactBytes` | `10737418240` | Largest media input or encoded output accepted by the child, up to 16 GiB. Ordinary files retain the upload limit. Encoding must also admit its conservative per-input output cap. |
| `Romd:Media:MaxLogicalBytes` | `Romd__Media__MaxLogicalBytes` | `10737418240` | Largest reconstructed logical size or recognized Nintendo ISO accepted for child inspection, up to 16 GiB. |
| `Romd:Media:WorkerPath` | `Romd__Media__WorkerPath` | Worker directory + `media-worker/Romd.Media.Worker.dll` | Absolute child DLL path. Missing or incompatible children fail worker startup. |
| `Romd:Media:DotnetPath` | `Romd__Media__DotnetPath` | Absolute .NET host from the running runtime | Absolute trusted .NET executable; discovery does not use the child's cleared PATH. |
| `Romd:Media:MemoryBytes` | `Romd__Media__MemoryBytes` | `536870912` | Child GC heap hard limit, 64 MiB–8 GiB; applies on every platform. |
| `Romd:Media:LinuxDataBytes` | `Romd__Media__LinuxDataBytes` | `1073741824` | Linux writable/data allocation limit; at least `MemoryBytes`, at most 16 GiB. |
| `Romd:Media:LinuxAddressSpaceBytes` | `Romd__Media__LinuxAddressSpaceBytes` | `34359738368` | Linux virtual-address-space limit, including CLR reservations; at least `LinuxDataBytes`, at most 64 GiB. This is not a resident-memory limit. |
| `Romd:Media:CpuTimeoutSeconds` | `Romd__Media__CpuTimeoutSeconds` | `600` | Child CPU budget, 1–14400 seconds; Linux also enforces it through `prlimit`. |
| `Romd:Media:ChdEncodeCpuTimeoutSeconds` | `Romd__Media__ChdEncodeCpuTimeoutSeconds` | `1200` | CHD encode-only CPU budget, 1–14400 seconds; inherited by chdman through Linux `prlimit`. Keep consistent across admin and worker. |
| `Romd:Media:ChdEncodeWallTimeoutSeconds` | `Romd__Media__ChdEncodeWallTimeoutSeconds` | `1200` | CHD encode-only wall deadline, 1–14400 seconds. Together with CPU budget, recipe and preferred representation, identifies a durable budget outcome. Keep consistent across admin and worker. |
| `Romd:MediaRead:MaxConcurrent` | `Romd__MediaRead__MaxConcurrent` | `4` | Concurrently open decoded readers per host, from `1` to `16`. One FIFO admission budget covers RVZ and CHD; native CHD handles retain their existing bound. A reader holds its slot through stream disposal, so an active long or slow download can delay queued reads. Newly arriving work cannot overtake an older waiter. Admission has no account isolation: one account can occupy every slot, including through consumer content grants; the separate VFS per-user request limit does not reserve decoder capacity for other accounts. |
| `Romd:MediaRead:MaxQueued` | `Romd__MediaRead__MaxQueued` | `8` | Maximum artifact snapshots awaiting decoded-reader admission, from `0` to `128`. Waiting sources remain owned until admission or refusal; queue exhaustion refuses immediately. `0` disables waiting. This is separate from the VFS HTTP request queue. |
| `Romd:MediaRead:AdmissionTimeoutMilliseconds` | `Romd__MediaRead__AdmissionTimeoutMilliseconds` | `5000` | Default shared-reader admission wait, from `0` to `300000` ms. `0` is fail-fast. An explicit caller timeout overrides the default; exports retain their 30-second override. Native-handle admission retains the caller's explicit timeout too, so that override can wait twice. A full queue or expired wait retains the bodyless VFS `503`/`Retry-After: 15` response. Cancellation and host shutdown remove waiters and release their sources; an admission grant racing cancellation returns its slot. |
| `Romd:MediaRead:MaxIdle` | `Romd__MediaRead__MaxIdle` | `2` | Decoded readers kept open for reuse; at most `MaxConcurrent`. CHD readers recheck their evidence on every open and are never kept; this also caps idle native CHD handles. |
| `Romd:MediaRead:CacheBytes` | `Romd__MediaRead__CacheBytes` | `67108864` | Byte budget for decoded media blocks shared by all readers in the host, up to 4 GiB. `0` disables caching. |
| `Romd:Native:LibchdrDirectory` | `Romd__Native__LibchdrDirectory` | `/app/native/libchdr` in linux/amd64 and linux/arm64 server images | Directory holding libchdr's `build-manifest.json` and `native/libmoonlark_chdr.so`; every server image ships it at `/app/native/libchdr`. The host verifies the library against its build record and compiled upstream pin at startup. CHD stays off on any host without it. A blank or relative value refuses startup; image defaults have no opt-out. Native recipe-2 qualification is recorded per RID; the ARM64 rerun remains pending. See [CHD media binaries](production-deployment.md#chd-media-binaries). |
| `Romd:Native:ChdmanDirectory` | `Romd__Native__ChdmanDirectory` | `/app/native/chdman` in linux/amd64 and linux/arm64 worker images; unset on API images | Worker only. Directory holding chdman's `build-manifest.json` and `native/chdman`; the worker image ships it at `/app/native/chdman`. Verified against its build record at startup. Requires `Romd:Native:LibchdrDirectory`; a worker with libchdr but without this setting refuses startup. |

Keep `temp/media-runs-v1` and `temp/staging-v1` beneath the shared durable data
directory across worker restarts and container recreation. Admission lease files
must keep their identity; never replace them or erase run/accounting records to
return capacity. Convergence uses the same media limits and shared
[aggregate staging budget](../architecture/temporary-staging.md#ownership-and-recovery), plus a
durable CAS source dependency. There is no separate convergence capacity or
canonical-recipe setting. Administrators can start bounded bulk convergence
with these defaults; see [media processing](media-processing.md#durable-convergence).

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

IGDB is optional. Administrators can configure it under **Integrations → IGDB**
using a Twitch Client ID and Client Secret. Save the settings, then
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
restore the original keys or replace the credentials through Integrations.
Connection test results describe the credentials tested at that time and do not
promise future provider availability.

## Path import

Server-side path import (`POST /api/upload/from-path`) is disabled until at least
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

## Temporary staging

All hosts sharing a data directory must use the same `Romd:TempStorage` limits.
Environment names follow the normal rule, for example
`Romd__TempStorage__MaximumBytes`.

| Setting beneath `Romd:TempStorage` | Default | Meaning |
| --- | --- | --- |
| `MaximumFiles` | `64` | Concurrent staging reservations; accepts 1–256 |
| `MaximumBytes` | `137438953472` (128 GiB) | Aggregate reserved bytes; at least the maximum admitted file size and at most 16 TiB |
| `MinimumFreeBytes` | `1073741824` (1 GiB) | Free-space reserve; accepts 0–16 TiB |
| `AdmissionTimeoutSeconds` | `5` | Wait for capacity/accounting; accepts 1–60 seconds |
| `CopyTimeoutSeconds` | `3600` | Copy deadline; accepts 1–86400 seconds |

These bounds cover the shared staging service, not every upload buffer or
archive workspace. Keep the durable reservation records across restarts. See
[temporary staging](../architecture/temporary-staging.md) for accounting and recovery.

## SteamGridDB

Configure the API key and enablement under **Integrations → SteamGridDB**.
Artwork automation preferences remain under **Settings**. Deployment-managed
configuration uses `Providers:SteamGridDb:ApiKey` and
`Providers:SteamGridDb:Enabled` (`Providers__SteamGridDb__ApiKey` and
`Providers__SteamGridDb__Enabled`) on both admin and worker. The options default
to no key and `Enabled=true`; a usable credential is still required.

An explicitly supplied API-key setting, even empty, selects deployment management
and locks UI editing. Remove that key from configuration to return to UI
management; do not add a blank override to templates. Credentials are encrypted
with the shared Data Protection keyring described above. Consumer hosts do not
manage provider credentials. See [artwork architecture](../architecture/artwork-pipeline.md).

## DAT subscriptions

Configure these on admin and worker. The consumer does not run the reader.
The source-host default is disabled; standard catalog-enabled images supply:

| JSON path | Image default |
| --- | --- |
| `DatSubscriptions:Enabled` | `true` |
| `DatSubscriptions:ReaderPath` | `/app/catalog-reader` |
| `DatSubscriptions:RootPath` | `/app/catalog-trust/1.root.json` |
| `DatSubscriptions:Site` | `https://moonlarkstudios.github.io/romd-dat-data` |
| `DatSubscriptions:BundlePath` | Unset; local signed-bundle alternative to `Site` |

Use `__` in environment names, for example `DatSubscriptions__Enabled=false`.
An enabled reader needs an executable, an independently supplied trust root and
exactly one of `Site` or `BundlePath`. Keep its durable verification cache.
[DAT subscriptions](../features/dat-subscriptions.md#trust-and-deployment)
owns native setup, trust and local-bundle procedures.

## Browser playback

The consumer host binds `Romd:BrowserPlayback`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `PlayerOrigins` | Empty | Array of exact `Parent` / `Player` origin pairs |
| `DefaultPlayerOrigin` | Unset | Fallback when no parent mapping matches |

For example, set `Romd__BrowserPlayback__PlayerOrigins__0__Parent` and
`Romd__BrowserPlayback__PlayerOrigins__0__Player`. The player must be a different
origin on the same site. Production Compose supplies one pair from
`ROMD_CONSUMER_PUBLIC_URL` and `ROMD_PLAYER_PUBLIC_URL`. Source Development
settings supply the loopback player; managed development adjusts checkout ports.

The separate player container accepts:

| Environment variable | Default | Meaning |
| --- | --- | --- |
| `ROMD_PLAYER_ALLOWED_PARENTS` | Required | Comma-separated exact parent origins allowed to embed the player; Compose supplies the consumer origin |
| `ROMD_PLAYER_EMULATORJS_SOURCE` | `cdn` | Only accepted mode; packaged `bundled` mode is deferred |
| `ROMD_PLAYER_EMULATORJS_DATA_PATH` | Version-pinned EmulatorJS CDN directory | Optional compatible HTTP(S) or root-relative mirror directory ending in `/` |
| `ROMD_PLAYER_CORES` | Pinned core list | Optional comma-separated subset of the shipped core pin |

Use the pinned assets; a configured mirror does not make arbitrary cores
compatible. See [playback compatibility](../features/playback-compatibility.md)
and [browser-player architecture](../architecture/browser-player.md).

## WebDAV / VFS

The consumer host binds `Romd:Vfs`; use environment names such as
`Romd__Vfs__Enabled`. Production Compose exposes `ROMD_VFS_ENABLED`,
`ROMD_VFS_MAX_REQUESTS`, `ROMD_VFS_MAX_REQUESTS_PER_USER` and
`ROMD_VFS_MAX_DIRECTORY_ENTRIES` in the installation `.env`. Other overrides
require an explicit consumer environment overlay.

| Setting beneath `Romd:Vfs` | Default |
| --- | --- |
| `Enabled` | `false` |
| `AllowDevelopmentBearer` | `false` (development-only helper; registered connections use app passwords) |
| `MaximumConcurrentMetadataRequests` | `2` |
| `MaximumConcurrentRequests` | `8` |
| `MaximumConcurrentRequestsPerUser` / `MaximumConcurrentRequestsPerConnection` | `4` / `4` |
| `MaximumQueuedRequests` | `128` |
| `MaximumQueuedRequestsPerUser` / `MaximumQueuedRequestsPerConnection` | `32` / `16` |
| `MaximumQueueWaitMilliseconds` | `10000` |
| `MaximumDirectoryEntries` | `20000` |

Metadata concurrency accepts 1–32; request concurrency accepts 1–128; queues
accept 1–4096; wait accepts 1–60000 ms; directory entries accept 1–100000.
Effective per-user/per-connection limits cannot exceed the enclosing limits.
See [VFS](../architecture/vfs.md) for authentication, admission, connection
exports and the qualified client scope.

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
