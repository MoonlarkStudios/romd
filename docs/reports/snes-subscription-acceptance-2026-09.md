# SNES subscription acceptance — September 6, 2026

## Scope and result

Starting from ROMD `533c28b62f5e707644f6cf6095e3f3f137fae463`, exercise the
published complete No-Intro SNES catalog through system setup, review and
activation. This proves catalog acceptance, not ROM import or hardware launch.
No user ROM or target device was provided during this acceptance run.

The shipped reader rejected the newly published No-Intro registry, preventing
both SNES and PlayStation discovery. ROMD also restricted verified catalogs and
reference data to Redump, and rejected legitimate size-less `nodump` records.
The change updates the pinned reader, permits the two supported provider and
representation pairs, and preserves unknown sizes during review. Malformed
sizes and dumped files without sizes remain invalid. The original DAT bytes
remain unchanged. Existing persistence still represents absent ROM sizes as
zero with `nodump` status; the source explorer displays Unknown for that case.

Admin catalog counts now use `entryCount` and `fileCount`, replacing disc/track
terminology. Deploy the matching admin client with this contract change.
The first-import review no longer identifies every subscription as PlayStation.

## Published source and trust

- Publisher code: `069bff51587ead4bcc62e6942af2bc8364ce3af0` in
  [romd-dat-catalogs](https://github.com/MoonlarkStudios/romd-dat-catalogs).
- Data commit: `4f18544db79f5a8f31e5cec937398d56adfa4908` in
  [romd-dat-data](https://github.com/MoonlarkStudios/romd-dat-data).
- Catalog: `no-intro/snes/standard`; stable system `snes`; No-Intro database `49`.
- Version: `20260818-050713`; 1,911,272 uncompressed bytes.
- SHA-256: `307fc5258970736d511c05b87d16268a9fbfff38d5bf6aa39dafd500e6500b19`.
- 4,358 entries and files, including 28 `nodump` and 18 `baddump` records.

Upstream qualification and publication were completed in the companion
repository. This acceptance downloaded its signed mirror, not the upstream
service. The public trust root remains unchanged, SHA-256
`6ab3222d28db2d8750c85fab1bfd6583b0fd9649252e24764865057200df7459`.
The updated reader verified reference data and both SNES/PlayStation candidates
using a copy of the existing deployment's retained production cache at metadata
version 3009. No trust-root rotation or cache reset was needed.

## Live acceptance

An isolated Docker project with fresh PostgreSQL and data volumes was used;
the existing review environment and unrelated working-tree changes were left
untouched.

1. Browser: Add system → search SNES → choose No-Intro subscription → review
   4,358 additions → approve → processing completes → Import your ROMs.
2. Downloaded active document matched the verified candidate byte for byte,
   including the exact hash and size above.
3. A repeated live check returned UpToDate with the same active DAT and no job.
4. Injected an unavailable publisher URL into the isolated admin container.
   CheckFailed explained that installed catalogs were unchanged and offered a
   retry; the active DAT remained downloadable and byte-identical.
5. Restored the real publisher. Retry returned UpToDate, cleared the error and
   scheduled the next daily check.
6. The Systems screen was inspected at 1440px and 390px widths; the mobile
   layout showed the enabled SNES system without horizontal page overflow.
7. Database inspection after these checks showed one DAT version, 4,358 entries,
   4,358 files, 28 nodump, 18 baddump, and one application job.

A real changed upstream SNES revision was not observed. Changed-candidate review,
activation, unchanged detection and failure preservation were exercised with
separate synthetic SNES and PlayStation host fixtures. These are not evidence
of a second real upstream release or a full day of unattended operation.

## Verification

- `mise run test`: 2,321 passing backend tests.
- Full hosting integration suite with all four connection-string exports
  removed after `mise exec`: 453 passing tests.
- `pnpm api:update`: generated admin contract updated; consumer unchanged.
- `pnpm lint`, explicit Biome check of eight touched handwritten files,
  `pnpm test` (490 tests), and `pnpm build`: passed.
- Standard Docker admin and worker builds: passed. A subsequently corrected
  review subtitle was rebuilt through `pnpm build` and copied to the isolated
  admin container; the standard images predate that text-only correction.
- `git diff --check`: passed.

Existing Vite bundle-size, SignalR annotation and React test act warnings remain.
No Flutter tests, ROM launch, NAS deployment, backup/restore or hardware
acceptance ran. Product regressions were fixed here; no new environment issue
warranted an addition to known-issues.md.

api clients: admin - generic catalog entry/file counts for cartridge and disc catalogs; consumer unchanged.
