# Release metadata and vulnerability gate

The release gate generates SBOMs and vulnerability results for the **final
published image digests** and the downloaded deployment bundle. The same generator
and policy also run on the exact built images and bundle in the mandatory
[pre-publication rehearsal](packaged-release-qualification.md#before-publication).
That evidence is marked `disposable-rehearsal` and bound into the publisher's
receipt. It cannot become public qualification or attestation evidence. The
independent final-digest gate runs after
packaged installation qualification and before final publication. A local policy
test, source lockfile scan, or successful tool download is not release evidence.
Provenance identifies the producing workflow and source; it does not establish
that software has no vulnerabilities.

## Runner and pinned tools

Use a disposable native Linux amd64 runner with Python 3.11+, Docker, network
access to the public image registry and vulnerability database, and a GitHub CLI
supporting artifact attestations. `scripts/security/tools.json` pins official
Syft and Trivy release archives by version, HTTPS URL and SHA-256. The installer
downloads no install script, checks archive hashes and ELF architecture, and
extracts only the named executable. Generation rechecks the archives and exact
installed executable bytes before running them.

```bash
python3 scripts/security/tools.py --output .artifacts/security/UNIQUE_RUN/tools
python3 scripts/security/metadata.py \
  --bundle /absolute/download/romd-v1.2.3-rc.1-deployment.tar.gz \
  --checksum /absolute/download/romd-v1.2.3-rc.1-deployment.tar.gz.sha256 \
  --images /absolute/publisher-evidence/images.env \
  --publisher-state /absolute/publisher-evidence/candidate.json \
  --revision FULL_40_CHARACTER_SOURCE_COMMIT \
  --tag v1.2.3-rc.1 \
  --tools-dir .artifacts/security/UNIQUE_RUN/tools \
  --output .artifacts/security/UNIQUE_RUN/metadata
```

Both output directories must be new. `--publisher-state` and `--images` must
come from the publisher-subjects artifact retained by the **same workflow run**
that produced and qualified the candidate. Do not obtain this trust input from
the release assets being checked. Before extracting the download or launching
any scanner, generation uses the packaged qualification verifier to compare the
bundle's exact SHA-256, image map, revision and tag with that independent
publisher state. It then verifies the archive, manifest, file hashes and image
map. A replaced archive with a rewritten valid manifest and checksum still
fails against the original publisher state. It
uses empty tool configuration, an empty ignore file, fresh vulnerability cache,
and explicit empty Docker and Podman authentication files. The child environment
uses an allowlist, discards the caller's home and cloud/token/config variables,
and redirects XDG/GitHub/registry configuration to private empty directories. It never sets
`--ignore-unfixed`, filters out severity levels, or accepts scanner errors. The
database's update timestamp must be within 72 hours and not implausibly in the
future; its version metadata is retained. An end-of-support OS reported by the
scanner blocks the release.

Refreshing tools requires a reviewed change to the exact version, official
asset URL and SHA-256 in `tools.json`. Confirm the checksum against the official
publisher's release metadata, run the deterministic tests, then exercise actual
image/SBOM scans and public verification in a disposable release. A successful
checksum verifies selected bytes; review of their publisher and update is still
necessary. The relevant upstream command references are
[Syft](https://github.com/anchore/syft),
[Trivy image scanning](https://trivy.dev/docs/latest/references/configuration/cli/trivy_image/),
and [Trivy SBOM scanning](https://trivy.dev/docs/latest/references/configuration/cli/trivy_sbom/).

## What the inventory covers

Service the build SDK and ASP.NET runtime together. The qualification tool's
published reference assemblies are shipped image contents even when NuGet
lockfiles do not list them. Before any restore, the portable/Linux lock gate
compares the selected SDK's own reference-pack and runtime servicing metadata
with the Docker runtime pin. Every CI SDK installation reads `global.json`
instead of maintaining a separate step version. Renovate groups SDK/runtime servicing proposals
without the weekly schedule delay; automerge remains disabled. Update the native
builder's SDK archive checksums from Microsoft's release metadata in the same
review, then verify the actual published tool and full image scans. Updating
only the runtime does not service SDK-supplied reference assemblies.

Syft inventories the published images directly by registry digest. It must find
OS packages in each image and actual backend dependencies in server images.
The catalog reader's Go dependencies come from its packaged Go binary.

Compiled frontend bundles discard package manifests, so the image build also
generates an inventory from each app's **installed production dependency tree**
after the frozen install. The build runs the following for each app, selecting
`admin`, `consumer`, or `player` and the matching app filter:

```bash
pnpm --filter romd-admin-app list --prod --depth Infinity --json > /tmp/admin-deps.json
node /tmp/frontend_inventory.mjs --input /tmp/admin-deps.json \
  --application admin --output /out/admin-frontend.cdx.json
```

`/tmp/frontend_inventory.mjs` is copied from
`scripts/security/frontend_inventory.mjs`. The player invocation also supplies
`--emulatorjs-pin /web/tools/emulatorjs/pin.json`. Each final frontend image must
carry its inventory at `/usr/share/romd/sbom/frontend.cdx.json`. The closure is
conservative: installed production dependencies may include modules removed by
tree shaking. Build-only dev dependencies and host paths are excluded. The
EmulatorJS component is explicitly marked as a pinned **external browser
runtime**, because the current player downloads it rather than embedding it.

Admin and worker images must carry the catalog reader receipt at
`/usr/share/romd/sbom/catalog-reader.json`, containing `schemaVersion: 1`, its
module name, full `sourceCommit`, `binarySha256`, and `goVersion`. The gate
extracts the receipt and `/app/catalog-reader` from a stopped container created
from the exact final digest and compares the actual binary hash. This preserves
the companion commit even when Go's main module version is `(devel)`.

The same extraction verifies libchdr's packaged build receipt and actual shared
library in all server images, plus chdman's receipt and executable in the worker.
Only source identity, versions and hashes enter the supplemental SBOM; private
builder paths do not. The worker also carries its reviewed
`/app/native/chdman/source-inputs.json` inventory. Its hash is retained for
traceability. Receipts and inventory are component evidence, not native runtime
qualification. Upstream C/C++ source components and statically linked libraries
may lack vulnerability database identifiers; retain the source inventory and
notices for maintainer review rather than claiming scanner completeness.

The gate merges these image-carried components into the image's CycloneDX SBOM
while preserving the discovered graph. It scans the actual image and merged
SBOM separately. Every image and image-SBOM scan must inventory OS packages.
Applicable backend scans require Npgsql and EF Core, portal SBOM scans require
React/Mantine/OIDC, and the player's conservative production closure requires
React/Mantine. Admin and worker scans must inventory actual linked Sigstore and
TUF Go dependencies; a receipt identifying only the reader's main module cannot
satisfy that check. SBOM coverage also requires the native components where
shipped. An empty scan or missing expected ecosystem cannot become a pass.

The bundle SBOM inventories extracted deployment files and references all five
digest-pinned runtime images. Frontend tooling is not installed into the bundle.
Original license notices stay in the images and bundle; SBOMs do not replace
those notices.

## Upstream PostgreSQL maintenance

Use the latest available supported PostgreSQL 18 Bookworm Docker Official Image,
pinned by version and digest in `scripts/deployment/base-images.json`. Preserve
its upstream entrypoint and initialization behavior. Renovate proposes image
updates separately without the weekly schedule restriction; review upstream
release/security notes and run deployment/recovery qualification before adopting
each update. ROMD does not build or maintain a patched PostgreSQL image.

PostgreSQL image and SBOM scans retain every finding with `result: reported` and
`enforcement: upstream-managed`. These reports inform upstream maintenance;
they do not require a ROMD per-CVE ledger or imply that the image is free of
vulnerabilities. Scanner errors, missing inventory, an unsupported OS and failed
runtime/recovery qualification still block. This reporting boundary applies only
to the Docker Official PostgreSQL image. ROMD-owned images and the deployment
bundle retain the severity gate below.

## Severity and exception policy

`scripts/security/policy.json` blocks **HIGH**, **CRITICAL**, and **UNKNOWN**
findings in ROMD-owned images and the deployment bundle, including findings with
no available fix. LOW and MEDIUM findings are
retained for triage. Scanner results are collected with `--exit-code 0` so the
policy evaluator can assess every finding; the overall metadata command exits
nonzero for blocked findings, malformed results, missing coverage or tool errors.
This does not convert findings into a release pass.

An exception requires a reviewed policy change identifying all of:

- the exact vulnerability ID;
- exact package PURL, or the scanner ecosystem/package identity when no PURL exists;
- installed package version and exact affected image/bundle `sha256:` digest;
- a specific rationale and accountable owner;
- an exclusive UTC expiry date, no more than 90 days away.

Wildcards, blanket directories/packages, mutable tags, absent owners, expired
exceptions and severity-policy weakening are rejected. An exception for one
version or image digest does not apply to a rebuild or upgrade. Renew through
review with current evidence; remove resolved or stale entries. The default
exception set is empty. Findings and proposed sensitive dispositions stay out of
public Git history until their maintainer publication review.

## Attestation and anonymous verification

The trusted publishing job must generate provenance for each final OCI digest,
the deployment bundle, and `release-metadata.json`. It must also attest each
subject's matching CycloneDX SBOM with predicate type
`https://cyclonedx.org/bom`. Attestation write and OIDC permissions belong only
to that job. Publish the complete attestation bundles as downloadable JSONL,
alongside the metadata files and checksummed deployment archive. Do not promote
a prerelease before generation, policy checks and verification succeed.

The PostgreSQL subject records the official upstream digest selected and
qualified by ROMD's workflow. Its inventory identifies the PostgreSQL 18 server
and OS packages. It does not establish that ROMD built PostgreSQL or attest the
upstream publisher's build process. Retain the upstream component/source
inventory and distinguish that qualification attestation from upstream origin.

Download these public assets into a clean directory. Supply the intended source
commit, tag, repository and exact trusted signer workflow independently of the
downloaded metadata:

```bash
python3 scripts/security/verify.py \
  --metadata-dir /absolute/download/metadata \
  --bundle /absolute/download/romd-v1.2.3-rc.1-deployment.tar.gz \
  --attestations /absolute/download/attestations.jsonl \
  --repository OWNER/REPOSITORY \
  --revision FULL_40_CHARACTER_SOURCE_COMMIT \
  --tag v1.2.3-rc.1 \
  --signer-workflow OWNER/REPOSITORY/.github/workflows/release.yml
```

The verifier checks the signed metadata index's file hashes, expected source
revision/tag, complete image/bundle subject inventory and the downloaded bundle
hash. It independently verifies the tar manifest and `images.env` select exactly
the indexed image subjects. Internally consistent hashes or independently valid
signatures cannot substitute different images for those selected by the bundle.
It invokes GitHub's cryptographic verifier for provenance and SBOM
attestations, enforcing repository, source digest/ref and signer workflow and
rejecting self-hosted signing runners. It compares every downloaded SBOM with
the verified attestation predicate, not merely with an unsigned JSON statement.
See [GitHub CLI attestation verification](https://cli.github.com/manual/gh_attestation_verify).

The verifier clears GitHub tokens, local GitHub configuration and registry
credentials. Public images and attestations must actually support anonymous
verification; YAML alone does not establish that access. A private registry
requiring credentials fails this public-release gate. Offline attestation
bundles avoid authenticated GitHub API lookup; trust-root fetching and public
OCI manifest access may still need network connectivity.

The final publishing job first verifies its trusted current-run inputs and uploads
metadata while the candidate remains unqualified. It then downloads the actual
GitHub release bundle/checksum, metadata index and indexed files, and attestation
JSONL into a new directory without authentication, cookies or inherited proxies.
Only HTTPS redirects to the explicit GitHub release-asset hosts are accepted.
Every byte count and SHA-256 must match the trusted current-run inputs. The job
verifies those downloaded files and rejects an altered copy before changing the
qualification notes. Inaccessible or changed public assets block that change;
Actions artifact copies cannot substitute for this round trip. Sanitized download
results are retained even on failure. A partial evidence upload remains an
unqualified candidate requiring maintainer disposition; retries do not replace
existing assets. Production promotion remains a separate checkpoint.

## Validation and evidence

```bash
python3 scripts/security/test_security.py
node --test scripts/security/*.test.mjs
```

These deterministic tests reject synthetic high/critical/unknown findings,
unfixed findings, broad/expired/mismatched exceptions, incomplete scan coverage,
stale databases, binary/receipt mismatches, changed metadata/bundles and SBOMs
that differ from verified predicates. They also check the installed frontend
dependency closure and external runtime distinction.

Release evidence additionally requires actual final-digest generation, inspection
of expected components, anonymous verification from a clean environment, and a
tampered downloaded bundle failing verification. Preserve the exact commit,
tool/database versions, commands, environment, output file hashes and artifact
locations. `release-metadata.json` records those commands and subject bindings;
the sibling files retain SBOMs, scans and policy decisions. Keep local synthetic
tests distinct from hosted and disposable-release results. Do not claim PR 14
qualification from tests or documentation alone.
