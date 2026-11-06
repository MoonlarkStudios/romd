# Console original disc-set installation

Acquisition and synthetic runtime orchestration are implemented; production group
playback remains gated pending actual PS1 controller/disc-switch/shared-save
qualification. A constructed M3U command or synthetic test is not that evidence.
See [delivery contracts](../../../docs/architecture/disc-sets.md#status-and-explicit-selection) and
[accepted decisions](../../../docs/decisions/original-disc-sets.md); the
[qualification matrix](../../../docs/architecture/media-status.md#qualification-status)
owns current support and remaining limits.

## User flow

Title details offer **Disc sets** separately from individual versions. The
controller-first selection screen shows exact edition, ordered members, unknown
or explicitly expected total and readiness. Install all known deliverable discs
when the total is unknown without claiming completeness. Known missing members
or expected slots require explicit partial confirmation, including a one-disc
partial install. A broken disc's dependency closure cannot be selected.

Installed disc sets have a separate entry in the local Library. Their title,
selected membership, total status and immutable delivery snapshot are explicit.
Retry/repair and an intentional install update request new authorization; a
server repair never changes an installed playlist during gameplay.

Production Play reports qualification pending until the registered runtime has
been exercised with original media, BIOS and a controller. The synthetic test
seam can exercise orchestration; it is not an end-user bypass switch or a
playability claim. Existing individual-release play remains unchanged.

## Identity, migration and storage

Group identity is discriminated from release identity. It includes server
instance, set ID/revision, selected ordered member IDs/ordinals, original layout
versions and immutable delivery snapshot. Group IDs never enter release ID,
release history or release activity synchronization fields.

The fresh SQLite v17 baseline includes separate group installs, profile
references/authority and grant-free acquisition journals. Older databases are
refused unchanged; there is no v15 or v16 upgrade path. Follow the
[persistence and cleanup rules](architecture.md#persistence-acquisition-and-cleanup)
before replacing an unsupported database. Generated `app_database.g.dart` is
produced with `mise run generate:database` when Drift declarations change.

Group content has its own filesystem namespace and snapshot-specific directories,
outside existing release enumeration and cleanup. Original descriptor paths and
all companions are materialized exactly, alongside the server's exact UTF-8/LF
playlist. All size/hash checks complete before publication; playlists have their
own derived identity and hash. Target containment, portable collisions and
symlink guards also apply locally.

Staging lives on the same volume as the final directory. The durable journal
records plan/publication progress so restart can reconcile filesystem rename
and SQLite commit without adopting unverified content. Failed/cancelled work
preserves the previous usable install. Uninstall removes profile references and
must respect other profiles' references to the same selected snapshot.

Superseded snapshot bytes are retained for safe recovery in this implementation; there is
no automatic reclamation of all unreferenced historical group snapshots. Existing
release eviction intentionally does not scan this namespace. Reclamation needs
an explicit reference/journal/active-session-aware policy rather than directory
age deletion. Do not interpret intake or staging limits as a cap on retained
local install storage.

## Strict API, renewal and authority

The handwritten Consumer Host adapter validates versioned identities, canonical
ordered membership, layout versions, count/completeness semantics, portable paths,
exact decimal byte counts, hashes and derived playlist bytes. Unknown/malformed
semantics fail closed. Credential-bearing requests stay with the discovered
selected server; no Admin Host or filename-derived grouping is used.

Persisted plans strip expiring URLs. Renewal obtains fresh status/observation
then requests the original `requiredSnapshotId` under current Library policy.
Compare immutable selected metadata independently of observation freshness: an
omitted member becoming ready must not prevent renewal of an unchanged selected
snapshot. A changed snapshot requires explicit replanning and cannot mix files
with an earlier acquisition.

Profile authority is independent from downloaded files. Group access must echo
the exact installed identity. Authoritative deny blocks; unavailable/inconsistent
responses cannot create allow or overwrite revoke. Cached decisions bind the
active local profile/server and verified group snapshot, with authority revision
checks and serialized publication so an install cannot overwrite a newer deny.
Repair applies a final server deny only to the matching installed snapshot; it
cannot revoke a different prior or newer snapshot by accident. A playlist or
short-lived content URL is never offline authority.

## Runtime boundary

Group launch reuses existing runtime providers, dependency resolution and the
shared active-session guard. Group authorization wins before local runtime
probes, provisioning, config or process effects. The resolved target carries a
group discriminant and no fake release ID. After asynchronous provisioning,
launch rechecks authority under the group mutation fence and holds that fence
only through process creation. Process lifetime waits outside the fence so
revocation can proceed. A profile change during process creation terminates and
joins the stale process. Existing shipped profile IDs and
default ordering remain unchanged.

The first scoped protocol path is default RetroArch SwanStation for PS1 original
M3U/CUE/BIN. Shared cards/save/config roots are isolated by profile, server and
set so edition or snapshot changes cannot silently overwrite another game's
progress. Save states additionally remain runtime/snapshot-specific. Individual
release saves are not silently moved into the group namespace.

The existing RetroArch writer emits group-specific core/shared-card settings and
menu/controller disk-control behavior. Process arguments remain structured and
no save/config data goes under evictable content. Actual boot, controller disc
switching and loading a save across discs must still be observed and recorded
before qualifying production group playback. Direct emulator smoke checks alone
do not prove console acquisition/authorization integration.

artifact resolver: no - existing PS1 executable/core/BIOS resolution is reused;
isolated clean-runtime provisioning for qualification does not change it.

config writer: yes - the existing writer scopes group shared-card/save/config
and controller disc handling, with focused configuration tests.

## Validation and remaining qualification

Run `mise run analyze` and `mise run test`; regenerate database code with
`mise run generate:database` when Drift declarations change, and include the
runtime skill shell/path negative checks for runtime changes. Meaningful cases
cover fresh v17 creation and unchanged refusal of older databases,
malformed HTTP/version/identity, unknown/partial selection,
M3U and companion hashes/order, exact renewal, corruption and symlinks, durable
resume/publication recovery, cross-profile/server isolation, access revocation
races and controller/keyboard UI. Full acquisition through launch with real
fixtures, controller-driven disc switching, loading a disc-1 save through disc 2,
and save continuity across reinstall remain required before enabling production
group playback. Keyboard or direct frontend boots cannot substitute for them;
source test results do not qualify a packaged console build.

Real-media qualification records original CUE/BIN and BIOS hashes, actual runtime
and core versions/artifacts, generated layout/config, controller eject/select/
insert steps and shared-memory-card save/load results. Do not check original
media, BIOS or personal saves into Git. Only systems/runtimes actually exercised
receive qualification evidence; no CHD or CD-sector verification is involved.

Deferred: automatic historical snapshot reclamation, group play history/activity
synchronization, browser/ZIP/VFS integration, additional runtimes/platforms and
any new offline entitlement policy. Actual production playback remains pending
until its qualification gate is met.
