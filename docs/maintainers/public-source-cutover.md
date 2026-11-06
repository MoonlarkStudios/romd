# Public source cutover

Use this checklist to prepare the first source publication at
`MoonlarkStudios/romd`. Preparation is local. Creating the destination, pushing
source, changing visibility and announcing the project require the maintainer's
explicit publication decision. The destination may not exist during preparation.

The launch is a **public pre-release**. Publishing source does not qualify
downloaded server artifacts or native console binaries; those follow
[release readiness](release-readiness.md).

## 1. Freeze and validate locally

- [ ] Start from current private `main` in an isolated worktree. Preserve the
  private repository, its history, issues and existing worktrees.
- [ ] Finish the source changes and commit them. Record the full source commit
  and tree IDs in private evidence. Reconcile any newer `main` changes before
  freezing; every subsequent content change invalidates the snapshot binding.
- [ ] Run `mise run agents:check`, `mise run release:check`, workflow policy
  tests and `mise exec -- actionlint`. Record environment-specific skips and
  failures as well as passes.
- [ ] Exercise first-push C# formatting with
  `mise run dotnet:style -- --base 0000000000000000000000000000000000000000`.
  The null previous tip checks every maintained C# file. Normal PRs and pushes
  retain incremental selection; missing named history remains an error.
- [ ] Run the applicable source checks from [release readiness](release-readiness.md#source-checks-available-in-a-development-environment).
  Keep local evidence separate from hosted results. Do not mark unrun backend,
  web, console or integration suites as passed.
- [ ] Review the README, roadmap, contribution and conduct policies, security
  policy, notices and screenshots. Capture the running UI without developer
  overlays; preserve the exact-file provenance for visible artwork.

Hosted CI on the private repository can be unavailable because of account
billing or runner allocation. Record it as **not run** with the actual reason.
It is not a prerequisite to the first authorized source push when unavailable.
The new repository's first CI and advisory runs are mandatory post-push gates;
they must pass before accepting further work, announcing the launch or tagging
a release. Keep the checked-in gates intact.

## 2. Build and audit the exact local snapshot

- [ ] Deliberately choose the public author and committer name/email. Confirm
  the private durable storage location for complete audit evidence.
- [ ] Follow [Prepare the source](publication-audit.md#prepare-the-source) to
  create a local repository with one root commit and the reviewed source tree.
  Preserve the `native/libchdr/upstream` gitlink; a file copy or `git archive`
  does not preserve it.
- [ ] Confirm that the snapshot has no remote and only `refs/heads/main`, with
  exactly one reachable commit. Record the source commit, snapshot commit and
  matching tree IDs. Check the intended public commit identity privately.
- [ ] Authenticate the pinned scanner installation, run its regressions and
  run the [publication audit](publication-audit.md#run-in-private-storage) on
  the snapshot with `--probe-access`. Keep raw outputs outside every Git checkout.
- [ ] Resolve every scanner finding, privacy candidate and coverage gap by exact
  object/fingerprint. Recheck known synthetic archive gaps with
  [the fixture review helper](publication-audit.md#recheck-known-archive-fixture-evidence).
  Preserve failed decoder results; zero independently detected secrets does not
  change their coverage status.
- [ ] Record maintainer decisions for the exact fixture, project artwork,
  screenshot, third-party mark, font and commit-identity scopes using
  [the disposition table](publication-audit.md#record-decisions-by-exact-scope).
  Leave decisions pending when the evidence or authority is missing.
- [ ] Copy the complete evidence package to the approved private durable
  location, verify its hashes and record the retained location. Temporary
  storage alone does not complete this gate.

Keep a private candidate record with these fields:

```text
Source commit / tree:
Snapshot root commit / tree / permitted refs:
Public author and committer identity:
Scanner version, archive hash and executable hash:
Audit result, finding counts and coverage gaps:
Independent fixture review result and reviewer attestation:
Privacy and asset decisions, reviewer and date:
Local validation results, environment and skips:
Private durable evidence location and verification:
Publication decision: pending
Destination CI and advisory runs: not run before first push
Destination settings and private-reporting test: pending creation
```

Tool output retains `publication_signoff: false`; approval is a separate
maintainer record. A prepared snapshot or checklist never authorizes its push.

## 3. Publication decision and destination creation

Stop here until the maintainer explicitly approves the named snapshot and tree.
The following steps are for that later authorized cutover.

- [ ] Check for automated mirrors or scheduled publishers. Stop any that could
  push private refs or repopulate the destination before it is reviewed.
- [ ] Create an empty repository under **MoonlarkStudios**, named **romd**, with
  the approved visibility. Do not initialize a README, license or `.gitignore`.
  Confirm the exact owner/name before adding content. Reuse no private source
  branches or tags. If replacing a prior repository, follow
  [the recreation procedure](repository-settings.md#recreate-the-public-repository).
- [ ] Apply available organization/repository settings from
  [repository settings](repository-settings.md): read-only Actions defaults,
  outside-contributor approvals and appropriate access. Record settings that
  require public visibility or registered check names for the next phase.
- [ ] Configure the public profile with this suggested description:
  **A self-hosted home for your personal game library.** Suggested topics:
  `rom-management`, `self-hosted`, `game-library`, `dotnet`, `react`, `flutter`.
  Set a project homepage only when one exists. Upload the reviewed social
  preview linked from [brand guidance](../../brand/README.md).
- [ ] Recheck the approved snapshot's ref inventory, root commit and tree.
  Push only its `main` branch. Never use `--mirror`, `--all` or `--tags`.
  Keep the private source repository private.

## 4. Verify the public repository

- [ ] In a fresh checkout, verify that public `main` initially has one commit,
  the approved tree and only the intended advertised refs. Initialize the
  public libchdr submodule and verify anonymous access to pinned build inputs.
- [ ] Wait for **CI gate** and **Dependency advisories** on the exact pushed
  commit. Record both run URLs and conclusions. Diagnose failures without
  weakening checks. Fixes require a newly reviewed commit and affected audit
  review; do not force-replace the published root or import private history.
- [ ] Activate branch and tag rules after the check names register. Protect
  `main` against direct changes, deletion and force pushes; restrict `v*` tags.
- [ ] Enable and verify secret scanning, push protection and private
  vulnerability reporting. Complete the harmless private-reporting test and
  confirm notifications reach the intended maintainers.
- [ ] Configure the protected `release` environment before any release tag,
  and install the intended Renovate integration. Confirm the effective settings
  using [the read-only settings checks](repository-settings.md#recreate-the-public-repository).
- [ ] Check anonymous README images, relative documentation links, issue forms,
  conduct/security contact routes, clone/build instructions and the social card.
  Review GitHub's community profile for missing public-facing files.
- [ ] Keep mirrors stopped until their checkout, root, permitted refs and access
  are deliberately retargeted and verified. Do not resume an old history mirror.
- [ ] Publish only the sanitized aggregate audit summary described in
  [publication audit](publication-audit.md#record-decisions-by-exact-scope).
  Announce the public pre-release after source CI, reporting and protections
  are working. Package releases remain subject to their separate qualification.
