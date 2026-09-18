# Console Controller Identity SDL Gate

Status: accepted - per-player launch contract landed; runtime correlation pending

Related:

- [Console Client Flutter Direction](console-client-flutter.md)
- [Consumer Runtime Boundary](consumer-runtime-boundary.md)
- [Emulator Runtime Pattern](../../clients/romd_console/docs/emulator-runtime-pattern.md)

## Context

The console client originally listed controllers through the `gamepads` package and
maps each device to `ConnectedGamepad` as `{id, name}`. Player-order claims are
session scoped and stored as controller names. Launch planning resolves those
claims to `padDeviceIndices`, then RetroArch and DuckStation config writers emit
runtime-specific index settings.

That preserves the current no-identity fallback, but it cannot reliably
distinguish two identical controller models after disconnect/reconnect. The
existing config writers already treat index assignment as a temporary bridge:
RetroArch emits `input_playerN_joypad_index`, and DuckStation emits
`SDL-<index>/...` bindings.

Remaining assignment work should not build more behavior on top of
name/order-only data. It should preserve the input source that lists connected
controllers and emits live button events from the same identity source.

## Current Implementation Checkpoint

As of July 9, 2026, this decision is mostly present in code:

- `ControllerIdentity` and SDL-backed controller enumeration exist under
  `clients/romd_console/lib/src/play/controllers/`.
- macOS SDL3 managed native dependency catalog/provisioning exists and is
  covered by focused tests.
- Identity-aware `ControllerSlotClaim` and `ResolvedControllerSlot` models exist.
- The default app path uses a provisioned SDL controller input provider for
  listing and button events, with the existing `gamepads` path as fallback.
- The Change Order ceremony can store session-scoped exact identity claims when
  SDL GUID and serial are available, with name/order fallback preserved.
- The Controllers screen preserves exact offline reservations visibly.
- `LaunchControllerSetup` centralizes launch controller data and
  `EmulatorLaunchPlan` keeps `padDeviceIndices` as a compatibility bridge.

The per-player launch contract now carries mapping, identity, capabilities, and
an explicit best-effort runtime input reference per resolved controller. Phase
6B remains in progress because provider enumeration order is not yet proven to
equal RetroArch or DuckStation's cross-process runtime input identity.

Treat this document as the accepted target state, with
`docs/console-controller-management-roadmap.md` as the authoritative handoff for
remaining phase order. Current source and tests remain authoritative for what
has actually shipped.

## Roadmap Follow-Up: First-Run Controller Usability

ROMD Console is a controller-first experience. A controller must be usable for
navigation before the user visits controller setup or explicitly assigns player
order. Setup should refine identity, ordering, labels, and mappings; it must not
be a gate before first controller input works.

Add implicit controller registration for the active session:

- use connected controllers/events to make an initial active controller usable
  immediately;
- seed session player order from connection order until the user runs Change
  Order;
- avoid writing durable controller mapping/profile rows from enumeration alone;
- keep explicit setup available for correcting order, choosing labels, and later
  mapping work.

This should be handled before the controller setup flow is treated as shippable
UX.

## Accepted Decision

Use SDL3 as the controller identity bridge for exact controller identity and
button events.

artifact resolver: yes - ROMD should manage SDL3 native artifacts when SDL3 is
used as the identity source, because stable controller identity should not
depend on Homebrew, distro packages, PATH contents, or a user's global SDL
install. Reuse the managed artifact resolver/provisioning pattern, but model SDL3
as a console native dependency rather than a `RuntimeProfileId` or emulator
runtime.

config writer: yes - RetroArch and DuckStation controller assignment output is
generated emulator config. Both writers should consume the same resolved
assignment model and translate it into their own config syntax.

## Binding Approach

Use handwritten Dart FFI bindings for a minimal SDL3 gamepad subset.

The first surface should cover only:

- SDL initialization/shutdown for the gamepad/events subsystem.
- Listing gamepad instance ids.
- Reading display name and SDL GUID by instance id.
- Opening a gamepad only when needed to read serial.
- Polling gamepad button add/remove/down/up events.
- Closing opened gamepad handles.

Do not generate broad SDL bindings for Phase 1. Generated bindings would add a
large derived surface before ROMD knows it needs more than gamepad identity and
button events. Revisit generation only if the FFI surface grows beyond this
bounded subset.

Use SDL's headers and the Dart `sdl3` package only as signature and struct-layout
references while writing the handwritten bindings; do not depend on `sdl3`.

Do not add broad SDL packages. Keep the binding surface handwritten and bounded;
`package:ffi` helper usage is acceptable for pointer allocation and UTF-8 string
conversion where it keeps the handwritten FFI code safe and readable.

## SDL3 Loading

Load SDL3 through an explicit resolver instead of relying on platform default
library search paths.

Resolution order:

1. ROMD-managed SDL3 artifact path for the current OS/architecture.
2. App-bundled SDL3 path for packaged builds, if the package process embeds it.
3. Developer override path for local testing.
4. No implicit system fallback in production.

If SDL3 cannot be loaded or initialized, the controller provider should fail
closed and let the existing `gamepads` list/events path preserve current
name/order behavior. That fallback must not write fake SDL GUIDs or serials into
session state or persistence.

## Native Artifact Strategy

Ship or manage SDL3 binaries through ROMD's managed artifact machinery.

The artifact catalog should be OS/architecture keyed like emulator artifacts and
install under a ROMD-owned native dependency root. The install marker should be
fingerprint based, as runtime artifacts are today, so upgrades are explicit and
repeatable.

The catalog, resolver signature, install marker, and provisioner contract are
platform neutral: resolving an artifact for `(os, arch)` yields a format-specific
install strategy, with macOS `.dmg`/framework handling as the first strategy
rather than the interface shape. Every managed native artifact on every platform
must carry a pinned hash in the catalog and must be verified after download
before install; unverified native binaries are never loaded through FFI.

Initial managed support should be macOS first, matching the current managed
runtime catalog. The domain model and resolver shape should still use
`Platform.operatingSystem` and `Abi.current` inputs so Windows and Linux
artifacts can be appended later without changing controller assignment
semantics.

Do not add or rename shipped `RuntimeProfileId` values. SDL3 is not an emulator
profile and should not affect runtime override rules.

## OS Support

Phase 1 support target: macOS.

Windows and Linux remain supported through the existing `gamepads` fallback until
SDL3 artifacts are explicitly added for those platforms. Unsupported platforms
must preserve current behavior:

- controller listing by current package behavior,
- session claims by display name,
- duplicate-name resolution by claim order and connection order,
- no emitted GUID/serial identity.

## Identity Semantics

Use this `ControllerIdentity` value object:

```text
ControllerIdentity(
  displayName: String,
  sdlGuid: String?,
  serial: String?,
)
```

Exact identity matching is allowed only when both `sdlGuid` and `serial` are
present. `sdlGuid + serial` wins over all fallback matching. Missing GUID or
missing serial means the controller uses the compatibility fallback path.

The connected controller model should retain:

- ephemeral provider id for event correlation,
- current runtime/SDL index for config output,
- identity for stable slot matching,
- display name for UI and fallback matching.

No fake GUIDs or synthetic serials should be created. Existing controller
preferences and binding rules remain keyed and stored exactly as they are today.
Slot claims remain session-only; no durable slot-claim table or migration should
be added for this change.

## Resolver Shape

Replace `padDeviceIndices` as the primary internal contract with a resolved
assignment model:

```text
ResolvedControllerSlot(
  playerSlot: int,
  controller: ConnectedController,
  runtimeIndex: int,
)
```

Adapters and config writers can still emit integer indices where an emulator
requires index syntax. The difference is that the index comes from a centralized
resolver that matched claims against real controller identity first, then fell
back to the current name/order behavior.

Fallback/no-exact behavior must remain compatible:

- no claims resolves to connection order,
- a name claim reorders and remaining devices fill by connection order,
- duplicate names claim distinct devices in claim order,
- disconnected fallback claims do not gain exact reservation strength,
- claimed middle slots without exact reservations leave earlier slots
  order-filled.

Exact disconnected claims are different: they visibly reserve their player slot
for the app session. Launch planning must match that reserved-hole behavior
before new per-emulator controller mapping work begins.

If any existing fallback expected value needs to change, stop and flag it before
implementation continues.

## Provider Boundary

Introduce a controller input provider abstraction before wiring SDL into UI or
launch planning.

The SDL-backed provider should be the only component that calls SDL APIs. It
must list controllers and emit live button events from the same SDL identity
source, so the L+R seating ceremony can claim the same identity that launch
planning later resolves.

The existing `gamepads` provider should remain as the fallback provider. It
should produce `ControllerIdentity(displayName: name)` with null SDL fields and
therefore keep compatibility matching.

## Config Writer Integration

`EmulatorLaunchPlan` should carry resolved controller slots, not separate
writer-specific assignment inputs. RetroArch, DuckStation, and standalone
adapter flows should pass that model through their config writers.

RetroArch should continue to emit `input_playerN_joypad_index` only when ROMD has
an explicit resolved assignment. DuckStation should continue to emit `SDL-N/...`
bindings from the resolved slot order. No-claim launches should preserve the
current autoconfig/connection-order output unless tests prove the SDL-backed
model requires an intentional change.

Save, state, and config output remains under `saveRoot`, `stateRoot`, and
`configRoot`. Process commands remain structured with `runInShell: false`.

## Batocera Reference For Config Writers

Use Batocera as a practical reference for translating one normalized controller
model into emulator-specific config output:

- Controller mapping philosophy: map by physical/cardinal button position, not
  vendor button label.
- Launch shape: resolve player controllers before launch, then pass them to the
  selected emulator's config generator.
- RetroArch reference: generate `input_playerN_*` bindings and
  `input_playerN_joypad_index` from the resolved player order.
- DuckStation reference: enable SDL input and write `PadN` bindings using
  `SDL-<index>/...` names.
- Dolphin and MAME references: write emulator-native config rather than forcing
  a RetroArch-shaped model through every runtime.

Reference sources:

- <https://wiki.batocera.org/configure_a_controller>
- <https://wiki.batocera.org/remapping_controls_per_emulator>
- <https://github.com/batocera-linux/batocera.linux/blob/271e78ab808b1ed7d6a4e9109371ddee39727588/package/batocera/core/batocera-configgen/configgen/configgen/controller.py>
- <https://github.com/batocera-linux/batocera.linux/blob/271e78ab808b1ed7d6a4e9109371ddee39727588/package/batocera/core/batocera-configgen/configgen/configgen/generators/libretro/libretroControllers.py>
- <https://github.com/batocera-linux/batocera.linux/blob/271e78ab808b1ed7d6a4e9109371ddee39727588/package/batocera/core/batocera-configgen/configgen/configgen/generators/duckstation/duckstationGenerator.py>
- <https://github.com/batocera-linux/batocera.linux/blob/271e78ab808b1ed7d6a4e9109371ddee39727588/package/batocera/core/batocera-configgen/configgen/configgen/generators/dolphin/dolphinControllers.py>
- <https://github.com/batocera-linux/batocera.linux/blob/271e78ab808b1ed7d6a4e9109371ddee39727588/package/batocera/core/batocera-configgen/configgen/configgen/generators/mame/mameControllers.py>

Batocera is a config-writer reference, not the identity/persistence contract.
ROMD should keep the stronger `sdlGuid + serial` exact seating model and the
profile/GUID mapping persistence described in
[Console Controller Mapping Profiles](console-controller-mapping-profiles.md).

## Acceptance Criteria

- Two identical controllers with distinct SDL GUID and serial values stay in
  stable player slots.
- A disconnected/reconnected controller with the same GUID and serial returns to
  its prior slot.
- Controllers without GUID or serial preserve current name/order fallback
  behavior.
- RetroArch and DuckStation consume one resolved assignment model through config
  writers.
- No shipped `RuntimeProfileId` changes.
- Commands remain structured with `runInShell: false`.
- Save, state, and config paths stay under ROMD-owned roots.
- `mise run analyze` and `mise run test` pass from `clients/romd_console/` after
  implementation phases.

## Approval Record

Approved on July 8, 2026:

- handwritten minimal SDL3 FFI vs generated SDL3 bindings,
- ROMD-managed SDL3 artifact vs system SDL dependency,
- macOS-first managed SDL3 support,
- managed artifact resolver ownership for SDL3 native binaries,
- one resolved controller assignment model feeding RetroArch and DuckStation
  config writers.

Sources checked for the SDL3 API surface:

- <https://wiki.libsdl.org/SDL3/SDL_GetGamepads>
- <https://wiki.libsdl.org/SDL3/SDL_GetGamepadGUIDForID>
- <https://wiki.libsdl.org/SDL3/SDL_GetGamepadNameForID>
- <https://wiki.libsdl.org/SDL3/SDL_GetGamepadSerial>
- <https://wiki.libsdl.org/SDL3/SDL_OpenGamepad>
- <https://wiki.libsdl.org/SDL3/SDL_GUIDToString>
- <https://wiki.libsdl.org/SDL3/SDL_PollEvent>
