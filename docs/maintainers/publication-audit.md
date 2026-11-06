# Publication audit

The publication audit collects evidence a maintainer uses to decide whether a
tree can be published. Use it on the local repository holding the single
initial commit of `MoonlarkStudios/romd` before the first push. It does not
authorize publication, rotate credentials, establish asset redistribution
rights, or change repository settings. If the tree changes after the audit,
create a new commit and run the audit again.

The [public source cutover checklist](public-source-cutover.md) orders local
preparation, the maintainer's publication decision, destination creation and
post-push CI. It supports preparing a snapshot before the destination exists;
an unavailable private CI run is not substituted for the new repository's gates.

## Prepare the source

The runner mirrors **every ref** in `--source` and scans everything reachable
from them. Point it at the local repository that holds the init commit, and make
sure that repository contains only the refs you intend to push, with no remotes,
remote-tracking refs, stashes, notes or extra tags:

```sh
git -C "$snapshot_repo" for-each-ref --format='%(refname) %(objectname)'
git -C "$snapshot_repo" rev-list --all --count
```

To create that repository from a reviewed commit, reuse the commit's tree so
every path, mode and the `native/libchdr/upstream` submodule gitlink carry over
unchanged (a `git archive` or file copy drops the gitlink). Set the author
identity you intend to publish:

```sh
git init -b main "$snapshot_repo"
git -C "$snapshot_repo" fetch --no-tags "$source_checkout" <reviewed-commit>
tree="$(git -C "$snapshot_repo" rev-parse 'FETCH_HEAD^{tree}')"
commit="$(git -C "$snapshot_repo" commit-tree "$tree" -m 'chore: initial public release')"
git -C "$snapshot_repo" update-ref refs/heads/main "$commit"
rm "$snapshot_repo/.git/FETCH_HEAD"
git -C "$snapshot_repo" reflog expire --expire=now --all
git -C "$snapshot_repo" gc --prune=now
```

For a single-commit publication the first command lists only the branch to
push (for example `refs/heads/main`), and the second prints `1`. Record the
commit ID and its tree ID (`git -C "$snapshot_repo" rev-parse HEAD HEAD^{tree}`).
To confirm the snapshot matches the reviewed source tree, check that this tree
ID equals `git rev-parse <reviewed-commit>^{tree}` in the source checkout. The
two match only if every path, mode, blob and submodule gitlink is identical.

## Run in private storage

Prerequisites: Git, Python 3.10 or later, network access to GitHub releases for
the scanner and, with `--probe-access`, to public upstreams. Gitleaks is pinned
to **8.30.0**. The installer downloads the archive for your platform and checks
it against the reviewed SHA-256 in
[`scanner.json`](../../scripts/publication/scanner.json); Linux and macOS on x64
and arm64 have pins. The pins come from the upstream
[release](https://github.com/gitleaks/gitleaks/releases/tag/v8.30.0) and its
checksum manifest. Updating the scanner is a reviewed change. After one, rerun
the synthetic regressions and a new full audit.

Run these commands from a ROMD checkout. The runner uses that checkout's
`.gitleaks.toml` and records its `HEAD` in `run.json`. Put the evidence in a
private directory outside every Git checkout:

```sh
umask 077
audit_parent="$(mktemp -d "${TMPDIR:-/tmp}/romd-publication.XXXXXXXX")"
python3 scripts/publication/install_scanner.py "$audit_parent/scanner"
python3 scripts/publication/test_audit.py \
  --gitleaks "$audit_parent/scanner/gitleaks" -v
python3 -B -W error -m unittest discover \
  -s scripts/publication -p test_report_contract.py
python3 scripts/publication/audit.py \
  --source "$snapshot_repo" \
  --gitleaks "$audit_parent/scanner/gitleaks" \
  --evidence "$audit_parent/audit" --probe-access
```

`mise run publication:audit -- --source ... --evidence ... --gitleaks ...`
runs the same audit script. `--source` accepts a local repository path or a
remote URL. The runner clones it with `git clone --mirror --no-local` and
rejects a shallow result. Never put credentials in a source URL.

`install_scanner.py` and `--evidence` both refuse a directory that already
exists. `--evidence` also refuses a directory inside any Git worktree. The
runner creates `0700` directories and `0600` files, keeps raw tool output in
private evidence, and prints only aggregate counts. Treat even redacted scanner
reports as sensitive: filenames, commit messages and private addresses can
identify people. Keep the complete evidence package in approved private durable
storage. `/tmp` is a working location, not that archive.

`--probe-access` makes anonymous HTTPS requests without Git credentials,
cookies or authorization headers. Without the flag, access records are written
as `not-run`.

Requested probes are qualified independently from the secret scans. The runner
records network and HTTP protocol failures, truncated responses, non-200
responses and checksum mismatches, then continues with the remaining probes.
Exception details can identify a local proxy; reports retain the error type
rather than its message. A requested access check that cannot qualify every
input exits `2` after writing the per-probe results and aggregate summary.

## Coverage and results

The runner records every acquired ref and object ID in `refs.json`. Compare it
with the ref check above. With one commit, scanning the history amounts to
scanning that commit's tree, but the runner still applies every pass below.

Scanning uses three separate passes:

- **History text.** Gitleaks scans the Git patch stream, merge diffs included.
  Replacement objects, external diff and text conversion are disabled.
- **Direct blobs.** Git patches omit binary contents. This pass scans every
  unique nonarchive blob up to 64 MiB from a private file named by its object
  ID. Larger blobs are recorded as coverage gaps that need manual inspection,
  and they cannot produce a clean pass.
- **Archives.** Archives are recognized by filename or file signature. ZIP and
  tar members are listed without extracting paths. Each archive is scanned in
  its own Gitleaks process with archive and decode depth five, so a decoder
  crash on one archive leaves the other results intact.

A clean pass requires zero exits and zero findings in all three passes, with
every expected report parsed as a JSON finding list. Several conditions leave
the scan incomplete:

- a missing or malformed report;
- a decoder or scanner error;
- an unreadable or encrypted archive;
- a direct blob that was skipped;
- a blob too large for the privacy text review.

Unsupported formats, nested archives deeper than the limit, malformed media and
large blobs need explicit manual inspection. The archive inventory does not
prove that all of an archive's contents were readable. The report-contract
regressions also run in `mise run agents:check` and in the required
agent-infrastructure CI job.

Nothing in the audited repository can suppress a finding: there are no ROMD
directory allowlists or baselines, and the runner ignores inline
`gitleaks:allow` comments and repository `.gitleaksignore` files. The pinned
scanner's default rules stay enabled. A false-positive exception must name an
exact fingerprint or value, the rule, a reason, an owner and a review date.
Broad exclusions of source or evidence are prohibited. Rotate any confirmed
secret, then remove it from the tree and create a new init commit before
publication.

Exit status:

| Status | Meaning |
| --- | --- |
| `0` | All scanner passes completed with no detected secrets, and requested access probes qualified; manual disposition is still required |
| `1` | All scanner passes completed and at least one reported candidate secret; requested access probes qualified; review findings privately |
| `2` | The audit, a scanner pass or a requested access qualification could not complete; keep the evidence and resolve the problem |

When the run gets as far as writing its summary, `summary.json` records
`scan_completed`, `scan_passed` and `publication_signoff: false`. If the run
fails after creating the evidence directory, for example at clone, it writes
`failure.txt` instead. A rejected evidence path exits `2` without creating
anything. Neither exit `0` nor merged implementation counts as publication
sign-off.

`access_qualification_requested` records whether the caller requested probes.
`access_qualification_passed` is `true` or `false` for requested probes and
`null` when they were not requested. The scanner's `scan_completed` and
`scan_passed` fields describe only scanner coverage and findings; a failed
access check does not rewrite those results or authorize publication.

The private evidence includes:

- `run.json`, `commands.json`, `refs.json`, and scanner logs and reports:
  runner revision and hash, environment, scanner version and hash, config hash,
  commands, and results.
- `historical-blobs.json` and `tracked-trees/`: object sizes and the exact
  tracked file inventory, with modes, for each ref.
- `direct-blobs.json`, `direct-blobs/`, `direct-blob-secrets.json`,
  `direct-blob-scan.log`, and `direct-blob-scan-result.json`: the direct-blob
  pass, with its findings, report status and size-limit gaps.
- `archives-review.json`, `archive-blobs/`, `archive-scans/`, and
  `archive-scan-results.json`: archive member inventories and the findings or
  decoder failures for each archive.
- `privacy-review.json` and `manual-large-blobs.json`: locations of candidate
  personal paths and private network addresses, plus text-review limits. A
  candidate gives a location; it does not claim that the match is private or
  must be removed.
- `assets-review.json`: screenshots, artwork, fonts and content fixtures that
  need a provenance review. Compare it with `THIRD_PARTY_NOTICES.md`, the
  supplied license files and the test-fixture generators.
- `external-inputs-review.json` and `companion-access.json`: build and runtime
  URLs taken from the committed Dockerfile and selected native and browser pin
  files, plus anonymous access and checksum results for pinned Docker
  downloads, the libchdr submodule and its pinned revision, and the catalog site
  and its timestamp metadata.

## Maintainer disposition

### Recheck known archive-fixture evidence

Some negative test fixtures deliberately contain malformed archive structures.
Keep their failed scanner results. A separately verified generator replay or
decoded fixture payload can support manual review; it cannot turn a decoder
failure into a successful scanner pass.

The fixture review helper rechecks the protected evidence for the known fixture
set against an exact source revision. It uses bounded transformations pinned
to the reviewed generator, not execution of code from the audited repository.
It scans the raw fixture bytes and retained verified decoded payloads in an
independent pass. It refuses missing, changed or unaccounted-for inputs and
does not extract hostile archives or download a decoder.

Run from the checkout containing the helper, with private evidence directories
outside every Git worktree:

```sh
python3 scripts/publication/archive_review.py \
  --source "$source_checkout" --revision "$reviewed_commit" \
  --audit-evidence "$audit_evidence" \
  --dispositions "$private_dispositions" \
  --gitleaks "$pinned_scanner" --evidence "$new_private_review"
```

`mise run publication:archive-review -- ...` invokes the same helper. Its
scanner installation must retain the downloaded archive. The helper verifies
those archive bytes against the checked-in pin and compares its bounded regular
scanner member with the executable before running it. Matching editable
receipt hashes alone are insufficient. For an older installation without the
archive, run the pinned installer into a new private directory; do not invent
or overwrite its receipt.

The helper's
`independent_technical_checks_passed` result describes only the supplied fixture
evidence and independent scan. Exit `0` does not mean that the original scanner
completed, that every archive's decoded completeness was proved automatically,
or that the source tree is approved for publication. The original
`scan_completed` and `scan_passed` remain unchanged;
`reviewer_attestation_required` stays `true`,
`technical_manual_coverage_complete` stays `false` and
`publication_signoff` stays `false` in the helper's output. Record a later
maintainer attestation separately; do not rewrite these tool results.

The helper is not a general exception mechanism or a fresh whole-tree secret
scan. A changed fixture, generator, seed or decoded member requires new review.
Retain the original reports, reconstruction checks, decoder provenance and
independent scan together; a manifest's self-declared hashes alone do not prove
decoded completeness or redistribution rights.

### Record decisions by exact scope

Keep the review record private. Bind each decision to the intended source tree,
the exact asset/object or finding fingerprints, its supporting evidence, the
reviewer's identity and review date. Leave pending decisions unset. Do not infer
permission from a successful scan, a filename, an attribution link, visual
approval or an asset's presence in a private source kit.

| Decision | Evidence and maintainer action |
| --- | --- |
| Synthetic archive coverage | Review exact fixture objects, generator/seed bindings and independently scanned payloads; attest to the inspected scope while retaining failed scanner results. |
| Candidate secrets and privacy | Approve or reject each proposed technical classification against its bound objects; false positives need the exact rule/fingerprint, reason, owner and review date. Confirmed secrets require rotation and removal. |
| Project artwork | Confirm authority for the selected originals and approve the exact application subset for public redistribution. A private kit's visual approval does not approve public distribution of the kit or its copies. |
| Third-party marks and screenshots | Record exact-file provenance, intended use and the applicable permission or other basis. Product screenshots may show provider-sourced box art; evaluate that specific use and its conditions rather than imposing a blanket ban. Provider access and attribution do not relicense the artwork. When a particular use cannot be supported, choose another capture or original/licensed demonstration content. |
| Font copies | Verify the supplied license and preserve its notices and conditions; do not replace a documented license with a blanket owner waiver. |
| Public commit identity and refs | Deliberately select the public author/committer name and email, then approve the exact root commit/tree and permitted refs. Do not publish private source history. |
| Evidence retention | Choose approved private durable storage outside Git before publication. Temporary working storage is not a completed archive. |

Approval of project-owned assets cannot establish a third party's rights or
authorize public disclosure of unrelated private source material. Keep accessible
SVG descriptions, font copyrights and required legal notices; remove unrelated
hidden payloads rather than blanket-stripping metadata. Rebind the review after
any content change, including an otherwise visually identical SVG cleanup.

Review candidate secrets privately and record which are confirmed and which are
false positives. Do not publish raw values or the locations that identify them.
Also review personal paths, private services, screenshots, compressed reports,
fixtures and notices. The only public record is a sanitized summary giving the
scanner version, the audited commit and tree IDs, aggregate unresolved counts
and the asset-review disposition.

Native source lives in this repository. The build fetches several external
inputs:

- the pinned catalog reader from `MoonlarkStudios/romd-dat-catalogs`;
- the catalog site from `moonlarkstudios.github.io/romd-dat-data`;
- the libchdr upstream submodule;
- MAME and SDL sources and native build tools.

Check their public access, pinned revisions, checksums, trust root and
redistribution obligations. Automatic URL checks are supporting evidence only:
they do not validate signed catalog metadata or every dynamic Dockerfile URL.
Browser playback also fetches pinned EmulatorJS CDN content, whose core license
limitations are recorded in the notices.

Retain the content-rating authority attributions and inventory in
[Third-Party Notices](../../THIRD_PARTY_NOTICES.md#content-rating-marks).
The maintainer must decide the provenance of platform marks and screenshots
that reproduce third-party artwork, emulator cores, and any media fixture that
was not generated. Keep the existing notice and license files while making that
decision.

The private reporting channel is
[GitHub private vulnerability reporting](https://github.com/MoonlarkStudios/romd/security/advisories/new).
The repository has to exist and be public before the channel can be enabled.
After the first push, the maintainer enables reporting, confirms that the
private form works and has the right access, and sends a harmless private test
report to check notifications. See [the security policy](../../SECURITY.md) and
[GitHub's configuration guidance](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository).

Before pushing, confirm that the pushed ref and commit are the ones that were
audited. Only the maintainer can record publication approval. Hosted security
settings, anonymous release pulls and packaged-install qualification belong to
their own release checkpoints.
