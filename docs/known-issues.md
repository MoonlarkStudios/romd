# Known Validation Issues

## Operational diagnostics sibling test shared a wall-clock budget

Status: FIXED

Command: `dotnet test tests/Romd.Application.Tests/Romd.Application.Tests.csproj --filter FullyQualifiedName~GetOperationalDiagnosticsQueryHandlerTests.HandleAsync_ReaderBlocksBeforeReturningTask`

Root cause: the synchronous-provider test used a 75 ms section budget for all
five probes, then inferred sibling independence from every non-blocking section
finishing successfully before that shared deadline. Under CI scheduler load, a
sibling could start late enough to exhaust its own budget even though the
production scheduling boundary correctly kept it independent of the blocked
provider.

Change: timeout behavior and sibling independence now have separate tests. The
timeout test asserts only the blocked section's budget result. The independence
test uses explicit provider-entry gates, releases the blocker only after every
sibling has started, and then verifies the completed response.

Agent guidance: use short wall-clock budgets only to characterize the section
that is expected to time out. Prove cross-section scheduling with entry gates;
do not require unrelated sections to complete within the same short deadline.

## Enrichment heartbeat lease test used wall-clock deadlines

Status: FIXED

Command: `dotnet test tests/Romd.Infrastructure.Tests/Romd.Infrastructure.Tests.csproj --filter FullyQualifiedName~EnrichmentExecutionClaimTests.Heartbeat_LiveExecutionPastLease_SameDeliveryCannotReclaim`

Root cause: the test used a 240 ms system-clock lease and slept across its boundary. A CI scheduler pause could delay the heartbeat until after the lease expired, allowing the same delivery to reclaim even though the production heartbeat contract was correct.

Change: the test now uses manual lease time plus a one-shot heartbeat renewal gate. It advances past the original lease boundary only after synchronizing on a successful renewal.

Agent guidance: lease-boundary tests must control repository time and synchronize on the relevant renewal or handoff. Do not use short wall-clock leases and sleeps as proof of heartbeat ordering.

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

## Root `mise run build` pnpm non-TTY abort

Status: FIXED

Command: `mise run build`

Root cause: the Release build runs MSBuild frontend targets that execute
`pnpm install --frozen-lockfile`. In a non-TTY shell, pnpm refuses to recreate
`web/node_modules` unless `CI=true` is set, and exits with
`ERR_PNPM_ABORTED_REMOVE_MODULES_DIR_NO_TTY`.

Change: the root `build` mise task now sets `CI=true`. Lockfile enforcement is
unchanged; the task still uses `pnpm install --frozen-lockfile`.

Agent guidance: `mise run build` is expected to run headlessly. If this error
returns, inspect the root `.mise.toml` build task before changing pnpm flags.
Do not use `--no-frozen-lockfile` as a workaround.

## Console `mise run analyze` Flutter ephemeral Swift package state

Status: DEFERRED

Command: `mise run analyze` from `clients/romd_console`

Current diagnosis: the reported Swift package link crash under
`clients/romd_console/macos/Flutter/ephemeral` did not reproduce on
macOS 26.5.2, Xcode 26.6, Flutter 3.44.2, and Dart 3.12.2. The analyzer passed
before and after `mise exec -- flutter clean`. `flutter clean` removed the
generated `macos/Flutter/ephemeral` directory and the next analyze regenerated
state cleanly. The ephemeral directory is already ignored by both
`clients/romd_console/.gitignore` and `clients/romd_console/macos/.gitignore`.

Agent guidance: if `mise run analyze` fails with an error pointing at
`macos/Flutter/ephemeral/Packages/FlutterGeneratedPluginSwiftPackage`, treat it
as generated Flutter state first. From `clients/romd_console`, run
`mise exec -- flutter clean`, then rerun `mise run analyze`. If the exact crash
still reproduces after a clean regeneration, capture the full error string and
then check upstream Flutter issues for that exact string.

## Root `mise run test` integration stall

Status: FIXED

Commands:

- Agent-safe backend tests: `mise run test`
- Full integration suite: `mise run test:integration`

Root cause: the previous root task ran `dotnet test` over every test project,
including `Romd.Host.IntegrationTests`. A stale worker-host assertion expected
three Hangfire servers after the intentional materialization queue added a
fourth. When that assertion failed, host/Hangfire cleanup could stay quiet long
enough for the suite to appear stalled. VSTest blame-hang identified the active
test as
`Romd.Host.IntegrationTests.Hosting.HostDeployableSmokeTests.WorkerHost_StartsWithoutHttpSurfaceAndOwnsWorkerServices`.

Related integration failures observed during the original reproduction were:

- `ProductionOverlayTests.ProdOverlay_AdvertisesPublicOrigins` expects
  `Romd__ConsumerHost__PublicUrl` in `compose.prod.yaml`, but the current
  overlay intentionally omits that pin and derives the consumer issuer from the
  forwarded host.
- `HostDeployableSmokeTests.WorkerHost_StartsWithoutHttpSurfaceAndOwnsWorkerServices`
  observed four Hangfire `BackgroundJobServer` hosted services while expecting
  three.

Change: `mise run test` now runs only deterministic backend unit/storage/
infrastructure test projects. `mise run test:integration` remains available and
adds `--blame-hang --blame-hang-timeout 60s --blame-hang-dump-type none`, so a
stall becomes an explicit failure with the active test named instead of an
indefinite hang. The production-overlay assertion now documents the intentionally
request-derived consumer issuer. The worker smoke test now verifies exactly the
`default`, `upload`, `enrichment`, and `materialization` server queues and their
worker counts from Hangfire's live server registry. The Release consumer host
also retains its Testing-only, explicitly activated, bounded startup rendezvous,
so the real two-process identity test executes in the same configuration as CI.

Verification: `mise run test` passed 1,183 tests and
`mise run test:integration` passed 263 tests with the 60-second blame-hang
collector on 2026-08-12.

Agent guidance: use `mise run test` as the default backend safety check. Use
`mise run test:integration` only when your change touches host topology,
deployment composition, Hangfire worker registration, integration fixtures, or
consumer/admin host behavior. If either historical failure returns, treat it as
a regression and preserve the blame-hang evidence rather than retrying an
unbounded solution-wide test command.

## Web `pnpm test` localStorage and React `act(...)` warnings

Status: MITIGATED

Command: `pnpm test` from `web`

Root cause: Node 22 exposes experimental Web Storage. Vitest workers touched
Node's experimental `localStorage` global before jsdom/test setup installed the
workspace mock, producing `ExperimentalWarning: localStorage is not available
because --localstorage-file was not provided.` React `act(...)` warnings come
from `AuthProvider` async session initialization updating state after synchronous
test renders.

Change: the web Vitest scripts now set
`NODE_OPTIONS=--no-experimental-webstorage`, which removes the Node localStorage
warnings from Vitest worker processes.

Remaining deferred warning class: React `act(...)` warnings still occur 50 times
across 11 files:

- `packages/romd-admin-app/src/components/Activity/ActivityCenter.test.tsx`
- `packages/romd-admin-app/src/components/AsyncBoundary/AsyncBoundary.test.tsx`
- `packages/romd-admin-app/src/components/DataTable/DataTable.test.tsx`
- `packages/romd-admin-app/src/components/EmptyState/EmptyState.test.tsx`
- `packages/romd-admin-app/src/components/ErrorBoundary/ErrorBoundary.test.tsx`
- `packages/romd-admin-app/src/components/layout/Header.test.tsx`
- `packages/romd-admin-app/src/pages/Jobs/Jobs.test.tsx`
- `packages/romd-admin-app/src/pages/Library/Library.test.tsx`
- `packages/romd-admin-app/src/pages/TitleDetail/ContentRatingRow.test.tsx`
- `packages/romd-admin-app/src/pages/TitleDetail/MediaGallery.test.tsx`
- `packages/romd-consumer-app/src/pages/Shelves.test.tsx`

Agent guidance: `pnpm test` currently passes despite the remaining React
warnings. If you work on auth test infrastructure, prefer a focused follow-up
that replaces live `AuthProvider` usage in default render helpers with a stable
test auth context, while preserving test assertions. Do not bulk-edit unrelated
tests just to wrap every render in waits.

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

## Console Android SDK absent on the macOS validation host

Status: DEFERRED

Command: `mise exec -- flutter build apk --debug` from
`clients/romd_console`

Root cause: the current macOS validation host has Flutter installed but no
Android SDK configured. Flutter exits before Gradle or Android resource
compilation with `[!] No Android SDK found. Try setting the ANDROID_HOME
environment variable.`

Change: None.

Agent guidance: report Android packaging as not run on this host; do not treat
the missing SDK as a product or resource failure. Validate Android launch and
icon resources on a host with a configured SDK rather than inventing an SDK
path or weakening the packaging check.

## Console full-suite synthetic cover golden collisions

Status: DEFERRED

Command: `mise run test` from `clients/romd_console`

Root cause: the default parallel full suite can render the dark or light catalog
component golden with another test's in-memory image in its synthetic third
cover slot. The light collision reproduced in two full-suite runs with a
33,996-pixel difference, and a later full-suite run confirmed the equivalent
dark collision. Every observed difference was confined to that 160x213 cover;
`mise run goldens:test` and the isolated affected baseline golden passed with
the intended deterministic fixture.

Change: None.

Agent guidance: if only the dark or light `baseline * catalog components`
golden fails and the isolated diff contains only the synthetic third cover, do
not regenerate the baseline from a parallel multi-file update. Run
`mise run goldens:test` and the isolated affected baseline test, report the
full-suite collision separately, and treat any difference outside that cover
as a real validation failure.

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

## Root release build concurrent frontend packaging

Status: FIXED

Command: `mise run build`

Root cause: global `BuildFrontend=true` activated the `Romd.Host`,
`Romd.Admin.Host`, and `Romd.Consumer.Host` frontend targets concurrently. Their
frozen installs and the Consumer Vite `emptyOutDir` cleanup overlapped, causing
an `ENOTEMPTY` removal failure and an MSB3073 error after Release compilation.

Change: the root build task in `.mise.toml` now adds `-m:1`. Two consecutive
exact builds passed with zero MSBuild warnings/errors, stable target order, and
byte-matching package indexes across all three outputs.

Agent guidance: preserve serial root-build scheduling unless frontend ownership
is centralized. Do not accept a retry-only green build as deterministic evidence
that this race remains fixed.

## Host recursive bin self-copy from literal backslash output directories

Status: FIXED

Command: `mise run test:integration`

Root cause: a directory literally named `bin\Debug` or `bin\Release` (backslash
as a filename character, legal on macOS) can exist inside a host project,
deposited by design-time tooling with Roslyn
`Microsoft.CodeAnalysis.Workspaces.MSBuild` BuildHost binaries. The Web SDK's
`**\*.json` Content glob (Sdk.StaticWebAssets.StaticAssets.ProjectSystem.props)
excludes `$(BaseOutputPath)/**`, but MSBuild's matcher prunes only the child
named `bin`, not the sibling named `bin\Debug`; it then normalizes the
backslash and enumerates the real `bin/Debug/**` output tree through that
portal. Every build copied the output JSONs (`appsettings*.json`,
`openapi/Romd.Host.json`, `romd.staticwebassets.*.json`,
`romd.runtimeconfig.json`, `romd.deps.json`) one level deeper into
`bin/Debug/net10.0/bin/Debug/...` until macOS path limits produced MSB3021
while building `tests/Romd.Host.IntegrationTests`.

Change: deleted the observed stray `src/Romd.Host/bin\Debug` and
`src/Romd.Worker.Host/bin\Release` directories and removed the
checked-in `<Folder Include="bin\Debug\...">` IDE items from
`Romd.Host.csproj` IDE items (artifacts of the same incident). Two consecutive
clean builds produced no nesting after the first incident. `.gitignore` covers
both literal `bin\\Debug/` and `bin\\Release/` variants because the depositing
tool may recreate either configuration.

Agent guidance: if MSB3021 with recursively nested `bin/Debug/net10.0/bin/...`
paths returns for any host project, look for a literal `bin\Debug`, `bin\Release`, or similar
backslash-named) directory beside `bin` in that project — `ls -la` shows it as
a separate entry; `git status` will not, because it is gitignored. Delete it
with quoted shell syntax (`rm -rf 'src/<Project>/bin\Debug'`), clean the normal `bin` and
`obj`, and verify with two consecutive builds. Do not patch csproj globs or
`DefaultItemExcludes` for this; the project files are not the cause.

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

## Dev database name depended on the typed path case on macOS

Status: FIXED

Commands:

- `mise run db:up`
- `mise run db:env`
- `mise env`

Root cause: `scripts/db/lib.sh` derived `ROMD_REPO_ROOT` with bash's builtin
`pwd -P`, which keeps whatever case the caller typed. On the case-insensitive
default macOS filesystem, `cd ~/Development/romd` and `cd ~/Development/Romd`
hashed to different database names, and mise's `_.source` (which receives the
canonical config path) disagreed with `mise run` tasks (which run from the
shell's spelling), so an activated shell could point the hosts at a database
`db:up` never created.

Change: `lib.sh` now uses the external `/bin/pwd -P`, whose `getcwd` returns the
on-disk spelling. Canonically spelled checkouts keep their existing name.

Agent guidance: if `mise env`, `mise run db:env`, and `mise run db:up` ever
report different database names, compare `pwd` with `/bin/pwd -P` before
suspecting the derivation. Do not add an override for the database name.

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

## Root `mise run test:integration` passed with connection strings CI never has

Status: FIXED

Command: `dotnet test tests/Romd.Hosting.IntegrationTests/Romd.Hosting.IntegrationTests.csproj`
run outside a mise-activated shell (as CI does), after the #128 PostgreSQL
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

Agent guidance: before pushing a change that touches host fixtures, run the
suite once without the mise exports, for example
`env -u ConnectionStrings__Romd -u ConnectionStrings__RomdProvisioning -u ConnectionStrings__Hangfire -u ConnectionStrings__HangfireProvisioning dotnet test ...`,
or from a shell that never activated mise. A fixture that needs data must own a
`PostgreSqlTestDatabase`; a green run under `mise run` alone does not prove that.

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

## EF CLI tools lag the EF Core runtime

Status: DEFERRED

Command: `dotnet ef migrations add DurableJobDispatch --project src/Romd.Persistence --startup-project src/Romd.Worker.Host --output-dir Migrations`

Root cause: the installed `dotnet-ef` reports version 10.0.2 while the
repository uses EF Core 10.0.7. Migration generation succeeded but emitted the
tool/runtime version warning on 2026-09-04.

Change: None. Update the EF CLI tools to match the repository's EF Core
version in a focused tooling follow-up.

Agent guidance: check `dotnet ef --version` and the repository's tool manifest
before updating the appropriate local or global installation. Do not suppress
the mismatch warning or treat successful generation alone as migration
validation; inspect the generated migration and run PostgreSQL migration tests.

## Consumer manifest assertion matched random grant token bytes

Status: FIXED

Command: `dotnet test tests/Romd.Hosting.IntegrationTests/Romd.Hosting.IntegrationTests.csproj`

Root cause: the manifest test rejected the substring `cas` anywhere in JSON. An
opaque signed grant can contain those letters by chance, causing a false failure
in `IssueReleaseManifest_OwnedRelease_ReturnsManifestWithRedeemableContentGrant`.

Change: assertions check explicit storage fields and `/cas/` paths. Existing
download-route, grant validation, and content redemption assertions remain.

Agent guidance: inspect structured fields and path boundaries when checking that
responses do not expose storage details; do not scan opaque tokens for words.

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

## Dev Compose IGDB credential contract is out of sync

Status: DEFERRED

Command: `mise run test:integration`

Root cause: `DockerPackagingTests.Compose_IgdbDeploymentCredentialsAreLimitedToAdminAndWorker`
expects `Providers__Igdb__ClientId` and `Providers__Igdb__ClientSecret` in the
`romd-admin` and `romd-worker` service blocks of both Compose files, but
`compose.dev.yaml` currently defines neither setting. The production
`compose.yaml` has both settings in the expected service blocks.

Change: None. Deployment composition is outside the artwork-role change that
confirmed the mismatch.

Agent guidance: Report the full integration suite separately from focused
integration coverage when this assertion is the only failure. Resolve the
intended development credential topology in a deployment-focused change; do
not weaken the packaging assertion or modify Compose as an unrelated test fix.

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

## Dark catalog golden can capture the cover fallback before image decoding

Status: FIXED

Command: `mise run test` from `clients/romd_console/`.

Root cause: `baseline dark catalog components` created a deterministic memory
image but only precached the platform logo before capture. A full suite run
captured the cover's fallback monogram (3.72% pixel difference); an isolated rerun
passed. The comparison images showed unchanged metadata and layout.

Change: explicitly precache both the generated cover and the platform logo
before capture. The unchanged golden then passed in the full 1,241-test suite.

Agent guidance: inspect golden differences for unresolved fixture artwork and
await image decoding before replacing baselines. Do not accept fallback imagery
as a new expected design merely to make this race pass.

## ASP.NET route analyzer crashes on an inline generic PATCH handler

Status: FIXED

Command: `cd web && pnpm api:update`

Root cause: placing an inline lambda with a generic body parameter directly in
`MapPatch` inside the generic reference-resource mapper caused
`Microsoft.AspNetCore.Analyzers.RouteHandlers.RouteHandlerAnalyzer` to throw
`NullReferenceException` (AD0001). Adding an explicit `FromBody` annotation did
not resolve the analyzer failure.

Change: construct the typed handler in `PatchHandler<TPatch>` and pass its
`Delegate` to `MapPatch`. Runtime binding and OpenAPI retain the concrete patch
DTO; subsequent host builds and client generation complete without AD0001.

Agent guidance: keep the typed delegate factory when extending the shared
resource mapper. Do not suppress analyzer warnings to hide a failed analysis.

The patch schema transformer describes editable field types explicitly. When
adding a collection field, update its array/items schema as well as the DTO:
the systems/company slice caught `manufacturerKeys` incorrectly exported as
a string. `ReferenceOpenApiContractTests` now guards the relationship-array
contract and open-ended system keys.

## Full SQL migration export is not directly executable with psql

Status: DEFERRED

Command: `dotnet ef migrations script --project src/Romd.Persistence --startup-project src/Romd.Worker.Host`, followed by `psql -v ON_ERROR_STOP=1` against an empty scratch database.

Root cause: `20260904214032_DurableJobDispatch` supplies an unterminated raw
`INSERT ... SELECT` statement. EF executes it as an individual command, but the
SQL exporter concatenates the migration-history insert, producing a syntax error
at `INSERT`. Other raw statements need review before relying on a complete SQL export.

Change: None to the historical migration. Schema comparison used the worker's
`--migrate-database` path, which successfully provisioned the empty scratch database.

Agent guidance: use the supported worker migration path for fresh schema
verification. Do not interpret successful script generation as successful SQL
execution; validate exported scripts before using them operationally.

## macOS release tar adds AppleDouble metadata

Status: FIXED

Command: `python3 scripts/deployment/test_contract.py`

Root cause: macOS tar added `._*` AppleDouble entries for copied notice files
and their parent directories. The bundle's exact web-file inventory check
caught those extra files while verifying that only font licenses were shipped.

Change: `scripts/deployment/bundle.sh` sets `COPYFILE_DISABLE=1` for archive
creation. The bundle inventory and checksum tests pass without metadata entries.

Agent guidance: retain this setting for portable release archives; do not
weaken the file inventory assertion to accept platform-specific metadata.

## Clean web installs require explicit shared test dependencies

Status: FIXED

Commands: `pnpm install --frozen-lockfile`, `pnpm lint`, `pnpm test`, `pnpm build` from a fresh checkout's `web/` directory.

Root cause: the shared root JSX fixture imported React without a root dependency,
and consumer UI tests imported Testing Library without declaring it. GitHub's
clean install failed to resolve those imports in 45 test files.

Change: declare the root fixture/config dependencies and consumer UI test
dependencies explicitly, retaining existing locked package versions. A fresh
install passed lint, all 728 web tests, and production builds.

Agent guidance: validate dependency changes with a fresh frozen-lockfile install;
existing node_modules can hide missing workspace declarations. Do not work around
missing declarations by broadly hoisting dependencies.

## BrowserPlayer session tracking reads bridge callback before its effect runs

Status: DEFERRED

Commands:
- `pnpm test` from `web/`
- `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run packages/romd-consumer-app/src/pages/BrowserPlayer.test.tsx` from `web/`

Root cause: the test `tracks one retry-safe session from the actual game-started
event through exit` waits for the iframe to render, then immediately reads the
mocked `createPlayerBridge` callback. The bridge is initialized in a React effect,
so the iframe's presence alone does not synchronize that callback. A September
2026 full run passed 733 tests and failed this assertion at
`BrowserPlayer.test.tsx:299` with `expected undefined to be type of 'function'`;
an unchanged isolated file run passed all eight tests. This is a different
assertion from the rapid-exit timing issue above.

Change: None; consumer player behavior and tests were outside the dashboard
statistics change that exposed the race.

Agent guidance: report the broad failure separately from an isolated pass. In a
focused test fix, wait for bridge creation before reading its callback rather
than introducing a fixed delay or weakening the session assertions. Do not
interpret an isolated pass as evidence that the full suite passed.
