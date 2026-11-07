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
or replace the separate [publication object/archive audit](publication-audit.md).
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
  commit-pinned action. C# uses the pinned SDK and an explicit solution build.
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
