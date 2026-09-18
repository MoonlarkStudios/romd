# DAT upstream acquisition qualification

Date: 2026-09-05. Related: [accepted direction](../decisions/dat-subscription-distribution.md),
[#145](https://github.com/JackSkylark/romd/issues/145).

## Result

Public sample acquisition works for both authorities. This is a bounded
technical probe, not full coverage, unattended reliability, redistribution
approval, or beta acceptance. No credentials, upstream messages, public
mirror, scheduled acquisition, or application changes were involved.

The proposed `JackSkylark/romd-dat-catalogs` repository did not resolve via
GitHub during this check. #145 remains open.

## Directly observed evidence

| Probe | Observed result |
| --- | --- |
| Redump downloads page | HTTP 200; 60 distinct `/datfile/` links, including BIOS variants. Discovery count only; those endpoints were not all fetched. |
| Redump Jaguar CD sample | HTTP 200 ZIP, 31,789 bytes; one 100,054-byte XML DAT with 38 `game` elements. Header version `2026-04-03 15-50-49`. |
| Repeated Jaguar CD download | ZIP bytes differed; extracted document SHA-256 was identical. No ETag or Last-Modified in the first sample response. |
| Redump HTTPS sample | Connection refused from this environment; HTTP succeeded. This does not establish a universal HTTPS outage. |
| Redump feed directory | Links to recent dumps, changes, deletions, and forum RSS. These are activity feeds, not an observed immutable DAT release contract. |
| No-Intro profile | HTTPS 200; `clrmamepro` XML with 294 `datfile` entries. The inspected first entry used `_` for both URL and file. This is not qualified download discovery. |
| No-Intro daily page | HTTPS 200; displayed pack date 2026-09-05. Standard enabled, Aftermarket disabled by default; collection and output-type controls are separate. |
| No-Intro daily request | Public form reached Download, then binary download exceeded the deliberate 40 MB response cap. Full archive validation was not performed. |
| No-Intro single-system sample | Public form for upstream system 64 returned HTTP 200 ZIP, 245,298 bytes; one 1,004,577-byte XML DAT with 2,165 `game` elements. Identity: Nintendo 3DS (Encrypted), header version `20260904-084903`. |

Extracted document SHA-256 values:

- Redump Jaguar CD:
  `14dd5e8a851427b074193fa6cb726e776c7c6ebda496b5965e3e8dd295478d6a`
- No-Intro Nintendo 3DS (Encrypted):
  `07cab4b52c093dbc68978ae939eec19c7f8622ae07f117171e7e6a2b758e5549`

Temporary raw responses and structured probe results are under
`/private/tmp/romd-dat-qualification/`. They are local evidence, not committed
DAT fixtures or a durable artifact store. The daily response was capped and
not retained as a complete archive.

## Acquisition and coverage decisions

Redump's public downloads page provides discovery and direct ZIP downloads.
Keep BIOS and other variants distinct. Do not infer that every system visible
elsewhere on the site has a publicly downloadable DAT. Hash extracted DAT
bytes; changing ZIP packaging is proven insufficient evidence of a change.
Use HTTPS where available, but record actual acquisition transport and do not
describe a publisher signature as an upstream signature.

No-Intro's public form was technically usable through an HTTP session without
a browser engine in this probe. That is still a website workflow, not a
supported API. Its dynamic form fields and download confirmation are adapter
maintenance dependencies. The ROMD application should only consume published
artifacts; an acquisition implementation belongs in the companion project.
Do not hard-code the observed dated submit-field names or treat their dates
as catalog freshness.

Complete catalogs require explicit coverage settings. Preserve upstream
system ID plus variant and collection distinctions; filenames alone are not
stable identities. Do not merge encrypted/decrypted, headered/headerless,
standard/parent-clone, or separate aftermarket catalogs. Capture acquisition
options with provenance. The single-system sample used the form's selected
defaults; its success does not prove every inclusion category was enabled.

The official [download guide](https://wiki.no-intro.org/index.php?title=DAT-o-MATIC_Guide)
describes per-system downloads and the daily pack. The
[download listing](https://datomatic.no-intro.org/index.php?page=download&s=31)
explicitly describes its WWW profile as having no direct downloads.
[Fresh1G1R's acquisition code](https://github.com/UnluckyForSome/Fresh1G1R/blob/main/automate.py)
is useful precedent but selects a subset of collection categories and uses
browser automation. Reusing it unchanged would not prove ROMD coverage.

## Distribution terms: unresolved, not inferred

The [No-Intro terms](https://datomatic.no-intro.org/stuff/terms.txt) reviewed
here contain disclaimers and attribution context but no explicit DAT mirror
permission. The sampled DAT header contains author, trademark, and piracy
notices. Preserve those bytes. No explicit Redump redistribution grant was
found in the inspected homepage, download page, or sample header. This is a
record of limited evidence, not a conclusion that redistribution is forbidden.

Before public DAT mirroring, establish the applicable permission/terms for
automated daily acquisition and redistribution of complete public catalogs,
including required attribution, retention, and upstream removal handling.
Existing third-party mirrors are precedent, not authority to grant rights.
No upstream contact has been sent. Contract implementation and synthetic
publisher tests can proceed while this remains unresolved.

## Remaining qualification

- Validate a complete daily pack with intentional size/decompression limits
  and map all intended public systems/variants to stable catalog identities.
- Exercise repeated checks across actual upstream changes, failures, and
  schema/layout changes; a same-day sample is not reliability evidence.
- Establish upstream acquisition/redistribution conditions and retention.
- Validate publication integrity, authenticated metadata, key rotation,
  freshness expiry, and recovery from a bad publisher release.
- Exercise actual ROMD diff/activation/library convergence and rollback.

The [draft publication protocol](../dat-catalog-publication-protocol.md)
defines the next implementation boundary. No product test suite was run for
this documentation-only qualification.
