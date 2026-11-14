# Release readiness

Use this checklist to assemble evidence for one release candidate. It does not
record a release pass. Current source and tests establish implementation;
accepted decisions establish intent; historical results qualify only their
recorded revision, artifacts, environment, and scope.

## Candidate and evidence record

Keep the release record in the pull request or private maintainer storage. Name
an accountable owner for each gate and record:

- full source commit, tag, and clean/dirty state; for packaged checks, the exact
  downloaded bundle SHA-256 and published image digest map;
- command and working directory, tool versions, OS/architecture, date, and any
  required runtime, device, or dataset identity;
- status (`passed`, `failed`, `not run`, or `not applicable` with a reason),
  counts/skips, evidence location, limitations, and remaining action;
- reviewer and maintainer disposition, including any exception permitted by the
  gate's owning policy.

Do not carry a pass to changed inputs without checking its scope and rerunning
the affected gate. Keep credentials, ROM/BIOS inventories, backups, and raw
sensitive diagnostics private. Commit maintained conclusions and links rather
than dated receipts or agent transcripts.

## Versions and promotion

- Before cutting a tag, require green PR CI, including the native Docker
  [packaged runtime/security rehearsal](packaged-release-qualification.md#before-publication),
  and normal review/merge. The publisher rehearses its exact built images again
  before any public writes; downloaded-artifact qualification remains required.
- Tag releases with a strict `v<SemVer>` on the current `main` tip. Each tag
  publishes its own immutable images, bundle and GitHub release; nothing
  replaces an existing version. Publication waits for a maintainer to approve
  the `publish` job in the protected
  [release environment](repository-settings.md#release-environment) after the
  tag's CI and source dependency advisory runs pass.
- Exercise a release with `-rc.N` tags. A failed candidate stays an unqualified
  prerelease, and the next attempt takes the next number. When a candidate
  passes every gate, keep `main` frozen and tag the final version on the same
  commit; that run's artifacts are the release.
- Promote by marking that same qualified release as the latest full release,
  replacing the workflow's qualification note with the release notes:
  `gh release edit vX.Y.Z --repo MoonlarkStudios/romd --prerelease=false --latest --notes-file NOTES.md`.
  Never retag, copy or rebuild images to promote: the bundle manifest records
  its tag, and the attestations bind its exact digests.
- Publish version tags only. There are no `latest` or minor-version image tags:
  operators install a bundle whose `images.env` pins digests, and worker-first
  migration cannot be left to a tag-following updater.
- Linux amd64 is the only published platform. The console and mount clients
  are source-only and experimental.
- Before 1.0, a release may require a fresh installation. Its release notes
  state whether upgrading from the previous release is supported, and the
  upgrade gate below applies only to a supported path.

## Public downloads and release page

A qualified release has five uploaded assets:

| Download | Purpose |
| --- | --- |
| `romd-vX.Y.Z-deployment.tar.gz` | Operator installation: Compose, pinned images and operational tools |
| `romd-vX.Y.Z-deployment.tar.gz.sha256` | Deployment archive integrity |
| `romd-vX.Y.Z-verification.tar.gz` | Complete SBOMs, scans, policy results, attestations, publisher identities and sanitized packaged runtime evidence |
| `romd-vX.Y.Z-verification.tar.gz.sha256` | Verification archive integrity |
| Reviewed MAME source archive | Corresponding GPL source for the worker's CHD tooling |

GitHub also provides its automatic source-code downloads. The original JSON and
JSONL evidence stays intact inside the verification archive; it is not removed
or replaced by a summary. The archive checksum and member manifest provide
transport integrity, while the existing signed metadata and attestation checks
establish the expected source and subjects. Follow
[public verification](release-metadata.md#attestation-and-anonymous-verification).

`scripts/release/release_notes.py` renders the page with prominent deployment
and checksum links, installation and recovery guidance, and the supported
platform and upgrade boundary. Initial notes clearly identify an unqualified
prerelease. Completion attaches the verification archive and marks the notes
qualified only after actual public downloads, runtime/security/provenance
verification and both tamper controls pass. Keep the maintainer promotion
checkpoint separate. Update the release notes with the accepted scope and
achieved evidence before stable promotion.

## Source checks available in a development environment

Use the pinned toolchain and prerequisites in [development setup](../development/getting-started.md).
Run checks for the affected surfaces on the integrated candidate; a narrow test
run does not establish a full-suite pass. These commands exercise source and
harness contracts; they do not qualify downloaded release artifacts.

| Scope and working directory | Checks | Acceptance and owner |
| --- | --- | --- |
| Instructions and documentation routing, root | `mise run agents:check` | Valid instruction/index links and registered validation routing; [documentation guidance](../AGENTS.md) |
| Backend and contracts, root | `mise run test`, `mise run contracts:check`; `mise run test:integration` for host/integration behavior | Deterministic behavior, contracts, and required real-host fixtures pass; [backend guidance](../../src/AGENTS.md), [split-host topology](../architecture/split-host-topology.md) |
| Web, `web/` | `pnpm lint`, `pnpm test`, `pnpm build` | Admin, consumer, player, and generated-client changes pass their applicable checks; [web guidance](../../web/AGENTS.md) |
| Console, `clients/romd_console/` | `mise run analyze`, `mise run test` | Analysis and applicable tests pass on the claimed environment; [console guidance](../../clients/romd_console/AGENTS.md) |
| Release and security tooling, root | `mise run release:check`, `mise run security:check` | Bundle, browser-harness, scanner, inventory, and policy contract checks pass; [packaged qualification](packaged-release-qualification.md), [release metadata](release-metadata.md) |

Check [known issues](../development/known-issues.md) before interpreting an unexpected failure.
Hosted CI runs the registered source checks for
every pull request and `main`; cite that run, and add local evidence only for
environments it does not cover.

For large-catalog changes, also retain representative cardinality and workload,
peak/retained memory, query counts, returned row counts, and latency where
relevant. Demonstrate bounded processing, cancellation/failure cleanup, and
complete pagination without omissions or duplicates. Measure aggregate-query
tradeoffs before choosing query shapes; a test asserting a batch size alone is
not a memory or performance measurement.

## Candidate and publication gates

These gates remain separate even when all source checks pass. Follow the linked
owner for procedures and prerequisites rather than substituting a source smoke
or a synthetic policy test.

| Gate | Acceptance | Owning guidance |
| --- | --- | --- |
| API compatibility boundary | Before the first console build distributed independently of a server release, or 1.0, whichever comes first, maintainers record a compatibility decision defining supported server/console version skew; updating repository call sites does not update installed console builds | [API compatibility policy](../decisions/api-contract-policy.md#first-party-audience-and-repo-atomic-compatibility) |
| Immutable candidate | Intended clean `main` revision and strict version tag; independently retained publisher bundle hash and image digests; no replacement of existing assets | [Maintainer release candidates](../self-hosting/production-deployment.md#maintainer-release-candidates) |
| Pre-publication packaged rehearsal | Required PR CI and exact publisher build pass image/SBOM/bundle security policy, the extracted package's browser/restart/backup/restore suite and both real fault controls, using disposable registry digests; publication validates the bound runtime/security receipt before public writes | [Packaged rehearsal](packaged-release-qualification.md#before-publication) |
| Downloaded Linux amd64 installation | Exact downloaded candidate passes fresh install, authenticated portal/browser playback, worker-first restart, backup/restore with identity/content preservation, and both required injected-failure runs | [Packaged release qualification](packaged-release-qualification.md) |
| Supported upgrade or approved reset baseline | Named starting release, schema, and content format upgrade to the candidate with worker-first migration/readiness, preserved identity/content, and demonstrated recovery using the matching pre-upgrade backup and previous release; unsupported starting formats have an explicit maintainer-approved fresh-install/reset baseline | [Upgrade](../self-hosting/production-deployment.md#upgrade) |
| Final-artifact security and provenance | Actual final-digest SBOMs/scans satisfy policy; exact bundle and metadata have verified provenance; anonymous public downloads and tamper rejection pass | [Release metadata and vulnerability gate](release-metadata.md) |
| Native console support | Exact packaged console build and emulator/core versions pass the claimed OS/architecture/controller matrix, including install interruption, launch/exit/focus, reconnect, profile/instance save isolation, and offline use | [Console setup](../../clients/romd_console/README.md), [runtime pattern](../../clients/romd_console/docs/emulator-runtime-pattern.md), [controller roadmap](../../clients/romd_console/docs/roadmaps/controller-identity.md), [Dolphin scope](../../clients/romd_console/docs/roadmaps/dolphin-runtime.md) |
| Promotion | Maintainer accepts the evidence, support matrix, known limitations, recovery instructions, and release notes; candidate remains a prerelease until that decision, then is promoted in place as described in [versions and promotion](#versions-and-promotion) | [Production deployment](../self-hosting/production-deployment.md#maintainer-release-candidates) |

A same-candidate backup/restore run does not establish supported-version upgrade
qualification. Retain the starting release's bundle/image identities, schema and
content format alongside the target candidate's evidence. Follow the upgrade
runbook's compatibility/reset policy and recovery procedure; do not treat older
images against an already migrated schema as rollback evidence.

The packaged harness needs a disposable native Linux amd64 Docker runner and
published candidate subjects. Public attestation generation/verification also
needs the trusted publisher workflow and anonymous registry/release access.
A private checkout and local contract tests cannot establish those public gates.

Native console acceptance needs the claimed OS SDKs, physical controllers,
provisioned emulator versions, and owner hardware runs. A checked-in runner or
runtime profile does not establish support. Retain older hardware results as
historical; advertise only combinations accepted for the candidate, and keep
native binaries experimental until their applicable acceptance is complete.
Source and asset changes still need the applicable
[privacy and provenance review](source-review.md); an automated secret scan
cannot establish redistribution rights. Maintain the hosted protections and
reporting channel in [repository settings](repository-settings.md).
