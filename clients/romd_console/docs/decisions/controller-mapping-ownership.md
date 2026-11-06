# Console Controller Mapping Ownership

Related decisions: [controller identity](controller-identity.md),
[virtual controller routing](virtual-controller-routing.md). The runtime
pattern is in [emulator runtime pattern](../emulator-runtime-pattern.md), and
remaining work is in the [controller identity roadmap](../roadmaps/controller-identity.md).

## Context

Controllers wire their buttons differently, and the same model can appear
differently over USB, Bluetooth or a different input mode. Players expect to
set a controller up once and have every emulator honor it. Each emulator,
however, has its own native configuration syntax, and an adapter that cannot
be proven to translate a setup correctly would silently play with the wrong
controls.

How a controller is wired is a fact about the hardware. Which shortcuts a
player prefers, and how a label is displayed, are preferences. Mixing those
questions would let one player's preference rewrite another's hardware truth.

## Decisions

### A Canonical Controller Layer

```text
raw physical button / axis / hat
    -> canonical ROMD gameplay pad
    -> runtime/system adapter
    -> emulator-native gameplay controls
```

- Players configure and verify a controller on one canonical-pad surface. ROMD
  translates that physical setup for every runtime whose adapter has a proven
  native configuration contract.
- Unsupported adapters stay automatic and fail closed. They are never presented
  as receiving a custom mapping.
- Shortcuts are a secondary behavioral layer, not the controller mapping. The
  mapping editor is not just a profile-specific label and in-game shortcut
  editor.

### Separate Identity And Ownership Spaces

| Question | Key and owner |
| --- | --- |
| Which physical unit is seated now? | session-only SDL GUID + serial or provider identity |
| How is this controller mode physically wired? | device-global SDL platform + GUID |
| Which labels does ROMD display? | the device-wide glyph preference |
| Which ROMD shortcuts does a player prefer? | profile and GUID behavioral rules |
| How does a system interpret the canonical pad? | the shipped runtime or system adapter |

- Serial is excluded from hardware mapping, so identical units with one GUID
  share one setup. SDL platform participates because raw input numbering and
  mapping syntax are platform and backend contracts. USB, Bluetooth, D-input,
  X-input and other modes may expose different GUIDs, and therefore different
  setups.
- Physical wiring is never profile-scoped: "raw button 2 is the bottom face
  button" is hardware truth, not a player's preference.

### Canonical Pad

- The canonical pad covers:
  - the D-pad (Up, Down, Left and Right)
  - the bottom, right, left and top face positions
  - left and right shoulders and triggers
  - left and right stick X and Y axes and stick presses
  - Select/Back, Start/Menu and an optional Guide/Home
- Face controls are positional. Labels such as A/B/X/Y or
  Cross/Circle/Square/Triangle are presentation metadata.
- Analog axes and triggers are first-class, and are never forced into the
  button-only shortcut model.

### SDL Boundary

- The SDL Gamepad API reports post-mapping input, so it cannot teach ROMD a
  controller's raw physical layout. ROMD uses the SDL Joystick API alongside it:
  - it enumerates known gamepads and unknown joysticks
  - it exposes typed raw button, signed or half-axis, and hat events
  - it keeps one provider and instance identity across inventory and events
  - one shared SDL event pump serves all consumers, so two never race
    `SDL_PollEvent`
  - it exposes the detected SDL mapping for known devices
  - it generates and validates a deterministic SDL mapping string for custom
    setups
- Normal navigation consumes normalized gamepad events. Live Test mode and an
  active Map-mode listener take an exclusive capture lease that synchronously
  suppresses gamepad navigation and join actions, and delivers raw events only
  from the selected controller.
- Fallback providers without SDL report setup as unsupported rather than
  inventing raw identity.

### Durable Store

- Custom hardware mappings persist in `controller_hardware_mappings`, keyed by
  `(sdlPlatform, sdlGuid)`, with a display name, a format (initially
  `sdl3-gamepad-v1`), the validated SDL mapping string and timestamps.
- The domain exposes typed `RomdPadControl` and `PhysicalControllerInput`
  values, and only a dedicated codec parses or serializes the SDL string. The
  codec round-trips buttons, axes, inversion and half ranges, and hats. It
  rejects duplicate canonical targets and raw-input conflicts, bounds indices
  and masks, and serializes deterministically.
- **Only explicit Save writes.** Reads, Test mode and Map-mode drafts create no
  rows. Explicit Save validates and atomically upserts the complete mapping,
  and `Use detected setup` deletes only the custom hardware row. Empty GUIDs are
  inert. Unknown formats and malformed saved rows are skipped and kept, without
  passive repair or deletion.
- **Earlier tables keep their meanings:**
  - `controller_preferences_rows`: the launcher glyph preference
  - `controller_binding_rules`: legacy device-wide shortcuts
  - `controller_mapping_profiles` and `controller_profile_binding_rules`:
    profile and GUID shortcut metadata and behavioral overlays

  None of them can yield a raw hardware mapping, so hardware mappings start
  empty and reinterpret no earlier row.

### Runtime Ownership

- `LaunchControllerSetup` carries the canonical gameplay mapping separately
  from shortcut actions. Each runtime adapter translates the canonical map with
  its own proven native syntax.
- **Hardware acceptance per runtime.** A runtime receives custom gameplay
  mapping only after every canonical control is calibrated reproducibly and
  accepted on hardware.
  - DuckStation receives it within the managed, verified-single-controller
    envelope. Multi-controller order stays automatic and uncorrelated.
  - RetroArch on macOS (Metal) consumes MFi input rather than ROMD's SDL
    provider, and its shortcut calibration is not a complete gameplay
    crosswalk, so its custom gameplay mapping stays disabled until it passes
    the same acceptance.
  - PCSX2 stays outside custom gameplay mapping until it passes the same
    acceptance.
- **Mapping and hotkeys are separate outputs.** A runtime may support one
  without the other, and the UI reports that distinction.
- **Runtime dependencies.** Canonical mapping needs no new runtime artifact:
  it uses the existing SDL identity and input dependency and the resolved
  emulator artifacts. It does need config writers, which translate the
  canonical map into supported emulator-native gameplay and hotkey
  configuration.

### UX Contract

- **Grouping.** Settings > Controllers groups hardware by platform and GUID and
  reports Detected, Custom, Needs setup or Setup unavailable. Duplicate units
  show one shared setup, and seating-identity attention never blocks model and
  GUID mapping.
- **One setup surface.** Selecting an exact controller model opens one
  `Controller setup` surface built around a neutral canonical pad. The diagram
  is positional rather than a copy of any vendor's shell. It names the face
  positions, D-pad, shoulders, triggers, sticks and Back/Home/Start, so the
  mapping contract is visible without promising a family-specific layout.
- **Two explicit modes.**
  - `Test` acquires target-only raw input, animates canonical controls and axes
    in place, and shows unmapped raw activity, without writing anything.
  - `Map` edits an in-memory draft. The player selects a canonical control,
    explicitly chooses `Change`, then supplies one physical button, axis or hat
    from the attributed controller. Testing or navigating never changes a
    binding.
- **Explicit editing.** Conflicts never silently steal an input. Clear,
  detected or custom reset, Cancel and atomic Save stay distinct operations,
  and the draft can be tested before Save.
- **Guided bootstrap.** A short guided bootstrap may serve a controller with no
  usable mapping, because an unknown device cannot yet navigate reliably. It
  uses the same diagram, and it is a recovery path rather than the primary
  editor.
- **Writes and leaving.** Only explicit Save, or a confirmed custom-map reset,
  writes. Disconnect keeps the in-memory draft for the current route. Back with
  changes offers Continue, Discard and Save, and focus returns to the selected
  canonical control after listening or dialogs.
- **Accessibility.** The experience stays controller- and keyboard-accessible,
  semantically concise, non-color-dependent and reduced-motion safe, and fits
  1280×720 with 2× text.

## Trade-offs

- **Setups are per platform and mode.** The same controller over USB and
  Bluetooth can need two setups.
- **Unaccepted runtimes stay automatic.** A player's custom setup does not
  reach a runtime until that runtime passes hardware acceptance.
- **Serial-blind mappings.** Identical units always share one hardware setup;
  per-unit wiring differences are not representable.

## Rejected Alternatives

- **A profile-scoped hardware mapping.** Rejected: physical wiring is hardware
  truth, so it is keyed by platform and GUID, not by player.
- **Vendor-label mapping.** Rejected: face controls are positional, and labels
  differ between families for the same position.
- **Gamepad-API-only mapping.** Rejected: post-mapping input cannot reveal a
  raw physical layout.
- **Offering custom mapping to every runtime.** Rejected: an adapter without a
  proven native contract would play with wrong controls, so unsupported
  adapters stay automatic.

## Guardrails

- **`play/sdl_gamepad_mapping_codec_test.dart`:** codec round trips and
  rejection of collisions, malformed or future formats, and nondeterministic
  output.
- **`play/controller_hardware_mapping_repository_test.dart` and
  `app_database_baseline_test.dart`:** zero-write reads, atomic save, fresh
  ownership constraints, and unsupported-file refusal without modification.
- **`controller_setup_screen_test.dart` and `controllers_screen_test.dart`:**
  attributed capture, conflicts, disconnects, duplicate models, focus,
  semantics and responsive layout.
- **`play/controller_mapping_runtime_integration_test.dart`,
  `play/duckstation_config_writer_test.dart` and
  `play/retroarch_config_writer_test.dart`:** DuckStation gameplay config from a
  saved setup, and unchanged RetroArch and multi-controller output.
- **Review:** each runtime's hardware acceptance is recorded before its custom
  gameplay mapping is enabled.
