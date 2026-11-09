# Source and asset review

Review new or changed source, fixtures, screenshots, artwork and build inputs
for secrets, privacy and redistribution obligations. Bind the review to the
exact commit/tree and changed objects. A previous review does not cover changed
bytes, even when an asset looks identical.

The initial public-source cutover is complete. Its snapshot and fixture-review
tools are retired; normal changes use pull requests and the existing hosted
gates. Keep the launch audit evidence in private durable storage. Source review
does not replace [packaged release qualification](packaged-release-qualification.md),
[release metadata verification](release-metadata.md) or the
[repository settings](repository-settings.md) baseline.

## Secret checks and coverage

[CI secret checks](continuous-integration.md#secret-checks) scan committed Git
patches using the checksum-pinned Gitleaks installer at
[`scripts/ci/install_scanner.py`](../../scripts/ci/install_scanner.py) and
[`scanner.json`](../../scripts/ci/scanner.json). The pins cover Linux and macOS
on x64 and arm64. Scanner updates require review and the real-scanner regressions
described in the CI guide.

Run `mise run secrets:check` for committed changes since `origin/main`, or
`mise run secrets:check -- --full` for every reachable local ref. These checks
do not inspect uncommitted work or prove binary/archive coverage. Review changed
binary objects and archive contents separately when the patch stream cannot
establish their contents.

`--full` includes every local ref. Migration checkouts can retain private
branches, tags, stashes or backup refs outside public `main`. Use a fresh clone
of the public repository to qualify public history; preserve private refs in
existing checkouts rather than deleting them to obtain a passing scan.

For an archive-aware review, inventory the exact objects, member contents,
decoder/tool versions, hashes and coverage limits in private evidence. Missing
or malformed reports, decoder failures, encrypted or unsupported archives,
unreadable members, depth limits and skipped or oversized blobs leave coverage
gaps. A zero finding count does not resolve an incomplete scan.

Some negative test fixtures intentionally contain malformed archives. Retain
their failed scanner results. Deterministic generator replay or independently
verified decoded payloads can support manual review, but cannot turn a failed
decoder into a successful scan. Recheck changed fixtures, generators, seeds and
decoded members; a manifest's self-declared hashes alone do not establish
completeness or redistribution rights. See the
[known decoder limitation](../development/known-issues.md#gitleaks-archive-decoder-failures-leave-incomplete-scanner-coverage).

Review findings privately. Confirmed secrets require rotation or revocation and
removal; follow [Security](../../SECURITY.md) for reporting. False-positive
exceptions need an exact object/fingerprint, rule, reason, owner and review date.
Preserve the pinned default rules and exact synthetic-fixture exception policy;
do not suppress coverage gaps or add broad directory exclusions.

## Privacy and provenance

Review personal paths, private services, hidden asset payloads, screenshots,
compressed reports, fixtures and notices alongside the visible source changes.
Do not infer redistribution rights from a successful scan, attribution link,
visual approval or an asset's presence in a private source kit.

| Material | Review and retained evidence |
| --- | --- |
| Synthetic archives and media fixtures | Exact objects, generator/seed bindings, independent payload inspection and any unresolved coverage limits |
| Project artwork | Authority for the selected originals and approval of the exact application subset for public redistribution |
| Third-party marks and screenshots | Exact-file provenance, intended use and applicable permission or other basis; provider access and attribution do not relicense visible box art |
| Fonts | Supplied license, copyright notices and redistribution conditions |
| Source and refs | Public author/committer identity and intended commits; do not import private source history or refs |

Approval of project-owned assets does not establish a third party's rights or
authorize disclosure of unrelated private material. Preserve accessible SVG
descriptions, font copyrights and required legal notices. Remove unrelated
hidden payloads and rebind the review after any content change. When a
screenshot's intended use cannot be supported, choose another capture or
original/licensed demonstration content.

Retain the attribution and inventory in
[Third-Party Notices](../../THIRD_PARTY_NOTICES.md), including the content-rating
marks, and preserve component licenses when reviewing platform marks, emulator
cores and fixtures that were not generated.

## External inputs

The build and runtime use the pinned companion catalog reader, signed catalog
site, libchdr submodule, MAME/SDL sources, native tools and EmulatorJS CDN
content. When an input changes, review anonymous access, pinned revisions,
checksums, trust roots and redistribution obligations against its owning build
recipe and notices. A successful URL request does not validate signed catalog
metadata or every dynamically constructed download URL.

## Evidence retention

Keep raw diagnostics and complete review evidence outside Git in private durable
storage. Temporary storage is a working location, not a completed archive.
Record the reviewed commit/tree and object hashes, tool versions, coverage gaps,
finding dispositions, supporting provenance, reviewer and date. Leave missing
decisions pending, and retain failed results alongside later manual review.

Public records contain maintained conclusions or sanitized aggregate summaries,
not raw values or paths that identify private findings. A successful automated
check supplies evidence; it does not establish privacy, asset rights or release
qualification on its own.
