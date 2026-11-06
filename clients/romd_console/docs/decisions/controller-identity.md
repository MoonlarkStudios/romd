# Console Controller Identity

Related decisions: [Flutter client](flutter-client.md),
[controller mapping ownership](controller-mapping-ownership.md),
[virtual controller routing](virtual-controller-routing.md). The runtime
pattern is in [emulator runtime pattern](../emulator-runtime-pattern.md), and
remaining phase order is in the
[controller identity roadmap](../roadmaps/controller-identity.md). Batocera
config-generator references are in
[Batocera config generators](../research/batocera-config-generators.md).

## Context

Players seat controllers for a session and expect the same physical
controller to keep its player slot. Display names and connection order cannot
tell two identical controller models apart after a disconnect and reconnect.
External emulators take controller assignment as runtime-specific index
syntax: RetroArch's `input_playerN_joypad_index` and DuckStation's
`SDL-<index>/...` bindings.

An identity source is only useful if the same source also lists connected
controllers and emits their live button events, so that seating claims the
identity that launch planning later resolves. Native libraries found through
system package managers or `PATH` vary by machine and cannot anchor stable
identity.

## Decisions

### SDL3 Is The Identity Source

- SDL3 is the console's bridge for exact controller identity and button
  events.
- **Artifact resolver: yes.** ROMD manages SDL3 native artifacts, because
  stable identity must not depend on Homebrew, distribution packages, `PATH`
  contents or a global SDL install. SDL3 is a console native dependency, not a
  `RuntimeProfileId` or emulator runtime, and it never adds or renames a
  shipped `RuntimeProfileId` or affects runtime override rules.
- **Config writer: yes.** RetroArch and DuckStation controller assignment is
  generated emulator configuration. Both writers consume the same resolved
  assignment model and translate it into their own syntax.

### Handwritten, Bounded Bindings

- SDL3 is reached through handwritten Dart FFI bindings for a bounded subset:
  - subsystem initialization and shutdown
  - listing gamepad instance ids
  - reading display name and SDL GUID by instance id
  - opening a gamepad only when needed to read its serial
  - polling add, remove, button-down and button-up events
  - closing opened handles

  [Controller mapping ownership](controller-mapping-ownership.md) adds the
  joystick surface that raw mapping needs.
- SDL's headers and the Dart `sdl3` package serve only as signature and layout
  references. ROMD does not depend on `sdl3` or other broad SDL packages.
  `package:ffi` helpers are acceptable for pointer allocation and UTF-8
  conversion.
- **Re-decision trigger:** generated bindings are reconsidered only if the FFI
  surface grows beyond this bounded subset.

### Explicit SDL3 Loading

- SDL3 loads through an explicit resolver, never through platform default
  library search paths. Resolution order:
  1. the ROMD-managed SDL3 artifact for the current OS and architecture
  2. an app-bundled SDL3 in packaged builds that embed it
  3. a developer override path for local testing

  There is no implicit system fallback in production.
- If SDL3 cannot be loaded or initialized, the controller provider fails closed
  to the `gamepads` list and event path, which keeps name and order behavior.
  That fallback never writes fake SDL GUIDs or serials into session state or
  persistence.

### Managed Native Artifacts

- SDL3 binaries ship through ROMD's managed artifact machinery. The catalog is
  keyed by OS and architecture like emulator artifacts, installs under a
  ROMD-owned native dependency root, and uses a fingerprint-based install
  marker, so upgrades are explicit and repeatable.
- The catalog, resolver signature, install marker and provisioner contract are
  platform-neutral. Resolving an artifact for `(os, arch)` yields a
  format-specific install strategy, using `Platform.operatingSystem` and
  `Abi.current`.
- Every managed native artifact on every platform carries a pinned hash in the
  catalog and is verified after download, before install. Unverified native
  binaries are never loaded through FFI.

### Platform Fallback

Platforms without a managed SDL3 artifact use the `gamepads` fallback and keep
its behavior:

- controller listing by the package's behavior
- session claims by display name
- duplicate names resolved by claim order and connection order
- no emitted GUID or serial identity

### Identity Semantics

- `ControllerIdentity(displayName, sdlGuid?, serial?)` is the identity value.
- Exact matching requires both `sdlGuid` and `serial`, and an exact match wins
  over every fallback. A missing GUID or serial uses the compatibility
  fallback.
- A connected controller keeps its ephemeral provider id for event correlation,
  its current runtime or SDL index for config output, its identity for slot
  matching, and its display name for the UI and fallback matching.
- No fake GUIDs or synthetic serials are created. Controller preferences and
  binding rules keep their existing keys and storage.
- Slot claims are session-only. No durable slot-claim table exists.

### Resolver Shape

- A resolved assignment model, `ResolvedControllerSlot(playerSlot, controller,
  runtimeIndex)`, is the primary internal contract. Adapters and config writers
  still emit integer indices where an emulator needs index syntax, but the index
  comes from one central resolver that matches claims against real identity
  first and falls back to name and order.
- Fallback behavior stays compatible:
  - no claims resolve to connection order
  - a name claim reorders, and remaining devices fill by connection order
  - duplicate names claim distinct devices in claim order
  - disconnected fallback claims never gain exact reservation strength
  - claimed middle slots without exact reservations leave earlier slots
    order-filled
- Exact disconnected claims visibly reserve their player slot for the app
  session, and launch planning honors that reserved hole.
- Changing any fallback expectation is a decision change, not an
  implementation detail.

### Provider Boundary

- A controller input provider abstraction sits between SDL and the UI and
  launch planning. The SDL-backed provider is the only component that calls SDL
  APIs. It lists controllers and emits live button events from the same
  identity source, so the seating ceremony claims the identity that launch
  planning resolves.
- The `gamepads` provider remains the fallback. It produces
  `ControllerIdentity(displayName: name)` with null SDL fields, and therefore
  compatibility matching.

### Config Writer Integration

- `EmulatorLaunchPlan` carries resolved controller slots, not writer-specific
  assignment inputs. RetroArch, DuckStation and standalone adapters pass that
  model through their config writers.
- RetroArch emits `input_playerN_joypad_index` only for an explicit resolved
  assignment. DuckStation emits `SDL-N/...` bindings from the resolved slot
  order. A launch with no claims keeps the autoconfig and connection-order
  output.
- Save, state and config output stays under `saveRoot`, `stateRoot` and
  `configRoot`. Process commands stay structured, with `runInShell: false`.

### Usable Before Setup

- A connected controller is usable for navigation before the user visits
  controller setup or assigns player order. Setup refines identity, order,
  labels and mappings; it is never a gate before first input works.
- The active session registers connected controllers implicitly: the first
  active controller is usable at once, and session player order is seeded from
  connection order until the user runs Change Order.
- Enumeration alone writes no durable controller mapping or profile rows.

## Trade-offs

- **Exactness needs a serial.** Controllers that expose no serial get name and
  order matching only, so two identical serial-less units can still swap.
- **Managed artifacts per platform.** Exact identity reaches a platform only
  when ROMD manages a verified SDL3 artifact for it.
- **Session-only seating.** Player order resets when the app restarts.

## Rejected Alternatives

- **Name and order-only identity.** Rejected: it cannot tell identical models
  apart after a reconnect.
- **A system or package-manager SDL.** Rejected: identity must not depend on
  machine-specific installs, and unverified binaries are never loaded through
  FFI.
- **Generated, broad SDL bindings.** Rejected: they add a large derived surface
  before ROMD needs more than identity, events and mapping input.
- **A durable slot-claim table.** Rejected: seating is a session decision, and
  claims are kept in memory.
- **Batocera's model as the identity and persistence contract.** Rejected:
  ROMD keeps the stronger `sdlGuid + serial` exact seating and its GUID-keyed
  mapping persistence. Batocera's config generators remain a reference for
  translating one controller model into emulator-native configuration.

## Guardrails

- **`play/sdl_native_dependency_test.dart`:** pinned, verified SDL3 artifacts
  and explicit loading.
- **`play/sdl_gamepad_enumerator_test.dart`:** listing and identity from the
  SDL source.
- **`play/controller_assignments_test.dart` and
  `play/emulator_launch_plan_test.dart`:** exact matching, fallback
  compatibility and reserved slots in launch planning.
- **`play/retroarch_config_writer_test.dart` and
  `play/duckstation_config_writer_test.dart`:** writer output from resolved
  slots, and unchanged no-claim output.
- **`change_order_screen_test.dart`:** the seating ceremony.
- **Console checks:** `mise run analyze` and `mise run test`.
