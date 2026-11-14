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
Syft, Trivy and crane release archives by version, HTTPS URL and SHA-256.
`scripts/security/tools.py` installs the scanners by default, or any subset
with `--tools`. The installer downloads no install script, checks archive
hashes and ELF architecture, and extracts only the named executable. Every
consumer rechecks the archive and exact installed executable bytes before
running it.

The release jobs read and tag GHCR images only through
[crane](https://github.com/google/go-containerregistry/tree/main/cmd/crane)
(`scripts/release/crane.py`). It logs in with the job's `GITHUB_TOKEN` over
stdin, into a private, temporary Docker config.

- **Absence:** a reference counts as absent only when the exact manifest
  endpoint returns GHCR's `MANIFEST_UNKNOWN` or `NAME_UNKNOWN`. Credential,
  token, transport and tool errors stop the release instead.
- **Tagging:** `crane tag` stores the manifest bytes already held under the
  qualified digest, and every new tag is read back to confirm it names exactly
  that digest.
- **Provenance:** upstream publishes SLSA provenance (`multiple.intoto.jsonl`)
  with each crane release. Review it alongside the checksum when updating the
  pin.

ROMD deliberately does not install scanners with `aquasecurity/trivy-action`,
`setup-trivy` or `anchore/sbom-action`. In March 2026 attackers force-pushed
almost every `trivy-action` and `setup-trivy` tag and published a malicious
Trivy v0.69.4 binary
([Aqua advisory](https://www.aquasec.com/blog/trivy-supply-chain-attack-what-you-need-to-know/)).
Those actions fetch the binary by version and trust checksums served from the
same release; an independent SHA-256 reviewed in this repository does not.
Everything after installation uses Trivy's own features: its ignore file,
VEX, severity filter and exit code.

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
uses empty tool configuration, the reviewed exception file
`scripts/security/trivyignore.yaml` (validated before any scan), fresh
vulnerability cache, and explicit empty Docker and Podman authentication files. The child environment
uses an allowlist, discards the caller's home and cloud/token/config variables,
and redirects XDG/GitHub/registry configuration to private empty directories. It never sets
`--ignore-unfixed`, filters out severity levels, or accepts scanner errors. The
database's update timestamp must be within 72 hours and not implausibly in the
future; its version metadata is retained. An end-of-support OS reported by the
scanner blocks the release.

Refreshing tools requires a reviewed change to the exact version, official
asset URL and SHA-256 in `tools.json`, or to the govulncheck pins in the
Dockerfile. Do not adopt a release until it has been public for at least 14
days. A checksum taken during a compromise only records the malicious file:
the malicious Trivy v0.69.4 release was live for about three hours before it
was pulled. The waiting period lets the publisher and the wider community find
and withdraw a bad release before ROMD pins it. Renovate does not manage these
pins, so the reviewer checks the release date. Then confirm the checksum
against the official publisher's release metadata, run the deterministic
tests, and exercise actual image/SBOM scans and public verification in a
disposable release. A successful checksum verifies selected bytes; review of
their publisher and update is still necessary. The relevant upstream command references are
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
The catalog reader's Go dependencies come from its packaged Go binary; see
[Catalog reader applicability](#catalog-reader-applicability).

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
Syft can omit the optional CycloneDX `components` field for this package-free
payload; the merger adds the five reviewed image references and rejects an empty
merged inventory. Trivy likewise omits an empty `Results` array for a package-free
SBOM. The parser requires its complete versioned CycloneDX report envelope; image
OS and ecosystem coverage checks still reject such an empty image scan.
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

## Catalog reader applicability

The catalog reader is ROMD's only Go binary. Trivy reports every advisory for
each module and standard library version linked into it, including advisories
whose vulnerable code the reader does not contain. For admin and worker images,
the gate extracts the shipped `/app/catalog-reader` and runs the pinned
govulncheck on it in binary mode with OpenVEX output (`scripts/security/go_vex.py`).
govulncheck checks the binary's actual symbols against the official Go
vulnerability database and marks absent code `not_affected` /
`vulnerable_code_not_present`. Both Trivy scans of that image take this
document through `--vex`, which suppresses exactly those findings and records
them under `ExperimentalModifiedFindings` (`--show-suppressed`).

- Any other govulncheck status, such as `affected`, blocks the release whatever
  the advisory's severity.
- The image scan must find exactly one Go binary, at `app/catalog-reader`, so
  the VEX cannot describe any other binary.
- Trivy matches a statement only by its exact package URL and version. The
  gate rewrites govulncheck's "Unknown Product" and percent-encoded module
  paths into the plain package URLs Trivy compares; it changes no status.
- govulncheck is built in the Dockerfile's qualification-only
  `govulncheck-build` stage, from module sums verified by the Go checksum
  database and the pins in `tools.json`. It never enters a runtime image.

Each admin and worker subject retains its `<role>.vex.json`. The VEX is
applicability evidence for one binary, not a risk exception: it never applies
to other images, and a rebuilt reader gets a fresh analysis.

## Severity and exception policy

Trivy enforces the gate. After each ROMD image, image SBOM and bundle scan,
`trivy convert --severity UNKNOWN,HIGH,CRITICAL --exit-code 3` fails for any
remaining **UNKNOWN**, **HIGH** or **CRITICAL** finding, including findings with
no available fix. The distinct exit code separates blocked findings from
scanner errors, which also fail. LOW and MEDIUM findings stay in the retained
JSON reports for triage. Scans never set `--ignore-unfixed` or filter severities.
Each subject's `<role>.policy.json` records every scan's result and the findings
it suppressed, with the reason.

Exceptions live in `scripts/security/trivyignore.yaml`, Trivy's YAML ignore
format written in JSON syntax, so the gate validates it without a YAML parser.
Each entry needs exactly:

```json
{
  "vulnerabilities": [
    {
      "id": "CVE-2026-12345",
      "purls": ["pkg:deb/ubuntu/example@1.2.3-1ubuntu1?arch=amd64&distro=ubuntu-24.04"],
      "statement": "Specific, reviewable reason the finding cannot affect ROMD.",
      "owner": "accountable-maintainer",
      "expired_at": "2027-01-31T00:00:00Z"
    }
  ]
}
```

`scripts/security/policy.py` rejects wildcards, unversioned package URLs, path
exceptions, missing owners or statements, and any expiry that has passed or is
more than 90 days away. Trivy stops applying an entry when it expires. An
exception matches only that vulnerability in that exact package version, so an
upgrade or rebuild with a different version needs a fresh review. It is not
bound to one image digest. Renew through review with
current evidence, and remove resolved or stale entries. The default exception
set is empty. Findings and proposed sensitive dispositions stay out of public
Git history until their maintainer publication review.

## Attestation and public verification

The `publish-metadata` job attests provenance for each final ROMD image digest,
the official PostgreSQL digest and the deployment bundle. It also attests each
subject's matching CycloneDX SBOM with predicate type
`https://cyclonedx.org/bom`. Attestation write and OIDC permissions belong only
to that job. Completion merges the attestation bundles into one public asset,
`romd-vX.Y.Z-attestations.jsonl`. Generated SBOMs, scans, policy results and
`release-metadata.json` stay in the run's `release-metadata-*` artifact for
maintainer review; they are not release assets. A scan is a point-in-time
result, and operators can rescan the attested digests themselves.

The PostgreSQL subject records the official upstream digest selected and
qualified by ROMD's workflow. Its inventory identifies the PostgreSQL 18 server
and OS packages. It does not establish that ROMD built PostgreSQL or attest the
upstream publisher's build process.

### Verify a release

Anyone can verify a release with the [GitHub CLI](https://cli.github.com/manual/gh_attestation_verify)
and no GitHub login. Download the bundle, its checksum and the attestations
file, then run:

```bash
sha256sum --check romd-v1.2.3-deployment.tar.gz.sha256
gh attestation verify romd-v1.2.3-deployment.tar.gz \
  --bundle romd-v1.2.3-attestations.jsonl --repo MoonlarkStudios/romd \
  --signer-workflow MoonlarkStudios/romd/.github/workflows/release.yml --source-ref refs/tags/v1.2.3
# For each image reference in the extracted bundle's images.env:
gh attestation verify oci://ghcr.io/moonlarkstudios/romd-admin@sha256:DIGEST \
  --bundle romd-v1.2.3-attestations.jsonl --repo MoonlarkStudios/romd \
  --signer-workflow MoonlarkStudios/romd/.github/workflows/release.yml --source-ref refs/tags/v1.2.3
```

Add `--predicate-type https://cyclonedx.org/bom` to check a subject's SBOM
attestation. The verified bundle's `images.env` pins every image by digest, so
the bundle's provenance also authenticates the images it installs. With a
GitHub login, `gh release verify-asset v1.2.3 romd-v1.2.3-deployment.tar.gz`
additionally checks the file against GitHub's immutable release attestation.

From a source checkout, `scripts/release/verify_release.py` runs all of these
checks: the bundle checksum, manifest and image map, followed by provenance and
SBOM verification of the bundle and all five images. It runs `gh` with no
tokens, GitHub configuration or registry credentials, so a private image or
attestation fails it:

```bash
python3 scripts/release/verify_release.py --repository MoonlarkStudios/romd \
  --revision FULL_40_CHARACTER_SOURCE_COMMIT --tag v1.2.3 \
  --bundle romd-v1.2.3-deployment.tar.gz --checksum romd-v1.2.3-deployment.tar.gz.sha256 \
  --images images.env --attestations romd-v1.2.3-attestations.jsonl
```

### Immutable publication

Every release stays a draft until all gates pass. Downstream jobs take the
bundle from the publisher's same-run artifact, which holds the exact bytes
uploaded to the draft. Completion then:

1. checks the packaged qualification reports against the publisher state;
2. runs `verify_release.py` on the bundle, image map and merged attestations;
3. runs `scripts/release/tag_images.py`, which uses crane to add the release's
   version tag to each qualified ROMD image digest. It checks every tag first: a
   tag that names a different digest blocks the release before any write, and
   a tag already on the qualified digest from an interrupted attempt is kept;
4. uploads the attestations file to the draft;
5. publishes the release once, as a prerelease with its qualified notes;
6. runs `scripts/release/confirm_publication.py`, which requires the published
   release to be immutable, to carry exactly the four expected assets, to pass
   `gh release verify`, and to attest each verified local asset through
   `gh release verify-asset`.

[Release immutability](repository-settings.md#release-immutability) must be
enabled. GitHub then locks the tag and assets at publication and signs a
release attestation over them; the title, notes and prerelease/latest flags
remain editable for the maintainer checkpoint. A release published without
immutability fails confirmation and cannot be promoted. A failed gate before
publication leaves only a draft, which maintainers delete or keep for diagnosis.
Production promotion remains a separate checkpoint.

## Validation and evidence

```bash
mise run release:check
mise run security:check
```

These deterministic tests check the gate command and its blocked-versus-error
exit codes, reject broad, unversioned, ownerless, expired and overlong
exceptions, and reject govulncheck statements that do not prove absent code, a
second Go binary under the reader's VEX, incomplete scan coverage, stale
databases, binary/receipt mismatches and changed metadata or bundles. They also
check the installed frontend dependency closure and external runtime
distinction. The Trivy and govulncheck behavior they rely on was checked against
the published v0.1.0-rc.7 images; the first candidate built from this change
exercises it in the release workflow.

Completion tests execute the actual workflow shell steps with controlled
failures. They require runtime evidence and attestation verification to pass
before the attestations upload, and the single publish before confirmation.
Verification tests require provenance and SBOM checks for the bundle and every
image, from the release workflow at the exact tag and commit, without credentials.

Release evidence additionally requires actual final-digest generation, inspection
of expected components and credential-free verification of the published
attestations. Preserve the exact commit,
tool/database versions, commands, environment, output file hashes and artifact
locations. `release-metadata.json` records those commands and subject bindings;
the sibling files in the `release-metadata-*` artifact retain SBOMs, scans and policy decisions. Keep local synthetic
tests distinct from hosted and disposable-release results. Do not claim PR 14
qualification from tests or documentation alone.
