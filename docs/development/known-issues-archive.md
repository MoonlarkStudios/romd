# Resolved Validation Issues

Entries here have status FIXED and were moved verbatim from
[known issues](known-issues.md). They are kept for regression diagnosis: search
this file by command, error text or tool name when a matching failure recurs,
then treat a match as a regression of the recorded fix rather than as accepted
debt. Current open, deferred and mitigated issues stay in
[known issues](known-issues.md).

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

Status: FIXED

Command: `pnpm test` from `web/`

Root cause: Node's experimental Web Storage was accessed before the test mock
was installed. Default render helpers also mounted a live `AuthProvider`, whose
asynchronous initialization outlived unrelated synchronous test assertions.

Change: Vitest retains `NODE_OPTIONS=--no-experimental-webstorage`. Default
render helpers now provide stable, configurable auth context. Dedicated tests
exercise the real provider's initialization, sign-out, session events, and
cleanup; Login integration tests still render the real provider. Tests that
exercise asynchronous artwork updates now await their observable results.

Verification: the full Vitest suite passed 1,369 tests across 156 files without
stderr or React `act(...)` warnings during the September 2026 warning cleanup.

Agent guidance: use the stable helper for unrelated components and explicitly
mount `AuthProvider` when testing auth behavior. Await observable asynchronous
results; do not suppress console output to hide lifecycle warnings.

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

## EF CLI tools lag the EF Core runtime

Status: FIXED

Commands:

- `mise run tools:restore`
- `dotnet ef --version`
- `dotnet ef migrations has-pending-model-changes --project src/Romd.Persistence --startup-project src/Romd.Worker.Host --configuration Release --no-build`

Root cause: the global `dotnet-ef` was 10.0.2 while the repository uses EF Core
10.0.7. Migration generation succeeded but emitted a tool/runtime mismatch
warning.

Change: `.config/dotnet-tools.json` now pins a repository-local CLI to 10.0.7,
with `mise run tools:restore` for setup. The global installation is unchanged.
Validation confirmed the local CLI version, no pending model changes, and no
mismatch warning. A newer CLI release notice during restore is informational;
the pin intentionally matches this repository's runtime.

Agent guidance: restore local tools before migration work and keep the manifest
aligned with `Directory.Packages.props` when upgrading EF Core. Inspect generated
migrations and run PostgreSQL migration tests; successful generation alone does
not validate a migration.

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

## Dev Compose IGDB credential contract is out of sync

Status: FIXED

Command: `mise run test:integration`

Root cause: when originally recorded, the packaging contract expected IGDB
credential overrides in both development API/worker service blocks, but the
development Compose file omitted them.

Change: current `compose.dev.yaml` defines `Providers__Igdb__ClientId` and
`Providers__Igdb__ClientSecret` for admin and worker. Neither the common
environment nor consumer receives them. The 2026-10-16 DockerPackagingTests run
passes this boundary for development and production Compose; the old deferred
status no longer describes current source.

Agent guidance: preserve that host boundary when changing container tooling.
Validate the packaging contracts and full integration suite; a focused pass
does not establish a full-suite pass.

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

Status: FIXED

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

Change: the session-tracking and pause/quit tests now explicitly wait for
`createPlayerBridge` to be called before reading its callback. The same race
failed the paused-quit test at line 215 in public CI run 35415437482, while the
release workflow for the identical commit passed. Production player behavior
and existing pause/resume/session assertions are unchanged.

Agent guidance: report the broad failure separately from an isolated pass. In a
focused test fix, wait for bridge creation before reading its callback rather
than introducing a fixed delay or weakening the session assertions. Do not
interpret an isolated pass as evidence that the full suite passed.

## Biome lint and format includes match no files

Status: FIXED

Command: `pnpm lint` from `web/`

Root cause: `linter.includes` and `formatter.includes` contained only the
negative pattern `!**/generated`. Biome therefore selected no files for those
checks, producing a misleading successful result.

Change: both lists now start with `**` and retain the generated-code exclusion.
The exposed lint issues were corrected and handwritten files normalized to the
configured formatter. `pnpm lint` now treats warnings as errors.

Verification: `biome check --error-on-warnings .` checked 575 files with no
diagnostics after the September 2026 cleanup.

Agent guidance: keep the normal workspace lint command effective. Fix causes
rather than narrowing its coverage or adding blanket suppressions. Regenerate
API clients instead of formatting or editing generated output manually.

## Mantine 9 autosizing textareas require font events in jsdom

Status: FIXED

Command: `cd web && pnpm test`

Root cause: Mantine 9.6.2 Textarea Autosize subscribes to
`document.fonts` loading events. jsdom does not supply FontFaceSet, causing
`Cannot read properties of undefined (reading 'addEventListener')` in textarea
mount effects and cascading form failures.

Change: admin and consumer test setups provide an EventTarget for font event
subscriptions when `document.fonts` is absent. Production behavior is unchanged.

Agent guidance: retain the font-event stub when changing test setup. It supports
event subscriptions, not real font loading or layout measurements.

## Mantine DataTable columns hidden by the test media-query mock

Status: FIXED

Command: `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run packages/romd-admin-app/src/pages/TitleDetail/ReleasesPanel.test.tsx` from `web/`

Root cause: Mantine DataTable 9.4 calls `useMediaQuery('')` for columns without
visibility restrictions. The admin test setup returned false for every query,
so the table rendered empty rows even though the same columns worked in the browser.

Change: the admin `matchMedia` mock now returns true for an empty query, matching
browser behavior. Other queries retain the existing false default.

Agent guidance: keep the actual DataTable in interaction tests. Preserve empty-query
matching when changing media-query mocks; do not mock the table to hide this failure.

## .NET 10 ZIP entry disposal performs synchronous metadata writes

Status: FIXED

Command: `dotnet test tests/Romd.Infrastructure.Tests/Romd.Infrastructure.Tests.csproj --filter FullyQualifiedName~ExportArchiveWriterTests.WriteAsync_HttpLikeStream_UsesOnlyAsynchronousWrites`

Root cause: even with `ZipArchive.CreateAsync`, `ZipArchiveEntry.OpenAsync` and
async disposal, .NET 10 closes an entry through `WrappedStream.Dispose`, reaching
`ZipArchiveEntry.WriteDataDescriptor` synchronously. A non-seekable destination
that forbids synchronous writes fails; MemoryStream-only tests miss it.

Change: the shared ZIP writer uses `ArchiveOutputStream` to buffer at most 64 KiB
of synchronous metadata. It drains that buffer asynchronously before payload
writes, after each entry and after archive disposal. Payloads remain streamed.

Agent guidance: preserve the HTTP-like stream regression test. Do not enable
synchronous I/O on Kestrel or buffer the whole ZIP to hide this runtime behavior.

## Media supervisor cancellation test competed with its wall deadline

Status: FIXED

Command: `dotnet test tests/Romd.Infrastructure.Tests --no-build --no-restore --filter FullyQualifiedName~InspectAsync_DeadlineOrCancellation_JoinsParentAndDescendantsBeforeReturning`

Root cause: the test used a one-second worker wall budget for both deadline and
caller-cancellation cases, then waited for two PID files before cancelling.
A retained Linux diagnostic showed that the wall-deadline result can complete
before a delayed caller cancellation. The original cancellation assertion then
observed a completed result. Setup can also outlast that short budget; a setup
failure does not prove the intended exception assertion was exercised.

Change: only the cancellation case receives a ten-second wall budget. The
deadline case retains one second, and both exception/result and process-stop
assertions remain intact. Production cancellation and timeout behavior are
unchanged.

Agent guidance: give distinct termination triggers separate test budgets.
Verify the assertion reached by every diagnostic failure, and retain faithful
compiled controls proving that ignoring caller cancellation still fails the
test. Do not label setup failures as cancellation evidence.

Second instance (2026-10-14): the deadline case of
`RvzEncodingServiceTests.EncodeAsync_WiiDeadlineOrCancellation_ReapsAndAllowsSiblingThroughSameVerifier`
forced the deadline with a four-second budget, then ran a real sibling encode
through the same verifier. Each run arms its own deadline from that configured
budget, so the sibling had only four seconds. Under full-suite macOS load it
returned `Rom.Media.WallDeadline`. The test now keeps the fixture's ten-second
default: its hanging child reaches the deadline whatever the budget, and every
assertion is unchanged.
`RvzVerifierTests.VerifyAsync_Deadline_IsAPermanentFileFailureAndReleasesCapacity`
had the same shape with a one-second budget and received the same change
preventively; it was not observed failing. When one supervisor serves both a
forced deadline and real work, size its budget for the real work.

## Operational diagnostics budgets ran on the wall clock

Status: FIXED

Command: `dotnet test tests/Romd.Application.Tests/Romd.Application.Tests.csproj --filter FullyQualifiedName~GetOperationalDiagnosticsQueryHandlerTests`

Root cause: the handler armed its overall and per-section budgets with
`CancelAfter`, which always uses the system clock. Tests that let a 75 ms budget
expire also asserted that every sibling section had completed inside that same
real 75 ms. Under CI scheduler load a sibling (for example Hangfire) started late,
its budget expired first, and the section reported `Unavailable`
(`HandleAsync_OverallBudgetExpires_PreservesCompletedSectionsAndRetainsAbandonedScope`).

Change: both budgets now come from the handler's injected `TimeProvider`
(`TimeProvider.System` in production, so runtime behavior is unchanged). The
budget tests use a manual clock, wait until every sibling section's scope has been
released (which the handler does only after the section's result is fixed), and
only then advance time past the budget. A sibling that is deliberately slower than
the budget in real time pins that scheduler delay can no longer spend a budget.

Agent guidance: drive diagnostics budget expiry through the injected
`TimeProvider`; never assert that unrelated sections finish within a real
wall-clock deadline. Observe section completion through scope disposal or entry
gates, not elapsed time.

## Mantine transition timers outlived unmount and failed the web run

Status: FIXED

Command: `pnpm test` (from `web/`)

Root cause: Mantine 9.6.2's `use-transition` schedules a two-frame enter or exit
transition. Its first animation-frame callback calls `ReactDOM.flushSync`. When
two transitions toggle together (a `Menu` closing, for example), that synchronous
commit can re-enter another transition's `handleStateChange`, which schedules a
new frame. The callback then overwrote `rafRef` without cancelling that frame, so
unmount could not cancel it. The orphaned frame later armed a 150 ms timer. On a
slow CI runner the timer fired after the jsdom environment was torn down, and
react-dom threw `ReferenceError: window is not defined`. Every assertion passed,
but Vitest failed the run on the unhandled error, usually attributed to
`TitleHero.test.tsx`. A leak probe found six affected tests in four files
(`TitleHero`, `ArtworkPanel`, `AddArtwork`, `Connections`).

Change: all `@mantine/*` packages were upgraded to 9.7.2. That release contains
the upstream fix ("Transition: Fix frames and callbacks running after unmount
during the first frame", mantinedev/mantine#9263): `rafRef` is reset on clear,
and a frame that is no longer current is abandoned. It replaces the temporary
`web/patches/@mantine__core@9.6.2.patch` backport, which has been removed. With
9.7.2, the same probe finds no transition timer left after cleanup across the
whole suite.

Agent guidance: an unhandled `window is not defined` from react-dom that runs
through a Mantine `Transition` timer means a transition outlived its component.
Do not mask it with fake timers, extra waits or retries. Find the leaking
scheduler with a temporary probe that wraps `window.setTimeout` and
`requestAnimationFrame` and reports timers still pending after `cleanup()`.

## Infrastructure scale tests tripped the CI hang detector

Status: FIXED

Command: `dotnet test tests/Romd.Infrastructure.Tests/Romd.Infrastructure.Tests.csproj --filter "FullyQualifiedName!~Romd.Infrastructure.Tests.Storage." --blame-hang --blame-hang-timeout 120s`

Root cause: two separate problems combined.
- `RomConvergenceBulkStore.AcceptPageAsync` read each candidate's convergence
  snapshot one ROM at a time, about five queries per ROM: 394 commands for one
  64-ROM page. `AcceptPageAsync_MoreThanTenThousandCanonicalCandidates_ReachesLaterWork`
  therefore spent 1.5–2.5 minutes on CI in database round trips.
- That test and
  `EnrichmentWorkbenchScaleTests.Read_RepresentativeCatalogWithConcurrentMutations_ReportsLatencyAndPreservesPages`
  ran concurrently on the assembly's shared PostgreSQL test server. Under that
  contention, one workbench query exceeded the 30-second command timeout.

When these two were the last tests running, nothing completed for 120 seconds,
so VSTest blame-hang aborted the run ("Test host process crashed").

Change:
- Needed convergence snapshots are read for the whole page in a fixed number of
  queries. `AcceptPageAsync_FullPageOfCandidates_ReadsSnapshotsWithoutPerRomQueries`
  pins fewer than two commands per ROM; the per-ROM representation lock is the
  only remaining per-ROM statement. The 10,001-candidate test went from 65 s to
  about 23 s locally.
- Both scale fixtures now run in the non-parallel `PostgreSqlScaleWorkloads`
  xUnit collection, so they never compete with each other or with other database
  tests.
- No hang timeout or command timeout changed.

Agent guidance: do not raise `--blame-hang-timeout` or command timeouts for a slow
database test. Measure per-phase time and command counts first (an EF
`DbCommandInterceptor` makes N+1 reads visible), and fix the query shape. Put
fixtures that generate heavy load on the shared PostgreSQL server in
`PostgreSqlScaleWorkloads`.

## Integration job polling expired after wall-clock discontinuities

Status: FIXED

Command: `mise run test:integration`

Root cause: `TestHelpers.WaitForJobCompletionAsync` measured its 30-second
polling budget with `DateTime.UtcNow`. In the October 15 integration receipt,
the Redump enrollment case recorded a 300.082-second wall interval but only
1.727 seconds of xUnit elapsed time and failed with the job still Pending.
Two passing cases also recorded wall intervals about 298 and 911 seconds longer
than their elapsed durations. Whether clock adjustment or suspend/resume caused
the discontinuities is unconfirmed; they do not establish a product job failure.

Change: the test helper measures the same 30-second budget with
`TimeProvider.GetTimestamp` / `GetElapsedTime`. Deterministic HTTP-only tests
move UTC forwards and backwards independently of elapsed time, verify that the
elapsed deadline still stops polling, and preserve caller cancellation. The
integration task's 60-second hang watchdog and product job timing are unchanged.

Agent guidance: compare TRX start/end timestamps with test elapsed duration when
a polling timeout fires much sooner than its configured budget. Use monotonic
elapsed time for test polling deadlines; do not increase timeouts, skip tests,
change the system clock or infer the underlying cause from wall timestamps alone.
A focused pass does not replace the required full integration gate.

## Cold native SDK startup can fail on rootless overlay storage

Status: FIXED

Command: `mise run native:libchdr`

Root cause: the first SDK 10.0.302 build in the AlmaLinux native builder failed
with `NuGet-Migrations` and `stat("/tmp/.dotnet") ... ENOENT` under a temporary
rootless Docker 29.1.3 daemon on Fedora amd64. The exact pre-build image and
command pass with a fresh `/tmp` tmpfs; explicitly initializing the shared
mutex directory also passes on the original overlay. This isolates the storage
interaction without establishing a general SDK or glibc defect.

Change: the native builder creates root-owned `/tmp/.dotnet` with sticky mode
`1777` before its first SDK invocation. The real default-overlay libchdr build
then passes compilation and all native identity, export and dependency checks.
SDK/archive pins and the native glibc ceiling remain unchanged.

Agent guidance: retain the original error and platform, initialize the shared
directory before cold SDK startup, and verify the actual build. Do not disable
NuGet migration checks, change native compatibility policy, or classify runtime
failures as this builder-only condition.

## Storage progress test observed posted callbacks before completion

Status: FIXED

Command: `mise run test` (`StoreAsync_ReportsProgress` in `Romd.Storage.Tests`).

Root cause: the test used `Progress<T>`, which posts callbacks asynchronously,
then asserted its collection immediately after `StoreAsync` returned. A loaded
host could finish storage before either callback reached the observer.

Change: the test awaits a bounded completion signal after both Hashing and
Complete reports arrive, preserving all original progress assertions.

Agent guidance: join asynchronous progress observers explicitly; completion of
an operation does not establish completion of its posted UI callbacks. Do not
add sleeps or weaken the required phases.

## Native lease release assertions raced unrelated process launches

Status: FIXED

Command: `mise run test` (`UnreapedChild_StartAsync_ClosesUnrelatedInheritableLease`
and `StartTool_StreamConstructionFails_KillsReapsAndClosesTransferredDescriptors`
in `Romd.Infrastructure.Tests`).

Root cause: the tests closed their own flock descriptors and immediately
asserted independent reacquisition in the shared parallel test process.
An unrelated concurrent fork can temporarily inherit the same open file
description, retaining its lock until exec closes the close-on-exec descriptor.
Atomic close-on-exec prevents retention after exec, but does not eliminate this
window. Repeating both original test bodies alongside concurrent `/bin/true`
launches reproduced their exact reacquisition failures.

Change: these scenarios execute in dedicated test processes, keeping their
original child identity, descriptor cleanup, native wait and immediate lease
reacquisition assertions. Nested payloads resolve the original test output
directory. Other test collections remain parallel.

Agent guidance: isolate assertions about immediate OFD closure from unrelated
fork/exec activity in the same process. Preserve real child cleanup and
close-only lease ownership; do not add `LOCK_UN`, sleep before reacquisition,
retry the assertion or disable parallelism across the suite.

## PostgreSQL test pools outlived recycled container ports

Status: FIXED

Command: the registered host integration suite, including
`MediaFactPublicationPrivilegeTests.Publisher_ProductionWorkerRole_PublishesGraphReadableByEveryRuntime`.

Root cause: role-container connection pools outlived their containers. Reusing a
published port could recreate an earlier connection-string pool key and rent a
socket belonging to the stopped server. A controlled two-container replacement
returned PostgreSQL `57P01: terminating connection due to administrator command`
on its first pooled query; a distinct application name or an unpooled connection
succeeded. The original failed gate did not retain mapped-port evidence, so its
port reuse remains inferred.

Change: `RomdPostgreSqlContainer` gives each container a distinct application
name across its five immutable connection strings and clears only those owned
pools before shutdown. Pooling remains enabled within a container. The regression
reuses the same published port, exercises schema provisioning and all roles,
and preserves another live fixture's pooled backend. Failed starts also dispose
their owned container.

Agent guidance: keep container and pool ownership together. Do not use
`ClearAllPools`, retry migration failures, or increase hang budgets to hide a
terminated connection. Other administrator shutdowns can produce the same SQL
state; retain lifecycle and endpoint evidence before attributing a new failure.
