# GitHub repository settings

Maintain these hosted settings for the public `MoonlarkStudios/romd`
repository. Checked-in workflows and configuration cannot enforce them. Review
this baseline after workflow, access, integration or organization-policy changes,
and before publishing a release. Each item cites the file that depends on it.
GitHub's settings names may change; the intent is what matters.

## Verify hosted settings

The following authenticated `gh` reads do not apply settings. Review the returned
statuses and effective branch rules against this page, and inspect each tag
ruleset in GitHub Settings. Include organization-level rules and Actions policies
in the review:

```bash
gh api repos/MoonlarkStudios/romd --jq '{visibility,default_branch,security_and_analysis}'
gh api repos/MoonlarkStudios/romd/actions/permissions/workflow
gh api repos/MoonlarkStudios/romd/private-vulnerability-reporting
gh api repos/MoonlarkStudios/romd/rulesets --paginate
gh api repos/MoonlarkStudios/romd/rules/branches/main
gh api repos/MoonlarkStudios/romd/environments/release --jq '{protection_rules,deployment_branch_policy,can_admins_bypass}'
gh api repos/MoonlarkStudios/romd/environments/release/deployment-branch-policies
```

Require `visibility: public`, default branch `main`, workflow
`default_workflow_permissions: read`, `can_approve_pull_request_reviews: false`,
private reporting `enabled: true`, and enabled secret scanning and secret
scanning push protection. A missing/forbidden API result is incomplete evidence;
verify with an administrator, not an assumed default. These repository settings
cannot prove that a maintainer actually receives vulnerability reports; complete
the harmless reporting test below.

## Branch and tag rulesets

- **Protect `main`.** Block force pushes and deletion, and require a pull
  request before merging. `.github/workflows/ci.yml` runs on `push` to `main`
  and on every `pull_request`. Require only its **CI gate** job as a status
  check. The gate needs every other
  CI job and fails unless each one succeeded, so adding, renaming or splitting
  a job does not change the branch rule; `scripts/ci/lint_workflows.py` rejects
  a gate that omits a job. Also require **Dependency advisories** from
  `.github/workflows/dependencies.yml`; it is a separate PR/main/daily workflow,
  not a release publishing job. Require at least one approving review and
  dismissal of stale approvals after code changes; apply the rules to
  administrators as appropriate for the maintainer team. Confirm both required
  check names against the current workflows and their hosted runs.
- [CI operation and scope](continuous-integration.md) owns early checks, the
  conservative PR-only CHD selection, once-built web assets, secret scanning
  and weekly/manual CodeQL/native maintenance. Those maintenance jobs add no
  required PR status check.
- **Restrict `v*` tags to maintainers.** `.github/workflows/release.yml` runs on
  any pushed `v*` tag. Its `publish` and `publish-complete` jobs hold
  `contents: write` and `packages: write`, so a tag push publishes container
  images to GHCR and creates a GitHub release. Restrict creation, update and
  deletion of `v*` tags to maintainers. `scripts/release/preflight.py` also
  rejects a tag that is not valid SemVer or does not point at the current tip
  of the branch named in `.github/release-policy.json` (`main`), but it cannot
  stop an untrusted person from pushing a tag.

## Release environment

`publish` in `.github/workflows/release.yml` declares `environment: release`,
and `scripts/ci/lint_workflows.py` requires it. The job therefore waits for a
reviewer after the tag's full CI and source advisory runs pass, before any image, release or
asset exists for that tag. Rejecting it leaves nothing published, while
`scripts/release/publication.py` refuses to republish a version that already
exists, so this approval is the last point at which a mistaken tag costs
nothing.

Create the environment under **Settings → Environments** before the first tag.
A workflow that names a missing environment creates it without protection
rules, so an unconfigured first tag would publish without waiting. Configure:

- **Required reviewers:** the maintainers who may publish. With a single
  maintainer, leave **Prevent self-review** off; otherwise nobody can approve.
- **Allow administrators to bypass configured protection rules:** off, so an
  administrator's tag waits for approval too.
- **Deployment branches and tags:** selected branches and tags, with one tag
  rule `v*` and no branch rules.
- **Secrets and variables:** none. The workflow uses only `GITHUB_TOKEN`.

Check the result with the `environments/release` reads above: `protection_rules`
names the reviewers, `can_admins_bypass` is `false`, and the only deployment
policy is the `v*` tag pattern. Approve a run under the workflow run's
**Review deployments** prompt, after checking that `verify` passed for the
intended commit and that `advisories` passed on that same tag.

## Actions

- **Fork pull requests.** Require approval before workflows run for pull
  requests from outside contributors. CI uses `pull_request`, not
  `pull_request_target`, and no workflow reads secrets other than
  `GITHUB_TOKEN` (only in `release.yml`).
- **Default token permissions: read-only.** Every workflow already declares
  `permissions: contents: read` at the top level, and
  `scripts/ci/lint_workflows.py` enforces it. Set the repository default to
  read-only as well, and leave "Allow GitHub Actions to create and approve pull
  requests" disabled.
- **Continuous security checks.** Enable secret scanning and push protection
  for this public repository, and verify that they remain enabled.
  Ensure the scheduled source advisory workflow is active on the default branch;
  scheduled runs use only `contents: read` and never publish releases. GitHub can
  disable schedules in inactive public repositories, so monitor workflow failure
  notifications and schedule availability.
- **Allowed actions (optional).** Every external action in `.github/workflows/`
  is pinned to a full commit SHA. If you want GitHub to enforce this, restrict
  allowed actions and require full-SHA pinning.
- **Runners.** Every job uses the standard `ubuntu-24.04` GitHub-hosted
  runner, which is free for public repositories. `scripts/ci/lint_workflows.py`
  rejects `-latest` labels, so a runner image changes only in a reviewed pull
  request. Larger runners are billed even for public repositories; do not
  select them. Do not attach self-hosted runners: pull requests from forks
  would run on them.
- **Artifact attestations.** `release.yml`'s `publish-metadata` job uses
  `id-token: write` and `attestations: write` with
  `actions/attest-build-provenance` and `actions/attest-sbom`.

## Container packages

`scripts/release/publication.py` publishes `romd-admin`, `romd-consumer`,
`romd-worker`, `romd-player` and `romd-postgres` under `ghcr.io/<owner>`. The owner comes from
`$GITHUB_REPOSITORY`, so here it is `ghcr.io/moonlarkstudios`. Operators pull
these images when they install a deployment bundle (see the
[deployment runbook](../self-hosting/production-deployment.md)), so release
packages must be public.

Every image carries an `org.opencontainers.image.source` label naming this
repository, and the publisher refuses an image whose label names another one.
GHCR uses the label to link a new package to the repository, which gives the
release workflow access to it. An existing package with one of these names that
this repository did not create gives the workflow no access: delete it, or
grant this repository access under the package's **Manage Actions access**,
before the first tag.

GHCR creates new packages as private. Both downloaded-installation qualification
and final-digest security scans require anonymous image pulls, so a new private
package blocks qualification and `publish-complete`. This applies when adding a
new image role as well as publishing the first release. Make new packages public
after publication, before qualification pulls them; if a gate has already failed,
treat that release as a bootstrap candidate:

1. Push the first `-rc.1` tag, approve its `publish` job in the release
   environment, and let it publish all five packages.
2. Confirm that each package is linked to this repository, then make any new
   packages public.
3. Pull every image digest in that release's `images.env` without credentials:

   ```bash
   DOCKER_CONFIG="$(mktemp -d)" sh -c "grep -E '^ROMD_(ADMIN|CONSUMER|WORKER|PLAYER|POSTGRES)_IMAGE=' images.env | cut -d= -f2 | xargs -n1 docker pull --platform linux/amd64"
   ```

4. Leave the bootstrap release as an unqualified prerelease and push the next
   `-rc` tag for a complete run. See
   [versions and promotion](release-readiness.md#versions-and-promotion).

## Private vulnerability reporting

[SECURITY.md](../../SECURITY.md) and `.github/ISSUE_TEMPLATE/config.yml` direct
reporters to `https://github.com/MoonlarkStudios/romd/security/advisories/new`.
Keep reporting enabled and verify delivery after maintainer or notification
changes:

1. Confirm that private vulnerability reporting is enabled.
2. Have each maintainer who receives reports watch the repository (all
   activity, or at least security alerts) and check that their notification
   delivery works. See GitHub's
   [vulnerability reporting configuration](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository).
3. Submit a harmless test report, with no real vulnerability or credential.
   Confirm that maintainers receive it, then close it.

## Dependency updates

`.github/renovate.json` configures Renovate. It does nothing until a maintainer
installs the Renovate GitHub App on this repository. The
[dependency update guide](dependency-updates.md) covers validation and limits.
