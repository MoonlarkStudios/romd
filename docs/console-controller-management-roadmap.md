# Console Controller Management Roadmap

Status: Slices 1-6 preserved as the authoritative landed record; the active
continuation is the Console Controller Identity Roadmap

Created: 2026-07-09

Last refined: 2026-07-14

This document is the authoritative record for ROMD Console controller Slices
1-6 and their UX, identity, seating, persistence, and writer-safety contracts.
After DuckStation and RetroArch research disproved safe cross-process ordinal
correlation, the runtime-routing sequence diverged to the
[Console Virtual Controller Routing Roadmap](console-virtual-controller-routing-roadmap.md),
which was itself parked on 2026-07-11 after macOS isolation and propagation
evidence failed and the owner fixed two product constraints (multi-emulator
support; stock kiosk input configuration). Deterministic per-player port
routing is out of scope for ROMD: emulators assign ports.

The active continuation is the
[Console Controller Identity Roadmap](console-controller-identity-roadmap.md).
It supersedes this document's planned Slices 7 and 9 (Phase 6B routing release
and PCSX2 mapping as originally gated) and carries Slice 10 and the mapping
editor forward under the pivoted product promise. Implementation changes must
update the relevant active roadmap slice in the same commit.

## Product Outcome

ROMD should feel like a purpose-built console. A person should be able to:

1. pick up a controller and press L+R at the distraction-free attract screen;
2. choose the active local profile and enter that profile's library;
3. see who is P1/P2/P3/P4 without visiting Settings;
4. change player order entirely from a controller;
5. launch without learning SDL, GUIDs, provider ids, runtime indices, config
   files, or emulator terminology;
6. get the displayed player order and each player's effective mapping in the
   emulator.

The current product is close to this experience. Session seating, exact
identity, canonical player projection, controller-first setup, and per-player
launch mappings exist. The primary runtime correctness gap is that ROMD has not
proven that its provider enumeration index identifies the same physical device
inside RetroArch or DuckStation. Route-independent physical-controller access
and an immutable reviewed-to-launch handoff are also still required.

## Non-Negotiable Contracts

- Player seating is ephemeral and lasts only for the couch/app session.
- Returning to attract clears all session seating and pending starter claims.
- Do not add durable player seating rows.
- Do not create controller profile/GUID rows from passive enumeration.
- Device-global hardware setup is keyed by SDL platform/GUID. Passive
  enumeration and screen opening may neither create nor delete it. Explicit
  `Use detected map` persists the detected mapping atomically; only an explicit
  removal when no detected map is available may delete that row.
- Exact seating identity is `sdlGuid + serial` when both exist.
- Mapping persistence identity is `localProfileId + sdlGuid`; serial is not
  part of the mapping key.
- Do not invent GUIDs or serials on fallback/no-SDL paths.
- Preserve no-claim, fallback, and no-SDL behavior unless an approved slice
  explicitly changes it with compatibility tests.
- Exact offline reservations remain holes; another controller must not be
  promoted into the reserved player slot.
- Change Order never changes the active profile or library.
- Additional controllers are session guests by default.
- Do not rename shipped runtime profile ids.
- Do not hand-edit Drift generated files.
- Keep emulator and persistence jargon out of normal user flows.
- Commands remain structured with `runInShell: false`.
- Save, state, and config output remain under ROMD-owned roots.

## Runtime Decisions

- `artifact resolver: yes - SDL3 native identity and events remain a ROMD-managed native dependency; runtime-specific correlation research must use the exact provisioned emulator artifacts and versions that ROMD supports.`
- `config writer: yes - controller seating, per-player mappings, and runtime input references are translated only for approved RetroArch and DuckStation envelopes; PCSX2 preserves emulator-owned input configuration.`

## Current Baseline

Latest controller milestone:

```text
Slice 4 (this commit): runtime input correlation contract and safe writer gate
```

Current console SQLite schema version: `10`. Slice 4's canonical controller
mapping migration added the device-global platform/GUID hardware-map store;
the RetroArch gameplay adapter adds no further schema or generated-file change.

The 2026-07-13 Reset diagnosis confirmed the detected 8BitDo positional map was
correct in the UI, but the then-current `Use detected map` action deleted its
durable row and therefore removed the explicit intent required for calibrated
RetroArch gameplay emission. A deeper audit found a second defect: the writer
treated SDL3's legacy label-hint indices as positions and reversed both face
pairs. The approved combined correction persists a detected map atomically,
keeps passive open write-free, and translates positional raw
`b0/b1/b2/b3` to MFi `0/8/1/9`. On 2026-07-13 the owner explicitly saved the
detected map and accepted the corrected live N64 result: B/South -> N64 A,
Y/West -> N64 B, X/North no longer -> B, left-stick movement, and right-stick C
controls. The persisted row and generated config matched the detected map and
calibrated `8/0/9/1` MFi face positions.
The owner then accepted a fresh South/East swap in *Super Mario World*. The
durable row contained only the intended `a:b0,b:b1` change and the generated
config replaced RetroPad South/East with MFi `0/8`, proving the previous face
bindings did not remain layered underneath.
After the test the owner restored the detected map; its exact
`a:b1,b:b0,x:b3,y:b2` data remained unchanged across a ROMD process restart.
Installed *ClayFighter* then provided accepted baseline Genesis Plus GX
six-button evidence: printed Y/B/A produced Genesis A/B/C and printed LB/X/RB
produced Genesis X/Y/Z. The approved implementation passes the selected core
id from `RuntimeProfile.coreRequirement` into the writer and applies the
Genesis policy only to `genesis_plus_gx`; the PicoDrive alternate remains
separate and intentionally unchanged. Platform/core identity is necessary but
not sufficient: Genesis policy bytes require the existing approved exact
single-controller gameplay envelope. Empty setups, fallback controllers,
unsupported singleton setups, and every multi-controller session retain their
prior bytes and receive no asymmetric player-one analog-D-pad policy. Genesis
Plus GX selects Joypad Auto, mirrors the left stick to its digital D-pad,
disables inherited global remap and override loading, and scopes its isolated
per-title remap directory beneath `configRoot` without creating a Genesis
core-options file. The *ClayFighter* result remains pre-change baseline
evidence. The owner then accepted the final reviewed build on the same hardware:
all six face/shoulder positions, D-pad movement, left-stick movement, Mode, and
Start worked as specified. The fresh per-title config selected Joypad Auto,
enabled analog-D-pad mode, emitted the restored MFi gameplay positions
`a=0,b=8,x=1,y=9,l=10,r=11`, disabled inherited remaps/overrides and remap
saving, and pointed at the existing empty per-title `remaps` directory. The
broader Slice 4 multi-controller hardware gate remains pending. After the
cardinality correction, the focused RetroArch adapter/writer matrix passed 53
tests and the full console suite passed 667 tests; analysis, Dart format,
runtime negative searches, and `git diff --check` were clean.

Run C then deliberately swapped the physical sticks. RetroArch correctly made
the right stick drive N64 movement and the left stick drive the C-button
cluster, proving saved axis reassignment reached the runtime. Both vertical
axes were also unintentionally inverted because the editor exposed
`LX`/`LY`/`RX`/`RY` separately and treated the first arbitrary axis gesture as
polarity. The approved correction groups each stick in the UI and captures one
continuous right-held-then-down gesture, committing both axes to the local
draft together. Complete detected SDL stick pairs require the exact companion
vertical axis; partial/unknown maps use the continuous hold as the pairing
signal. Existing persisted X/Y bindings, schema 10, and runtime writer formats
remain unchanged.

- `artifact resolver: no - stick-level capture uses the existing SDL input provider and resolved runtime dependencies.`
- `config writer: yes - saved stick axes continue through the existing fail-closed emulator configuration writers without writer changes.`

The focused controller-setup widget suite passed 32 tests and the full console
suite passed 679 tests. Analysis, Dart format, runtime negative searches, and
`git diff --check` were clean. Schema 10, Drift declarations/generated output,
runtime profile ids, and emulator config writers are unchanged. The owner then
accepted corrected Run C2 in *The World Is Not Enough*: physical right stick
drove N64 movement and physical left stick drove the C-button cluster, all four
directions were correct, and the old roles no longer acted. The saved row was
exactly `leftx:a2,lefty:a3,rightx:a0,righty:a1` with no inversion markers; the
fresh N64 config emitted matching left X `-2/+2`, left Y `+3/-3`, right X
`-0/+0`, and right Y `+1/-1`. Paired-stick hardware acceptance is complete;
the owner then used `Use detected map`, and the primary verified exact detected
axes `leftx:a0,lefty:a1,rightx:a2,righty:a3` plus the original buttons and
triggers, with no inversion markers. At that point, trigger and
multi-controller gates remained pending.

Run D established the normal *Ape Escape* LB/LT baseline, then swapped physical
LT onto canonical Left shoulder and physical LB onto canonical Left trigger.
The owner accepted the reversed camera actions with no old-action leakage. The
durable row and managed DuckStation `gamecontrollerdb.txt` both contained
exactly `leftshoulder:+a4,lefttrigger:b9`, while DuckStation kept its canonical
INI bytes `L1 = SDL-0/LeftShoulder` and `L2 = SDL-0/+LeftTrigger`. This closes
representative cross-type trigger capture and managed DuckStation runtime
emission. The owner then used `Use detected map`; the primary verified exact
normal bindings `leftshoulder:b9,lefttrigger:a4`, detected stick axes `0/1` and
`2/3`, original buttons, and no inversion markers. The multi-controller gate
remains pending.

- `artifact resolver: no - Genesis controller policy uses the existing resolved RetroArch core and adds no provisioning requirement.`
- `config writer: yes - the Genesis Plus GX canonical-to-core policy is isolated in the existing fail-closed RetroArch configuration writer.`

- `artifact resolver: no - the detected-map intent and face-position correction use the existing runtime artifact and add no provisioning requirement.`
- `config writer: yes - the fail-closed RetroArch writer corrects its four face-button SDL-to-MFi translations so canonical positions reach the intended physical buttons.`

Focused correction evidence passed 39 RetroArch writer/integration tests and
20 controller-UI tests. The combined controller-UI/provider/launch/writer/
integration matrix passed 104 tests, the full console suite passed 661 tests,
and analysis, Dart format, runtime negative searches, and `git diff --check`
were clean. Representative non-face swaps and multi-controller hardware checks
remain pending.

### Landed Capability Summary

| Capability | Status | Evidence |
| --- | --- | --- |
| Coherent controller provider at app root | landed | `276a1e5` |
| SDL listing and live event identity | landed | `2fcec84` |
| Exact/fallback multiplayer seating semantics | landed | `f9894c5` |
| Profile/GUID mapping persistence | landed | `beae4a2` |
| Capability-filtered mappings | landed | `251b1bc` |
| Central `LaunchControllerSetup` writer contract | landed | `4363bb0` |
| Exact reserved holes preserved at launch | landed | `f1fb83f` |
| Canonical Players projection and atomic provider failover | landed | `b81dc40` |
| Integrated profile-led Players cluster | landed | `b81dc40` |
| Conditional pre-launch Players review/setup | landed | `b81dc40` |
| Contextual focus-safe Players panel | landed | this milestone commit |
| Revision-checked reviewed launch snapshot | landed | `afe1178` |
| Change Order reconnect and ceremony polish | landed | `b81dc40` |
| Per-player launch mapping entries | implemented | `b81dc40` |
| Runtime physical-device correlation | blocked release gate | not proven |
| PCSX2 mapping | prohibited | two 2.6.3 macOS hardware experiments rejected |

### What Is Strong

- Attract is visually clean and only the controller that completes L+R starts
  the controller path.
- Profile selection and player seating are separate concepts. Choosing P1 does
  not silently switch the active library.
- Returning to attract clears only ephemeral seating; durable controller
  preferences remain intact.
- `SessionControllerSlotClaims` owns all seating state in memory.
- `ControllerAssignments` and `PlayersProjection` give UI and launch planning
  one canonical interpretation of exact claims, fallback claims, reservations,
  blocked ambiguity, and automatic order.
- Exact reconnect matches GUID+serial, including replacement of a stale
  provider id without consuming another player slot.
- SDL listing and events fail over together so they cannot expose mixed
  provider identity spaces.
- Profile/GUID mappings are isolated by local profile and controller layout.
- Mapping capability filtering prevents impossible physical buttons from being
  emitted while retaining explicit unbinds.
- `LaunchControllerSetup` carries immutable player-slot keyed identity,
  mapping, template/capability metadata, reserved holes, blocked slots, and an
  explicit runtime input reference.
- RetroArch and DuckStation writers consume that shared launch model, and P1
  alone owns in-game shortcuts.
- Players setup, Change Order, and the unified Test/Map workspace under
  Settings > Controllers are controller navigable and retain keyboard support.
- Most normal user copy uses Players, Button labels, Shortcuts, and In-game
  shortcuts rather than emulator configuration language.

### Remaining Product/Correctness Gaps

Ordered by severity:

1. **Runtime routing remains unverified.**
   `RuntimeControllerInputReference.bestEffortRuntimeIndex` is ROMD provider
   enumeration order. RetroArch consumes a joypad-driver index and DuckStation
   consumes its own `SDL-N` id. Those values have not been proven equivalent.
   Per-player mapping data is correct, but it may be applied to the wrong
   physical pad.
2. **Controller icons are generic.**
   The cluster correctly shows one icon per connected seated controller, but a
   future catalog should distinguish major controller families without making
   exact recognition a launch prerequisite.
3. **Profile/GUID `templateId` is stored but intentionally inactive.**
   It may become active only after an explicit layout or mapping interaction;
   passive row existence is not user intent.

## Approved UX And Information Architecture

### Vocabulary

| Concept | User-facing term | Meaning |
| --- | --- | --- |
| Session seating | Players | Who is P1/P2/P3/P4 now |
| Device/setup | Controllers | Connected devices, testing, device preferences |
| Display glyph choice | Button labels | Labels ROMD displays; does not remap input |
| Runtime hotkeys | Shortcuts / In-game shortcuts | Menu, save/load state, screenshot, quit |
| Offline exact seat | Waiting | The named controller can reclaim that player spot |

Avoid `mapping`, `GUID`, `SDL`, `runtime index`, `fallback`, `reservation`, and
`emulator port` in ordinary UI.

### Attract And Profile Selection

- Attract contains no Players bar, status cluster, or controller notifications.
- The prompt remains `Press L + R to Start`.
- The starter transition may say `Who’s using <controller>?`.
- The starter becomes P1 only after the active profile is selected.
- Backing out discards the pending starter claim and clears session seating.
- Confirmation is expressed in the transition/header state, not a clickable
  Snackbar or notification.

### Home And Route-Independent Access

- Home integrates one compact profile-led Players cluster into the native top
  header; do not add a detached full-width controller bar.
- The cluster combines active-profile context with connected seated-controller
  icons, for example `John · 1 player` or `John · 3 players`.
- Focusing/activating the cluster opens Players.
- Pushed and immersive routes remain visually clean.
- A physical-controller action must open Players from eligible routes through
  a dedicated `ShowPlayersIntent`; it must not depend on a synthetic key event.
- Attract and profile selection suppress both the cluster and global Players
  action.

Start/Menu is the approved global Players action. Physical Y remains Details,
and Guide/Home remains unused because the operating system may intercept it.

### Players Surface

Players shows:

- the active profile as `Playing as <name>`;
- P1/P2/P3/P4 as Connected, Waiting, Check order, or Open;
- one clear consequence when a controller is offline or ambiguous;
- Change player order;
- Switch primary profile as an explicit, separate library action.

Controller testing, Button labels, and mapping/device configuration belong
under the real Settings > Controllers route, not in Players. Testing and
mapping share one exact-controller workspace so they cannot imply different
identity or input contracts.

Every opening path—Home cluster, global controller action, and pre-launch
review—must supply the same active-profile context and explicit switch action.

### Pre-Launch Behavior

Launch directly when zero or one controller is connected and there are no
claims requiring attention, reservations, or ambiguity.

Show Players review when:

- more than one controller is connected;
- an exact player controller is offline;
- fallback and exact same-name devices are ambiguous;
- duplicate devices cannot be distinguished confidently;
- controller status cannot be refreshed;
- a future title/platform signal says multiplayer setup matters.

When ROMD is uncertain, focus Change player order. `Play anyway` may preserve
the ROMD session spots shown, but copy must never imply verified emulator
routing where the adapter cannot provide it.

### Change Order

1. Start with the connected-and-unseated roster visible.
2. Prompt `Hold L + R on Player 1's controller`.
3. After a claim, say `Player 1 seated. Now Player 2.`
4. A reconnect of the same exact GUID+serial replaces its stale ceremony id; it
   cannot consume another player spot.
5. Duplicate presses give immediate feedback without moving the controller.
6. Confirm commits the whole ceremony atomically.
7. Cancel preserves the previous assignment.
8. Ready with nothing seated clears custom claims and returns to automatic
   connection order; the consequence is stated before activation and does not
   require a second confirmation.

## Architecture Contract

### Identity Spaces

Do not collapse these into one integer:

| Space | Identifier | Stability/use |
| --- | --- | --- |
| Exact ROMD seat | SDL GUID + serial | exact physical identity in this session |
| ROMD live provider | provider device id | ephemeral event/list correlation |
| ROMD listing | enumeration order | observation only; not durable identity |
| RetroArch | selected joypad-driver device index | runtime-local |
| DuckStation | `SDL-N` source | runtime-local |
| Fallback/no-SDL | Apple/gamepads provider identity/order | separate stack |

### Shared Projection

`ControllerAssignments.resolveSlotResolution` is the canonical resolution
algorithm. `PlayersProjection` is its read-only product projection. All of the
following must consume it or the same canonical result:

- Home Players cluster;
- Players setup;
- Change Order confirmation refresh;
- conditional pre-launch disposition;
- launch preparation;
- config-writer player numbering.

Presentation code may format states but may not reimplement matching, fill
holes independently, or infer stronger identity than the projection reports.

Pre-launch review must produce a launch snapshot tied to claim and device
revisions. Launch consumes that exact snapshot, or revalidates and returns to
review when either revision changed. A boolean `approved` result without the
reviewed roster is not a sufficient handoff.

### Provider Boundary

- One active `ControllerInputProvider` owns listing and events.
- Listed ids and event ids must share one identity space.
- SDL failure switches listing and events atomically to fallback.
- Fallback remains supported without fake exact identity or profile/GUID rows.
- Passive observation never changes seating or persistence.

### Persistence Boundary

Durable tables:

```text
controller_binding_rules
controller_mapping_profiles
controller_profile_binding_rules
controller_preferences_rows
```

Resolution order for a controller with an SDL GUID:

1. built-in mapping;
2. selected template/capability layer;
3. legacy device-wide global/title rules;
4. profile/GUID global rules;
5. profile/GUID title rules.

Controllers without an SDL GUID stay on the legacy fallback path. Stale enum
values are skipped, not destructively deleted. Null bindings remain explicit
unbinds.

### Launch Boundary

`LaunchControllerSetup` is the sole runtime-neutral launch controller contract.
Each player entry may carry:

- player slot;
- connected controller identity and provider id;
- effective capability-filtered mapping;
- template/capability metadata;
- a runtime-specific input reference and correlation confidence.

Reserved and blocked player slots remain explicit. Compatibility getters on
`EmulatorLaunchPlan` may remain temporarily, but no new writer work may derive
player order from a compact connected-device list.

The runtime-reference model should evolve beyond a universal integer:

```text
provider: sdl3 | appleGameController | other
providerDeviceId: String
providerOrdinal: int?
runtimeReference: int | String | null
correlation: verified | acceptedBestEffort | uncorrelated
```

Writer policy:

| Correlation | Behavior |
| --- | --- |
| verified | emit forced player-specific runtime references |
| accepted best effort | emit only for an explicitly approved pinned adapter policy |
| uncorrelated | omit forced references and preserve automatic emulator order as the approved safe best-effort fallback |

The safe best-effort approval does not authorize forcing a guessed ROMD
ordinal. Any pinned adapter policy that deliberately forces an
`acceptedBestEffort` reference still requires explicit evidence and approval.

Gameplay mapping has two distinct ownership layers. The durable controller
setup maps physical inputs to ROMD's canonical RetroPad. A runtime adapter then
maps that canonical pad to the emulated system's controls; core-specific
semantics must not leak back into or rewrite the physical hardware map. For
N64, the active identity roadmap owns the approved South-to-N64-A,
West-to-N64-B, left-stick/control-stick, right-stick/C-cluster, L2/Z,
shoulder/L-R, and direct D-pad/Start policy.

RetroArch N64 core options and remaps must be generated per title beneath
`configRoot`, with global override loading, per-game core-option discovery, and
automatic remap load/save disabled so global frontend state cannot change
ROMD's canonical-to-N64 policy. The current Mupen64Plus-Next core is a mutable
nightly `/latest` dependency without a digest; its behavior is not a frozen
runtime contract until the existing resolver/provisioner pins and versions that
artifact. See identity-roadmap Slice 5 for the active contract, live diagnosis,
and remaining acceptance work.

### Batocera Lesson

Batocera demonstrates a useful orchestration shape: EmulationStation resolves
players, passes current device metadata to one launch generator, injects a
consistent SDL mapping environment, and lets each pinned emulator writer emit
native configuration. ROMD should copy that controlled launch pipeline.

Batocera does not prove a universal identity protocol. It still writes frontend
SDL indices into RetroArch, DuckStation, and PCSX2 and relies on its controlled
Linux image, device paths, packages, SDL environment, and emulator drivers to
keep enumeration aligned. ROMD must prove the equivalent policy for each
supported macOS runtime rather than treating Batocera's success as proof that
two arbitrary indices are interchangeable.

References:

- [Controller identity decision](decisions/console-controller-identity-sdl-gate.md)
- [Mapping profiles decision](decisions/console-controller-mapping-profiles.md)
- [ROMD emulator runtime pattern](../clients/romd_console/docs/emulator-runtime-pattern.md)
- <https://wiki.batocera.org/configure_a_controller>
- <https://github.com/batocera-linux/batocera-emulationstation/blob/master/es-core/src/InputManager.cpp>
- <https://github.com/batocera-linux/batocera.linux/blob/master/package/batocera/core/batocera-configgen/configgen/configgen/controller.py>
- <https://github.com/batocera-linux/batocera.linux/blob/master/package/batocera/core/batocera-configgen/configgen/configgen/generators/libretro/libretroControllers.py>
- <https://github.com/batocera-linux/batocera.linux/blob/master/package/batocera/core/batocera-configgen/configgen/configgen/generators/duckstation/duckstationGenerator.py>
- <https://github.com/batocera-linux/batocera.linux/blob/master/package/batocera/core/batocera-configgen/configgen/configgen/generators/pcsx2/pcsx2Generator.py>

## Sequenced Roadmap

### Slice 1 - Controller-First Players Access And Copy Polish

Status: landed in `f25ca34`

Purpose: close the remaining gap between the advertised Players affordance and
real physical-controller behavior before deeper runtime work.

Scope:

- add a dedicated `ShowPlayersIntent`;
- dispatch it from `GamepadNavigator` on Start/Menu;
- remove the synthetic-key-only contract and resolve the Y/Details collision;
- retain keyboard access as a secondary binding;
- verify controller dismiss/back from Players and the pre-launch surface;
- replace remaining normal-flow `Reserved` copy with consequence-based
  `Waiting` language;
- propagate the active-profile context and explicit profile-switch action
  through global and pre-launch Players entry points;
- replace pre-launch copy that promises the shown emulator order with honest
  session-order/correlation-aware copy;
- keep Attract and profile selection free of Players actions/chrome.

Acceptance criteria:

- A real normalized gamepad event opens Players from Home and an eligible
  pushed route.
- The action does not change the active profile, activate the focused game, or
  open Details.
- The controller can close Players without a keyboard.
- Offline copy names the affected player and explains that others will not take
  the spot.
- Home, global, and pre-launch entry show the same active profile and actions.
- Copy does not promise emulator player order before its adapter policy is
  verified.
- No persistence, schema, launch mapping, or runtime config changes.

Primary risk: choosing a global controller action that conflicts with existing
game actions or OS-reserved buttons.

Evidence (2026-07-10): normalized Start/Menu dispatches `ShowPlayersIntent` on
Home and a pushed route; normalized Y remains Details; physical B dismisses
Players; inactive-profile suppression, active-profile/action parity, Waiting
copy, and correlation-honest pre-launch copy have focused widget coverage.
`mise run analyze` and `mise run test` passed from `clients/romd_console`.

### Slice 2 - Contextual Players Panel And Visual Integration

Status: landed in `83af554`; visual follow-ups through this milestone commit

Purpose: turn the functionally correct full-screen Players route into the
approved polished console panel/sheet while retaining controller focus safety.

Scope:

- present Players as a bounded contextual panel with scrim on normal routes;
- use the same component for conditional pre-launch review;
- retain the active-profile row, P1-P4 state, Change Order, and explicit
  profile switching; controller testing now lives with mapping under Settings
  > Controllers rather than as a separate normalized event log;
- preserve the Home header cluster and clean pushed-route chrome;
- add real-header layout tests for long profile/controller names;
- keep generic controller icons now, with an extensible icon resolver seam.

Acceptance criteria:

- Opening and closing the panel restores focus to the previous route target.
- Controller Back dismisses it consistently.
- Pre-launch Continue/Play anyway returns one explicit disposition plus the
  reviewed snapshot required by Slice 3.
- 1280x720 and couch-distance layouts do not overflow.
- Attract/profile selection remain unchanged.

Primary risk: focus restoration and nested route behavior around Change Order.

Evidence (2026-07-10): normal and pre-launch entry share one bounded,
scrim-backed Players panel while the underlying route remains visible. Closing
with controller Back restores the prior route focus; Change Order returns to
the same panel. The separate Input Test route evidenced in this milestone was
later superseded by the unified exact-controller Test/Map workspace.
Pre-launch approval returns an explicit approved
disposition plus the reviewed snapshot, while Back returns an explicit
dismissed disposition. Focused 1280x720 coverage proves long profile and four
long controller names remain within the panel bounds without overflow. The
Home cluster remains unchanged and its generic icon now uses an injectable
resolver seam. `mise run analyze` and `mise run test` passed from
`clients/romd_console`.

Transition follow-up (2026-07-10): Players and Settings > Controllers now open
Change Order through one shared route whose console background establishes
before the ceremony content fades and subtly scales in. Focused transition
coverage prevents foreground components from preceding the background.

Header follow-up (2026-07-10): the Home header removes the ROMD mark and
persistent button plates. Connected seated controllers render as their own
controller-only Players item; the profile avatar is independently actionable,
opens Settings for now, and carries the ROMD connection badge at its lower
right. Current time is the rightmost item behind an injectable formatter seam
for the planned time-format preference. Resting actions are visually quiet and
gain a plate only while focused.

Header interaction follow-up (2026-07-10): the Players item is anchored to the
true header center. Its focus geometry stays stable; the compact control grows
symmetrically only when additional controller icons appear, without reflowing
the surrounding header actions. Primary tabs now separate exploration from
commitment: focus moves among Home, Library, and Platforms without changing the
active surface, while A, Enter, or pointer activation selects it. The entry
stage and launcher share one root-owned `ConsoleClock`; it translates and
scales into the launcher header during profile selection instead of crossfading
between unrelated clock widgets.

Continue Playing follow-up (2026-07-10): Home now treats recent play as the
user's active rotation instead of another catalog rail. A `YOUR GAMES` eyebrow
and human `IN ROTATION` count replace the technical recent-play caption. Covers
retain a consistent rail rhythm; the focused title alone replaces its standard
platform metadata with an exact last-played date plus platform. Existing
ordering, deduplication, focus memory, and detail-opening semantics are
preserved.

### Slice 3 - Revision-Checked Reviewed Launch Snapshot

Status: landed in `afe1178`

Purpose: make the roster approved in pre-launch review the roster consumed by
launch planning, or force re-review when session/device state changes.

Scope:

- define an immutable reviewed launch snapshot from the canonical resolver;
- include claim and connected-device revisions or an equivalent invalidation
  token;
- pass the snapshot through Game Detail into launch preparation;
- consume it without silently re-resolving to a different roster;
- if state changed, cancel the pending launch and reopen Players review;
- keep mapping lookup failure isolated from seating/snapshot validation.

Acceptance criteria:

- Disconnect, reconnect, provider-id replacement, or claim mutation after
  review cannot launch a different unreviewed roster.
- Direct zero/one-controller launches still use a current canonical snapshot.
- Reserved and blocked holes remain unchanged between review and writer input.
- No durable seating or profile/GUID rows are introduced.

Primary risk: stale snapshots around asynchronous install/runtime selection and
focus-safe return to the review panel.

Evidence (2026-07-10): Players review and direct launches produce immutable
snapshots containing the claim revision, complete ordered provider inventory,
and canonical `ControllerSlotResolution`. The snapshot travels through runtime
preparation; launch validates it before setup and again before adapter start.
Claim mutation, disconnect/reconnect/provider-id replacement, reorder, or a
listing recovery after an unavailable review returns to Players without
starting a process. Focused tests preserve reserved holes and prove the adapter
receives the reviewed resolution unchanged. `mise run analyze` and
`mise run test` passed from `clients/romd_console`.

### Slice 4 - Runtime Input Correlation Contract

Status: landed in this milestone commit; adapter policies remain Slices 5-6

Purpose: replace the current unverified provider ordinal with an honest,
runtime-specific correlation contract and safe writer policy.

Scope:

- extend `RuntimeControllerInputReference` with provider, runtime reference,
  and correlation confidence;
- make RetroArch and DuckStation writers refuse unapproved forced indices;
- preserve automatic emulator order and omit forced indices when custom player
  order cannot be guaranteed;
- preserve automatic/no-claim and no-SDL fallback behavior;
- add instrumentation/probe output usable by runtime research without exposing
  jargon in normal UI.

Acceptance criteria:

- No writer treats ROMD enumeration order as verified by default.
- Each forced device reference is backed by a named adapter policy.
- Uncorrelated paths preserve automatic emulator order without a forced ROMD
  index; they do not silently promise custom order.
- Existing per-player mappings remain independently resolved.
- P1 shortcuts cannot be attached to a known-wrong or promoted controller.

Primary risk: tightening the policy may temporarily reduce custom-order support
for multi-controller launches. Correct degradation is preferred to confidently
wrong routing.

Evidence (2026-07-10): `RuntimeControllerInputReference` now separates provider
kind/id/ordinal from the adapter-native runtime reference and classifies each
entry as `verified`, `acceptedBestEffort`, or `uncorrelated`. Forced references
require a named adapter policy; production launch planning creates honest
uncorrelated entries and exposes structured diagnostic fields without treating
provider order as a runtime id. RetroArch omits joypad-driver indices and pad
shortcuts for uncorrelated entries. DuckStation preserves its automatic
`SDL-0`, `SDL-1` order and suppresses controller-specific P1 shortcuts until a
policy supplies approved runtime references. Focused tests cover contract
equality/diagnostics, uncorrelated fallback, verified policy output, reserved
holes, and P1 shortcut safety.

### Slice 5 - DuckStation Correlation Research And Adapter Policy

Status: researched 2026-07-10; `uncorrelated`; immutable artifact pin implemented

Purpose: determine whether ROMD can safely correlate its exact controller
identity to DuckStation's `SDL-N` references for the provisioned macOS runtime.

Research requirements:

- pin the exact provisioned DuckStation artifact/version;
- record DuckStation's SDL version, backend, hints, and mapping database;
- compare ROMD GUID, serial, name, path, provider id, and order with
  DuckStation's runtime inventory/log/config observations;
- test connection-order reversal, Change Order reversal, reconnect, two
  identical controllers, USB/Bluetooth changes, and no-SDL fallback;
- determine whether an identity-bearing config/API exists or whether a tightly
  controlled same-stack index policy is sufficient.

Possible outcomes:

- `verified`: implement and test the runtime reference;
- `acceptedBestEffort`: requires explicit owner approval with documented
  platform/version limits;
- `uncorrelated`: omit forced `SDL-N` seating and preserve safe defaults.

No new runtime profile id and no passive durable writes.

Evidence (2026-07-10): ROMD pins official DuckStation `v0.1-10998`
(`9b0a4ec55`) by immutable URL and verified archive SHA-256, with the digest in
the managed-install fingerprint. Exact-revision source shows `SDL-N` is a
DuckStation process-local player id: SDL's player index when usable, otherwise
the lowest free id, across both gamepads and fallback joysticks. It is not a
GUID, serial, path, or SDL instance id. ROMD's separate gamepad-only provider
cannot safely correlate physical identity to it. Classification is
`uncorrelated`; no forced-reference policy or owner approval is requested.
DuckStation automatic order and P1-shortcut suppression remain unchanged. The
complete manual hardware matrix is explicitly untested. Full findings and the
reusable research method are under `docs/runtime-controller-policies/`.

### Slice 6 - RetroArch Driver-Aware Correlation

Status: researched 2026-07-10; `uncorrelated`; frontend artifact hash pinned

Purpose: correlate ROMD identity with the actual RetroArch joypad driver rather
than assuming SDL enumeration.

Research requirements:

- pin the supported RetroArch artifact/version and joypad driver per platform;
- inspect the runtime's actual device inventory and autoconfiguration identity;
- determine whether ROMD can select by device identity/reservation or must use
  a verified driver index;
- test both connection orders, reversed seating, reconnect, identical pads,
  reserved offline P1, and P1 shortcut ownership;
- explicitly classify non-SDL/MFi paths.

Acceptance criteria match Slice 4. A policy proven for DuckStation does not
automatically apply to RetroArch.

Evidence (2026-07-10): ROMD's official RetroArch 1.22.2 Metal DMG is pinned by
verified SHA-256 in the install fingerprint. A follow-up closes the legacy
install gap: the RetroArch provisioner now replaces markerless/stale frontends,
verifies the archive before unpack, and writes provenance only after successful
unpack. Exact-tag source and binary
inspection prove the macOS frontend uses Apple GameController/MFi for joypads.
Its runtime index is `GCController.playerIndex` or a reassigned first-free MFi
slot. The MFi autoconfig path exposes a generic name, Apple's nonunique vendor
display name, and zero VID/PID; reservation matches only name/display name or
VID:PID. It exposes no GUID, serial, path, transport, or shared identity ROMD's
independent SDL 3 provider can select. Classification is `uncorrelated`; no
forced index or `acceptedBestEffort` approval is proposed. RetroArch automatic
order and controller-specific P1 shortcut suppression remain unchanged. The
complete hardware matrix and all non-MFi drivers are explicitly untested. Full
findings are under `docs/runtime-controller-policies/`.

### Slice 7 - Complete Phase 6B Release Gate

Status: blocked on Slices 4-6

Purpose: declare the existing per-player launch mapping contract releasable
only for adapters whose physical routing policy is accepted.

Acceptance criteria:

- each connected controller entry resolves mapping against that controller's
  GUID and the one active local profile;
- reserved and blocked holes remain non-compact;
- P1 alone owns shortcuts;
- verified adapters reproduce the displayed seating on hardware;
- unsupported adapters degrade according to the approved policy;
- no-SDL fallback remains honest and compatible;
- full console validation passes.

### Slice 8 - PCSX2 Runtime Research Gate

Status: research completed 2026-07-14; both the same-GUID and exact
target-domain experiments were rejected by owner hardware evidence

Purpose: document the exact controller configuration contract for ROMD's
provisioned PCSX2 version. Research does not authorize production mapping.

Research requirements:

- controller keys and port/multitap numbering;
- SDL/device reference semantics and correlation with Slice 4;
- first-run/Qt overwrite behavior;
- stale-key clearing and idempotence;
- one/two-controller, reversed-order, reserved-hole, and reconnect behavior;
- exact ROMD-scoped user-directory behavior.

Exit criteria:

- reproducible evidence and proposed writer fixtures are documented;
- blockers are explicit;
- a separate owner approval is obtained before editing PCSX2 mapping output;
- `pcsx2:ps2:standalone` remains unchanged.

Initial audit boundary:

- the current macOS 2.6.3 asset is version-addressed but lacks an archive
  SHA-256, so controller research does not yet have a frozen byte envelope;
- the writer does not accept `LaunchControllerSetup`, and no production code
  may re-enumerate devices to compensate;
- observed `SDL-N` vocabulary is process-local and cannot authorize
  deterministic multi-pad routing; a singleton policy still requires clean
  runtime evidence;
- complete Pad/hotkey key ownership, stale clearing, first-run/shutdown rewrite
  behavior, axes/triggers/hats/rumble syntax, and ROMD-scoped save/state paths
  remain separate research items;
- this audit changes no PCSX2 profile, adapter, writer output, or artifact.

Research resolution:

- Batocera's controller generator is the behavioral reference, pinned for this
  audit to commit `965190aa9513bb32ad04597be1ba764591e6426f`. ROMD will
  independently implement the compact mapping contract rather than copy GPL
  generator code;
- the official PCSX2 2.6.3 macOS archive is 28,960,388 bytes with SHA-256
  `cb7b9e6330f1abf0cf92c94065f7eb983d0fa8affcfe6b0ccb9c2a4ebf067f1a`.
  The executable and bundled SDL 3 library in the existing managed install
  matched the files extracted from that archive byte-for-byte;
- the installed runtime links SDL 3 and accepts `SDL_GAMECONTROLLERCONFIG`, and
  its log observed the sole connected pad as `SDL-0`. Those facts established
  a viable experiment but did not authorize correlation because the ordinal
  and SDL domain are process-local;
- the first owner run exposed a cross-process identity mismatch: arm64 ROMD's
  MFi SDL path identified the Pro 2 as `030001f2...026800`, while x86_64 PCSX2
  under Rosetta opened the same unit through IOKit/HID against its
  `03000000...010000`/`...020000` mappings. Loading ROMD's user database was
  therefore not proof that its GUID entry matched PCSX2's device. The bounded
  experiment disabled PCSX2's IOKit driver and retained its MFi driver, but
  PCSX2 still opened the 19-button HID domain; no driver-forcing policy is
  approved or emitted;
- Batocera's positional baseline remains authoritative: canonical
  South/East/West/North map to PCSX2 Cross/Circle/Square/Triangle for every
  label family. Nintendo-style changes the displayed label at South to B; it
  never changes South's physical or runtime meaning;
- Batocera clears and reconstructs Pad sections on each launch. That is useful
  syntax and ownership evidence for an appliance that controls its complete
  input domain, but it does not authorize ROMD to replace PCSX2 Pad sections;
- the managed 2.6.3 `-testconfig` command exits successfully but did not create
  `PCSX2.ini` in an isolated user directory. ROMD's writer must create required
  configuration deterministically instead of depending on that side effect;
- Batocera's multitap and `SDL-N` placement depend on its appliance-owned
  controller order. They are evidence for PCSX2 syntax only and do not approve
  equivalent ROMD multiplayer routing;
- Batocera emits PCSX2 controller hotkeys as keyboard actions. The historical
  experiment was limited to gameplay mapping; it did not include controller
  shortcut chords, and neither surface is approved for production emission;
- the owner approved a bounded single-controller hardware experiment on
  2026-07-14. That approval did not survive the input-domain mismatch found by
  the experiment.

Subsequent target-domain resolution:

- a Cocoa-hosted x86_64 helper loading PCSX2's exact bundled SDL 3.2.26 under
  Rosetta observed the USB Pro 2 as target GUID
  `0500b7b5ac05000004000000452f6d04`, VID/PID/version
  `05ac:0004:2f45`, with 6 axes, 19 buttons, and no hats, path, or serial;
- a plain command-line host and a Cocoa host that did not service the run loop
  both saw zero controllers, making the AppKit/run-loop context part of the
  verified target domain;
- attributed capture of every physical control established a complete
  source-MFi-to-target-HID raw-input translation. In particular, physical
  South/East/West/North are target buttons `3/2/8/7`; PCSX2's selected generic
  mapping used `2/3/7/8`, exactly explaining both reversed face-button pairs;
- a production-shaped experiment then re-verified that complete envelope
  immediately before launch and supplied the corrected mapping only to that
  process. The owner still observed incorrect face buttons in a PS2 game;
- therefore helper-side target-domain capture does not authorize PCSX2
  emission. PCSX2's complete Qt/input stack remains behaviorally distinct from
  the observable helper surface even when artifact, SDL, hints, device,
  topology, and mapping all match.

- `artifact resolver: yes - the existing PCSX2 2.6.3 managed artifact is cryptographically pinned; the pin does not authorize controller emission.`
- `config writer: yes - the PCSX2 writer removes only ROMD's rejected framed mapping and preserves PCSX2-owned input configuration.`

### Slice 9 - PCSX2 Mapping Implementation

Status: custom mapping rejected 2026-07-14; fail-closed rollback restored;
ordinary PCSX2-owned input remains available

Final exact-domain attempt and rejection:

- the experimental policy was restricted to the managed PCSX2 2.6.3 artifact,
  exact bundled SDL/database fingerprints, arm64 macOS host, x86_64/Rosetta
  runtime, one USB 8BitDo Pro 2, exact source and target identities, complete
  captured capabilities, and one P1;
- the helper re-observed the target domain immediately before launch and the
  adapter translated every saved source raw input into its attributed target
  raw input. Provider ordinals were never used as PCSX2 indices;
- the corrected SDL mapping and backend hints were launch-scoped. The writer
  temporarily replaced the complete `InputSources`, `SDLHints`, `Pad`, and
  `Pad1` surface and restored the owner's sections after process exit;
- automated mapping/config bytes and native packaging all passed, but the
  owner hardware run still produced incorrect face buttons;
- the experimental verifier, helper, policy id, mapping translation, Pad
  emission, and tests were removed. Schema remains version 10;
- do not attempt another PCSX2 config-writer calibration. Reconsider only if
  PCSX2 exposes a supported deterministic external input contract verifiable
  in the actual emulator process, or ROMD owns the integration deeply enough
  to eliminate the cross-process ambiguity.

Rejected experiment envelope (historical, not a production policy):

- only the managed, digest-pinned PCSX2 2.6.3 macOS runtime is eligible;
- only one controller in a reviewed, inventory-available, exact SDL snapshot
  may correlate to PCSX2 `SDL-0`/Pad1;
- gameplay emission additionally requires a saved physical-to-canonical map
  whose SDL platform and GUID match that controller;
- the writer binds the DualShock2 gameplay surface to canonical SDL names and
  injects the saved mapping through PCSX2's user controller database and
  `SDL_GAMECONTROLLERCONFIG`;
- an approved launch disables PCSX2's IOKit driver and enables its MFi driver
  before SDL initialization, matching the driver domain that produced ROMD's
  saved GUID and raw inputs;
- controller shortcuts remain disabled;
- fallback identity, missing or mismatched maps, external executables, other
  operating systems, and every multi-controller session suppress the custom
  mapping. Suppression actively removes ROMD's prior singleton database entry
  and Pad override;
- no ROMD player slot or provider ordinal is presented as a PCSX2 port;
- missing canonical inputs remain unavailable rather than being recovered from
  PCSX2's bundled device database;
- focused coverage must prove exact INI/environment bytes, idempotence,
  singleton-to-suppressed cleanup, launch-provider gating, adapter propagation,
  and repository-to-runtime integration;
- live acceptance uses Final Fantasy X to cover face positions, both sticks,
  shoulders/triggers, Start/Select, one saved remap, persistence, restoration,
  and clean return to ROMD.

Rejection resolution:

- forcing PCSX2's IOKit setting off and MFi setting on did not converge the
  runtime domain: PCSX2 still opened a 19-button HID device while ROMD observed
  the same Pro 2 as a 15-button MFi device;
- the attempted face mapping remained reversed from canonical positions, so no
  PCSX2 controller policy id is active and the provider keeps PCSX2
  `uncorrelated`;
- the adapter injects no `SDL_GAMECONTROLLERCONFIG` and the writer emits no Pad
  bindings;
- the writer preserves PCSX2-owned `InputSources`, `Pad`, and `PadN` sections,
  while removing only ROMD's explicitly framed database entry from the rejected
  experiment;
- the canonical editor and stored mapping remain unchanged. Button-label style
  stays presentation-only;
- the second experiment recorded the SDL build/library, process architecture,
  backend/driver, transport/mode, runtime artifact, GUID, raw capability
  surface, and full physical translation for both sides. The real emulator
  still disagreed, proving those observations are necessary but insufficient;
- controller names, VID/PID values, GUIDs, helper enumeration, or attributed
  raw capture must never create a PCSX2 alias. Reconsideration requires a
  supported deterministic contract verifiable inside the actual emulator
  process, not another config-writer calibration.

Automated evidence (2026-07-14):

- Flutter analysis: no issues;
- focused fail-closed PCSX2 writer, adapter, launch-provider, and
  repository-to-runtime integration matrix: 52 tests passed;
- full ROMD Console suite: 685 tests passed;
- the Flutter macOS release build passed, strict deep signature verification
  passed, and the rejected PCSX2 helper is absent from the app bundle;
- runtime negative searches found no shell-string launch and no durable
  `contentRoot` save/state/config path;
- `git diff --check`: clean;
- schema remains version 10, with no Drift declaration, migration, or generated
  file changes;
- rollback source matches committed fail-closed behavior; documentation commit
  is pending explicit owner request;
- target-domain raw capture passed for every canonical control, but the final
  owner in-game run still produced incorrect face buttons and rejected the
  calibrated approach;
- initial owner Final Fantasy X run: PCSX2 loaded ROMD's user database but
  opened the controller through its different IOKit/HID identity, so the saved
  GUID mapping did not apply. A PCSX2-local face remap exposed the mismatch.
  The subsequent MFi-forced run still used the different 19-button HID domain
  and produced reversed face positions, conclusively rejecting the experiment.

Existing PCSX2 memory cards, save states, screenshots, and settings still live
under its shared ROMD runtime user directory instead of the per-title
`saveRoot`, `stateRoot`, and `configRoot`. Moving those persisted files requires
a migration-safe follow-up and remains blocking for declaring the complete
PCSX2 runtime contract closed; it is not silently mixed into controller
mapping.

### Slice 10 - Nintendo-Native Action Semantics

Status: approved future independent slice; schedule after Slice 1 resolves the
global action map

Purpose: let Nintendo-layout controllers use east/A for Select and south/B for
Back as a behavioral preference, distinct from Button labels.

Rules:

- preference is tied to controller GUID;
- passive enumeration creates no row;
- hints continue to derive from normalized physical positions;
- other GUIDs and fallback/no-SDL behavior do not change;
- Button labels remains display-only.

Required coverage:

- activation and dismissal throughout launcher routes;
- footer hints;
- coexistence with global Players access;
- a second controller with a different GUID;
- persistence round trip and no-passive-write behavior.

### Future Backlog

These are intentionally unscheduled:

- explicit mapping/layout editor that activates profile/GUID `templateId`;
- controller-family-specific icon catalog;
- optional controller-to-profile affinity after explicit owner action;
- raw SDL joystick support outside the standard gamepad API;
- platform-level mapping scope unless real duplication proves it necessary;
- virtual-controller output as a possible long-term deterministic routing
  strategy;
- live reseating of an already-running external emulator.

## Validation Matrix

Default console validation runs from `clients/romd_console`:

```bash
mise run analyze
mise run test
```

If validation fails unexpectedly, read `../../docs/known-issues.md`. If the
only failure is `Operation not permitted` while Flutter/Dart writes its SDK
cache, rerun the same command with scoped escalation and report both results.

| Slice | Focused automated evidence | Manual evidence | Full validation |
| --- | --- | --- | --- |
| 1. Players access/copy | physical normalized event -> `ShowPlayersIntent`; Y/Details regression; controller dismiss; attract suppression; copy tests | real controller opens/closes Players on Home and pushed route | analyze + test |
| 2. Contextual panel | focus restoration; nested Change Order/Tester routes; pre-launch result; 1280x720 real-header golden/layout coverage | couch-distance traversal and Back behavior | analyze + test |
| 3. Reviewed snapshot | revision invalidation; direct/review paths; reconnect/provider replacement race; reserved/blocked parity | change a device between review and launch | analyze + test |
| 4. Correlation contract | confidence model/equality; writer gating; no-claim/no-SDL compatibility; P1 shortcut safety | inspect probe output on supported host | analyze + test |
| 5. DuckStation policy | pinned-version fixtures; runtime inventory parser; writer output for verified/uncorrelated states | reversed order, identical pads, reconnect, USB/Bluetooth | analyze + test when code changes |
| 6. RetroArch policy | driver-specific fixtures; autoconfig/reservation tests; writer gating | each supported driver plus P1 shortcuts | analyze + test when code changes |
| 7. Phase 6B release | two-GUID per-player mapping; failure isolation; reviewed snapshot; reserved/blocked holes; adapter policies | complete emulator/hardware matrix | analyze + test |
| 8. PCSX2 research | none required unless a probe/helper is added | controlled one/two-pad config experiments | useful searches; code validation only if code changes |
| 9. PCSX2 implementation | writer bytes/sections; stale-key clearing; idempotence; adapter launch plan; negative paths | provisioned runtime matrix | analyze + test |
| 10. Nintendo actions | GUID isolation; persistence; select/back actions; hints; no-passive-write; fallback | Nintendo and non-Nintendo controllers together | analyze + test |

### Always-Required Regression Coverage

- no-claim connection order;
- fallback/name-only claims;
- exact GUID+serial claims;
- exact offline P1 with connected P2;
- blocked same-name ambiguity;
- two identical controllers with and without usable serials;
- disconnect/reconnect with a changed provider id;
- Change Order cancel and empty Ready;
- mapping-store failure independent of seating;
- seating/listing failure independent of mapping;
- P1 shortcut ownership;
- structured commands and ROMD-owned save/state/config roots.

### Hardware Release Matrix

- 8BitDo Pro 2 in D-input mode;
- 8BitDo Pro 2 in X-input mode;
- Xbox-style controller;
- DualSense over USB and Bluetooth;
- two identical controllers;
- mixed controller families;
- fallback/no-SDL path.

Record provider, GUID, serial availability, path if any, event identity,
connection order, reconnect behavior, ROMD seating, emulator seating, and P1
shortcut owner. Any physical mismatch blocks forced seating for that adapter
policy.

## Risk Map

| Risk | Boundary | Mitigation |
| --- | --- | --- |
| UI and launch disagree | session projection | one canonical resolver, revision-checked reviewed snapshot, and parity tests |
| mixed provider ids after SDL failure | provider lifecycle | atomic one-way failover for listing and events |
| same-model controller collision | exact identity | GUID+serial; honest ambiguity when unavailable |
| persisted seating reappears | schema/session boundary | no seating table; schema tests; attract reset |
| passive observation creates or removes intent | mapping persistence | opening stays write-free; explicit Save/Use detected persists atomically; removal is explicit |
| wrong physical runtime pad | adapter/writer boundary | correlation confidence and writer gating |
| runtime update invalidates policy | artifact resolver | pin artifact/version and policy fixtures |
| global Players action conflicts | input IA | dedicated intent and owner-approved physical binding |
| generated Drift corruption | persistence | edit declarations/migrations and regenerate; never hand-edit |
| PCSX2 scope creep | runtime gate | research first; separate implementation approval |

## Explicit Non-Goals

- durable P1-P4 seating;
- automatic controller-to-profile association;
- changing the active library when Change Order changes P1;
- a PlayStation-style account prompt for every additional controller;
- emulator/runtime configuration in normal Players UI;
- profile/GUID rows from enumeration;
- renaming runtime profile ids;
- broad SDL joystick support in the current roadmap;
- PCSX2 mapping before its research and implementation approvals;
- pretending an unverified runtime index is exact;
- reseating a running emulator process;
- controller-specific art as a prerequisite for correct seating.

## Owner Approval Ledger

Accepted:

- `Controls` becomes `Shortcuts`; destination title is `In-game shortcuts`.
- `Button Label Style` becomes `Button labels` and explicitly says it does not
  change button behavior.
- Player state is available from the top Home/header experience and through a
  controller-first route-independent action, without attract chrome.
- Ambiguity defaults into Players/controller setup.
- Empty Change Order Ready may return to automatic order without a second
  confirmation.
- Runtime correlation is a release prerequisite for Phase 6B.
- Profile/GUID template activation requires explicit user interaction.
- Nintendo-native action semantics is a GUID-scoped setting, not display
  preference.
- PCSX2 research and two mapping experiments are complete and rejected by
  owner hardware evidence. Another config-writer calibration is not eligible;
  reconsideration requires the supported actual-process contract defined in
  Slice 9.
- Change Order never switches the active profile/library.
- Additional controllers are session guests.
- Controller-to-profile affinity remains deferred.
- Start/Menu is the global physical-controller Players action; Y remains game
  Details.
- When an adapter is uncorrelated, ROMD uses the safe best-effort fallback:
  omit forced indices, preserve automatic emulator order, and avoid promising
  custom runtime seating.

Open approvals:

1. Later, decide whether mapping reuse may broaden beyond exact SDL GUID to a
   compatible layout family.

## Parallelization

After Slice 4 defines the shared correlation contract, DuckStation and
RetroArch work may proceed in parallel because their runtime artifacts,
evidence, and adapter policies are distinct. PCSX2 research and both rejected
experiments are historical evidence, not an active parallel workstream. Any
future reconsideration must first satisfy Slice 9's supported actual-process
contract gate and may not begin as config-writer implementation.

Nintendo action semantics may run independently of runtime research after
Slice 1 fixes the shared input-intent map.

Do not parallelize:

- input-intent changes and the same global navigation tests;
- `ControllerAssignments`, `PlayersProjection`, and launch parity changes;
- `LaunchControllerSetup` contract edits and config-writer migrations;
- Drift schema declarations and generated database artifacts;
- shared full-app shell widget tests.

## Amendment Protocol

For every landed slice:

1. change its status and record the commit;
2. record exact validation evidence without overclaiming;
3. update acceptance criteria only when behavior actually changes;
4. preserve the owner ledger and non-negotiable contracts;
5. move a durable validation/environment fact to `docs/known-issues.md` only
   when it is genuinely new;
6. keep ordinary implementation history in commits rather than expanding this
   roadmap back into a chronological run log.
