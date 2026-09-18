# RetroArch macOS 1.22.2 Metal Controller Policy Research

Status: current; multi-controller classification `uncorrelated`;
single-controller port policy implemented; calibrated 8BitDo shortcut policy
owner hardware accepted

Validated: 2026-07-10

Amended: 2026-07-13 (automated evidence, reproduced hardware calibration, live
ROMD acceptance, and N64 adapter-policy diagnosis)

## Frozen Runtime Envelope

- Official asset: <https://buildbot.libretro.com/stable/1.22.2/apple/osx/universal/RetroArch_Metal.dmg>
- Archive SHA-256: `81b79121ba26d539064ae13b4d0419a120c3d165afbe656cf5f5412b15fdb434`.
  ROMD verifies it before unpacking and includes it in the install fingerprint,
  so changed bytes at the stable URL fail closed.
- Source: [v1.22.2 commit `69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576`](https://github.com/libretro/RetroArch/tree/69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576).
- Bundle: `com.libretro.dist.RetroArch`, version `1.22.2`, executable
  `Contents/MacOS/RetroArch`, minimum macOS 10.13.
- Observed managed executable SHA-256 during shortcut calibration:
  `ed90b54434a2899de0ddbfed59f335255fb462c8691bd970a1a761ebb5656d65`.
- Universal `arm64`/`x86_64`; macOS 15.0 SDK, Xcode 16.0, build OS `23H626`.
- Hardened-runtime signature, team `UK699V5ZS8`; observed CDHash
  `8fd39d6855e1239844451a2db554300f3bd655d0`.
- Joypad backend: Apple GameController/MFi. The executable also incorporates
  SDL 2.30.6 (`SDL-2.30.6-ge310f25`) for other facilities, but SDL is not this
  build's default macOS joypad identity provider.
- ROMD provider: a separate managed SDL 3 gamepad provider in another process.
- Production acceptance is limited to ROMD's managed artifact path with this
  pinned digest and install fingerprint. A locally discovered frontend or
  developer override may exercise the adapter, but it is outside this hardware
  envelope and cannot supply acceptance evidence for the named policy.
- The current Mupen64Plus-Next core is fetched from a mutable nightly `/latest`
  artifact with no digest. It does not construct the frontend's joypad indices,
  but its unfrozen RetroPad-to-N64 behavior is an explicit reproducibility and
  acceptance boundary.
- Inspection host: macOS 26.5.2 build 25F84, arm64. A later partial 8BitDo USB
  Gate 0 trial is recorded below.

## Primary-Source Analysis

The Metal build enables MFi, which RetroArch selects as the default joypad
driver. See [default selection](https://github.com/libretro/RetroArch/blob/69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576/configuration.c#L670-L715)
and [driver registration](https://github.com/libretro/RetroArch/blob/69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576/input/input_driver.c#L270-L285).

The MFi driver consumes `[GCController controllers]` and addresses input by
`GCController.playerIndex`. At connection it accepts that index when free or
assigns the first free slot from zero through three; disconnect frees the slot.
See [MFi connection and reassignment](https://github.com/libretro/RetroArch/blob/69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576/input/drivers_joypad/mfi_joypad.m#L500-L565).
`input_playerN_joypad_index` therefore selects a runtime-local MFi slot, not an
SDL instance id, ROMD ordinal, GUID, serial, or device path.

MFi autoconfiguration identifies each device as `mFi Controller`, uses Apple's
`vendorName` only as display name, and passes null config plus zero VID/PID.
The driver name callback also returns `mFi Controller`. See [autoconfiguration](https://github.com/libretro/RetroArch/blob/69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576/input/drivers_joypad/mfi_joypad.m#L320-L330)
and [driver descriptor](https://github.com/libretro/RetroArch/blob/69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576/input/drivers_joypad/mfi_joypad.m#L850-L880).

Device reservation accepts exact device/display name or VID:PID. See
[reservation matching](https://github.com/libretro/RetroArch/blob/69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576/tasks/task_autodetect.c#L440-L525).
For MFi this provides no GUID, serial, path/location, transport, or physical
identity. Duplicate-name indices are derived from current runtime port order.

Other compiled joypad drivers are separate envelopes. ROMD does not force a
driver, so HID, SDL, or any non-MFi path remains explicitly unclassified and
must fail closed rather than inherit an MFi conclusion.

## Probe Method And Commands

The DMG was inspected read-only with `shasum -a 256`, `hdiutil attach
-readonly`, `plutil -p`, `file`, `lipo -archs`, `otool -L`, `codesign -dvvv`,
and `strings`. Exact-tag source was inspected with `rg` and bounded reads. No
ROMD seating, mapping, profile, or schema writes occurred.

Verbose `[MFI]` and `[Autoconf]` logs can capture connection order, selected
slot, reassignment, vendor name, product category, capabilities, and
disconnect. They expose no ROMD SDL GUID/serial, device path, transport, or
identity-bearing reservation key. A future read-only probe should combine
those logs with `RuntimeControllerInputReference.diagnosticFields`, redact
unrelated paths, and never classify automatically.

## Candidate Rule And Falsification

Candidate: ROMD SDL observation ordinal equals RetroArch's MFi joypad index and
can be reserved by shared device identity.

Falsified structurally. The processes use different provider stacks and
inventories. RetroArch derives its index from Apple `GCController.playerIndex`
or a reusable first-free MFi slot; ROMD observes SDL 3 gamepad order.
RetroArch's MFi reservation data contains only generic/display name and zero
VID/PID. Reconnect, duplicates, filtering, or event timing can change one
ordinal without changing the other.

## Complete Matrix Summary

One partial physical case was executed after the original correlation research:
an 8BitDo Pro 2 over USB with device mode unknown, exact RetroArch 1.22.2, and
successful IOHID seizure. It failed Gate 0 because RetroArch received physical
input. Still untested: explicit D-input/X-input modes; mixed families A/B and
B/A; reversed Change Order; reconnect with and without replaced ROMD
provider id; identical controllers with and without serials; DualSense USB and
Bluetooth; mixed transport; connection before/after RetroArch start; offline
P1; same-name ambiguity; fallback/no-SDL; RetroArch-only restart; and ROMD-only
restart. The 8BitDo D/X-input, Xbox-style, DualSense, identical, mixed-family,
and fallback hardware was unavailable.

The provider/identity mismatch prevents `verified` multi-controller identity
correlation. Successful ordinal alignment on hardware would not establish
shared identity or distinguish identical controllers.

## Mismatches And Untested Cases

Blocking mismatch: ROMD observes SDL 3 identity while RetroArch observes Apple
GameController objects and exposes only reusable MFi slots and nonunique names.
Manual inventory, observed ports, and shortcut-owner evidence is absent. All
non-MFi drivers are outside this envelope.

## Classification And Rationale

Standing multi-controller classification: `uncorrelated`.

There is no supported shared identity-bearing selector, and the hardware matrix
is absent. This is not a request for `acceptedBestEffort` approval.

The single-controller amendment does not correlate two identity spaces. With
one physical controller in the approved launch snapshot, RetroArch's only input
is that controller by cardinality. That narrower fact can establish port 1
without treating ROMD's provider ordinal as RetroArch identity.

## Exact Policy Rule And Single-Controller Amendment

Policy id: `retroarch-macos-single-controller`.

ROMD classifies one reference `verified` with runtime reference `0` only when
all of these conditions hold:

1. launch received a supplied, revision-checked `ReviewedLaunchSnapshot`;
2. its provider inventory is available and contains exactly one physical
   controller;
3. that controller came from the SDL provider and has exact nonempty GUID and
   serial identity;
4. the host operating system is macOS and the selected adapter is RetroArch;
5. the reference belongs to that same resolved controller entry.

The classification never reads a live writer-time count. Fresh listings only
validate the reviewed inventory before mapping resolution and again before the
process starts. The runtime reference is the cardinality result `0`, never the
ROMD provider ordinal.

An offline exact reservation remains a ROMD session-slot hole and does not add a
physical controller. The sole connected controller keeps its ROMD player slot,
while the writer projects it to RetroArch port 1 with
`input_player1_joypad_index = "0"`. It does not compact or rewrite session
seating.

Zero or multiple controllers, null-snapshot/live resolution, unavailable
inventory, fallback/no-SDL devices, GUID-only devices, Linux, PCSX2, unknown
adapters, and any wrong/partial policy shape remain `uncorrelated`. Multi-pad
name, display name, duplicate-name suffix, MFi slot, VID:PID, and provider
ordinal must not be promoted to identity.

Calibrated shortcut policy id:
`retroarch-macos-single-controller-mfi-8bitdo-pro2-usb`.

That narrower policy additionally requires ROMD's observed SDL identity to be
display name `8BitDo Pro 2`, GUID
`030001f2c82d00000660000000026800`, and a nonempty serial. It retains runtime
reference `0` and authorizes only the physical MFi positions measured below.
The serial is required for exact singleton identity but is not pinned to one
unit. Any other name/GUID, driver, transport, platform, or runtime envelope
stays on the port-only policy with shortcuts suppressed.

## Safe Fallback Behavior

For populated setups outside the exact rule, ROMD omits
`input_playerN_joypad_index`, preserving automatic RetroArch order. It emits no
controller-specific hotkeys. Per-controller mapping resolution remains separate
and cannot promise physical routing. No-SDL/fallback and non-MFi drivers remain
`uncorrelated`.

The generic singleton policy emits the port-1 selector, but its shortcut
authorization is false. ROMD writes `input_enable_hotkey_btn` and managed
`input_*_btn` actions as `"nul"` because RetroArch consumes physical button
indices or hat syntax, not ROMD's positional/RetroPad indices.

The calibrated 8BitDo policy emits only positions proven twice under MFi:
Select `2`, Start `3`, Left/Right shoulder `10`/`11`, D-pad left/right `6`/`7`,
and face North/X `1`. A user mapping that asks this policy for another physical
position is explicitly nulled rather than falling back to RetroPad-derived
guesses. Its generated config explicitly pins `input_joypad_driver = "mfi"`;
generic single-controller, multi-controller, fallback, and legacy configs do
not add that driver override. The legacy wholly unresolved no-controller path
is retained for compatibility; it is not evidence for either singleton policy.

Connecting a second controller after process start is outside the reviewed
snapshot promise. ROMD does not reclassify or rewrite the running config;
RetroArch owns subsequent hotplug behavior.

### Canonical gameplay refinement gate

The device-global canonical mapping added in Slice 4 is SDL raw-source data.
It does not broaden either current RetroArch singleton policy: the pinned
runtime consumes Apple GameController/MFi positions, and the existing
8BitDo policy authorizes only the seven reproduced shortcut values above.
Automated byte-parity coverage proves that attaching a populated canonical
gameplay mapping changes no RetroArch output for generic singleton, calibrated
singleton, or multi-controller setups.

The duplicated calibration gate completed on 2026-07-13. The approved gameplay
policy id is
`retroarch-macos-single-controller-mfi-8bitdo-pro2-usb-gameplay-v1`. It requires
explicit gameplay authorization distinct from `controllerShortcutsAllowed` and
inherits the managed-artifact, macOS, MFi, reviewed-singleton, exact
name/GUID/serial, and runtime-reference-zero requirements above. It must never
authorize another controller, transport, frontend, driver, or a multi-pad
launch.

The writer owns all 48 port-1 gameplay alternatives: `_btn` and `_axis` for
the 16 digital RetroPad controls, plus `_btn` and `_axis` for both directions
of both stick axes. A successfully authorized projection writes every owned
key. An absent canonical binding writes `"nul"` to both alternatives. Any
present gameplay input outside the duplicated crosswalk rejects the complete
custom projection; partial custom/autoconfig mixtures are prohibited. Guide is
frontend-only and is never emitted as ordinary gameplay. Multi-controller
correlation remains out of scope and `uncorrelated`.

### Approved N64 adapter/core policy

N64 adds a second mapping layer that must not be folded into the physical
controller setup:

```text
physical controller -> ROMD canonical RetroPad -> N64 virtual controls
```

ROMD's controller map owns only the first arrow. The RetroArch adapter and its
selected core policy own the second. The approved modern-controller layout is:

- canonical South / RetroPad B -> N64 A;
- canonical West / RetroPad Y -> N64 B;
- left stick -> N64 control stick;
- right stick -> the four C buttons;
- left trigger / RetroPad L2 -> N64 Z;
- left and right shoulders -> N64 L and R;
- D-pad and Start -> the corresponding N64 controls.

For each N64 title, the writer owns a core-options file under `configRoot` with
`mupen64plus-alt-map = "False"` and explicit right/left/down/up C-button values
`C1`/`C2`/`C3`/`C4`. It also owns an isolated remap directory under that
title's `configRoot`, disables automatic remap loading with
`auto_remaps_enable = "false"`, and disables remap persistence with
`remap_save_on_exit = "false"`. The generated N64 config also disables global
override loading with `auto_overrides_enable = "false"` and disables
per-game core-option discovery with `game_specific_options = "false"`.
Global RetroArch overrides, core options, and remaps must not be allowed to
redefine this canonical-to-N64 policy.

This policy is not frozen while the Mupen64Plus-Next dependency continues to
come from mutable nightly `/latest` without a digest. Pinning and versioning
that core through the existing resolver/provisioner is required before ROMD
can claim reproducible N64 control semantics.

- `artifact resolver: yes - the current Mupen64Plus-Next core is fetched from mutable nightly /latest with no digest, so a reproducible N64 control policy requires pinning/versioning the core artifact in the existing resolver/provisioner before claiming frozen behavior.`
- `config writer: yes - the RetroArch writer must emit ROMD-owned N64 core options and fail-closed remap paths so global RetroArch state cannot change the canonical RetroPad-to-N64 policy.`

## Invalidation And Revalidation Triggers

Re-research is required for frontend artifact/source, GameController/MFi or
other joypad driver, macOS/architecture, reservation semantics, ROMD provider
enumeration, relevant autoconfiguration, or hardware mismatch changes. A future
identity-bearing selector or exact shared Apple identity provider still needs a
new frozen envelope and complete matrix.

The singleton policy additionally invalidates if snapshot validation,
GUID+serial exactness, host-OS gating, adapter id, runtime-reference semantics,
or the managed artifact path changes. The calibrated shortcut policy also
invalidates if the exact SDL name/GUID, USB transport, MFi driver, or any saved
physical value changes.

## Automated Fixtures And Tests

Catalog coverage pins the frontend digest and its install fingerprint. The
RetroArch provisioner now requires a matching frontend marker, replaces
markerless or stale app bundles, verifies the configured archive digest before
unpack, and writes the marker only after successful unpack. Focused tests cover
cold/current/markerless/stale installs and digest rejection. For this change,
the RetroArch writer suite covers exact singleton port-1 output,
reserved-hole projection, multi-pad and fallback parity, wrong policy/provider/
correlation rejection, generic shortcut suppression, calibrated physical
values, screenshot `1` regression, and fail-closed unmeasured positions. The
provider suite
covers snapshot-only cardinality, exact identity, macOS gating, Linux/null-
reference branch was added: the production classifier and writer policy admit a
singular own-policy reference by construction. No parser was added because MFi
logs expose no correlatable identity key.

The gameplay-v1 fixtures cover the exact 48-line golden block, button/hat/axis
swaps, stick source swaps and inversion, full/positive trigger forms, explicit
unbinds, stale clearing, invalid-source whole-projection suppression, metadata
and identity mismatch, permission independence, old-policy parity,
multi-controller suppression, and persisted SQLite mapping through the launch
provider into config bytes. The 2026-07-13 focused matrix passed 102 tests and
the N64-inclusive focused matrix later passed 83 tests, including all 36
RetroArch writer tests. The full console suite passed 656 tests; analysis was
clean.

## Manual Hardware Evidence

One partial hardware case now exists. A 2026-07-10 empty-controller baseline against the exact
reprovisioned frontend directly confirmed joypad driver `mfi` and emitted no
controller connection/autoconfiguration records. Independent controller
inventories were empty. `--max-frames` did not bound menu mode, and verbose
startup consulted default user locations despite a temporary config argument;
raw logs containing unrelated identifiers were not retained. This observation
does not change the `uncorrelated` classification or satisfy any hardware case.

An 8BitDo Pro 2 over USB was then observed as one physical HID device, one
Apple synthetic compatibility HID device, one GameController object, and one
ROMD SDL gamepad with exact GUID+serial identity. Device mode was unavailable
and remains unknown. During successful exclusive seizure of the physical IOHID
candidate, ROMD received 42 callbacks, RetroArch assigned MFi index 0 / port 1,
and the owner directly observed menu movement from button presses. Physical
input therefore bypassed the assumed seizure boundary. This is a blocking
Gate 0 mismatch; it does not change the physical-identity correlation
classification from `uncorrelated`.

### Completed owner acceptance: port 1 and calibrated shortcuts

Run this against the managed pinned artifact, not a discovered local frontend
or developer override:

1. Quit RetroArch. Disconnect every game controller, then connect only the
   8BitDo Pro 2 by USB. In ROMD, open Settings > Controllers > the connected
   controller > Test and confirm one exact SDL pad responds. Do not connect
   another pad during the run.
2. Select a known working managed RetroArch title, record its ROMD title config
   root, and back up any existing `<configRoot>/retroarch.cfg`. Confirm the
   resolved frontend is the managed 1.22.2 artifact with the digest above.
3. Launch through ROMD. While the game is running, read the generated
   `<configRoot>/retroarch.cfg`. It must contain
   `input_player1_joypad_index = "0"`, contain no
   `input_player2_joypad_index`, retain `config_save_on_exit = "false"`, and
   contain modifier/menu/save/load/slot next/slot previous/screenshot values
   `2`/`3`/`11`/`10`/`7`/`6`/`1` respectively.
4. In a title where player/port is observable, exercise the d-pad, both sticks,
   face buttons, shoulders, triggers, Start, and Select. Record that the only
   physical pad controls port 1. Exercise Select+Start, Select+R, Select+L,
   Select+D-pad right/left, and Select+X; confirm menu, save, load, next/previous
   slot, and screenshot respectively, with no bare action button firing.
5. The owner completed this run on 2026-07-11 with one 8BitDo Pro 2 over USB.
   Port-1 gameplay and Select+Start, Select+R, Select+L, Select+D-pad right/left,
   and Select+X all performed the intended menu, save, load, slot, and screenshot
   actions. The post-run file retained the calibrated values, MFi driver, port-1
   selector, and `config_save_on_exit = "false"`. RetroArch had expanded it to
   its canonical full config, including default higher-player entries; ROMD
   remains authoritative by replacing the session config at every launch.

### Completed owner calibration: physical RetroArch hotkeys

Calibration was executed separately from the acceptance run above:

1. Copy the generated ROMD config to a temporary directory; never let RetroArch
   save into ROMD's owned config. In the copy, set
   `config_save_on_exit = "true"`.
2. Start the managed executable directly with the temporary file using:

   ```text
   RetroArch.app/Contents/MacOS/RetroArch --config <temporary-retroarch.cfg> --menu
   ```

   Keep only the 8BitDo Pro 2 USB connected.
3. In Settings > Input > Hotkeys, bind Hotkey Enable, Menu Toggle, Save State,
   Load State, Next/Previous State Slot, and Screenshot using the intended
   physical buttons. Save the current configuration and quit.
4. Read `input_enable_hotkey_btn`, `input_menu_toggle_btn`,
   `input_save_state_btn`, `input_load_state_btn`,
   `input_state_slot_increase_btn`, `input_state_slot_decrease_btn`, and
   `input_screenshot_btn` verbatim from the temporary config. Preserve hat forms
   such as `h0right`; do not translate them through RetroPad canonical indices.
   Repeat once from a clean temporary copy and require identical output.
5. Both clean runs produced byte-identical saved configs, SHA-256
   `32d26a5cac4d8a683803fd8d00b2190f3f35b06db917431a42de8d10562476d4`.
   They recorded modifier `2`, menu `3`, save `11`, load `10`, slot next `7`,
   slot previous `6`, and screenshot `1`. Screenshot disproved the former
   RetroPad-derived guess `9`. The device probe observed exactly one USB pad,
   display name `8BitDo Pro 2`, exact identity, and SDL GUID
   `030001f2c82d00000660000000026800`; the serial was redacted. Code now emits
   these values only under the distinct calibrated policy. The live ROMD run
   above subsequently accepted the generated shortcuts.

### Completed owner calibration: canonical RetroArch gameplay

On 2026-07-13 the owner completed two clean `Set All Controls` passes against
the managed executable and identical temporary copies of a ROMD-generated
config. Only the exact 8BitDo Pro 2 USB controller was connected. Both passes
produced 48 owned binding lines, 24 active bindings, and the identical sorted
binding SHA-256
`1e1a0827235e9fe211553af2c707f2f256dada1fbe8dcec2ed334a90c5a10d80`.
The normal RetroArch config and global MFi autoconfig hashes were unchanged.

The duplicated physical-position calibration, corrected after comparing SDL3's
positional output with RetroArch's MFi values, is:

- SDL3 positional South `b1`, East `b0`, West `b3`, and North `b2` -> MFi
  buttons `8`, `0`, `9`, and `1` respectively; equivalently, raw buttons
  `b0/b1/b2/b3` -> MFi buttons `0/8/1/9`;
- Select `b4` -> `2`, Start `b6` -> `3`;
- D-pad hat `h0.1/h0.4/h0.8/h0.2` -> buttons `4/5/6/7`;
- shoulders `b9/b10` -> `10/11`;
- triggers `a4/a5` -> buttons `12/13`;
- stick clicks `b7/b8` -> `14/15`;
- stick X axes `a0/a2` -> `-0/+0` and `-2/+2`;
- stick Y axes `a1/a3` -> `+1/-1` and `+3/-3` for RetroArch's
  minus/plus keys respectively.

SDL's detected mapping for this exact mode was captured read-only as one
six-axis, fifteen-button, one-hat mapped controller with GUID
`030001f2c82d00000660000000026800`. Guide was raw `b5`; it was not prompted by
RetroArch's gameplay binder and remains unsupported for gameplay. The paddle
inputs were also outside the canonical gameplay contract. Trigger mappings may
arrive from detected SDL metadata as full axes or from ROMD capture as positive
axes; for this exact `a4/a5` envelope those two representations describe the
same reproduced physical triggers. Negative or inverted trigger forms remain
unsupported.

This calibration authorizes implementation and config-byte fixtures, not final
runtime acceptance. Before the policy is marked complete, a live ROMD launch
must prove representative face, D-pad, shoulder, trigger, and stick swaps, and
must prove each prior binding no longer acts.

The first live ROMD face-button case on 2026-07-13 appeared to pass at the
action-label level but is not accepted as positional evidence after the deeper
crosswalk diagnosis. Through the unified Map UI, the owner saved a layout and
launched a managed RetroArch SNES title. The durable row contained `a:b0,b:b1`,
while the old writer emitted `input_player1_b_btn = "8"` and
`input_player1_a_btn = "0"`. Those bytes exposed the defect: SDL3's detected
positional mapping makes raw `b1` South and `b0` East, so the old translation
reversed that face pair rather than preserving it.

The owner then confirmed in Super Mario World that the D-pad retained its
expected directions and the physical L/R shoulders moved the camera left/right
respectively. Only the intended A/B behavior changed. This accepts the live
D-pad and shoulder portion of the complete replacement block. Representative
trigger/stick swaps and proof that their prior bindings stop acting remain
pending; the N64 observation below establishes baseline response only.

### N64 live diagnosis: The World Is Not Enough

On 2026-07-13 the owner launched *The World Is Not Enough* through the managed
RetroArch path. The left stick moved the player, the right stick operated the C
cluster, and both physical trigger inputs responded. This is useful live
evidence for the existing physical-to-RetroPad projection, but it does not
freeze the mutable Mupen64Plus-Next core or complete representative swap and
prior-binding rejection acceptance.

The owner also reported that the game identified a B press when the printed X
button was pressed. Follow-up Reset testing showed the detected positional map
correctly as printed X -> North, Y -> West, B -> South, and A -> East. SDL3 had
translated the controller's legacy label-hint mapping into positions: South
`b1`, East `b0`, West `b3`, and North `b2`. RetroArch's calibrated MFi positions
are South `8`, East `0`, West `9`, and North `1`.

Two defects explain the observation. First, the then-current
`Use detected map` operation deleted the durable platform/GUID hardware-map row,
so that launch fell back to global MFi autoconfig, where physical MFi `9` / top
X became RetroPad Y/West and Mupen correctly emitted N64 B. Second, the old
custom writer used raw `0/1/2/3` -> MFi `8/0/9/1`, reversing both South/East and
West/North; preserving the row would therefore still have produced the wrong
face positions. The correct positional translation is raw `0/1/2/3` -> MFi
`0/8/1/9`. Neither defect belongs to the N64 core policy.

The code correction makes `Use detected map` explicit durable intent: when a
detected mapping exists, it is atomically saved under its platform/GUID key. An
explicit removal remains permitted when no detected map exists. Passive open,
enumeration, and detection failure remain zero-write and may not delete a row.
The writer now uses the corrected four-face crosswalk. Together with the
detected-map persistence fix, the diagnosis is resolved in implementation. On
2026-07-13 the owner explicitly saved the detected map and accepted the live
N64 result: printed B/South produced N64 A, printed Y/West produced N64 B,
printed X/North no longer produced B, left-stick movement remained correct, and
the right stick still drove the C cluster. The durable row contained SDL3's
`a:b1,b:b0,x:b3,y:b2` positions, while the generated config emitted RetroPad
South/East/West/North as MFi `8/0/9/1` and retained the ROMD-owned N64 options.
The owner then accepted a fresh corrected-build South/East swap in *Super Mario
World*. The durable row changed those inputs to `a:b0,b:b1`, the generated
RetroArch config changed RetroPad South/East to MFi `0/8`, and the prior face
actions no longer remained active. This replaces the earlier swap observation
that had inadvertently compensated for the defective crosswalk.
The owner then restored the detected map. Its exact
`a:b1,b:b0,x:b3,y:b2` row remained unchanged after ROMD was terminated and
relaunched, closing the explicit-save process-restart persistence check.

- `artifact resolver: no - the detected-map intent and face-position correction use the existing runtime artifact and add no provisioning requirement.`
- `config writer: yes - the fail-closed RetroArch writer corrects its four face-button SDL-to-MFi translations so canonical positions reach the intended physical buttons.`

Focused correction validation passed 39 RetroArch writer/integration tests and
20 controller-UI tests. The combined controller-UI/provider/launch/writer/
integration matrix passed 104 tests, the full console suite passed 661 tests,
and analysis, Dart format, runtime negative searches, and `git diff --check`
were clean. Representative non-face swaps and multi-controller hardware safety
remain pending.

Approved Genesis Plus GX adapter policy (2026-07-13):

- Genesis Plus GX remains the shipped default and natively maps RetroPad
  West/South/East to Genesis A/B/C and RetroPad L/North/R to Genesis X/Y/Z;
- the owner accepted all six positions in installed *ClayFighter* using the
  restored detected map: printed Y/B/A -> A/B/C and printed LB/X/RB -> X/Y/Z;
- selected-core identity comes from the chosen `RuntimeProfile` core
  requirement and is passed into the writer. Only core id `genesis_plus_gx`
  receives this policy; the PicoDrive alternate remains separate and
  intentionally unchanged;
- platform/core identity is necessary but not sufficient. Emission also
  requires the existing approved exact single-controller gameplay envelope.
  Empty setups, fallback controllers, unsupported singleton setups, and every
  multi-controller session retain their prior bytes; ROMD does not add an
  asymmetric player-one analog-D-pad policy to an uncorrelated session;
- Genesis Plus GX selects Joypad Auto so its six-button canonical mapping stays
  explicit, mirrors the left analog stick to the digital D-pad, disables global
  remap and override inheritance, and owns an isolated per-title remap directory
  beneath `configRoot`. No Genesis core-options file is created;
- the owner accepted the final reviewed build in *ClayFighter*: printed Y/B/A
  -> Genesis A/B/C, printed LB/X/RB -> Genesis X/Y/Z, D-pad and left-stick
  movement, Mode, and Start all worked as specified. The fresh per-title config
  emitted `input_libretro_device_p1 = "1"`,
  `input_player1_analog_dpad_mode = "1"`, restored MFi gameplay positions
  `a=0,b=8,x=1,y=9,l=10,r=11`, disabled inherited remaps/overrides and remap
  saving, and pointed at the existing empty per-title `remaps` directory. The
  Genesis Plus GX singleton policy is hardware accepted; this does not complete
  the broader Slice 4 hardware or multi-controller acceptance matrix.
- after the cardinality correction, 53 focused RetroArch adapter/writer tests
  and all 667 console tests passed; analysis, Dart format, runtime negative
  searches, and `git diff --check` were clean.

- `artifact resolver: no - Genesis controller policy uses the existing resolved RetroArch core and adds no provisioning requirement.`
- `config writer: yes - the Genesis Plus GX canonical-to-core policy is isolated in the existing fail-closed RetroArch configuration writer.`

## Owner Approvals And Dates

The owner approved implementation of the cardinality policy for this change.
The owner completed two reproducible isolated shortcut calibration runs on
2026-07-11 and then accepted the live ROMD port and shortcut behavior.
`acceptedBestEffort` is not proposed.

2026-07-13: the owner directed the canonical RetroArch initiative, completed
both clean full-controller calibration passes, and authorized implementation of
the separately gated gameplay policy. Final production acceptance remains
pending the live swapped-control matrix above.

2026-07-10: the owner directed a [GCController playerIndex propagation
research effort](../virtual-controller-routing/macos-playerindex-propagation.md)
testing whether pre-seeded `playerIndex` crosses process boundaries and is
adopted by this envelope's MFi slot assignment. No classification change;
`uncorrelated` stands until that research completes a full matrix and receives
owner review.

2026-07-11: that research executed and classified `perProcess` — pre-seeded
`playerIndex` never reached this envelope's MFi slot assignment, and no
write-back was observed. `uncorrelated` stands. The same session confirmed the
frozen artifact activates the `hid` (iohidmanager) and `sdl2` joypad drivers
via config; both remain separate unclassified envelopes.

2026-07-11 (later): under the `hid` envelope, the frontend enumerated,
autoconfigured, and received input from a physical pad whose IOHID device was
exclusively seized by ROMD's probe. The owner then decided to stop pursuing
deterministic controller order on macOS. The safe fallback behavior in this
document remains the standing multi-controller behavior; no multi-controller
correlation policy is planned for any macOS joypad envelope. The singleton
cardinality amendment does not reopen deterministic routing.
