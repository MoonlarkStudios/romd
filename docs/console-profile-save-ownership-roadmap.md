# ROMD Console Profile Save Ownership Roadmap

Status: profile ownership, pre-release compatibility cleanup, validation, and
owner runtime acceptance are complete. Stable-server-instance collision
isolation is planned under reopened Gate 0 and is not implemented. This work
follows Dolphin checkpoint `ff0bd84`.

## Product contract

The active launcher profile owns gameplay progress. Controller seating and a
guest profile assigned to another player never redirect save ownership.

The completed baseline partitions saves and states by local profile and title:

```text
<base>/profiles/{localProfileId}/games/{platform}/{title}/saves
<base>/profiles/{localProfileId}/games/{platform}/{title}/states
```

Different releases of the same title share progress within one profile. Two
profiles never share writable save or state roots. Installed content remains a
device-wide asset under `content/{platform}/{title}/{release}`.

Before multi-server local libraries ship, planned paths add canonical server
instance identity:

```text
<base>/profiles/{localProfileId}/servers/{serverInstanceId}/games/{platform}/{title}/saves
<base>/profiles/{localProfileId}/servers/{serverInstanceId}/games/{platform}/{title}/states
<base>/content/servers/{serverInstanceId}/{platform}/{title}/{release}
<base>/config/servers/{serverInstanceId}/{platform}/{title}
```

Two ROMD instances that reuse encoded ids must never share content, progress, or
generated config. Same instance at a new origin retains paths; a replacement at
the same origin does not mount them.

Shared installation does not imply access. Planned visibility and launch require
a `ProfileLocalGame` keyed by profile + server instance + release, created only
after verified Store acquisition/attach as defined in
`docs/decisions/console-profile-access-and-offline-grants.md`. Revoking one
profile updates that row without deleting the device asset or progress.

Runtime configuration remains device-wide within one server instance. The
completed baseline uses `config/{platform}/{title}`; planned collision hardening
adds instance identity. Standalone
emulators preserve native settings in their ROMD-scoped runtime user
directories. Graphics, audio, runtime safety settings, and ROMD-generated
launch configuration describe the console. ROMD reasserts launch-specific
controller and durable-path settings immediately before each launch, and
active-session ownership prevents simultaneous emulator mutation.

Deleting a local profile does not implicitly delete its progress. Profile-data
deletion and export require a separate, explicit product flow.

## Pre-release compatibility contract

ROMD has no external consumers of the former shared save layout. After the
sole development installation completed profile-isolation acceptance, the
launch-time legacy import and initialization system was removed rather than
shipping unreleased compatibility debt.

ROMD does not scan old top-level save/state paths or native emulator user
directories, show a migration dialog, copy shared data, or create an
initialization marker. Launch resolves the active profile's roots directly and
the selected runtime creates only the directories it uses. Existing files from
development builds remain untouched on disk but are ignored by the product.
A future general-purpose save importer, if desired, is a separate explicit
feature rather than launch-time migration behavior.

## Runtime contract

Every adapter consumes only `ResolvedPlayTarget.saveRoot` and
`ResolvedPlayTarget.stateRoot`:

- RetroArch emits both roots in its generated configuration.
- Dolphin routes memory cards and states through its managed config and
  ROMD-owned user-directory links.
- DuckStation emits the profile memory-card directory and save-state directory
  in its existing INI writer.
- PCSX2 emits the profile memory-card and save-state directories in its
  existing non-controller INI writer. This does not re-enable ROMD gameplay
  mapping for PCSX2.

`PlayTarget.localProfileId` and `ResolvedPlayTarget.localProfileId` are required
launch facts. The content service resolves profile identity and durable roots
together; callers cannot replace only the identity after resolution.

Before server switching ships, resolution must also carry the selected canonical
`serverInstanceId`; callers cannot authorize one instance and resolve another's
content or durable roots. This crosses runtime path composition and requires the
`romd-console-runtime` workflow.

## Persistence and schema

The completed profile-only slice required no SQLite migration and kept schema
version 10. The planned server-instance feature requires a later migration:
installs become instance+release keyed and profile history gains instance
identity. Version-10 installs become hidden legacy orphans rather than being
assigned to an origin.

Save/state/config roots remain derived, but derivation adds instance UUID.
Legacy ambiguous paths remain preserved and are not attributed automatically;
verified adoption/import is explicit.

Profile ids remain validated as safe path segments. Canonical lowercase UUID-D
instance ids are additionally validated before path construction. Runtime
profile ids and controller mapping keys remain unchanged.

## Acceptance

- Two profiles share content and config roots but receive different save/state
  roots for the same title.
- Releases of one title share one profile-owned progress scope.
- Offline and connected launch paths carry the same active profile id.
- Guest/controller profile assignments never change save ownership.
- Unsafe profile ids cannot escape the application-support root.
- Uninstall removes content only and preserves every profile's progress.
- Launch performs no legacy-path inspection, copying, migration prompt, or
  initialization-marker write.
- Existing data already inside a profile/title root remains directly usable.
- RetroArch, Dolphin, DuckStation, and PCSX2 config-byte tests contain the
  profile-owned roots.
- Switching profiles replaces shared writer path settings and Dolphin links
  without leaking the prior profile.
- Analysis, focused tests, the full console suite, runtime negative searches,
  and `git diff --check` pass.
- Owner hardware checks prove save and state isolation with two local profiles.

## Required runtime decisions

- `artifact resolver: no - profile-scoped durable roots do not change runtime dependency resolution.`
- `config writer: yes - existing emulator writers must continue consuming saveRoot/stateRoot after those roots become profile-scoped.`

Pre-release migration cleanup:

- `artifact resolver: no - save-migration cleanup changes no runtime dependency.`
- `config writer: yes - permanent emulator writers continue targeting profile-owned save and state roots.`

## Validation evidence

Historical implementation validation recorded 2026-07-14 before the
pre-release compatibility contraction:

- `mise run analyze`: no issues.
- Focused final persistence, import, launch-flow, Dolphin, and runtime-profile
  inventory tests: 78 passed.
- `mise run test`: 735 passed.
- Runtime negative searches: no shell-string launch and no durable path under
  `contentRoot`; the only matches were the approved structured process runner,
  SDL provisioning, and instruction comments.
- `git diff --check`: clean.
- `AppDatabase.schemaVersion`: 10.
- Drift declarations, migrations, and generated files: unchanged.
- Independent review: Pass after resolving fail-closed ambiguous-destination
  handling, Dolphin's empty-skeleton adoption race, missing nested-link
  coverage, and the first-launch initialization-marker lifecycle.

Owner runtime acceptance completed on 2026-07-15: the same titles were exercised
under isolated local-profile roots, and RetroArch, Dolphin, DuckStation, and
PCSX2 all resumed and saved as expected. PCSX2 acceptance covers only
memory-card/state ownership; ROMD gameplay mapping remains unsupported.

First Dolphin owner run: legacy Super Mario Sunshine data copied successfully
and the shared source remained available, but launch exposed a settings/game
handoff conflict: Dolphin Settings had created empty `GC` and `StateSaves`
directory skeletons where the game path expected managed links. ROMD now
adopts only data-free skeletons and continues to reject any populated path. A
post-fix launch then exposed a second migration defect: the copied save remained
at Dolphin's regional `USA/Card A` path while ROMD's custom flat GCI override
made Dolphin create a separate root-level save. The flat save and imported save
remain distinct and preserved. The development config was cleaned, and the
final writer omits the override so Dolphin's default regional path resolves
through the profile-owned `GC` link without shipping a stale-setting migrator.
Owner verification passed on 2026-07-15: the imported one-Shine progress
loaded, a new save completed, and Dolphin exited cleanly. Post-run inspection
confirmed that the regional `USA/Card A` file changed while the preserved flat
file did not; `Dolphin.ini` retained folder-card mode without any custom GCI
path keys, and `GC` still targeted the active profile/title save root.

The 2026-07-15 follow-up owner run also passed for RetroArch, DuckStation, and
PCSX2. Generated RetroArch and DuckStation configuration targeted the active
profile/title save and state roots. PCSX2's imported `Mcd001.ps2` and
`Mcd002.ps2` were byte-for-byte identical to their shared originals; PCSX2
recognized the first as a formatted 8 MB card, booted Final Fantasy X, and the
owner confirmed normal operation. The initially apparent PCSX2 launch failure
was not a card-copy failure: the emulator log and live process showed that the
game had started while ROMD remained visible. The owner subsequently confirmed
the two-profile isolation matrix.

Follow-up validation recorded 2026-07-15 for the Dolphin GCI correction:

- `mise run analyze`: no issues.
- Focused Dolphin adapter tests: 11 passed; broader Dolphin settings and launch
  flow tests: 38 passed.
- `mise run test`: 736 passed.
- Runtime negative searches: no shell-string launch and no durable path under
  `contentRoot`; matches were limited to the approved structured process
  runner, SDL provisioning, and instruction comments.
- `git diff --check`: clean.
- `AppDatabase.schemaVersion`: 10; Drift declarations, migrations, and
  generated files are unchanged.
- Independent review: Pass. Dolphin 2606 source confirms that an empty custom
  GCI path selects `GC/<region>/Card A`, while the obsolete nonempty override
  flattened saves into its exact directory.

Final pre-release cleanup validation recorded 2026-07-15:

- Removed the launch-time legacy scan, migration prompt, copy/staging path,
  initialization marker, native-emulator legacy source inventory, and all
  corresponding public service APIs and tests.
- `mise run analyze`: no issues.
- Focused content-store, install-service, launch-flow, runtime-writer, adapter,
  and application tests: 127 passed.
- `mise run test`: 724 passed. The reduction from 736 is the 12 deleted
  migration-only tests; no permanent behavior test was disabled.
- Migration-symbol search: no production or test matches for the deleted
  legacy types, operations, marker, or temporary Dolphin cleanup hook.
- Runtime negative searches: no shell-string launch and no durable save, state,
  or config path under `contentRoot`; matches were limited to the approved
  structured process runner, SDL provisioning, and instruction comments.
- `git diff --check`: clean.
- `AppDatabase.schemaVersion`: 10; Drift declarations, migrations, and
  generated files are unchanged.
- Independent review: Pass with no findings. The reviewer also ran 80 focused
  persistence, launch-flow, Dolphin, DuckStation, and PCSX2 tests.
