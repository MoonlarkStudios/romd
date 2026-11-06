# ROMD Mount

Build a local self-contained Apple Silicon validation bundle from the repository
root with Python 3.11+ and the .NET 10 SDK:

```sh
python3 scripts/mount/bundle.py --output /tmp/romd-mount-bundle
python3 scripts/mount/bundle.py --verify /tmp/romd-mount-bundle
/tmp/romd-mount-bundle/romd-mount --help
```

The output directory must be new. The builder pins .NET runtime 10.0.10 and
rclone 1.75.1, checks the official rclone archive SHA-256, and includes source,
notices and a file manifest. `--rclone-archive PATH` can supply a cached archive;
its checksum is still enforced. Keep the entire bundle together. Its launcher
uses bundled rclone for `mount` unless `--rclone` overrides it. An installed .NET
runtime is not needed to run the bundle. Manifest verification detects changed
contents, modes, extra files and symbolic links; it does not authenticate the
manifest. This is a local development artifact, without ROMD release signing or
notarization. It neither installs macFUSE nor resolves the NFS isolation limit.

Set `ROMD_VFS_MOUNT_CLI` to that launcher plus `ROMD_MOUNT_KEYCHAIN_TEST=1` and
`ROMD_VFS_MOUNT=1` to run `MountCli_Packaged` integration validation. It removes
SDK paths from the child environment, checks a bounded cross-app Keychain
denial, then performs login, restart/refresh, mounted hashing, graceful shutdown,
crash recovery and logout through the packaged app. The SDK-hosted probes remain
separate because their process identity must not read the packaged app's secrets.

Status: development CLI with `login`, `status`, `logout`, `cache-info`, `cache-clear`, `recover`, and a supervised
read-only `mount` on macOS. It is not yet an installable or supported client.
The CLI mounts the default layout at `/vfs/library/`; use it with a server from
the same development revision. Unkeyed `/vfs/` and legacy routes are not supported.

From the repository root, with .NET 10 installed:

```sh
dotnet run --project clients/romd_mount -- login --server https://romd.example --profile player
dotnet run --project clients/romd_mount -- status --server https://romd.example --profile player
dotnet run --project clients/romd_mount -- cache-info --server https://romd.example --profile player
dotnet run --project clients/romd_mount -- cache-clear --server https://romd.example --profile player
dotnet run --project clients/romd_mount -- recover --server https://romd.example --profile player
dotnet run --project clients/romd_mount -- mount --server https://romd.example --profile player --mount-backend nfs-development --rclone /absolute/path/to/rclone --mountpoint /new/absolute/mountpoint
dotnet run --project clients/romd_mount -- logout --server https://romd.example --profile player
```

Login prints a verification URL and device code. Complete approval in your browser;
the command respects polling/slow-down responses and waits at most ten minutes.
Passwords and tokens are never command arguments. Status verifies the instance
and renews the session without printing credentials. Both commands hold exclusive
profile and credential leases. A changed instance is rejected; use a new local
profile to explicitly sign in to a replacement instance.
Logout revokes the server session before deleting the keychain credential. It
requires server access and retains credentials if revocation fails, allowing a
retry. Repeated logout succeeds when the saved credential is already absent.

`cache-info` works offline using the saved server-instance binding. It reports
the selected profile's cache path, file/directory counts, logical file bytes,
and filesystem-reported allocated file bytes including cache metadata files.
It neither reads Keychain nor renews credentials. Sparse files can allocate much
less than their logical sizes. Allocation sums each file path's native block
count; it excludes directory allocation and is not an estimate of reclaimable
space. Hard links, APFS clones, and snapshots can share blocks.
Inspection requires an unmounted profile with no unresolved reservation and
holds both ownership leases. It rejects symbolic links and stops rather than
returning a partial total after 100,000 entries or 64 nested directory levels.
It does not delete cache contents or verify the current server instance online.
The first cache inspection or mount creates private namespace metadata if absent;
this does not create a content-cache directory. Inspection shows the current
login's namespace and a separate total for all retained generations beneath this
profile in the selected state directory. Other state directories are separate.
Older development versions used flat generation directories without a retained
profile mapping; those directories are not included in this profile total.

`cache-clear` deletes cached downloads and metadata beneath this profile's cache
directory in the selected state root, including older login generations. It
works offline and requires both leases and no unresolved mount reservation.
Unmount first. Credentials, profile binding, login namespace, other profiles,
and alternate state roots remain unchanged. Earlier unattributed flat caches
are not removed. Subsequent reads download content again.

Cleanup preflights at most 100,000 entries and 64 directory levels, rejects
symbolic links, special files, and filesystem-device changes, and deletes through
open directory handles. It rechecks entry identity before deletion and never
recursively deletes an unchecked path. Interruption or a concurrent change can
leave a partially cleared cache; inspect and retry after resolving the cause.
This is cache removal, not secure erasure or a promise about reclaimed APFS space.

Mount requires an existing mountpoint parent, a new mountpoint directory, and an
explicit rclone executable (tested with 1.75.1). Backend selection is required;
`nfs-development` exposes the library through rclone's loopback NFS mount.
Keep the command running; Ctrl+C or
SIGTERM unmounts before releasing credential ownership. Close emulator files if
unmount is pending. While mounted, commands using the same credentials are busy;
unmount before running status or logout.
Before launching the mount child, the supervisor writes a token-free reservation
under the shared credential-lock directory. It records the mountpoint, backend,
executable, and owner/child process identities. Version 2 records also retain the
macOS boot session and, before the first readiness directory read, the kernel filesystem ID,
source, type, owner, and read-only flag. These are recovery evidence; a pathname
alone must not authorize unmounting a volume. Login, status, logout, and mount
refuse to reuse that credential scope while a reservation remains, even through
an alternate state root. Graceful unmount and child exit clear it. A killed
supervisor leaves it in place; malformed records also block reuse. Explicit
`recover` is available for verified NFS mounts and recorded NFS children that
have not mounted, using version-2 records.
Do not remove a reservation or its cache
merely because the recorded PID is absent: startup may have been interrupted
before the child identity was recorded in an older, ungated launch.

New mounts use a launch handshake: a shell waits on a private stdin pipe while
the supervisor persists the child's PID and start time. Only then does the
supervisor release it to execute rclone, retaining the same process identity.
Supervisor death before release closes the pipe and exits the launcher without
mounting. Arguments are passed literally as positional parameters. The
reservation records `LaunchGated`; older records without that field remain
ungated and retain their conservative recovery rules.

`recover` works offline and holds both profile/credential leases. It refuses a
live recorded supervisor. In the same boot session, it requires a recorded NFS
child and matches the current kernel mount identity before unmounting; a changed
mount is untouched. It tries ordinary unmount, then forced ejection of that
verified read-only mount if needed, which may interrupt remaining readers.
After unmount, it stops only a child matching the recorded PID, start time, and
canonical executable path. It clears the unchanged reservation last and retains
it whenever cleanup is unconfirmed. After a reboot it clears an old reservation
only if the mountpoint is unused, without acting on any reused process IDs.
Cache, credentials, and mountpoint directories remain intact. For NFS remounting,
choose a new mountpoint or remove the verified empty old directory yourself.
If startup recorded the child but no filesystem identity, recovery can stop that
verified child only while the mountpoint is unused. It rechecks after child exit;
a mount appearing during cleanup leaves the reservation intact. A gated record
without a child identity can be cleared when its supervisor is dead and no
mount exists: that launch never received permission to execute. A live mount
without recorded filesystem identity, an ungated startup record missing child identity,
version-1 records, and same-boot FSKit recovery remain unsupported.
New reservations also record the private broker directory and
socket filesystem identities. After the child stops, recovery removes only that
matching socket and empty directory before clearing the reservation. Changed
identities or unexpected directory contents preserve the reservation for review.
Older records without this metadata can leave unattributed broker directories;
recovery does not guess their ownership.
If token renewal reports an ended session or a changed server instance, the
supervisor stops retrying credentials and requests immediate ejection of its own
read-only mount through macOS Disk Arbitration. Failed cleanup retains supervision
and the reservation. Ordinary shutdown uses graceful unmount. Ordinary
network errors do not trigger this policy. Cached bytes remain on disk. The
native revoked-read probe passed; error-delivery latency remains a performance gap
and is recorded in the implementation notes.
The local NFS listener itself has no authentication: loopback limits network
reachability but does not isolate other users on the same machine. This adapter
is therefore still a development transport; local multi-user isolation is a
release gate.

`--mount-backend macfuse-fskit` is a candidate adapter for macOS 26+, requiring
separately installed macFUSE with its FSKit extension enabled. It uses
`rclone mount -o backend=fskit`, requests read-only access with directory/file
modes 0700/0400, and never falls back to NFS. FSKit volumes report `noowners`:
every local caller appears to own the files, so those modes do not restrict
other users. The mountpoint must therefore be inside an existing private
directory owned by the mounting user (mode 0700, no ACL, not a symbolic link),
for example `~/.romd/<server>/<profile>/vfs`; `/Volumes` is rejected because
another user read files through a mount there. FSKit never reports the kernel
read-only flag, so after mounting the supervisor requires a create at the mount
root to fail with `EROFS`. A private-parent mount blocked `nobody` even when it
was granted traversal of the home directory (macOS probe, 2026-09-27); a real
second account, Spotlight, and same-boot FSKit recovery remain untested.

The supervisor owns the token broker. rclone obtains access tokens through a
private Unix socket and authenticated child command; refresh tokens stay in
Keychain. No token is written to rclone configuration or command arguments.
Reads start at 64 KiB and grow to 4 MiB. FSKit mounts also use a 4 MiB rclone buffer and start reads at 1 MiB: without the buffer, rclone restarted downloads at 64 KiB after each read and throughput collapsed. NFS keeps no buffer and a 64 KiB start. The private cache is scoped by server
origin, instance, profile, and a random login generation, and retained after unmount.
Every successful device login starts a fresh cache, even for the same account;
refresh and mount restarts preserve the generation. The generation record lives
under the shared credential-lock directory so switching accounts also invalidates
cache selection through alternate state directories. It is flushed and replaced
before new credentials are saved. Missing metadata creates a fresh generation;
legacy profile-only caches are never reused. New generations are grouped under
`cache/<credential-scope>/<login-generation>` so retained bytes remain attributable
to their profile. Old bytes remain on disk. `cache-info` reports the current
generation and the complete profile directory, including retained generations.
The 1 GiB size setting applies only to the mounted generation and
is an eviction target, not a hard disk quota. Cached content remains on disk
while in use and can exceed that target. In a 2026-09-26 macOS development NFS
probe (rclone 1.75.1), a fully read 1.46 GB disc image exceeded the target and
was removed about 46 seconds after close; this is one observation, not a
promised eviction deadline. A two-round concurrent probe the same day verified
four complete reads and automatic cleanup after each pair, while observing
2.92 GB cached immediately after close. Leave room for active files;
the target does not cap peak allocation.
For each `mount` invocation, set `--cache-size-mib N` to change the target
(default 1024, positive whole MiB), and `--cache-min-free-mib N` to ask the
cleaner to maintain free space on the cache volume (default 0, disabled).
For example, `--cache-size-mib 4096 --cache-min-free-mib 2048` selects a 4 GiB
cache target and 2 GiB free-space target. Values must be whole numbers no larger
than 2147483647; one MiB is 1,048,576 bytes. Both settings are printed at startup
and apply to that mount only. They do not alter the profile, clear old caches,
or change the generation selected for a login. Re-supply them on the next mount.
The cleaner cannot guarantee the free-space target while files are active;
neither setting prevents disk exhaustion or reserves space from other apps.
A bounded pressure probe inside a disposable 64 MiB APFS image (2026-09-26)
verified eviction below the size target when available space fell below the
configured free-space target, and observed space return above that target.
Cached content can also remain on disk
after logout; use `cache-clear` explicitly to remove this profile's retained
cache. Recovery support and its limits are described above.

By default, metadata lives beneath the platform LocalApplicationData directory
in `ROMD/Mount`. It contains only origin, instance ID, and profile; refresh tokens
live in Keychain. `--state-directory` accepts an absolute alternate state root.
The state root, cache parent, and credential-scoped cache must have Unix mode
0700 and must not be symbolic links. New directories receive that mode; existing
permissive directories are rejected without changing permissions or contents.
On macOS, any extended ACL is also rejected, even if mode 0700 is present.
ACLs are inspected through a native directory handle and are never stripped or
rewritten. Protection against hostile concurrent path replacement remains
unverified.
Credential locks always use `ROMD/Mount/credential-locks` under LocalApplicationData,
even with alternate metadata roots. Alternate roots do not isolate credentials;
use distinct local profile names for independent sessions. `--allow-loopback-http`
is only for local development. Error exits:
2 invalid/busy/unavailable local state, 3 authentication, 4 keychain, 130 cancellation
or device-login timeout. `--help` works without credentials or network access.

`MountIdentityClient` implements the `romd-mount` device-flow HTTP contract:
begin login, poll once, and exchange a refresh token. It owns its HTTP client and
must be disposed. HTTPS certificate validation uses platform defaults.
Origins must have no path, credentials, query, or fragment. HTTP requires explicit
opt-in and is restricted to loopback for local development. Redirects and cookies
are disabled. The verification URI must retain the configured origin.

Call `BindAsync` before selecting saved credentials. It discovers the canonical
instance ID without credentials and rejects an unexpected instance. Use the
returned `BoundMountIdentity` for login and refresh: it rechecks discovery before
every exchange and refuses to send credentials when discovery fails or the ID
changes. Keep the underlying client alive until the bound session is finished.
This detects replacement at the configured origin; it is not certificate pinning
and cannot make discovery and a later token request atomic.

Protocol responses are bounded to 64 KiB with a 30-second request deadline.
Token objects redact `ToString()`, and exceptions expose fixed categories rather
than remote diagnostic text. Credentials are sent in form bodies, never URLs.
Callers must still avoid serializing or logging credential properties.

The caller must respect device polling intervals and expiry. `Pending` and `SlowDown` are polling outcomes;
`SignInRequired`, `Denied`, and `Expired` require user-facing recovery. Caller
cancellation remains cancellation.

`MountCredentialScope` keys credentials by approved origin, discovered instance,
and local profile. `MacKeychainCredentialStore` stores refresh tokens as local
generic-password items in `com.romd.mount.refresh-token.v1`, using native SecItem
APIs without shell commands or secret arguments. It fails when keychain access
requires interaction; it does not prompt or fall back to plaintext storage.
Native operations finish once dispatched, even if caller cancellation arrives.
Windows/Linux stores and signed-distribution keychain access remain unverified.

`MountTokenSession` serializes requests within one running mount, keeps access
tokens in memory, renews before expiry, and persists each rotated refresh token
before returning its access token. A failed save retains the replacement in
memory for another save attempt. Process death in that window can still require
sign-in again. Pass the rejected access token after a 401 so overlapping failures
share one replacement. Acquire `MountProfileLease` using the credential scope's
key before reading/writing its credentials, and hold it for the entire broker
lifetime. It uses an exclusive file handle in a private lock directory. A live
owner blocks another process; the OS releases ownership after process death.
Keep the lock files after release: deleting them can create competing owners.
Production callers use `AcquireCredential` for the shared credential directory.
This is cooperative
ownership, not protection against another process deliberately deleting locks.

Short mounted token expiry, two-second service outages, and two-second HTTP
connection interruptions are covered by the native recovery probe. The mount
uses `dumbtimer,timeo=100,retrans=3` with soft/interruptible NFS reads so macOS
does not reduce the retry interval to loopback latency. Longer outages and
recovery latency under realistic WAN conditions remain unverified.

Still required: extended outage testing and recovery latency tuning, other-platform
credential stores, rclone provisioning, startup/FSKit crash recovery, legacy IPC/cache reconciliation,
emulator recovery after failed reads, sustained cache eviction, and packaging.
Interrupted device polling currently ends the
login attempt; it does not automatically retry transient network errors.

Validation from the repository root:

```sh
dotnet test tests/Romd.Mount.Tests
```

The tests are included in `mise run test`. The opt-in `Vfs_RcloneTokenCommand`
host integration test uses this library against the real consumer identity
service and rclone; it requires `ROMD_VFS_RCLONE` and Python.
Its native variant additionally requires `ROMD_MOUNT_KEYCHAIN_TEST=1` on macOS;
it uses the actual keychain-backed broker and reloads a rotation in a fresh broker.

`Vfs_Https` integration tests cover local TLS certificate validation, device
login, refresh, authorized ranges and revocation. The ordinary test needs no
external client. Set `ROMD_VFS_RCLONE` for the real-rclone variant; additionally
set `ROMD_VFS_MOUNT=1` on macOS for mounted reads and a full-file hash. These
tests use a temporary private CA, scoped to test handlers and rclone's
`--ca-cert`, without changing system trust. They do not add private-CA support
to the production CLI or validate a public deployment. The mount variant can
read a user-supplied image through `ROMD_VFS_PROBE_FILE`; the source is read-only.
For sensitivity measurements, `ROMD_VFS_REQUEST_DELAY_MS` adds 0–500 ms before
each VFS request, including HTTPS. `ROMD_VFS_BANDWIDTH_KIB` applies rclone's
application-level bandwidth cap only to the mounted client (0 disables it).
These controls do not model packet loss, TCP latency, handshake delay or jitter;
the direct HTTP comparison bypasses the client bandwidth cap. Save mount
measurements with `ROMD_VFS_PROBE_REPORT` and retain those limitations with them.

For the opt-in macOS keychain round-trip test, set `ROMD_MOUNT_KEYCHAIN_TEST=1`.
It creates, rotates, and deletes a uniquely scoped synthetic secret. Ordinary CI
skips this native test and still exercises the renewal state machine.

Set `ROMD_MOUNT_DISK_TEST=1` on macOS to enable disposable 64 MiB APFS image
tests. They mount beneath unique `/private/tmp` fixtures, verify cleanup refuses
filesystem boundaries, then fill only the scratch volume to test failed namespace
replacement and recovery after cache deletion. The fixture checks mount and image
attachment state before removing its files. Ordinary tests skip these disk-image
cases.

The integration probe `MountCli_CacheVolumeFull_ReadFailsAndRecoversAfterSpaceIsFreed`
adds `ROMD_MOUNT_DISK_TEST=1` to the native mount prerequisites below. It requires
the default 4 MiB synthetic content (no `ROMD_VFS_PROBE_FILE` override), fills only
a disposable cache volume, checks that an uncached mounted read fails without
returning bytes, then frees space and verifies the same range and full hash on
the same mount. This covers filesystem read recovery, not emulator behavior
after a failed read or sustained eviction/quota behavior.

The opt-in `MountCli_MountedReadsAndGracefulShutdown` integration test additionally
requires `ROMD_VFS_MOUNT=1` and `ROMD_VFS_RCLONE`. It verifies real CLI login,
exclusive credentials, a full-content hash through the native mount, SIGTERM
unmount, and subsequent logout/revocation. It uses synthetic test content.
`MountCli_MountedExpiryAndServiceOutage` uses the same opt-ins and requires
observed 401s, refresh-token rotation, 503s, and aborted HTTP connections before
accepting successful uncached reads on the same mount.
