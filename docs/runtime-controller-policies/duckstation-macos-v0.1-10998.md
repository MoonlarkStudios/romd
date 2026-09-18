# DuckStation macOS v0.1-10998 Controller Policy Research

Status: current; multi-controller classification `uncorrelated`;
single-controller policy owner hardware accepted

Validated: 2026-07-10

Amended: 2026-07-13 (canonical mapping-file lifecycle refinement)

## Frozen Runtime Envelope

- Official immutable asset: <https://github.com/stenzek/duckstation/releases/download/v0.1-10998/duckstation-mac-release.zip>
- Archive SHA-256 (direct observation): `ed8d58d6075bccb4d201b9ba3fab4e3cc1e967db311851cb44b131f7abf3f4e3`.
- Release/source: [v0.1-10998, commit `9b0a4ec55`](https://github.com/stenzek/duckstation/tree/9b0a4ec55).
- Bundle identifier/version: `com.github.stenzek.duckstation`,
  `0.1-10998-g9b0a4ec55`; executable `Contents/MacOS/DuckStation`.
- Minimum macOS: 13.3. Universal Mach-O: `arm64` and `x86_64`.
- Signature: ad-hoc, no team identifier; observed CDHash
  `2a1c97028ce0801e8d1534ab61007e73213bbc0e`.
- SDL is bundled and dynamically loaded from
  `Contents/Frameworks/libSDL3.0.dylib`, SHA-256
  `72d166f5deea21e0c81b59904115172bd01a31eeba011b5d82345b9b77fbb2a9`.
  Embedded revision string: `SDL-release-3.4.2-0-g683181b47`.
- macOS backend hints: DuckStation explicitly configures IOKit and MFi plus
  enhanced reports and controller-specific HIDAPI hints before initializing
  joystick, gamepad, and haptic subsystems.
- Bundled mapping database SHA-256:
  `a2b656a5f5370f05b37a4c722e59e98f7945ebc8160f16b18f5ede7d21976ed1`.
- Binary inspection host: macOS 26.5.2 build 25F84, arm64. Runtime hardware
  trials were not performed on this host.
- ROMD now fingerprints the pinned archive digest and verifies it before
  unpacking. `duckstation:psx:standalone` is unchanged.
- Production acceptance is limited to ROMD's managed artifact path with this
  pinned digest and install fingerprint. A locally discovered executable or
  developer override may exercise the adapter, but it is outside this hardware
  envelope and cannot supply acceptance evidence for the named policy.

## Primary-Source Analysis

At the matching revision, DuckStation [sets its mapping file and SDL hints,
then initializes joystick, gamepad, and haptic subsystems](https://github.com/stenzek/duckstation/blob/9b0a4ec55/src/util/sdl_input_source.cpp#L540-L612).
It consumes hotplug events and [opens both SDL gamepads and fallback SDL
joysticks](https://github.com/stenzek/duckstation/blob/9b0a4ec55/src/util/sdl_input_source.cpp#L1010-L1070).

`SDL-N` is DuckStation's `player_id`, not SDL's device instance id and not a
GUID, serial, or path. On open, DuckStation asks SDL for the gamepad/joystick
player index; when it is negative or already used, DuckStation assigns the
lowest free nonnegative integer. It stores the separate joystick instance id
only for event lookup. See [OpenDevice and GetFreePlayerId](https://github.com/stenzek/duckstation/blob/9b0a4ec55/src/util/sdl_input_source.cpp#L1130-L1205).
Enumeration then formats that process-local player id as
[`SDL-{player_id}`](https://github.com/stenzek/duckstation/blob/9b0a4ec55/src/util/sdl_input_source.cpp#L645-L663).
Disconnect removes the entry, making the number reusable.

Consequently `SDL-N` is recomputed within each DuckStation process and can be
affected by SDL player-index state, event arrival, joystick-only devices,
DuckStation hints, enhanced mode, IOKit/MFi classification, and reconnects.
The binding parser accepts only this ordinal form; this source exposes no
binding selector by GUID, serial, path, or SDL instance id.

ROMD independently initializes its managed SDL3 provider, lists only SDL
gamepads, and retains GUID/serial for ROMD seating. Its provider ordinal is an
observation from another process and cannot identify DuckStation's player id.
Even matching SDL versions would not share process-local player assignment;
the frozen build also carries its own SDL library, hints, and mapping database.

## Probe Method And Commands

Binary observations used `shasum -a 256`, `plutil -p`, `file`, `lipo -archs`,
`otool -L`, `codesign -dvvv`, and `strings` against the extracted official
archive. Source analysis used `rg` and exact-revision reads of
`src/util/sdl_input_source.cpp`. No app, seating, mapping, profile, or schema
writes were made.

DuckStation's supported logs expose name, SDL instance id, player id, GUID,
gamepad/joystick classification, axes/buttons/hats, mapping count, hints and
connect/disconnect events. They do not expose serial, path/location, transport,
vendor/product, or a stable identity-bearing binding key in the inspected
source. Future trials must preserve missing fields as missing.

## Candidate Rule And Falsification

Candidate: ROMD SDL gamepad observation ordinal equals DuckStation `SDL-N`.
Falsified structurally: the domains differ (gamepads-only versus gamepads plus
joysticks), DuckStation prefers SDL player index and otherwise allocates a
reusable lowest-free id, and the independent processes do not share inventory,
hints, mapping state, event timing, or player assignment. No number equality
can establish physical identity.

## Complete Matrix Summary

No physical controller cases were executed. All required cases remain untested:
one controller; A/B and B/A mixed families; reversed Change Order; reconnect
with and without provider-id replacement; identical pads with and without
serials; DualSense USB/Bluetooth; mixed transport; pre/post-runtime connection;
offline P1; same-name ambiguity; fallback/no-SDL; DuckStation-only restart; and
ROMD-only restart. The required 8BitDo D/X-input, Xbox-style, DualSense,
identical, mixed-family, and fallback hardware is unavailable in this run.

Because the ordinal candidate rule is already disproven and no identity-bearing
key exists, hardware success could not promote that multi-controller rule to
`verified`. Hardware trials remain useful for documenting automatic-order
behavior, the separate singleton cardinality policy, and any future upstream
identity feature.

## Mismatches And Untested Cases

The source-level inventory/classification and identifier-construction mismatch
is blocking. Manual runtime inventory, emitted-config/observed-port parity, and
P1 shortcut ownership have not been observed on hardware. No retry or ordinal
alignment is counted as evidence.

## Classification And Rationale

Standing multi-controller classification: `uncorrelated`.

DuckStation accepts a process-local player ordinal while ROMD owns stable
identity in another process. There is no supported identity-bearing selector
to bridge them, and the mandatory hardware matrix is incomplete. This is not a
request for `acceptedBestEffort` approval.

The single-controller amendment does not equate ROMD order with DuckStation's
player id. With one physical controller in the approved launch snapshot,
DuckStation's only SDL input is that controller by cardinality; its process-
local name is therefore `SDL-0` without cross-process identity correlation.

## Exact Policy Rule And Single-Controller Amendment

Policy id: `duckstation-macos-single-controller`.

ROMD classifies one reference `verified` with runtime reference `0` only when
all of these conditions hold:

1. launch received a supplied, revision-checked `ReviewedLaunchSnapshot`;
2. its provider inventory is available and contains exactly one physical
   controller;
3. that controller came from the SDL provider and has exact nonempty GUID and
   serial identity;
4. the host operating system is macOS and the selected adapter is DuckStation;
5. the reference belongs to that same resolved controller entry.

The classification never reads a live writer-time count. Fresh listings only
validate the reviewed inventory before mapping resolution and again before the
process starts. Runtime reference `0` is the cardinality result, not ROMD's
provider ordinal.

An offline exact reservation remains a ROMD session-slot hole and does not add a
physical controller. The sole connected controller keeps its ROMD player slot,
while the writer projects its effective mapping to DuckStation `[Pad1]` with
`SDL-0` sources. It does not compact or rewrite session seating.

Zero or multiple controllers, null-snapshot/live resolution, unavailable
inventory, fallback/no-SDL devices, GUID-only devices, Linux, PCSX2, unknown
adapters, and any wrong/partial policy shape remain `uncorrelated`. Provider
ordinal remains diagnostic-only and no multi-pad set may force a reference.

## Safe Fallback Behavior

For populated setups outside the exact rule, ROMD preserves DuckStation's
automatic `SDL-0`, `SDL-1` order, ignores ROMD provider ordinals, and suppresses
controller-specific shortcuts. Per-pad mapping data remains independently
resolved but must not claim physical routing. Fallback/no-SDL stays
`uncorrelated`. Explicit zero-player reserved/blocked holes retain keyboard
fallback where applicable but omit the blocked slot's synthesized SDL source.

The named singleton policy writes the controller's mapping under `[Pad1]` with
`SDL-0` sources and authorizes DuckStation's symbolic SDL shortcut chords under
`[Hotkeys]`. This is source-backed policy authorization, not completed owner
hardware validation.

Connecting a second controller after process start is outside the reviewed
snapshot promise. ROMD does not reclassify or rewrite the running config;
DuckStation owns subsequent hotplug behavior.

### Canonical mapping-file lifecycle

ROMD's singleton canonical mapping is a temporary user-database override, not
the automatic fallback database. Managed entries are framed and replaced on
each approved singleton launch. When a later launch is automatic,
uncorrelated, or multi-controller, ROMD removes its entry; if no user-owned
content remains it deletes the user `gamecontrollerdb.txt` so DuckStation can
load its bundled mapping database. Writing an empty override file is not an
acceptable fallback.

The cleanup path also recognizes the earlier two-line ROMD marker format.
Damaged or truncated markers remove only ROMD marker material; unrecognized
following content is retained. Nonempty user database entries remain outside
ROMD ownership.

## Invalidation And Revalidation Triggers

Re-research is mandatory for any artifact/source revision, SDL/backend,
mapping-database, platform/architecture, ROMD provider enumeration, or hardware
mismatch change. A future supported GUID/serial/path selector may justify a new
candidate rule; it still requires a newly frozen envelope and complete matrix.

The singleton policy additionally invalidates if snapshot validation,
GUID+serial exactness, host-OS gating, adapter id, `SDL-0` construction,
symbolic binding syntax, or the managed artifact path changes.

## Automated Fixtures And Tests

Added coverage pins the immutable URL/digest, includes the digest in the
install fingerprint, accepts matching bytes, and rejects mismatched bytes
before unpack/marker creation. For this change, the 20-test DuckStation writer
suite covers exact singleton Pad1 output and symbolic shortcuts, reserved-hole
projection, multi-pad/fallback byte parity, wrong policy/provider/correlation
rejection, and explicit zero-player holes. The 30-test provider suite covers
snapshot-only cardinality, exact identity, macOS gating, Linux/null-snapshot/
fallback exclusions, holes, and the reconnect race. No duplicate-reference
branch was added: the production classifier and writer policy admit a singular
own-policy reference by construction. No runtime inventory parser was added
because the supported log lacks the stable identity needed to form a
correlation rule.

## Manual Hardware Evidence

Owner functional acceptance completed 2026-07-11 against the managed
DuckStation setup with one 8BitDo Pro 2 connected over USB. The owner confirmed:

- the lone physical controller drove port 1;
- menu, save state, load state, next slot, previous slot, and screenshot
  shortcuts all worked;
- no second controller was connected during the session.

No separate settings-file checksum or verbatim config capture was reported in
the acceptance message, so those provenance details are not claimed here.

### Reproduction procedure: port 1 and symbolic shortcuts

Run this against the managed pinned artifact, not a discovered local executable
or developer override:

1. Quit DuckStation. Disconnect every game controller, then connect only the
   8BitDo Pro 2 by USB. In ROMD, open Settings > Controllers > the connected
   controller > Test and confirm one exact SDL pad responds. Do not connect
   another pad during the run.
2. Confirm the resolved executable is the managed v0.1-10998 artifact with the
   digest above. Back up
   `<base>/runtimes/duckstation/user/Library/Application Support/DuckStation/settings.ini`
   if it exists, and record its pre-launch checksum.
3. Launch a known working PlayStation title through ROMD. While it runs, read
   that scoped `settings.ini`. Under `[Pad1]`, verify representative mapping
   lines including `Cross = SDL-0/A`, `Select = SDL-0/Back`,
   `Start = SDL-0/Start`, d-pad sources, both sticks, shoulders, and triggers.
   Under `[Hotkeys]`, verify the configured chords use `SDL-0` only, including
   `OpenPauseMenu = SDL-0/Back & SDL-0/Start` and the nonempty
   `SaveSelectedSaveState`, `LoadSelectedSaveState`,
   `SelectNextSaveStateSlot`, `SelectPreviousSaveStateSlot`, and `Screenshot`
   values.
4. In game, exercise the complete pad and record that the only physical
   controller drives port 1. Then exercise each nonempty generated shortcut:
   menu, save, load, next/previous slot, and screenshot. Confirm the observed
   action matches the exact `[Hotkeys]` line and no bare action button fires.
5. Exit normally, confirm ROMD regains foreground, and reread `settings.ini`.
   Verify the Pad1 and Hotkeys values still match the inspected launch config;
   record the post-launch checksum, artifact digest, macOS version, transport,
   controller mode if known, and results.

## Owner Approvals And Dates

The owner approved implementation of the cardinality policy for this change.
On 2026-07-11 the owner granted functional hardware acceptance for port 1 and
all configured symbolic shortcuts with the lone 8BitDo Pro 2 USB controller.
`acceptedBestEffort` is not proposed. The current DuckStation writer suite
passes 25 tests, including singleton-to-automatic cleanup, legacy managed-entry
migration, damaged-marker preservation, user-entry preservation, and
idempotent singleton output.
