# Release rehearsals

Use **Release readiness rehearsal** in GitHub Actions before cutting a release
tag. It builds and tests a clean source commit without creating public tags,
images, releases or assets. The workflow also runs daily against `main`, and
on pull requests that change its entry point. Scheduled failures are visible in
Actions; investigate them before preparing a candidate.

For a manual run, select the workflow ref containing the rehearsal tooling.
Optionally provide `revision` as a full 40-character commit and `version` as the
intended strict `v<SemVer>` version, such as `v0.2.0-rc.1`. An empty revision uses
the selected workflow ref's commit. An empty version uses a disposable version
derived from that commit. The selected source must contain this tooling; older
revisions that predate it cannot use this entry point.

The workflow uses a standard native Linux amd64 Docker runner with read-only
repository permissions. It installs the selected source's locked browser,
builds source/version-labelled images and runs the existing production source
drill. It then uses the same
[packaged rehearsal](packaged-release-qualification.md#before-publication) as PR
CI and publication: actual-image and merged-SBOM security policy, authenticated
browser playback, restart, backup/restore, both real fault controls and complete
container/registry cleanup. It reuses the publication receipt verifier before
reporting a pass. PostgreSQL retains its official-image upstream reporting policy.

To run the same entry point locally, use a clean, isolated checkout and the
native Docker/browser prerequisites in the packaged qualification guide:

```bash
revision="$(git rev-parse HEAD)"
python3 scripts/release/readiness.py --revision "$revision" \
  --tag v0.2.0-rc.1 --output .artifacts/readiness/UNIQUE_RUN
```

The output directory must be new. `readiness.json` and `summary.md` record the
source, intended version, gate results, blocking stage/check codes, exact image digests and
verified receipt hash. Reports are updated before each long stage, so an
interrupted run cannot leave a passed report. Arbitrary subprocess output stays
in private temporary storage; retained records contain only output hashes.
GitHub shows the Markdown report in its run summary and retains sanitized
evidence for seven days, including failed runs. Do not upload private container
logs, backups or credentials from outside that evidence directory.

A passing report means that the named inputs passed a disposable rehearsal.
It does not qualify a public release or replace normal source CI/review.
Publication still rehearses its exact build and requires its own bound receipt.
Downloaded final artifacts still require runtime/security checks, provenance,
anonymous public verification, tamper rejection and the maintainer's promotion
decision. Changed source, version, image digests or advisory data require fresh
evidence. Follow [release readiness](release-readiness.md) for those gates.

For each release failure, identify whether the product, packaging, test harness
or an external input failed. Reproduce that failure in the narrowest meaningful
check, show that the fix addresses it, and route the regression through the
appropriate required CI gate. Keep raw diagnostics private and evidence in the
pull request. A successful retry alone does not demonstrate a correction.
