# Known Validation Issues

This file lists open, deferred and mitigated tooling, environment and test
issues. Entries whose status became FIXED live verbatim in the
[resolved-issue archive](known-issues-archive.md), kept for regression
diagnosis. On an unexpected failure, search this file by the command, error
text or tool name and read the matching section; if nothing matches, search the
archive the same way. A recurring archived issue is a regression to diagnose,
not an accepted failure.

Entries tied exclusively to removed tools, schemas or workflows are pruned.
Continuing operational rules belong in the owning guide rather than a historical
incident narrative. The archive retains fixes relevant to the current toolchain.

## Native macOS WebDAV EOF-crossing reads can return padded bytes

Status: DEFERRED

Command: opt-in `Vfs_NativeWebDav_ReadsMatchAuthorizedCas` with a 1.46 GB
GameCube ISO, macOS 26.6.2. Isolated with
`scripts/mount/native_webdav_eof_matrix.py`.

Observed: a pread requesting 4,096 bytes from 1,024 bytes before EOF returned
4,096 bytes with zero padding. Direct HTTP returned the correct 206 and 1,024
bytes. This reproduced against a standalone rclone WebDAV fixture and against
ROMD's actual endpoint. Cold-file `/bin/cp` and sequential 1 MiB reads produced
padded output in every run, including unthrottled loopback.

Root cause: macOS webdavfs kernel defect, matching Apple's published
`webdavfs-403` source. While the background whole-file download is more than
32 KiB behind a read's end (and offset is not 0), `webdav_read_bytes` fetches a
closed range and copies the requested count, not the received count. Warm
caches, reads within the 32 KiB window, and offset-0 reads are unaffected. No
correct 206 avoids it. In the matrix, a `User-Agent` targeted 503 for
EOF-crossing ranges produced exact reads, at the cost of stalling cold
EOF-crossing reads until the whole file has downloaded.

Change: added an opt-in qualification probe and a diagnostic matrix, including
the 503 experiment. The earlier proposal to ship this workaround is superseded
by the [WebDAV connection decision](../decisions/vfs-webdav-connections.md).
Native macOS WebDAV mounting is unqualified and unsupported for release 1;
do not implement the client-specific 503 rule. ROMD's range semantics remain
standards-correct.

Agent guidance: preserve exact read-length assertions and test EOF while the
file is cold. Do not truncate native results, return non-compliant 416s, count
title-screen rendering as qualification, or claim that the separate
authentication adapter implements a production sign-in flow.

## Installed macFUSE does not establish FSKit readiness

Status: DEFERRED

Command: `rclone mount <disposable-source> /Volumes/ROMD-FSKit-Probe --option backend=fskit --read-only`

Observed on macOS 26.6.2 with macFUSE 5.4.0 and rclone 1.75.1: Homebrew and
package receipts confirmed installation, but mounting exited 1 with
`File system extension not enabled`. The macOS log identified
`io.macfuse.app.fsmodule.macfuse-local`; its System Settings switch remained off.
The host is managed. Whether management policy caused the failure is unknown.

Update 2026-09-26: after the user enabled the extension, the same command
mounted with flags `macfuse, nodev, nosuid, noowners, noatime, fskit`. The
owner's reads were exact, including EOF-crossing reads, and rclone held no
network sockets. **At `/Volumes`, local-user isolation failed:** `sudo -u nobody`
listed the mount (files shown as owned by `nobody`) and read correct file bytes
despite 0700/0400 modes. macFUSE documents that its FSKit backend lacks caller
context and most kernel mount options. Unmount also deleted the admin-prepared
`/Volumes` mount point.

Update 2026-09-27: mount points beneath the home directory (`~/Volumes/…`,
`~/.romd/…`) mounted without administrator help, contrary to macFUSE's
documentation, and survived unmount. A private (0700) parent blocked `nobody`
even when granted traversal of the home directory. A mount point beneath
`/private/tmp` failed FSKit preflight.

Change: none to the backend. The disposable probe unmounted and left no process.

Agent guidance: check actual native mount readiness, not only bundle presence.
With FSKit, mount only beneath a private (0700) directory; the volume itself
reports `noowners`, so its file modes do not restrict other local users.
Do not interpret this setup failure as a WebDAV or CAS performance result, switch
to the kernel backend, reset LaunchServices, or reboot as an automatic workaround.
Continue qualification on an eligible host following
[ROMD Mount](../../clients/romd_mount/README.md).

## Browser playback SHA-256 verification on insecure LAN origins

Status: DEFERRED

Surface: consumer browser playback on plain-HTTP, non-localhost LAN origins

Root cause: the parent portal verifies downloaded ROM bytes with
`crypto.subtle.digest` before transferring the verified `Blob` to the isolated
player origin. Browsers expose Web Crypto `subtle` only in secure contexts
(with localhost treated specially), so an HTTP LAN hostname or address may not
provide the required SHA-256 API.

Change: the consumer player now detects the missing API before requesting a
manifest and reports browser playback as unavailable. Regular downloads are
unchanged.

Agent guidance: do not bypass or downgrade content verification. Use HTTPS for
the portal origin (or localhost during development). Treat a missing
`crypto.subtle` report on plain-HTTP LAN as this known platform limitation,
not as an emulator asset or content-grant failure.

## Console `mise run analyze` Flutter ephemeral Swift package state

Status: DEFERRED

Command: `mise run analyze` from `clients/romd_console`.

If an error names
`macos/Flutter/ephemeral/Packages/FlutterGeneratedPluginSwiftPackage`, regenerate
ignored Flutter state with `mise exec -- flutter clean`, then rerun analysis.
This is a recovery route, not a claim that every package-link crash has that
cause. If it persists, retain the exact error and toolchain versions and diagnose
it before changing dependencies or replacing generated files.

## Console Flutter SDK cache writes under sandbox

Status: DEFERRED

Commands:

- `mise run analyze` from `clients/romd_console`
- `mise run test` from `clients/romd_console`
- `mise exec -- dart format ...` from `clients/romd_console`
- `mise exec -- flutter test ...` from `clients/romd_console`
- `mise exec -- dart run ...` from `clients/romd_console`

Root cause: the Flutter/Dart SDK installed by mise may try to update files under
`~/.local/share/mise/http-tarballs/.../bin/cache`, such as
`engine.stamp.tmp.*` or `engine.realm`, before running the requested command. In
a workspace-write agent sandbox that cache path is outside the writable roots,
so the command can fail with `Operation not permitted` before project analysis,
formatting, tests, or tools actually run.

Change: None. This is an agent sandbox/tool-cache interaction, not a ROMD code
or test failure.

Agent guidance: if one of the commands above fails only with `Operation not
permitted` under the mise Flutter SDK cache path, treat it as a sandbox artifact.
Rerun the same command with scoped escalation and report both the sandbox failure
and the rerun result. Do not clean generated Flutter state or change project code
unless the rerun reaches a real project error.

## Console theme validation scripts on Windows

Status: DEFERRED

Command: `mise run analyze` from `clients/romd_console` on Windows

Root cause: the theme-governance steps are Bash scripts that also use Unix
utilities and `rg`. Mise executes their bare `.sh` paths through `cmd.exe` on
Windows, which reports `'tool' is not recognized as an internal or external
command` before Flutter analysis starts.

Change: None. The Windows console can still be analyzed with `flutter analyze`,
but that command does not run the additional theme-governance checks.

Agent guidance: report the wrapper limitation explicitly and run
`flutter analyze` as a partial Windows signal. Do not claim that
`mise run analyze` passed unless the Bash-based checks actually ran. Prefer a
focused cross-platform port of the governance checks over silently dropping
them from the task.

## Console macOS debug bundle seal after native-asset packaging

Status: DEFERRED

Commands:

- `mise exec -- flutter build macos --debug` from `clients/romd_console`
- `codesign --verify --deep --strict build/macos/Build/Products/Debug/Ottercade.app`

Root cause: Flutter 3.44.2's debug macOS packaging produced individually valid
`App.framework` and `objective_c.framework` signatures, but the enclosing app
seal reported those two generated frameworks as modified. Two clean debug
rebuilds reproduced the same result. The equivalent release build packaged
successfully and passed strict deep signature verification. Release packaging
also warned that the `objective_c` and `sqlite3` native-asset hooks supplied
different framework names per architecture before selecting one name.

Change: None. The PCSX2 helper added by ROMD was present, x86_64, and valid in
both configurations; manually re-signing generated build output would hide the
Flutter packaging signal rather than fix project source.

Agent guidance: use a release macOS build for final packaging/signature
acceptance. If a debug deep-signature check names only these generated native
asset frameworks, verify each framework and any newly embedded ROMD helper
individually, report the debug limitation, and confirm the release bundle with
`codesign --verify --deep --strict`. Treat any other modified nested code, or a
release-bundle failure, as a new blocking signal.

## Dolphin Qt CPU-feature probe under agent sandbox

Status: DEFERRED

Command: `Dolphin.app/Contents/MacOS/Dolphin --help`

Root cause: the signed universal Dolphin 2606 macOS executable aborts inside
the workspace agent sandbox with Qt reporting that the `neon` CPU feature is
missing. The identical arm64 executable exits successfully and prints its help
text when run outside the sandbox. The host and process architecture are both
arm64, so this is a sandbox visibility/probe interaction rather than evidence
of an incompatible Dolphin artifact.

Change: None.

Agent guidance: if Dolphin exits in the sandbox with only the Qt
`Incompatible processor` / missing `neon` message, rerun the identical
read-only version or help probe with scoped escalation. Do not change the
artifact, force Rosetta, or weaken runtime validation based on the sandbox-only
signal. A matching failure outside the sandbox or during a real ROMD launch is
blocking.

## MSBuild named-pipe permission failure in the sandbox

Status: MITIGATED

Command: `mise run test:integration`

Root cause: the restricted execution sandbox can deny MSBuild or VSTest permission
to bind its local named pipe. The command then exits before tests run with
`MSB1025` and `System.Net.Sockets.SocketException (13): Permission denied` from
`NamedPipeServerStream`; the unchanged command passes outside the sandbox.

Change: None; this is an execution-environment limitation, not a product or build
configuration defect.

Agent guidance: when this exact stack trace occurs before test execution, rerun
the same validation once with escalation scoped to that command and report both
results. Do not change MSBuild settings, disable parallelism, or alter product
code as a workaround. A failure outside the sandbox or after tests start is not
covered by this mitigation and requires diagnosis.

## BrowserPlayer rapid-exit timing assertion in web CI

Status: DEFERRED

Command: `pnpm test` from `web/`

Root cause: the `BrowserPlayer` test named `shares an in-flight shutdown so
rapid exits cannot remove the iframe early` can assert the mocked shutdown call
before React Router has delivered the navigation update. The observed CI
failure reported zero calls at `BrowserPlayer.test.tsx:189` together with an
unwrapped `MemoryRouter` update warning; rerunning the unchanged commit passed
the complete web job (444/444 tests).

Change: None.

Agent guidance: when this exact assertion fails on a branch that does not touch
the consumer player or routing, inspect the full log and retry the failed web
job once. A clean retry is flaky-test evidence and must be reported as such. A
second failure, a different BrowserPlayer assertion, or any player-related diff
is blocking and should be diagnosed instead of retried again.

Recurrence (2026-09-29): main CI run `36510679526` failed this assertion (now at
`BrowserPlayer.test.tsx:275`) with the same unwrapped `MemoryRouter` warning on a
backend-only commit; the single web-job retry passed (1,370 tests).

## Integration tests inherit a disposed `JobActivator.Current`

Status: MITIGATED

Command: `mise run test:integration`

Root cause: several integration fixtures build ASP.NET Core hosts whose
Hangfire registration replaces the process-global `JobActivator.Current` with a
DI-bound activator, then dispose the host. A later test that starts its own
in-process `BackgroundJobServer` without naming an activator runs jobs through
that stale activator and every job fails with
`Cannot access a disposed object. Object name: 'IServiceProvider'`. The same
test passes when run in isolation, so the failure looks flaky.

Change: `PostgreSqlRolePrivilegeTests` sets `BackgroundJobServerOptions.Activator`
explicitly; `HangfirePostgreSqlColdStartTests` saves and restores
`JobActivator.Current` around its server.

Agent guidance: any test that starts a `BackgroundJobServer` in the integration
test process must supply its own `JobActivator` (or save/restore the global one).
Treat an isolated pass plus a full-suite `disposed IServiceProvider` failure as
this leak, not as a storage or role problem.

## Docker daemon unreachable: Rancher Desktop Lima instance `Broken`

Status: MITIGATED

Commands:

- `docker info`
- `mise run test:integration`
- `mise run db:up`

Root cause: after an unclean stop, Rancher Desktop's Lima instance `0` can keep
a stale host-agent socket and pid files (`ha.sock`, `ha.pid`, `vz.pid` under
`~/Library/Application Support/rancher-desktop/lima/0`). `limactl list` then
reports the instance as `Broken`, `rdctl api /v1/backend_state` returns
`{"vmState":"ERROR"}`, and every start fails with
`dial unix .../ha.sock: connect: connection refused` while the app itself keeps
running. Testcontainers tests fail before any container starts.

Change: None in the repository. Recovery: confirm the pids in `ha.pid` and
`vz.pid` are dead (`ps -p <pid>`), `rdctl shutdown`, delete those three files,
relaunch Rancher Desktop, and wait for `docker info` to answer (about 30 s).

Agent guidance: check `docker info` before interpreting any Testcontainers or
`db:*` failure. Never delete the Lima socket or pid files while their pids are
alive, and never touch `diffdisk`/`basedisk`.

## Rancher Desktop guest disk I/O errors after the host volume filled up

Status: MITIGATED

Commands:

- Rancher Desktop restart from the app ("Error Starting Rancher Desktop … limactl
  exited with code 1", `sudo: unable to open /etc/sudo.conf: I/O error`,
  `you do not exist in the passwd database`)
- `docker system df`, `docker exec …` (`input/output error`)
- `dotnet build` (`MSB3026 … No space left on device`)

Root cause: the macOS Data volume reached zero bytes free while Testcontainers
suites and the scale harness were writing. The Lima VM's 100 GiB `diffdisk` is a
sparse file on that volume, so the guest's virtio disk started returning I/O
errors even though the guest filesystem itself was only 17% used. The guest
kept running with a damaged in-memory view of `/dev/vda1` (reads of
`/etc/passwd` and `/etc/sudo.conf` failed), `rdctl api /v1/backend_state` reported
`{"vmState":"ERROR"}`, and the in-app restart failed because Rancher Desktop
needs `sudo` inside the guest to reconfigure logrotate. This is not the
`Broken`-instance case above: `limactl list` still reported `Running` and the
pid files were live.

Change: none in the repository. Recovery: free host space first, then
`rdctl shutdown` (it shut the instance down cleanly here; `limactl stop -f 0`
is the fallback), relaunch Rancher Desktop, and wait for `docker info`. The
fresh boot remounted the disk with its journal intact; images, the dev
PostgreSQL volume, and the container all came back.

Agent guidance: check `df -h /System/Volumes/Data` before generating scale
datasets or running long Testcontainers suites, and stop when free space drops
below the dataset's expected footprint (the 1M harness dataset needs roughly
6–10 GiB of PostgreSQL space). Never restart or reset the user's Rancher
Desktop without their word; Factory Reset wipes every image and volume.

## Docker cache cleanup does not immediately reclaim host disk space

Status: MITIGATED

Commands:

- `docker builder prune --all --force`
- `df -h /System/Volumes/Data`
- `rdctl shell sudo fstrim -v /mnt/data`

Root cause: after recovering from host-disk exhaustion on 2026-09-09, Docker
reported 68.2 GB of build cache reclaimed, but macOS still had only 6.1 GiB
free. The virtual disk retained allocation for already-free guest blocks.

Change: after the user-approved Rancher Desktop restart and cache cleanup,
trimming the guest data mount returned free blocks to the host. macOS then
reported roughly 70 GiB free. Application images and volumes were preserved.

Agent guidance: verify the guest data mount with `rdctl shell df -h` before
trimming it. This guest uses BusyBox `fstrim`, which does not support `-a`;
specify the verified mount. Compare host free space before rebuilding, rather
than treating Docker's reclaimed-byte total as host capacity. Cache cleanup
itself can fail with an I/O error until the VM has restarted; restart only
with user approval, and never factory-reset or delete the VM disk.

## Backup restore drill can retain a recurring-job lock

Status: DEFERRED

Command: `mise run test:integration`

Root cause: A September 2026 run restored a Hangfire recurring-job lock and the
new worker exited with `PostgreSqlDistributedLockException` for
`hangfire:lock:recurring-job:dat-subscription-checks`. The failing assertion was
`targetWorker.HasExited` in `BackupSetRestoreDrillTests`. This occurred while a
Docker image build was running; whether load contributed is unconfirmed.

Change: None to production or test behavior. An isolated rerun with
`dotnet test tests/Romd.Hosting.IntegrationTests/Romd.Hosting.IntegrationTests.csproj --no-build --filter FullyQualifiedName~BackupSetRestoreDrillTests`
passed (1 test).

Agent guidance: Inspect the worker output before attributing this failure to
schema changes. Report the failed broad run separately from an isolated pass.
Do not clear production Hangfire locks or weaken readiness checks to make the
drill pass. If it recurs, investigate source-worker shutdown and restored lock
expiry as a separate backup/restore task.

## Rancher Desktop cache cleanup may need filesystem trim

Status: MITIGATED

Commands:
- `docker builder prune --all --force`
- `rdctl shell df -h`
- `rdctl shell sudo fstrim -v /mnt/data`

Root cause: Deleting unused Docker build cache freed VM storage but did not
immediately return sparse disk blocks to macOS. In September 2026 the host still
had about 250 MiB free after cache cleanup while the VM data filesystem had more
than 76 GiB free. Trimming its confirmed `/mnt/data` mount returned host free
space to 66 GiB without deleting images, containers, or volumes.

Change: Removed regenerable build cache and trimmed already-unused VM blocks.

Agent guidance: Confirm the runtime is Rancher Desktop and inspect its mounts
before trimming. Its Alpine BusyBox `fstrim` requires a mountpoint and does not
support `-a`. Do not delete persistent volumes to resolve build-cache pressure.

A later full-disk incident left Docker metadata operations and guest `sudo`
failing with I/O errors, so trimming could not run. A user-approved Rancher
Desktop shutdown/start restored guest access; trimming then succeeded and macOS
reported 33 GiB free. Existing development volumes survived and both API readiness
checks passed. If this state recurs, account for other running projects before
restarting, wait for the existing shutdown/start operation, then verify services
and remove only the identified disposable test resources. Do not reset the runtime
or prune persistent volumes as a recovery shortcut.

## Flutter tests fail in objective_c when the Xcode license is unaccepted

Status: MITIGATED

Commands:

- `mise run test` from `clients/romd_console/`
- `xcrun --sdk macosx --show-sdk-path`

Root cause: the `objective_c` native-asset build hook reads the first line of
`xcrun` output. When Xcode requires license acceptance, `xcrun` returns no SDK
path and the hook throws `Bad state: No element` before Flutter tests execute.
The direct `xcrun` command reports the unaccepted Xcode license.

Change: the user accepted the Xcode license; rerunning `mise run test` now
executes the Flutter tests past the native-asset build hook. On affected hosts,
the user must review and accept the license through Xcode or
`sudo xcodebuild -license`, then rerun the tests. Flutter static analysis alone
is not evidence that native test execution succeeded.

Agent guidance: diagnose the SDK lookup before changing native dependencies or
clearing Flutter caches. Do not accept the license on the user's behalf.

## Docker consumes scripts streamed over SSH stdin

Status: MITIGATED

Command: `ssh host 'bash -s' < maintenance.sh` with Docker Compose/exec commands
inside the script that inherit stdin.

Root cause: Docker can consume the remaining shell script from stdin. A NAS
upgrade attempt stopped APIs, then ended before worker shutdown or migration;
the release pointer and migration history remained unchanged. An exit trap ran,
so a zero exit status was not evidence that the upgrade had completed.

Change: copied the maintenance script to the remote host and executed it using
`ssh -n host 'bash /path/to/maintenance.sh </dev/null'`. The complete upgrade and
post-deployment health/authentication verification then passed.

Agent guidance: execute remote maintenance scripts from a file with stdin closed,
or explicitly redirect every Docker invocation from `/dev/null` when streaming
scripts. Verify the release pointer, image digests, migration, and health after
execution rather than treating the SSH exit code alone as completion evidence.

## Enrichment scale-test timeout on a hosted CI runner

Status: MITIGATED

Commands: PR #462 CI runs `41496435161` and `41502243209`, infrastructure
other partition, Release with coverage. The latter passed 3,794 cases and failed
only the concurrent full-scope `ReadCountsAsync` aggregate at its normal
30-second command deadline. No lock blockers were observed.

Root cause: the SQL facts function caches a generic plan after bounded page
reads. A fresh-session reproduction with the same full graph measured a cold
whole count at 350 ms, then warmed automatic counts around 4.8 seconds. Nested
plans changed from 65,117 estimated rows to 81 for the same 102,000 actual
requirement rows. The generic nested loop discarded 199,980,000 join pairs.
Forced generic reproduced that shape; forced custom returned identical counts
around 1.05 seconds. These are instrumented diagnostic timings, not portable
latency thresholds. The function already sets `jit=off`; the caller's `jit=on`
setting did not identify the work inside it. PostgreSQL's
[SQL executor](https://raw.githubusercontent.com/postgres/postgres/REL_18_STABLE/src/backend/executor/functions.c)
and [planning policy](https://www.postgresql.org/docs/18/sql-prepare.html)
explain why an unprepared outer command can still encounter cached inner plans.

Change: only workbench counts split the scoped ID array into disjoint ordinal
batches of at most 512 titles before calling the unchanged canonical facts
function. The final per-title classification and aggregate remain one SQL
statement with the same snapshot and command deadline. PostgreSQL 18's shipped
[information-schema array helper](https://raw.githubusercontent.com/postgres/postgres/REL_18_STABLE/src/backend/catalog/information_schema.sql)
provides named value/ordinal columns; the EF mapping is read-only and provisions
no view. No planner setting, SQL resource pin, migration or readiness version
changes. Numeric ID-range bucketing was measured privately and rejected because
widely spaced IDs increased invocation overhead.

Local controls retained all 20,000 titles, 10,000 tracked titles and the complete
identity/artwork/history graph. The final implementation measured roughly
0.45–0.52 seconds for dense and sparse IDs, with all five counts identical under
automatic and forced-generic planning. Sparse IDs were spaced 4,096 apart; the
population was not reduced. Private probe methods and raw plans are not committed.

Regressions cover empty, exact and partial arrays, complete requirement/cause and
summary parity across multiple batches, signed/sparse IDs, untracked malformed
evidence, and runtime-role access. Keep the existing four-reader/writer workload,
full seed, 30-second query deadline and 120-second inactivity guard. Do not replace
server batching with client loops or rely on an outer Function Scan to diagnose
an inner plan; use bounded nested-plan and contention evidence.

History: release runs `35681112631` and `36512345933` failed this count path while
independent main runs passed. An earlier nonpooled diagnostic did not reproduce
the transition and forced custom planning was slower, so it did not justify a
production planning change. The later matched full-graph/page-read reproduction
above establishes the missing parameter-history evidence. Earlier unchanged
retries are historical passes, not fixes. Separating independent panel tests
fixed their individual inactivity budgets but did not fix this count timeout.

## macOS rclone NFS probe can stall with default hard mount options

Status: MITIGATED

Command: `ROMD_VFS_MOUNT=1 ROMD_VFS_RCLONE=<verified-rclone-path> dotnet test tests/Romd.Hosting.IntegrationTests --filter FullyQualifiedName~Vfs_RcloneMounted`

Root cause: not established. On ARM64 macOS with rclone 1.75.1, the initial
probe mounted successfully but stalled on the first filesystem lookup. `nfsstat
-m` confirmed `hard,nointr`; the HTTP host remained responsive. A managed
cancellation token could not interrupt the synchronous filesystem lookup.

Change: the opt-in probe supplies `ro,soft,intr,nolocks,noquota,timeo=2,retrans=1`,
uses a loopback listener, and bounds its unmount process wait. The revised probe
completed byte-accurate reads, whole-file hashing, and unmount successfully.
This does not establish the cause of the original stall or guarantee every
kernel filesystem operation is interruptible.

Agent guidance: keep this test opt-in. Check the mount table and child processes
after failure; never recursively delete a directory containing a live mount.
Force-unmount may require macOS administrator authentication through the system
prompt. Do not treat a mount-table entry as proof of working filesystem reads,
or use these experimental probe options as a production deployment claim.

## Rclone WebDAV mkdir can report success for rejected writes

Status: MITIGATED

Command: registered-connection qualification with stock rclone 1.75.1,
`rclone mkdir romd:write-must-fail` against read-only ROMD WebDAV.

Root cause: the WebDAV backend treats an MKCOL HTTP 405 as an existing directory
and returns success. The probe observed exit 0 while a subsequent listing
confirmed that no directory existed. Actual uploads failed. See the
[upstream implementation](https://github.com/rclone/rclone/blob/v1.75.1/backend/webdav/webdav.go#L969-L1004).

Change: the qualification probe independently verifies namespace state and
upload rejection. Its FSKit mount uses `--read-only` and verifies that a local
write fails with no file created. ROMD's method rejection remains unchanged.

Agent guidance: do not interpret mkdir's exit status as proof of a writable
server. Use read-only mounts and retain the separate local-write assertion.

## FSKit can retain same-size, same-time file bytes after directory refresh

Status: MITIGATED

Command: `ROMD_VFS_REGISTERED_FSKIT=1 ROMD_VFS_RCLONE=<verified-rclone-path> dotnet test tests/Romd.Hosting.IntegrationTests --filter FullyQualifiedName~Vfs_Https_RegisteredFskit_RefreshesSameSizeReplacement`

Root cause: exact ownership is unproven. On macOS 26.6.2, macFUSE 5.4.0 and
rclone 1.75.1, a reopened file returned old bytes despite refreshed directory
metadata. Debug output showed open/release with no content read reaching
rclone. The behavior persisted with `--direct-io`; do not recommend that flag
as a verified fix.

Change: qualification separately checks rclone's VFS cache and records FSKit
same-mount behavior. A normal remount retains the disk cache and forces fresh
filesystem reads.

Agent guidance: do not infer mounted content freshness from PROPFIND, new ETags,
or directory refresh alone. Record hash-verified bytes. The current Mac refresh
procedure requires remounting; same-mount replacement is not qualified.

## Rclone 1.75.1 rapid replacement during cache close grace period

Status: DEFERRED

Command: run the VFS freshness probe with the six-second post-read settling
wait removed.

Root cause: rclone debug logs show checksum invalidation removing an existing
cache file while its download-close grace period still holds the descriptor.
The HTTP VFS frontend returned an empty response on reopening the replacement.
The precise upstream defect has not been patched or isolated outside rclone.

Change: the qualification probe explicitly waits for a settled disk cache and
documents this narrower scope. Rapid replacements are not qualified.

Agent guidance: retain the settling interval and its explanation; do not claim
this test covers replacements during active/recent reads. Keep the separate
FSKit limitation visible.

## HttpClient byte-array inspection of large HEAD responses

Status: MITIGATED

Command: run `tools/Romd.Media.Probe` against a Wii ISO representation with
`ReadAsByteArrayAsync()` in the HEAD-body assertion.

Observed on .NET 10: a successful HEAD advertising the 4,699,979,776-byte ISO
length failed in `HttpContent.CreateTemporaryBuffer` with `Cannot write more
bytes to the buffer than the configured maximum buffer size: 2147483647`.
The response has no payload; byte-array buffering nevertheless consults its
advertised content length. This is a test-client buffering limitation, not
evidence that the server sent a full ISO in response to HEAD.

Change: the probe separately asserts Content-Length and reads one byte from
`ReadAsStreamAsync()`, requiring EOF. Large GET bodies are also verified using
streaming hashes rather than byte-array buffering.

Agent guidance: use streaming body assertions for large-file HEAD tests. Keep
the original advertised length; do not change server headers or weaken the
empty-body assertion to work around this client behavior.

## chdman verification can return success exit status after hash errors

Status: DEFERRED

Command: `chdman verify -i <scratch-copy.chd>` using the researched macOS ARM64
`chdman 0.289 (unknown)` build, against a scratch copy with a deliberately
corrupted header hash.

Observed: corrupting the raw SHA-1 or overall SHA-1 header returned exit 0 while
printing a mismatch. Zeroing raw SHA-1 also returned 0 with "No verification to
be done". The overall-hash failure printed successful raw-hash verification first.
These observations were reviewed from the saved research evidence; they were not
rerun during the representation architecture review.

Root cause: this build's exit status does not distinguish these failed or absent
verification outcomes from successful verification. A raw-hash success message
alone also does not establish overall/container integrity.

Change: recorded the integration gate in the
[game media decision](../decisions/game-media.md#standalone-dvd-chd). Current
CHD production paths independently measure logical/container evidence and do not
rely on chdman's success exit status. No upstream-tool fix is established here.

Agent guidance: gate publication on explicit complete verification for the
supported profile and independently computed logical hashes/lengths, not exit
code alone or any single success line. Reject absent required checksums. Keep
negative fixtures for raw/overall mismatch and missing hashes when wrapping the
pinned tool. Never use `verify --fix` on an immutable CAS artifact. Requalify the
behavior when changing tool versions rather than assuming all versions share it.

## Rancher Desktop temporary bind mounts need the resolved host path

Status: MITIGATED

Commands: a media qualification container mounted `/tmp/romd-rvz-verification`
but could not find the host's `fixtures.json`; mounting
`/private/tmp/romd-rvz-verification` at the same container destination exposed it.

Root cause: on this macOS/Rancher Desktop setup, `/tmp` in a Docker bind source
did not resolve to the host's `/private/tmp` files. A container can start with an
empty directory rather than report a missing fixture mount.

Change: qualification commands use `/private/tmp` as the bind source and keep
the fixture-recorded `/tmp` paths as container destinations.

Agent guidance: use resolved absolute host paths and check one expected fixture
inside the container before large probes. Do not regenerate or copy fixtures to
work around an unverified mount.

## Media recipe probe can stall during host startup in the macOS sandbox

Status: MITIGATED

Command: `dotnet /absolute/repo/tools/Romd.Media.Probe/bin/Release/net10.0/Romd.Media.Probe.dll --recipe-probe /absolute/spec.json /absolute/results.json`
from a scratch directory on macOS 26.6.2 with .NET SDK 10.0.302.

Observed: two restricted launches, from the checkout and from scratch, wrote
`Status: in-progress` with no reports, then stalled in
`Host.CreateApplicationBuilder()` before the first fixture progress message.
A native sample showed `.NET File Watcher`, `FSEventStreamStart` and
`FSEvents_connect` frames. Both owned processes were terminated with exit 143.

Root cause: scoped escalation let the unchanged binary complete, implicating
the restricted launch environment. The exact underlying permission or runtime
cause has not been established; changing the working directory did not fix it.
This is separate from build/restore failures.

Change: the same compiled probe completed with scoped escalation from scratch,
then passed again after the final build. Both runs recorded `Status: passed`,
three complete decode/hash trials and 20,000 byte comparisons, including empty
EOF reads.

Agent guidance: an in-progress checkpoint is not a pass. If a restricted launch
stalls before fixture work, retain its output and collect startup/sample
evidence before terminating only the owned process. For this matching failure,
rerun the unchanged command with scoped escalation and report both attempts.
Do not disable file watching, change host settings, or suppress warnings to
make qualification pass. A stall after fixture work or outside the sandbox
needs separate diagnosis.

## Shouldly async assertions can change cancellation exception identity

Status: MITIGATED

Commands:

- `dotnet test tests/Romd.Hosting.IntegrationTests/Romd.Hosting.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~VfsProbeReadTests|FullyQualifiedName~VfsProbeContentTests|FullyQualifiedName~VfsProbeProcessTests'`
- `dotnet test tests/Romd.Hosting.IntegrationTests/Romd.Hosting.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~VfsProbeDiagnosticsTests|FullyQualifiedName~VfsProbeEvidenceTests'`

Root cause: the exact internal cause is not established. With Shouldly 4.3.0
on .NET 10, two cancellation identity checks received a `TaskCanceledException`
from `Should.ThrowAsync<OperationCanceledException>` instead of the original
`OperationCanceledException`. Direct `await` and `catch` observed the original
exception object in both checks.

Change: the cancellation checks in `VfsProbeReadTests` and
`VfsProbeEvidenceTests` await the tested operation directly, catch its exception,
and retain `ShouldBeSameAs` plus the read-join and cleanup assertions.

Agent guidance: when testing cancellation exception identity, capture the
exception through direct `await` and `catch`. Keep object identity and cleanup
checks; do not weaken them to a base-type assertion or change production
cancellation behavior to accommodate an assertion wrapper. Other exception
types and future Shouldly versions need their own evidence.

## Restricted .NET builds can stall without diagnostics

Status: MITIGATED

Command: `dotnet build src/Romd.Worker.Host/Romd.Worker.Host.csproj --no-restore`

Root cause: not established. On ARM64 macOS 26.6.2, a restricted EF automatic
build failed after about five minutes with only its generic build-failed
message. An explicit restricted build then produced no output for over six
minutes. A native sample showed socket-connect and sleep frames, with
unsymbolicated managed frames; neither log contained `MSB1025` or a permission
error. This does not establish the named-pipe failure described above.

Change: the same explicit build command completed with scoped escalation in
about 16 seconds, with zero warnings and errors. EF migration generation then
passed with `--no-build` against that fresh build. The stalled explicit build
was terminated with TERM; its wrapper recorded exit 0, which is an interrupted
run rather than a successful build.

Agent guidance: retain output and process evidence for a matching no-progress
build before terminating only the owned process. Rerun the unchanged command
once with escalation scoped to that command and report both attempts. Record
owned interruption explicitly even when the observed exit code is 0. Use EF
`--no-build` only after a successful build from the same source and matching
configuration; migration generation does not replace migration tests. Keep
build flags, MSBuild settings and validation checks unchanged. A quiet build
outside the sandbox, or a build with actionable errors, needs separate diagnosis.

## Rosetta amd64 startup traps under the Linux address-space limit

Status: DEFERRED

Commands: the production amd64 image under existing macOS ARM64 Docker/Rosetta
emulation, with its unchanged cleared child environment:

```bash
/usr/bin/prlimit --as=34359738368 --data=1073741824 --cpu=600 --core=0 -- \
  /usr/share/dotnet/dotnet /app/media-worker/Romd.Media.Worker.dll --help
```

Root cause: the tested Rosetta translation cannot start this runtime under the
32 GiB address-space limit. It exits 133 with `rosetta error: could not find free
space for allocation size ffffffff9999a000`. An otherwise identical control
omitting only `--as` reaches normal invalid-CLI exit 64; this control qualifies
nothing. Actual supervised handshakes also fail before permission, with both
3-second and default 600-second deadlines. This does not establish a native
amd64 defect. Evidence: macOS 26.6.2 ARM64, .NET 10.0.12, production worker
image, 2026-10-03.

Change: None. Production address-space, DATA, GC, CPU and wall limits remain
unchanged; no emulator or tool was installed.

Agent guidance: preserve failed reports and report emulated amd64 containment as
unqualified. A successful image build or `dotnet --info` does not qualify child
startup, limits or recovery. Do not remove or raise production limits to obtain
a pass, infer native amd64 behavior from ARM64, or install another emulator
without owner approval. Qualify native amd64 on an available approved Linux
runner; keep diagnostic controls separate from acceptance evidence.

## Reduced-priority encoding test fails under an already-niced agent session

Status: DEFERRED

Command: `dotnet test tests/Romd.Infrastructure.Tests/Romd.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~RvzEncodingServiceTests'`

Root cause: `EncodeAsync_AlreadyReducedParentPriority_PreservesUnixNiceOrAppliesWindowsClass`
launches the real media worker through `/usr/bin/nice -n 15` or `-n 19` and
assumes the test process runs at niceness 0. Claude Code background sessions on
macOS run at niceness 5, so the child lands at 20 (macOS `PRIO_MAX`;
`/usr/bin/nice -n 15 sh -c 'ps -o nice= -p $$'` prints `20` there). The
worker's `NativePriority.TryReduce` (`src/Romd.Media.Worker/Program.cs`) accepts
only 10 to 19; outside Linux it then calls `setpriority(..., 10)`, which an
unprivileged process cannot use to raise priority from 20, so the child replies
`IoFailure` and both cases fail. The supervisor and protocol 10-to-19 bounds are
a second guard that this path never reaches. Linux clamps at 19. The failure
reproduces identically on an exported copy of the 2026-10-05 `main` revision, so it is
independent of the code under test. Evidence: macOS 26.6.2 ARM64, 2026-10-05.

Change: None. The test, protocol bounds and worker priority policy are
unchanged.

Agent guidance: check `ps -o nice= -p $$` before treating these two cases as a
regression. From a niced session, report them as failed with this cause and
rerun the class from a niceness-0 process when an unqualified pass is required.
A job submitted with `launchctl submit -l <label> -- /bin/sh <script>` starts at
niceness 0; use absolute tool paths, `--no-build` against the same build, and
`launchctl remove <label>` afterwards.
Do not raise the protocol bound, edit the expected range or skip the cases to
obtain a pass. Whether macOS workers started from niced parents should accept
20 is a separate product decision.

## ClangSharp 21.1.8.4 needs newer glibc than the libchdr Linux builder

Status: MITIGATED

Command (AlmaLinux 8.10 arm64 builder): `dotnet run --project tools/Moonlark.Native.Engineering -c Release -- generate --check`

Root cause: the pinned ClangSharpPInvokeGenerator Linux arm64 executable
requires GLIBC_2.29, GLIBC_2.32 and GLIBC_2.34. Tool restore succeeds on the
approved glibc 2.28 builder, but execution fails in the dynamic loader before
binding generation starts. This is independent of libchdr's native binary:
its build, ABI inspection and tests passed in that builder.

Change: the Dockerfile `clang-resources` stage exports the pinned Clang 21
resource headers from the Alma builder, and `scripts/native/build.sh
generation-check` runs the pinned generator on the Linux host with
`--clang-resources`, separately from the native build in the older-glibc stage. The exported headers are
hashed before and after generation. Explicit `char=sbyte` mapping keeps the
pinned API's plain-char pointers identical across host architectures. Actual
hosted x64 and ARM64 generation matched tracked bindings; the native glibc
ceiling and macOS drift check remain unchanged.

Agent guidance: keep baseline native builds in the approved older-glibc
builder. Run the pinned generator in a separate compatible environment and
verify its output with the exported pinned resource headers. CI keeps raw
generation output as an artifact when the drift check fails.
Do not raise the native library's glibc ceiling, substitute
a tool version, or call a successful tool restore a generation pass.

## ROMD media runtime cannot use the AlmaLinux 8 native builder

Status: MITIGATED

Command (ROMD, AlmaLinux 8.10 arm64, SDK 10.0.302): `dotnet test tests/Romd.Infrastructure.Tests/Romd.Infrastructure.Tests.csproj --no-build --no-restore`

Root cause: that builder's glibc 2.28 does not export `fstat` or
`posix_spawn_file_actions_addclosefrom_np`, which the existing ROMD media
runtime imports directly. `readelf --dyn-syms --wide` and `ctypes` symbol
lookup confirmed both missing exports. Tests fail with
`EntryPointNotFoundException`; downstream worker waits also fail. The observed
run aborted after 126 failures and 1,552 passes, so it is not a completed
Infrastructure result. These imports predate the backend CHD prerequisites.

Change: none to product code. A cached Ubuntu 24.04 arm64 image has the two
exports, versioned GLIBC_2.33 and GLIBC_2.34 respectively. Backend validation
there uses the same pinned SDK from a separately verified local archive.

Agent guidance: use an environment compatible with ROMD's process supervisor
for backend checks. Keep the older-glibc builder for native library baseline
qualification. A native libchdr pass in that builder does not qualify the ROMD
runtime, and changing its native glibc ceiling does not repair these imports.
Record platform and suite results separately; do not skip media tests or add
libc import fallbacks merely to reuse the native builder.

## Root bypasses the unreadable-identity test fixture

Status: MITIGATED

Command (Ubuntu 24.04 arm64 container): `dotnet test tests/Romd.Infrastructure.Tests --no-build --no-restore --filter FullyQualifiedName~StartAsync_UnreadableExistingIdentity`

Root cause: the test removes all Unix permission bits and expects an
`UnauthorizedAccessException`. Container root can still read the file, so the
assertion fails. The unchanged test passed under UID 501 in the same image and
SDK. Other Linux recovery and probe failures remained under that user; root
does not explain those failures.

Change: validation runs as an ordinary user, with access to its scratch outputs
and the Docker socket group for PostgreSQL fixtures.

Agent guidance: run permission-sensitive backend tests without root privileges.
Do not skip the assertion or change product file handling to accommodate root.

## Whole Linux validation exhausted the shared Rancher VM

Status: OPEN

Command: container builds and `dotnet test tests/Romd.Infrastructure.Tests --no-build --no-restore`

Confirmed resource failure: four overlapping builds initially exceeded the
shared 5.786 GiB Rancher VM alongside existing development services. Three
exact validation containers had Docker OOM events. Later, full-suite runs in
an exclusive Linux window also aborted: 2,600 passes with testhost exit137,
container OOMKilled and cgroup oom_kill1, then 2,050 passes with the same
container/cgroup OOM evidence despite xUnit.MaxParallelThreads=1. Concurrency
one alone did not resolve the whole-suite resource failure. A final unfiltered
retry with concurrency one and a 2 GiB parent GC heap cap also aborted after
2,690 passes: container OOMKilled and cgroup oom_kill1. The actual testhost
environment contained DOTNET_GCHeapHardLimit=80000000; that cap did not resolve
the observed OOM. Those three exclusive-window runs had no assertion failures.

A separate first final run aborted after 2,773 passes without retained cgroup
facts; its cause remains unestablished. Printed cleanup/writer IOExceptions
also occur in deliberate fault-injection tests that passed. Neither those
messages nor an active blame test establishes crash cause. A partial or
aborted TRX is not a suite pass; OOM does not explain every failed assertion.

Update 2026-10-14: with the suite split (Storage versus other), nine Storage
runs (1,564–1,630 tests, depending on the head) ran in a 4 GiB validation
container. Two were OOM-killed (container OOMKilled, cgroup oom_kill 1) after
1,418 and 1,595 passes. Two others reached the 4 GiB ceiling and still passed,
and five peaked at 4.10–4.22 GB. The second OOM-killed head then passed
1,630/1,630 with `xUnit.MaxParallelThreads=1` (peak 4.22 GB). Concurrency one
does not guarantee headroom: one of the ceiling runs, and the earlier
OOM-killed head's passing retry, also ran at concurrency one. The other
partition (3,520 tests) peaked at about 2.4 GB.

Change: heavy Linux qualification is serialized, including PostgreSQL-backed
Mac tests that consume the same VM. Whole-suite Linux qualification remains
open on this VM; no further cleanup or resource mitigation was attempted.
Completed focused checks and Mac gates are reported separately. Shared services,
VM settings, product limits and assertions remain unchanged.

Agent guidance: retain exact container identity, events/cgroup counters, raw
output and incomplete TRXs before retrying. Report completed checks separately
from aborted attempts and record any runner resource/concurrency conditions.
Do not prune, restart services, skip tests or raise product limits to obtain a
passing result. The worker launcher clears inherited environment variables
and explicitly sets its own memory controls; preserve that isolation if a
parent harness condition is used.

## Linux backup drill needs a Docker CLI as well as the socket

Status: MITIGATED

Command: `dotnet test tests/Romd.Hosting.IntegrationTests --no-build --no-restore --filter FullyQualifiedName~BackupSetRestoreDrillTests`

Root cause: Testcontainers could use the mounted Docker socket, but the backup
drill invokes the Docker CLI directly. The cached Ubuntu validation image did
not contain that executable; socket access alone did not satisfy the drill.

Change: qualification reused the existing Rancher VM Docker 29.1.3 executable
and its musl loader through read-only mounts. The ordinary validation user had
the socket's supplementary group. The actual CLI version and unchanged backup
drill passed before the integrated Linux run.

Agent guidance: check both CLI execution and daemon access under the validation
user before running backup qualification. Record the executable and loader
identities. Reuse approved tools; do not install a client or weaken the drill
merely because Testcontainers connectivity passes.

## `git worktree remove` refuses worktrees containing submodules

Status: MITIGATED

Command: `git worktree remove .worktrees/<name>`

Root cause: Git refuses to move or remove a linked worktree that contains
initialized submodules, such as `native/libchdr/upstream`, and fails with
`fatal: working trees containing submodules cannot be moved or removed`. Running
`git submodule deinit` in the worktree first does not change the result. On git
2.55.0, `git worktree remove --force` removes such a worktree.

Change: none. Workaround:

1. Confirm that the worktree is clean, including its submodules
   (`git -C <wt> status --porcelain --ignore-submodules=none` prints nothing),
   and that its branch is merged.
2. Run `git worktree remove --force <wt>`.

If `--force` still fails, `rm -rf <wt>` followed by `git worktree prune` is the
fallback, after the same checks.

Agent guidance: `--force` also skips Git's own clean check, so step 1 is
mandatory. Never force-remove a worktree with uncommitted or unmerged work, and
remove only the exact worktree path you checked.

## Deleted Rancher Desktop bind-mount files stay allocated on macOS

Status: MITIGATED

Commands:
- `mise run e2e:chd` (a second disc's space preflight)
- `lsof +L1`
- `rdctl shell sudo sh -c 'sync && echo 2 > /proc/sys/vm/drop_caches'`

Root cause: Rancher Desktop's virtiofs server (`com.apple.Virtualization`) keeps
host descriptors for files whose inodes the Linux guest still caches. Deleting a
bind-mounted run folder on macOS therefore frees nothing until the guest evicts
those inodes. In October 2026, after the FFX end-to-end run, GT4's space
preflight failed at 37.5 GiB free although FFX's run folder had been deleted.
After GT4 also ran, the server held 51.7 GB (48.1 GiB) of deleted CAS objects
and staging slots open (`lsof +L1`, link count 0) and macOS reported 19 GiB
free. `fstrim` does not help, because the blocks belong to macOS files rather
than the VM disk. Dropping the guest's dentry and inode caches released them:
open deleted bytes fell to 50 MB and free space rose to 52 GiB, without
restarting the runtime or touching volumes.

The drop is VM-wide: other projects' containers lose cached dentries and inodes
and briefly read them back, and `sync` first waits for pending writes from every
container.

Change: on macOS the CHD end-to-end harness drops the guest's dentry and inode
caches after deleting each run folder, and warns with the manual command if
that fails. Its space preflight names the same command when it fails on macOS.

Agent guidance: when macOS free space stays low after deleting large
bind-mounted files, check `lsof +L1` for the virtualization process before
deleting anything else. Drop only the dentry and inode caches (`echo 2`). Do not
restart Rancher Desktop or prune images or volumes to recover this space.

## RetroArch startup and signature probes differ inside the workspace sandbox

Status: MITIGATED

Commands:
- `codesign --verify --deep --strict /path/to/RetroArch.app`
- `/path/to/RetroArch.app/Contents/MacOS/RetroArch --version`
- `/path/to/RetroArch.app/Contents/MacOS/RetroArch --help`

Root cause: during macOS ARM64 original-disc qualification, the workspace sandbox
changed the result for the same byte-identical pinned RetroArch 1.22.2 executable
(Git 69a4f0ea). Inside the sandbox, signature verification reported modified code
and startup aborted with SIGABRT and no output. The exact checks passed outside
that sandbox. The OS-level mechanism was not established; these observations do
not establish binary corruption. A newly extracted official archive matched the
runtime catalog's SHA-256 and reproduced the same inside/outside difference.

Change: qualification uses scoped approved execution outside the workspace
sandbox for these native-runtime probes. No app was re-signed, replaced in its
installed location, or granted a security exception.

Agent guidance: first verify the exact artifact against its registry pin, then
compare the same narrowly scoped command outside the sandbox through normal
approval. Do not re-sign a runtime, disable security, remove quarantine, or
download another version merely because the sandbox probe fails. A successful
version/help check does not qualify playlist boot, controllers, disc switching
or save continuity; record those separately.

## .NET 10.0.302 watch rejects the source-generator project graph

Status: MITIGATED

Command: `dotnet watch --project src/Romd.Worker.Host`

Root cause: SDK 10.0.302's watch graph evaluation throws a duplicate
`ProjectInstanceId` exception while evaluating the netstandard2.0
`Romd.ReferenceData.Generators` project, after an ordinary host build succeeds.
The failure occurs before the worker starts; see
[the upstream SDK issue](https://github.com/dotnet/sdk/issues/54552).

Change: the managed `mise run dev:source` command watches build-input hashes,
rebuilds affected hosts, and restarts the worker before APIs. It deliberately
describes this behavior as rebuild/restart, with separate process diagnostics.

Agent guidance: do not advertise SDK in-process hot reload from a successful
build. Use the managed source mode or manual split-host startup. If revisiting
SDK watch, reproduce the graph failure and qualify an actual edit/reload before
changing the canonical command. Preserve the checkout's database/data pairing.

## Gitleaks archive decoder failures leave incomplete scanner coverage

Status: MITIGATED

Command: `python3 scripts/publication/audit.py --source SOURCE --evidence NEW_PRIVATE_DIRECTORY --gitleaks PINNED_BINARY`

Root cause: Gitleaks 8.30.0's archive decoder panics on some deliberately malformed
7z test fixtures and reports an error on an encrypted fixture. A zero finding
count from an interrupted decoder is incomplete coverage.

Change: publication auditing scans historical archive objects individually,
retains per-object scanner exit status in private evidence, and fails the
overall scan when any decoder fails. The 2026-10-15 audit separately verified
the failed fixture objects against their deterministic generation provenance
and, for the encrypted fixture, a pinned official decoder.

Agent guidance: retain the nonzero scanner result. Review every coverage gap
privately using immutable object hashes and a bounded decoder/provenance check;
do not exclude an archive directory, suppress errors, or call the scan clean.
Repeat this review when failed object hashes or their provenance change.

## Rootless Docker published ports can collide with host TCP connections

Status: MITIGATED

Command: `mise run test:integration` with a temporary rootless Docker daemon

Root cause: Docker allocated port 32778 inside its network namespace, while
the host namespace already retained a TCP `TIME_WAIT` connection from
`127.0.0.1:32778`. RootlessKit's all-interface publication failed with
`listen tcp4 0.0.0.0:32778: bind: address already in use` before application
startup. A controlled host listener reproduced the same collision.

Change: the owned temporary daemon's default-bridge binding uses
`--ip=127.0.0.2`, paired with `TESTCONTAINERS_HOST_OVERRIDE=127.0.0.2` in its
evaluation environment. A same-port host listener on `127.0.0.1` coexists with
the isolated publication, and a real forwarded PostgreSQL query passes. The
existing database's explicit `127.0.0.1` binding and volume were preserved.

Agent guidance: diagnose both network namespaces before classifying this as
an application failure. For an owned temporary daemon, validate a dedicated
loopback binding and matching Testcontainers host override with an actual
connection. This mitigation covers default-bridge publications; explicit host
bindings retain their requested address. Do not alter system daemons, change
host ephemeral-port policy, disable Ryuk, or waive the failed suite.

## Hosted CI rejected before job startup

Status: DEFERRED

Command: `gh run view RUN_ID --json conclusion,jobs`

Root cause: GitHub Actions can reject all hosted jobs before checkout when
account payments or spending limits block runner allocation. Confirmed during
the development-container PR validation: failed jobs had zero steps, and the
check-run annotation explicitly reported the billing/spending-limit block.

Change: None. Account configuration requires the repository owner's action;
local validation does not clear the hosted release gate.

Agent guidance: inspect check-run annotations when a failed job has no steps
or logs. Report these checks as not run, retain local evidence separately, and
resume CI after the owner resolves the account block. Do not change workflow
checks or retry unchanged jobs to work around an account restriction. A failure
after steps begin requires its own diagnosis.


## Synthetic worker compilation retains native assembly images until collection

Status: MITIGATED

Command: `mise run test` on a native Linux desktop

Root cause: each synthetic worker/tool compilation used
`MetadataReference.CreateFromFile` for the runtime and test assemblies. Roslyn
eagerly copies those files into native memory and releases the copies only when
the references are collected. Repeating this across concurrently running
fixtures can accumulate substantial native memory without a matching managed
heap size. A native Linux OOM snapshot recorded the infrastructure testhost at
8.6 GiB resident when the kernel killed ChatGPT. This identifies a contributor;
it does not attribute every full-suite failure or the earlier Rancher VM OOMs
to the same cause.

Change: the four synthetic compiler fixtures now own `AssemblyMetadata` and
dispose its native images immediately after emission, including failed
emissions. A controlled twenty-compilation comparison reduced the testhost's
final resident memory from about 1.1 GiB to 350 MiB. Each emitted worker remains
fresh and uses the current startup source; no compiled output is cached.

Agent guidance: run heavy desktop validation in an independent resource group
with a hard memory bound and retain its peak/OOM counters. On systemd Linux,
`systemd-run --user --wait --pipe -p MemoryMax=6G -p MemoryHigh=5G -p MemorySwapMax=0`
can wrap the normal validation command; choose limits that preserve desktop
headroom. This host protection does not change worker limits or establish a
suite pass. A hard bound alone can still throttle an oversubscribed suite.

The qualified desktop profile also uses an absolute `RunSettingsFilePath`
pointing to this [xUnit runsettings](https://xunit.net/docs/config-runsettings)
configuration:

```xml
<RunSettings>
  <xUnit>
    <MaxParallelThreads>2</MaxParallelThreads>
    <DiagnosticMessages>true</DiagnosticMessages>
  </xUnit>
</RunSettings>
```

Set `UseSharedCompilation=false`, `MSBUILDDISABLENODEREUSE=1` and
`DOTNET_CLI_USE_MSBUILD_SERVER=0` on the validation command so idle build
processes do not consume the test job's memory allowance. These are parent
harness conditions: retain them with the evidence, and distinguish this
profile from default-concurrency qualification. The tests' explicit concurrent
work, assertions, deadlines and production child limits remain unchanged.
Do not retry an unrestricted run after a desktop OOM, force garbage collection
as the fix, skip assertions, or raise production limits.

## Source advisory database extraction exceeds tmpfs checkout quota

Status: MITIGATED

Command: `mise run dependencies:check` from a worktree under a quota-limited `/tmp`

Root cause: Trivy's vulnerability database extraction failed with
`write .../cache/db/trivy.db: disk quota exceeded` when the source-security cache
followed a tmpfs-backed checkout. Free space on the host's persistent filesystem
did not make that checkout's storage available. A failed database extraction is
incomplete advisory coverage, not an advisory-free result.

Change: the same pinned Pub scanner passed with complete hosted-package coverage
after its ignored cache and scratch directory were moved onto a uniquely owned,
disk-backed artifact directory. Scanner options and severity policy were retained.

Agent guidance: allow several GiB for the current database and download/extraction
scratch space. Prefer a disk-backed checkout; the source scanner stores its
cache at `.artifacts/source-security/cache` within that checkout. Set `TMPDIR`
to an existing private, disk-backed directory for scratch files. If a worktree
must remain on tmpfs, its ignored cache can point to a uniquely owned directory
on persistent storage; confirm ownership before moving any existing cache.
`TMPDIR` alone does not relocate the scanner cache. The source scanner has no
`--evidence-dir` cache override. Preserve the failure and rerun only after fixing
the storage condition; do not skip database updates, accept empty coverage,
or clear another task's artifacts.


## Temporary rootless Docker pasta ports are unreachable

Status: MITIGATED

Command: `mise run test:integration` against a temporary rootless Docker daemon
using the `pasta` network helper and its implicit port driver

Root cause: on the observed native Fedora setup, Docker 29.9.1 reported a
running Ryuk container with a published port, but connections from the host
were refused. The selected pasta/implicit networking path did not expose that
port to the host. Testcontainers could not initialize its database fixture,
and the unchanged 60-second inactivity watchdog aborted the run. This was a
qualification environment failure, not an application assertion failure.

Change: only the owned temporary daemon was restarted with the recommended
`slirp4netns` helper (verified version 1.3.6) and the default builtin port driver.
A real published nginx HTTP request then returned 200, and the unchanged full
host integration suite passed. The daemon retained its dedicated loopback
binding described in the preceding rootless Docker port-collision guidance.

Agent guidance: verify a real host-to-container published connection before
starting a full suite on a newly provisioned rootless engine. If the pasta
implicit path is unreachable, use the recommended helper within the owned
environment. Preserve the failed evidence; do not disable Ryuk, remove the hang
watchdog, alter host security policy or stop another engine to obtain a pass.
See [RootlessKit network drivers](https://github.com/rootless-containers/rootlesskit/blob/master/docs/network.md).

## Go archive qualification inherits enclosing Git metadata

Status: MITIGATED

Command: `go build -trimpath -o reader ./cmd/distribution` from an extracted
source archive beneath another Git checkout

Root cause: Go's automatic VCS discovery can find an enclosing repository even
when the archive itself contains no `.git`. A qualification build under ROMD's
ignored evidence directory recorded ROMD's revision in the companion reader.
An unrelated shared `/tmp/.git` marker also prevented automatic discovery in a
later scratch directory. Neither output established equivalence with the
archive-only Docker build.

Change: archive reproduction explicitly uses `-buildvcs=false` and checks the
actual binary's build information for absent `vcs.*` settings. The affected
receipt was superseded, then the reader was rebuilt and rescanned.

Agent guidance: inspect `go version -m reader` before comparing artifact hashes.
Use a scratch directory outside Git ancestors, or disable VCS stamping when
reproducing Docker's archive-only inputs. Keep source-commit provenance bound to
the verified archive and receipt, and compare against the actual Docker-built
binary. Preserve superseded evidence with its invalidation reason; a directory
name or an empty local `.git` search does not establish a clean VCS context.


## CHD qualifiers can race shared-daemon subnet selection

Status: MITIGATED

Command: simultaneous `scripts/e2e/chd_cd_upgrade.py` invocations against one daemon.

Root cause: each qualifier scans occupied networks before Compose creates its
selected subnets. Concurrent prepare/start phases can select the same free range;
one then fails with `Pool overlaps with other one on this address space`.

Change: qualification startup was serialized until each stack's networks existed;
all four real fixture upgrade/recovery runs passed with project-scoped cleanup.

Agent guidance: run fixtures sequentially or wait for one stack's network creation
before starting the next. Preserve failed receipts and clean only the failed
project; do not prune shared networks or volumes.
## Admin web `findBy` waits time out under parallel test load

Status: MITIGATED

Commands:

- `pnpm test` from `web/` on a loaded host
- `NODE_OPTIONS=--no-experimental-webstorage npx vitest run --maxWorkers=24`
  from `web/` after `pnpm test:prepare`

Root cause: under CPU contention, a test's default 1 s `findBy*` or `waitFor`
window fills with work. Some of that work is the polling itself. jsdom drops its
document-wide computed-style cache on every DOM mutation. A role query by name
then computes the accessible name and styles of every candidate element, and
`findBy*` repeats it on every mutation and every 50 ms, so slower runs poll
more. Real-time waits inside the window also count, for example a debounce or
a redundant request. Separately, render-bound tests of 0.6 to 1.4 s reach the
10 s test timeout once a run's median test takes about 8 times its serial
duration; part of their cost is floating-ui positioning the closed dropdowns
that Mantine Select keeps mounted. On 2026-10-20 this failed tests in
Libraries, AddArtwork, EnrichmentAgreement, Catalog, CatalogEnrichmentFilter,
CatalogTracking, SystemHub, Dashboard, TitleDetail and the consumer Connections
page.

Change: Games no longer evaluates twice on open. Its draft started from the
saved configuration, whose JSON key order differs from what the rules form
reports, so it evaluated again after the 600 ms debounce. Catalog search no
longer sends a relevance-sorted request without the query when its 300 ms
debounce fires. AddArtwork, Dashboard, CatalogTracking, SystemHub and
Connections wait on label or text queries. The render-bound timeouts and the
waits that span the Catalog search debounce remain.

Agent guidance: interleave original and changed stress runs and record `uptime`
beside each, because load from other sessions varies widely. Compare durations
with temporary copies of the original and changed test in the same file and
run, and judge starvation by each run's median slowdown against a
`--maxWorkers=1` baseline. Profile with `--execArgv=--cpu-prof`. First look for
timers or duplicate requests inside the waited window. Then wait on a cheap
text signal, or scope role queries to a labelled region, and keep role and name
assertions as one-shot checks. On the Catalog page one check after a mutation
cost 27 ms by role and name, 11 ms by label and 1.7 ms by text; on large pages
such as TitleDetail a label wait gained nothing. Keep sequential waits
separate, because merging them shortens the time budget. Do not raise timeouts
or skip tests. A test that times out keeps running and can fail the next test
in its file; count that as one failure.

## Inherited PATH selects the system SDK inside mise child processes

Status: MITIGATED

Commands:

- `mise run native:test-inputs`
- `mise exec -- python3 scripts/validation/run.py --group integration`
- `mise exec -- python3 -I -c 'import shutil; print(shutil.which("dotnet"))'`

Root cause: the inherited PATH placed Homebrew/system executables before
already-present mise install directories. In this environment, `mise current`,
`mise which dotnet`, and direct `mise exec -- dotnet --list-sdks` can identify
the pinned SDK while a Python child's `shutil.which("dotnet")` selects the
system executable. Observed children selected SDK 10.0.401 despite the checkout
requiring 10.0.402; native and integration commands exited 155. The observed
PATH order establishes the selection problem; mise's internal cause is unconfirmed.

Change: a session-local filter removes only inherited mise install/shim entries
before invoking mise, allowing the selected tool directories to lead the child
PATH. Unrelated entries, including the container-engine path, are preserved.
Read-only direct, task-shell and nested-child probes selected 10.0.402 with this
filter. No source, tool pins, profiles or caches changed.

Agent guidance: inspect the actual child executable and SDK before retrying.
Apply the filter only to the affected invocation, then run the original checks:

```sh
romd_gate_path=$(python3 -I -c 'import os; prefix=os.path.expanduser("~/.local/share/mise/"); print(":".join(p for p in os.environ["PATH"].split(":") if not p.startswith(prefix+"installs/") and p != prefix+"shims"))')
env PATH="$romd_gate_path" mise run test
```

Apply the same environment prefix to the integration/contracts/web gate or its
wrapper. Do not change SDK pins, remove caches, skip checks, or treat a probe
as a suite pass. Commit any intended documentation change before its final gate.

## TestServer normalizes typed request headers before dispatch

Status: MITIGATED

Command: `dotnet test tests/Romd.Hosting.IntegrationTests/Romd.Hosting.IntegrationTests.csproj --filter FullyQualifiedName~PreconditionEndpointsTests`

Root cause: TestHost copies `HttpRequestHeaders` by enumeration, which parses
known header values even after `TryAddWithoutValidation`. For example, its
entity-tag list parser normalizes `*,` or `, *` to `*` before the endpoint sees
`If-Match`. A client-side raw string therefore does not establish the exact
request header received by a TestServer endpoint. This was confirmed with the
pinned TestHost 10.0.13 and the malformed-precondition regressions.

Change: malformed-header endpoint tests use `TestServer.CreateHandler`'s
context callback to set the original value at the ASP.NET request boundary.
Direct `DefaultHttpContext` tests also pin syntax classification. Endpoint
checks retain the problem envelope, field attribution and persisted nonmutation
assertions.

Agent guidance: distinguish header normalization in a test transport from
server parsing. Use the context callback or a real wire transport when exact
malformed bytes matter; `TryAddWithoutValidation` alone is insufficient. Do not
weaken production validation or change expected errors to accommodate a
normalized test request.

## Development socket tests require local socket access

Status: MITIGATED

Command: `mise run agents:check`

Root cause: the restricted agent sandbox can deny Python socket creation in
the development contract tests. The occupied-port and TIME_WAIT restart tests
fail in `socket.socket()` with `PermissionError: [Errno 1] Operation not permitted`,
before exercising the runner. The unchanged check passes with scoped local
socket access outside the sandbox.

Change: None; this is an execution-environment restriction.

Agent guidance: for this exact socket-creation failure, rerun the unchanged
command once with scoped escalation and report both outcomes. Do not skip the
port tests or alter the development runner. A failure after socket creation or
outside the sandbox needs separate diagnosis.

## Pinned brand font tools need compatible Python wheels

Status: MITIGATED

Command: `pip install -r brand/tools/requirements.txt`

Root cause: `uharfbuzz==0.51.0` has no wheel for the local Python 3.14 setup.
Pip attempted a source build, which failed with `Python.h: No such file or directory`
because this host has no Python development headers. The pinned tools installed
successfully from wheels in an isolated Python 3.12 environment.

Change: the [brand export guide](../../brand/README.md#reproducible-exports)
recommends Python 3.12 or 3.13 for its isolated environment.

Agent guidance: use a compatible Python environment with the pinned requirements.
Do not change the font-tool pins or install system packages just to work around
missing development headers. Keep the temporary environment outside Git.

## Vite can retain inlined brand SVGs after an export

Status: MITIGATED

Command: `bash brand/tools/export_web_brand.sh`

Root cause: a running Vite 7.3.7 consumer server retained an old inlined
wordmark in its SVG `?import` module after the canonical and shared-foundation
files were regenerated. Browser reloads still showed the previous artwork;
the files on disk and production builds contained Squish. The exact watcher
or cache-invalidation cause was not established.

Change: the brand export guide now requires checking the live header and
describes a targeted Vite configuration reload. Touching the affected app's
`vite.config.ts` without changing its contents restarted that server; the
same SVG import and live header then contained the new artwork.

Agent guidance: compare the imported SVG module with the canonical source
before treating an old logo as an export failure. Request a configuration
reload only for the affected Vite app, then reload the browser. Do not clear
unrelated caches, kill global processes, or alter the artwork to work around
stale development output. Production rendering failures need separate diagnosis.

## Root `mise run test:integration` passed with connection strings CI never has

Status: OPEN

Command: `dotnet test tests/Romd.Hosting.IntegrationTests/Romd.Hosting.IntegrationTests.csproj`
run outside a mise-activated shell (as CI does), after the PostgreSQL
cutover.

Root cause: `.mise.toml` sources `scripts/db/env.sh`, which exports
`ConnectionStrings__Romd`, `ConnectionStrings__RomdProvisioning`, and the
Hangfire pair for the dev database. `mise run test`, `mise run test:integration`,
and any mise-activated terminal therefore hand every in-process host a valid
application connection string. Fixtures that never configured
`ConnectionStrings:Romd` (host smoke tests, tracked-collection endpoint tests)
passed locally by reading the developer's dev database and failed in CI with
`ConnectionStrings:Romd must be configured`. Two real-process tests also used a
2-second `HttpClient` timeout against a readiness endpoint whose own evaluation
budget is 5 seconds, which surfaced as `TaskCanceledException` on the slower
runner.

Change: every fixture now sets the connection string explicitly, using a
`PostgreSqlTestDatabase` when the test reads data and a placeholder when the
host is only inspected. `WaitForStatusAsync` and `WaitForReadinessCheckAsync`
treat a per-request timeout as "not ready yet" until their own deadline, and the
process tests use a 10-second client timeout.

Update 2026-10-28: recurred during the October 2026 Actions billing outage.
Fourteen `RomdHostTestFactory` integration tests added or changed while no
hosted job ran passed locally by reading the dev database and returned 500 in a
CI-like environment. The factory now sets an unreachable `ConnectionStrings:Romd`
default, which a test's own `settings` override. It does not pin the Hangfire
pair: `AddRomdHangfireClient` skips Hangfire only in the `Testing` environment,
so a factory host booted as `Development` can still read the mise-exported
`ConnectionStrings__Hangfire` locally while CI has none.

Agent guidance: before pushing a change that touches host fixtures, run the
suite once without the mise exports, for example
`env -u ConnectionStrings__Romd -u ConnectionStrings__RomdProvisioning -u ConnectionStrings__Hangfire -u ConnectionStrings__HangfireProvisioning dotnet test ...`,
or from a shell that never activated mise. A fixture that needs data must own a
`PostgreSqlTestDatabase`; a green run under `mise run` alone does not prove that.

Under mise the unset must happen inside `mise exec`, because mise re-injects
the exports for every `mise exec` or `mise run` child: use
`mise exec -- env -u ConnectionStrings__Romd -u ConnectionStrings__RomdProvisioning -u ConnectionStrings__Hangfire -u ConnectionStrings__HangfireProvisioning dotnet test ...`.
Unsetting the variables before `mise exec` or `mise run` has no effect.

## Failing web-host startup can hide its original exception

Status: MITIGATED

Command: `mise run test:integration`

Root cause: ASP.NET Core MVC.Testing 10.0.13 reads the host's
`IHostApplicationLifetime` before awaiting entry-point failure. An entry point
that immediately disposes its failed host can therefore produce
`ObjectDisposedException: IServiceProvider` in `DeferredHost.StartAsync` instead
of the original startup error. This stack differs from the Hangfire activator
leak above. See the [pinned framework implementation](https://raw.githubusercontent.com/dotnet/aspnetcore/v10.0.13/src/Mvc/Mvc.Testing/src/DeferredHostBuilder.cs).

Change: `ChdHostStartupTests.NativeStartupFactory` holds actual native-runtime
construction until the test wrapper installs its startup waiter. The test-only
rendezvous has a ten-second bound and preserves native exception assertions.

Agent guidance: synchronize negative startup tests at the failing service
boundary. Do not accept a disposed provider as the expected native failure or
weaken exception assertions to accommodate this race.


## Immediate accounting lease assertions can race parallel fork windows

Status: MITIGATED

Command: `mise run test` (infrastructure suite, `MediaProcessingRuntimeTests.EnsureJobStoppedAsync_AccountingContention_RechecksCustodyAfterLockRelease`)

Root cause: an immediate test-side accounting lease acquisition can return busy
after the runtime closes its descriptor. POSIX fork temporarily duplicates open
file descriptions; close-on-exec closes the child's copy at exec, not at fork.
A bounded kernel probe confirmed that the flock remains held during that window.
This is consistent with the observed parallel-suite setup failure; no production
lease leak was established. The suite already isolates other immediate OFD-release
assertions for this reason.

Change: the three accounting contention/timeout/cancellation scenarios now use
the existing `NativeLeaseProcessFixture` on POSIX. Every custody, journal,
timeout and cancellation assertion runs unchanged in the isolated process.

Agent guidance: isolate immediate-release preconditions from unrelated process
launches using the existing fixture. Preserve the failing run and validate the
complete suite after the scoped fix. Do not force-unlock inherited custody,
add polling to hide it, relax assertions or disable suite parallelism globally.

## Dotnet format cannot fix naming rules across a solution

Status: MITIGATED

Command: `dotnet format style Romd.sln --no-restore --diagnostics IDE1006 --severity info --include PATHS`

Root cause: the repository-pinned SDK 10.0.402 reports `Unable to fix IDE1006.
Code fix NamingStyleCodeFixProvider doesn't support Fix All in Solution.` The
command can exit zero while leaving private fields without the configured
underscore prefix. Whitespace formatting does not enforce field naming either.

Change: the media formatting cutover used Roslyn semantic field renames,
including references in related partial files, followed by compilation and a
token audit that checked literals and `nameof` results remained unchanged.

Agent guidance: do not infer naming compliance from this command's exit code.
Use a symbol-aware rename for a bounded naming change, inspect references across
partial files, and run the affected validation. Do not replace identifiers by
text substitution or weaken `.editorconfig`. A whitespace verification gate
checks spacing and layout; it does not replace naming validation.

## Storage cancellation test assumes a wall-clock ordering

Status: MITIGATED

Commands:

- `mise run test`
- `mise exec -- python3 scripts/validation/run.py --id backend.storage`

Root cause: `FileSystemContentStoreTests.StoreAsync_Cancellation_ThrowsAndCleansUp`
used a 100,000-byte stream with 10 ms delays, waited 50 ms, then canceled. It did
not synchronize cancellation with an unfinished read. The store could finish
before the cancellation continuation ran. The assertion first failed in a full
backend run; an unchanged 203-test pass afterward did not fix its timing race.
PR #462 CI run `41511670353` reproduced it (202 passed, one failed).

Change: the fixture returns a first chunk, signals entry into the next read and
blocks that read until its supplied cancellation token fires. The test waits on
that event before canceling, so a successful copy cannot win a timer race. Both
seekable and non-seekable paths retain the cancellation exception and temporary
file cleanup assertions. The non-seekable case also proves a live staging file
exists before cancellation, making its cleanup check non-vacuous. Finally cleanup
cancels, independently releases the read gate and joins the owned store task even
if an assertion fails. Storage implementation, payload size, production timeouts
and scheduler parallelism are unchanged. Read-entry and cancellation-result waits
have bounded test failure guards.

Agent guidance: synchronize on operation events when testing cancellation. A
bounded wait on the read event is a failure guard, not the cancellation trigger.
Do not increase timing delays, disable the test, or treat a later unchanged pass
as a timing fix.

## SDL publisher URL times out during native image builds

Status: MITIGATED

Commands:

- `mise run native:test-inputs`
- Hosted Production Compose and recovery drill image build

Root cause: the pinned SDL2 archive at `www.libsdl.org/release/SDL2-2.32.10.tar.gz`
repeatedly timed out from both local Podman builds and the hosted Docker build.
This prevents the image build before qualification tests run; it is not a passing
native or deployment check.

Change: the Dockerfile downloads the publisher's identical archive from
`github.com/libsdl-org/SDL/releases/download/release-2.32.10/SDL2-2.32.10.tar.gz`.
An unauthenticated download recomputed the existing pinned SHA-256
`5f5993c530f084535c65a6879e9b26ad441169b3e25d789d83287040a9ca5165`.
The deployment contract and source notice follow that publisher URL. The version,
archive bytes, checksum, native source pins, and build recipe versions are unchanged.

Agent guidance: retain the source checksum when changing transport locations and
verify the exact publisher asset before using a mirror. A matching cached build
may unblock isolated local checks after its recorded inputs and binary hash are
verified; it does not qualify a fresh production image build. Do not remove the
checksum or rerun unchanged hosted jobs to conceal an unavailable build input.

## Hosted production build cache leaves too little space for synthetic CHD inputs

Status: MITIGATED

Command: the Production Compose and recovery drill in `.github/workflows/ci.yml`.

Root cause: PR #462 CI run `41464804164` built the runtime images and passed
production startup, login, restart, and backup/restore, then stopped at the
synthetic CHD preflight: 4,295,512,064 bytes free versus 7,516,192,768 required.
Unused image-build cache remained on the disposable GitHub-hosted runner.

Change: after the production drill, a hosted-Actions-only step removes unused
build cache and verifies all four runtime image IDs remain identical before
starting the synthetic harness. It preserves images and volumes, and leaves
the harness free-space requirement intact. Hosted CI confirmed capacity rose
from about 4 GiB to 12 GiB with the runtime images unchanged, and the synthetic
capacity preflight passed. Local tests exercise the cleanup script with a fake
Docker command only.

Agent guidance: do not run this cleanup on local or self-hosted engines, lower
the harness capacity guard, or substitute cache-reclamation counts for an
actual free-space measurement and a passing synthetic run.

## Composite scale measurements can exceed CI's inactivity guard

Status: MITIGATED

Command: the backend infrastructure CI partition (`--blame-hang-timeout 120s`).

Root cause: the representative workbench case combined independent catalog-filter
measurements with workbench/history reads and concurrent mutations. A focused
local run completed in 138 seconds; measured operations completed in at most
about 7.3 seconds, but CI sees no completed test during the combined case and
aborts after 120 seconds. The local backend route has no Blame inactivity guard,
so an ordinary local pass did not cover this CI condition.

Change: the six catalog-filter workloads are separate theory cases, retaining
both measurements, the full 20,000-title artwork/history fixture, query inputs,
and assessment assertions. The first split passed locally but the remaining
composite case still exceeded CI's guard. Workbench, history, and preview reads
are now separate full-fixture cases too. All their assertions remain, and the
concurrent readers/writer, acceptance, and retry scenario stays together intact. A large export fixture also timed
out during publication setup while other database fixtures ran; it now joins
`PostgreSqlScaleWorkloads`. No query timeout, inactivity guard, data population,
repetition, or assertion changed. The focused fixtures passed with CI's guard.

Agent guidance: measure per-phase progress before treating an inactivity abort
as a deadlock. Keep independent workloads identifiable without dropping their
coverage or raising guards. Continue to fix slow query shapes when an individual
query, rather than a composite measurement, exceeds its budget.

## Request-log fixtures can lose their host's completion logger

Status: MITIGATED

Command: `mise run test:integration` (`CapabilityTokenLoggingTests`).

Root cause: Serilog's default request-completion logger resolves process-global
`Log.Logger`. Repeated in-process host entry points replace that state; a
controlled replacement reproduced a missing request event even though the
request completed. Waiting longer does not restore the missing event. Separately,
the DB-free hub-token test sent an opaque reference token through real OpenIddict,
which attempted a lookup against the fixture's deliberately unreachable database.

Change: `CapturingLogSink.Register` binds completion logging to the real configured
host logger, retaining its sink and redaction enrichers. A deterministic test
replaces the global logger while the host is running. The hub fixture substitutes only
its reference-token lookup with "not found", verifies the exact lookup and 401,
and keeps real token promotion/authentication and all secrecy assertions.

Agent guidance: keep the single-event assertion and existing wait bound. Use the
host-owned logger in capture fixtures; do not replace it with a mock or duplicate
the production redaction configuration. DB-free auth fixtures should substitute
the persistence boundary and assert that the real rejection path ran.


## CPU-budget tests cannot infer joined usage from the first kernel signal

Status: MITIGATED

Command: `mise exec -- python3 scripts/validation/run.py --id backend.media`.

Root cause: the first-limit tests assumed every one-second kernel CPU-limit kill
would report at least 980,000 microseconds through `wait4`. Hosted runs returned
the expected signals but failed that attribution cutoff. Linux enforces CPU
limits using [profiling-clock samples](https://github.com/torvalds/linux/blob/v7.0/kernel/time/posix-cpu-timers.c),
while [reported accounting can be adjusted against scheduler runtime](https://github.com/torvalds/linux/blob/v7.0/kernel/sched/cputime.c).
The kernel does not establish a universal 20 ms agreement bound. Exact usage
was absent from the original failure messages; diagnostics now include it.

Change: the attribution predicate and conservative 20 ms allowance stay
unchanged. The sole Linux wait owner now samples the final CPUCLOCK_PROF while
the exact child remains an owned zombie (`waitid(WEXITED|WNOWAIT)`), then joins
with `wait4` and retires PID ownership. Adjusted usage remains the conservative
fallback if that clock cannot be read. First-limit cases require the positive
final enforcement-clock evidence, exact signals, installed limits and joins.
Boundary tests and additional native cases remain: both adjusted usage and the
profiling clock must establish 1.25 seconds before a later kernel exit. Both low-CPU external
SIGKILL and SIGXCPU must remain `Malformed` without budget evidence.

Agent guidance: compare the clock that enforces a limit, retain adjusted
accounting as a fallback, and preserve the failed runs. Do not widen the cutoff
or infer exhaustion from a signal alone. The registered media check runs at
nice 10, like its worker; normal-priority setup failures supply no CPU evidence.
The retained native controls have their owned 15-second cancellation deadlines.

## Covered test runs sharing one output tree can corrupt assemblies

Status: MITIGATED

Command: concurrent `dotnet test tests/Romd.Infrastructure.Tests -c Release
--no-build --collect:"XPlat Code Coverage"` invocations with different filters.

Root cause: coverage instrumentation/restoration overlapped in the same Release
output directory. Both hosts aborted, with `Invalid assembly public key` and
`BadImageFormatException: Bad IL range`; these runs establish no test result.

Change: rebuild the affected worktree outputs and run coverage checks serially.

Agent guidance: different filters/results directories do not isolate instrumented
assemblies. Use separate worktrees/build output trees for concurrent coverage, or
serialize it. Preserve the aborted evidence and rebuild before interpreting a
subsequent result; do not change application behavior for these assembly errors.

## Hosted integration job cancelled at its 20-minute budget

Status: MITIGATED

Command: `Backend integration tests`, `python3 scripts/validation/run.py --ci
--job integration`, main CI run `41524685631` and PR #465 run `41565885937`.

Observed: both runs were cancelled at the job's `timeout-minutes: 20` while
tests were still passing; neither recorded a hang. Across 16 consecutive hosted
runs the same serial suite took 13.4–20.5 minutes end to end, including about
2.3 minutes of restore and build, with no change in test count (2,133). In run
`41563687916` the reported test time was 13.6 minutes. Per-test durations summed
to 6.1 minutes for `Endpoints` and 5.3 minutes for every other namespace; the
rest was fixture and host startup. The variance is runner throughput, not a
regression in one test.

Change: the integration job is a two-part matrix, like the infrastructure job.
`endpoints` selects `FullyQualifiedName~Romd.Hosting.IntegrationTests.Endpoints.`
and `hosting` selects its exact complement. Each part keeps the unchanged
20-minute budget, the 60-second hang guard, coverage collection and the CI gate.
The registered check takes the partition through `--filter {filter}`; route
validation now requires `--filter` for any check whose CI command uses it.
Local `mise run test:integration` still runs the whole suite unfiltered.

Agent guidance: a cancelled integration part is a budget failure to diagnose
from its recorded durations. Do not raise the budget, drop coverage or the hang
guard, or move tests between parts without measuring both halves again.


## Shared temporary-filesystem user quotas can interrupt validation

Status: MITIGATED

Command: local registered host integration checks using the default temporary
folder, and execution-sandbox startup beneath `/tmp`.

Root cause: Linux's shared `/tmp` tmpfs can enforce a per-user quota independently
of its total free space. An exhausted user quota denied small fixture writes with
`Disk quota exceeded` and sandbox mount-target creation with `Quota exceeded`,
even while `df` reported available bytes and inodes. A truncated log or a
validator record still marked running does not establish a completed test run.

Change: the host group can use an absolute, ignored, task-owned workspace
folder for `TMPDIR`, with stdout logs and evidence on that volume. The complete
unchanged host group passed there with its existing hang guard. Publication
audit checks require their scratch evidence outside every Git checkout; use a
private task-owned cache on a volume with capacity for those checks. Application
code and validation assertions were not relaxed.

Agent guidance: inspect `quota -s` alongside `df` after an `EDQUOT` failure.
Set the owned `TMPDIR` after mise initializes the command, and retain the CI-like
dev-connection-string unsets described above. Preserve failed or incomplete
receipts and rerun the affected registered group. Do not purge shared temporary
folders, another task's data, or unrelated `.git` markers to recover capacity.

## Documentation bundle tests require canonical temporary paths on macOS

Status: MITIGATED

Command: `mise run agents:check`

Root cause: the bundle relocation fixture retains the temporary source path as
created, while link destinations are resolved. When macOS supplies a temporary
path through `/var/folders/`, its resolved `/private/var/folders/` spelling fails
the containment comparison. The fixture reports `invalid source link
configuration.md#configuration` even though its generated document exists.
The default temporary path was confirmed noncanonical; the unchanged checks
passed with a canonical temporary parent.

Change: no checker or assertion changes. A session-local canonical temporary
parent avoids the alias mismatch.

Agent guidance: after confirming this exact fixture failure and path mismatch,
run `TMPDIR=/private/tmp mise run agents:check`, or use another writable canonical
temporary parent. Report the original failure and the corrected-environment
result. Do not bypass link/anchor validation or delete shared temporary data.

The same macOS alias also affects the fresh-output cases in
`Romd.Infrastructure.Tests.Storage.ChdCdQualificationProbeTests`: VFS probe
path validation rejects the `/var` symlink before reaching the unsupported-OS
guard. The seven protected-root cases and overlapping-report case passed
unchanged with `TMPDIR=/private/tmp`; the default alias produced
`InvalidOperationException` instead of the expected
`PlatformNotSupportedException`. Use a canonical temporary parent for local
`mise run test` as well. Preserve the real-path safety checks and both outcomes.

## Coverlet hit writers can collide across process sessions

Status: MITIGATED

Command: `python3 scripts/validation/run.py --ci --job infrastructure` (storage
partition), public CI run `42352364088`.

Observed: the isolated owner for
`EnsureJobStoppedAsync_IndependentlyAcquiredLease_ReapsOnlyMatchedAdoptedZombie`
exited 134 from Coverlet's `UnloadModule`: a `Romd.Media_*` hit file was already
open. This is distinct from concurrent instrumentation of one output tree.
Coverlet 6.0.4 uses a session-scoped named mutex around a shared hit file;
instrumented tools in separate POSIX sessions can therefore write concurrently.
A private reproduction using the official tracker sources and four concurrent
separate-session writers failed all three trials on 6.0.4. Version 10.1.0 retained
every writer's hits and exited cleanly in all three trials.

Change: pin the test-only collector to 10.1.0 and regenerate the complete lock
inventory. Its [upstream fix](https://github.com/coverlet-coverage/coverlet/releases/tag/v10.1.0)
adds an exclusive lock file with bounded acquisition and atomic hit-file writes.

The covered POSIX fixture also needs an explicit owned `TMPDIR` on macOS:
without it, both old and new trackers fail creating their named mutex with
`lstat("", ...)` and exit 134. A cleared-environment control reproduced this
for both versions; a minimal `TMPDIR` control passed, while `HOME` or
`XDG_RUNTIME_DIR` alone did not. The fixture supplies its own temporary directory
on macOS and asserts that exact environment; no user settings are inherited.
Child-exit assertions now retain bounded stderr for future diagnosis.

Agent guidance: retain the original child diagnostics and distinguish tracker
exit failures from custody assertions. Keep coverage and process-session tests
enabled; do not suppress child exits, discard hits, change production process
handling or disable suite parallelism to hide a collector failure.

## Docker lifecycle startup can miss its first probe event

Status: MITIGATED

Command: `python3 scripts/deployment/smoke.py --build --vfs`, hosted production
drill run `42387121031`.

Observed: images built, but the initial lifecycle barrier timed out with
`collector-probe-timeout`. Its sanitized receipt was not retained because the
failure artifact covered only the later CHD workspace. The missing receipt
limits retrospective attribution of that particular run.

Confirmed defect: launching `docker events` does not prove its subscription is
active before the first `docker create`. A delayed-subscription regression
reproduced the lost acknowledgment. A synchronized real-Docker control admitted
subscription only after create completed: live-only collection timed out;
bounded-history replay acknowledged both projects, with no owned resources left.

Change: request Docker's retained event history for each fresh project before
following its live stream. Docker [retains at most 256 events](https://docs.docker.com/reference/cli/docker/system/events/);
the initial and final probe acknowledgments, event limits, ownership checks,
service coverage and default 20-second deadline are unchanged. CI now retains
only the sanitized lifecycle receipt alongside generated CHD diagnostics.

Agent guidance: preserve the receipt before diagnosing another startup failure.
Do not raise the deadline, skip acknowledgments or accept a retry-only green
run. The real control verifies startup barriers, not a complete deployment drill.
Raw container output remains private and must not be included in CI artifacts.
