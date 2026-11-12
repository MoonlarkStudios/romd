# Dependency update proposals

## Continuous advisory checks

Run `mise run dependencies:check` from the root on Linux x64 with the pinned
.NET/pnpm tools, a Docker daemon with BuildKit/buildx, and network access. Podman
artifact export is not qualified for this gate and fails explicitly; it never
skips Go coverage. The command prepares the existing
hash-locked Python parser and runs the same `dependencies.source` registry
check as `.github/workflows/dependencies.yml`. Its **Dependency advisories** job
runs on every pull request, push to `main`, and daily at 06:23 UTC. The release
workflow also calls it at the tagged revision and requires success before the
publisher can run. It does not invoke full CI or publish anything itself.
Make that status check required in the
[repository settings](repository-settings.md).

| Ecosystem | Coverage and source |
| --- | --- |
| NuGet | Fresh locked restores of every tracked/current `.csproj`, including tests and tools outside the solution; complete resolved inventories plus `dotnet package list --vulnerable --include-transitive --format json --output-version 1`, using nuget.org advisory data. Missing locks, projects, inventory, advisory data, failed restores and advisory-source warnings fail the check. |
| npm/pnpm | The complete `web/pnpm-lock.yaml`, including every workspace importer and development, transitive and optional dependencies, through pinned pnpm's registry audit. A temporary copy uses neutral configuration, so local ignore lists and production-only settings cannot hide findings. No install scripts or web build run. |
| Release browser npm | `scripts/release/browser/package-lock.json` through the pinned Node distribution's `npm audit --package-lock-only`, including development, optional and peer dependencies. The audit uses a temporary copy, neutral configuration and no install scripts. Package counts, advisory totals, affected versions and the root manifest must match the lockfile. |
| Dart/Pub | Every pub.dev-hosted package in the committed console `pubspec.lock`, including direct/transitive and development dependencies, through the release-pinned, SHA-256-verified Trivy. Package coverage must match the lockfile. Trivy uses the GitHub Advisory Database for Pub. Flutter/Dart SDK components are outside this lockfile check; git/path/custom-registry packages require explicit coverage before introduction. |
| Go reader | Every direct and indirect `require` from the checksum-verified source archive selected by the Dockerfile, plus the actual compiler's standard library, through pinned Trivy. `go.sum` is checksum history, not the selected graph; the source `go` directive is a minimum, not the compiler. A fresh qualification-only Docker target exports the actual reader, trust root, runtime receipt, compiler build information, complete package graph and checksum-verified official govulncheck tool. |

The check reports all returned advisory severities and blocks **HIGH, CRITICAL
and UNKNOWN**, matching the existing release severity policy. Missing or changed
scanner schemas, unsupported platforms, failed downloads, network failures and
incomplete inventories fail closed. No source advisory exceptions or baseline
are applied. NuGet restore auditing is also enabled for direct and transitive
packages; restore warnings alone do not replace the dedicated gate.

JavaScript coverage compares every tracked/current `package.json` and lockfile
with the audited pnpm importers and browser package. A new manifest or lockfile
fails until it is covered; adding a separate tool cannot silently bypass audits.

Go module warnings remain in the raw Trivy report with their original severity
and `rawBlocked` status. The shared source/final-image helper can establish that
all affected packages are absent only when a complete pinned govulncheck
`-mode=binary -scan=package -format=json` stream agrees with the compiler and
linked-module inventory, the complete source import graph, and fresh official
primary Go OSV records for the exact affected module/version and ID or alias.
Any affected package import blocks even when its vulnerable symbol is uncalled.
Conservative package findings from missing binary symbols also block. Unknown
IDs, ambiguous aliases, unsupported records, missing inventory or malformed,
truncated and failed scans remain unresolved and blocking. A JSON exit code of
zero alone does not establish absence. This is a package applicability proof,
not a risk exception, vulnerability ignore list or baseline.

The Go DB publication timestamp can legitimately be older than 72 hours. The
proof instead binds a successful current fetch of the official DB index and
primary records, rejects future/malformed timestamps, and requires the index to
agree with the scanner. Trivy retains the separate 72-hour database freshness
requirement. Neither database is frozen to achieve a pass.

The final admin/worker image scans use this same helper after extracting the
actual reader and checking its hash and receipt against a fresh qualification.
They scan that extracted binary; image findings must identify its exact path,
and SBOM findings must belong exclusively to that path. Other Go binaries and
unbound components cannot inherit its proof. Flat release JSON evidence retains
the full raw Go stream, primary OSVs, compiler/module/package inventories and
hash-bound source/tool receipts alongside the unchanged raw Trivy reports.

Trivy is installed only from the existing release pin in
`scripts/security/tools.json` into ignored `.artifacts/source-security/` and
reverified before each use. A fresh runner downloads current advisory data;
local Trivy database refresh follows its normal freshness rules. The check
needs several GiB of cache/scratch storage; see the
[tmpfs quota mitigation](../development/known-issues.md#source-advisory-database-extraction-exceeds-tmpfs-checkout-quota)
when using a worktree under `/tmp`. The check
retains normalized package/version/advisory/severity/coverage results in the
validation evidence directory, plus the complete public-source Go export and
proof in its private `go-reader-evidence` subdirectory. Use a new evidence output
for each run; existing Go evidence is never reused. CI retains these for seven
days. `ROMD_GO_READER_BUILDER` can select an explicitly prepared Buildx builder.
For desktop Go qualification, bound the Docker-container builder to two CPUs,
3.5 GiB RAM with no swap and BuildKit `max-parallelism = 1`; Go tool compilation
also uses two compiler processes. Retain its memory events/peak and stop only
that owned builder when finished. Custom-daemon qualification may use a task-only
Docker wrapper with its explicit endpoint and validated credential-free client
configuration; release scanners retain their anonymous environment. This local
harness does not establish a published-image or release-attestation pass.
Arbitrary scanner
diagnostics, credentials and absolute source paths are not copied into normalized
advisory results. Go export evidence describes only pinned public-source inputs.
A scanner error is an investigation requirement, not evidence
that dependencies are safe. Run the relevant native command privately to
diagnose detailed registry errors without publishing credential-bearing logs.

Source checks do not cover base-image OS packages, downloaded native tools,
emulator payloads or every SDK/runtime vulnerability. Existing native pin review,
Renovate updates and final-image/bundle digest vulnerability scans remain
required. An advisory-free result only reflects the sources' current knowledge.
See the supported [.NET JSON command](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list),
[pnpm 10 audit](https://pnpm.io/10.x/cli/audit),
[Trivy Dart coverage](https://github.com/aquasecurity/trivy/blob/v0.76.0/docs/guide/coverage/language/dart.md)
and [Trivy advisory sources](https://github.com/aquasecurity/trivy/blob/v0.76.0/docs/guide/scanner/vulnerability.md),
plus the pinned [govulncheck source and binary scan semantics](https://github.com/golang/vuln/tree/v1.9.0/cmd/govulncheck).

## NuGet lockfiles

Commit each project's `packages.lock.json`, including projects outside the
solution. CI enables locked mode for implicit restores as well as explicit
restore commands. Image builds use separate `packages.linux-x64.lock.json` and
`packages.linux-arm64.lock.json` graphs so they cannot rewrite portable locks.
The native engineering builder also restores in locked mode.

After changing NuGet package references or the SDK, regenerate and review all
graphs, then verify that locked restores leave every lockfile unchanged:

```bash
mise run dependencies:locks -- --update
mise run dependencies:locks
```

The backend CI job runs the same verifier before building. It restores the image
targets first and portable projects last, including projects outside `Romd.sln`.
Commit the resulting lockfiles with the dependency change. Dependency bots may
need this maintainer step for the additional Linux graphs; do not waive a failed
locked restore. Source advisory scans use those locks with fresh advisory data,
without re-resolving or updating them.

## Update proposals

`.github/renovate.json` selects Renovate for NuGet, pnpm, Flutter/pub, GitHub
Actions, Docker inputs and mise tool versions. Related packages are grouped;
at most three proposals may be open and two created per hour. Automatic merge
is disabled, including platform automatic merge. Renovate rebases a proposal
only when it conflicts with its base (`rebaseWhen: conflicted`), so a merge to
`main` does not rerun CI on every open proposal. NuGet and npm updates must be
three days old (`minimumReleaseAge`), which leaves time for a compromised or
broken release to be withdrawn. With `internalChecksFilter: strict`, Renovate
opens no proposal until some version has aged. A maintainer must enable the
Renovate GitHub App for this repository; checking in configuration does not
activate it or prove that required checks are protected.

Validate a proposed configuration using a reviewed pinned Renovate version:

```bash
npx --yes --package renovate@44.164.6 renovate-config-validator --strict .github/renovate.json
LOG_LEVEL=debug npx --yes --package renovate@44.164.6 renovate --platform=local --dry-run=extract
```

The validator rejects unknown option names and wrong value types, but it does
not check every enumerated value: `"rebaseWhen": "sometimes"` passes. Compare
enumerated values with the
[configuration options reference](https://docs.renovatebot.com/configuration-options/).

Use a disposable checkout and an ignored evidence directory for logs. The local
platform reads tracked repository configuration. Its extraction/lookup dry runs
do not create branches or prove a hosted proposal. Use the actual RE2 engine;
an unavailable native addon with a fallback-regex warning is incomplete regex
validation. The [local platform documentation](https://docs.renovatebot.com/modules/platform/local/)
describes its limitations. Before completing activation, review one real
proposal per ecosystem, including lockfile/digest changes, retained notices and
the appropriate existing checks. A failed check must leave the PR unmerged.

## Container refresh

The Dockerfile pins its syntax frontend and all base images to reviewed digests.
`scripts/deployment/base-images.json` pins the PostgreSQL release input and
qualification ingress; a custom Docker manager updates those JSON references.
Compose's release image variables are populated from the immutable candidate
map, not independently upgraded by the bot.

PostgreSQL digest and minor/patch updates have a separate proposal group and
no weekly schedule restriction. Use the current supported PostgreSQL 18
Bookworm Docker Official Image, keep its version and digest pinned, and review
and qualify upstream updates promptly. Major-version upgrades require a planned
database upgrade; do not switch the deployment to an unbounded `latest` tag.

For each refresh, verify the upstream repository, version, supported platform and
manifest digest from the official registry. Review its release/security notes and
preserve source/license notices. Keep .NET SDK selection consistent with
`global.json`, mise, CI and the native builder's checksum-pinned SDK archives;
`mise run toolchain:check` rejects mismatches. Upgrade the final ASP.NET runtime
image as well as NuGet packages: package auditing does not patch a container's
shared framework. Use Microsoft's current
[.NET release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json)
for runtime/SDK versions and archive hashes, and verify multi-platform manifest
digests directly from the official registry. An older SDK feature band may no
longer receive the current runtime security patch; move all SDK pins together
to a serviced stable feature band without relaxing `global.json` selection.
Run:

```bash
mise exec -- python3 scripts/validation/run.py --id deployment.contracts
python3 scripts/deployment/smoke.py --build --vfs
```

Use `mise exec` so a Podman setup finds the pinned Compose client.

The latter creates disposable resources and performs the full production
recovery drill; a configuration-only pass does not qualify the refreshed image.
Also run frozen installs and the default checks for affected backend, web or
console dependencies. Release metadata/scanner and downloaded-package gates
apply to the final digests before supported publication.

## Pins requiring explicit review

The native-media maintainer owns `native/libchdr/libchdr.json`,
`native/chdman/mame.json`, builder RPM/tool archives and their associated recipes,
source receipts, checksums and notices. The catalog maintainer owns the companion
repository commit, archive hash, Go version and trust root in the Dockerfile.
EmulatorJS runtime/core pins likewise follow their existing runbook. These
multi-field source/trust updates need a focused reviewed change; do not treat a
bot's base-image proposal as authorization to update them or reduce their gates.

Hosted proposal creation, required-check enforcement and representative update
validation remain maintainer checkpoints until their actual results are recorded.
