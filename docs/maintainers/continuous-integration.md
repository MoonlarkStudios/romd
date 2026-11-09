# Continuous integration

ROMD uses standard `ubuntu-24.04` GitHub-hosted runners. Keep the existing public
runner and storage allowances; this workflow does not require paid runners,
self-hosted machines or an additional scanning service.

## Required source checks

Every pull request and main push runs `.github/workflows/ci.yml` and
`.github/workflows/dependencies.yml`. Release verification calls those same
workflows from the tagged source. Main requires **CI gate** and **Dependency
advisories**; see [repository settings](repository-settings.md).

The early `source-checks` job verifies workflow policy, the checksum-pinned
Gitleaks installation and its positive/negative controls, new commit secrets,
portable and both Linux NuGet graphs, and registered C# style checks. Builds
wait for this job. Each backend runner still restores its own portable locked
assets before building. NuGet cache keys include the SDK, package/project inputs
and every lockfile; locked restore remains authoritative.

The guidance, console, native, backend, infrastructure/integration partitions,
web/OpenAPI and deployment contract suites remain required. The gate requires
every job to succeed, including the deployment job when its expensive steps are
unnecessary. A failed or skipped job cannot produce a passing gate.

## Network qualification probes

The two existing integration partitions install Ubuntu's nginx package and
checksum-verified Linux x64 rclone 1.75.1. The rclone version matches the existing
qualification baseline; its versioned GitHub release asset and archive digest
are pinned in `scripts/ci/network_probes.py`, matching the publisher checksum.
The direct download host returned HTTP 403 to hosted runners during qualification.
Tool paths are exported only to those jobs. No runner, external service or
additional artifact retention is added.

Eight previously optional probes now run on every PR, main push and reusable
release verification:

- Two real nginx streaming probes exercise both shipped ingress templates:
  bounded backpressure, no VFS disk spooling, ranges, conditional reads,
  encoded paths and replacement of forged forwarding headers. A buffered
  control must spool, proving the test can detect buffering regressions.
- Two nginx HTTPS endpoint probes check the actual application behind the
  shipped template, certificate validation, authentication and revocation,
  including a real rclone client.
- Four rclone protocol probes cover loopback device login/listing/ranges,
  TLS trust, exported registered connections with credential rotation and
  revocation, and token-command recovery after an unauthorized response.
  Bearer/token-command cases retain regression coverage for the parked helper;
  they do not qualify it as a supported mounted client.

The registered integration command emits a runner-local `integration.trx`.
A required follow-up checks the exact probe names for that partition
and requires one passing result each. Missing reports, skipped/failed probes
and duplicate results fail the job. The workflow policy prevents omission,
conditional execution or reordering of preparation, tests and receipt checks.
Existing coverage artifacts retain only a small `network-probes.json` receipt
of selected names, outcomes and durations; raw TRX transcripts are excluded.

The other 22 opt-in probes remain manual:

| Probes | Why they stay outside regular CI |
| --- | --- |
| 19 native mount, keychain, FSKit, cache/disk-pressure and packaged-client probes | Need a representative macOS host, user credential store, mount support and sometimes installed/signed packages or large source files. A loopback Linux protocol check cannot establish those guarantees. |
| 1 real-disc media delivery probe | Requires private disc/DAT fixtures and private evidence. Generated archive/media fixtures remain covered by the normal storage and CHD gates. |
| 2 load/throughput measurements | Require controlled duration, concurrency and storage conditions. Shared-runner timings are not deployment capacity evidence; deterministic admission/queue tests remain required. |

For local nginx/rclone probes, supply absolute `ROMD_VFS_NGINX` and
`ROMD_VFS_RCLONE` paths; the token-command case also uses `ROMD_VFS_PYTHON`
(default `/usr/bin/python3`). Missing local tools still produce explicit skips.
Native-client and real-media results belong in the existing
[media qualification matrix](../architecture/media-status.md), with private raw
evidence, rather than being inferred from these CI protocol checks.

To verify an existing partition receipt locally:

```bash
mise run ci:network-probes -- hosting PATH_TO_RESULTS
mise run ci:network-probes -- endpoints PATH_TO_RESULTS
```

Create receipts with `dotnet test --logger 'trx;LogFileName=integration.trx'`
and an explicit `--results-directory`; use a new results directory for each run.

## CHD drill selection

`scripts/ci/change_scope.py` permits omission of the real image/recovery/CHD
steps only on a pull request whose entire changed-path inventory is unrelated:
root community documents, documentation, brand assets, console files, issue
forms, or web package `src/` files. Web dependency/build configuration and schemas
are excluded from that exception. Every backend, test, native, infrastructure,
build, workflow or unknown path requires full verification. Rename detection is
disabled so both old and new paths are classified; missing/invalid base history
fails the early job. Empty inventories require full verification.

Main pushes and reusable release invocations always run the complete drill.
Deployment/ingress and CHD harness contract tests always run, including on an
unrelated PR. The three expensive steps share one tested condition. New paths
default to full coverage; extend the unrelated list only with regression tests.

## Build web assets once

The web job installs the frozen workspace, runs lint/tests/build/OpenAPI drift,
then stages those fresh `dist` files into the two hosts. It builds the hosts
without invoking another frontend build. `scripts/ci/frontend_assets.py`
rejects missing entry points and links, compares every staged file with the web
output, and verifies matching SHA-256 integrity and publication settings in
both MSBuild static-asset manifests.

To reproduce after a fresh `pnpm build` in `web/`:

```bash
mise run build:prebuilt
```

`mise run build` retains the integrated development build route.

## Secret checks

```bash
mise run secrets:check               # committed changes since origin/main
mise run secrets:check -- --full     # every reachable public ref
mise run secrets:check -- --base FULL_COMMIT_SHA
```

The helper installs the existing checksum-pinned scanner into temporary storage
unless `--gitleaks PATH` supplies an authenticated installation with its original
archive. Reports remain redacted in temporary storage; scanner output is not
echoed. Repository ignore files and inline allow directives cannot hide findings.
The sole reviewed synthetic sentinel exception binds its rule, path, line range
and exact Git blob. A finding from different bytes is not covered by it.

These checks scan committed Git patches. They do not inspect uncommitted changes
or establish binary/archive coverage, privacy or redistribution rights. Follow
[source and asset review](source-review.md) for those separate reviews.
`--full` scans every local ref, including any retained private migration refs;
use a fresh public checkout when qualifying public history.
Local `workflows:lint` runs the helper/policy unit tests; set
`ROMD_TEST_GITLEAKS` to the authenticated executable to include the real-scanner
regressions. Hosted `source-checks` always supplies it.

## Weekly and manual security maintenance

`.github/workflows/security-maintenance.yml` runs weekly on Sunday at 06:47 UTC
and supports manual selection of all suites, secrets, CodeQL or native fuzzing.
It adds no required PR check. Failures remain visible in Actions and CodeQL
findings appear in the repository's code-scanning view.

- Full reachable-public-history secret scanning uses the same pinned scanner
  and exact exception policy.
- CodeQL analyzes C#, JavaScript/TypeScript, Python and GitHub Actions using a
  commit-pinned action. C# uses the pinned SDK, cleans the complete solution
  once, then performs a serial complete build for extraction. Per-project
  rebuilds from `--no-incremental` can clean shared worker dependency outputs
  again before their consumers compile, even with serial scheduling. The
  workflow policy guards the separate clean/build commands.
  Only that job has `security-events: write`; it has no publication permissions.
- Native maintenance runs the existing sanitizer canaries, guard parity and
  fuzz gate with generated fixtures. Weekly campaigns use 1,800 seconds per
  target. A manual 60-second selection is a workflow smoke run and does not
  establish the full campaign result. See [native fuzzing](../../native/libchdr/FUZZING.md).

Successful source jobs retain only small early receipts for three days; existing
test coverage and bounded failure diagnostics retain their short lifetimes.
Native maintenance uploads selected failure logs/findings for three days, not
build trees or the whole corpus. Keep packaged-release evidence under the
separate [release readiness](release-readiness.md) policy.

Production drill failures retain the sanitized lifecycle receipt alongside CHD
diagnostics; raw container output stays private. The lifecycle collector replays
Docker's bounded retained history for each fresh project before its first probe,
then observes continuously. Initial/final acknowledgments, deadlines, ownership
checks and service-coverage assertions remain mandatory.
