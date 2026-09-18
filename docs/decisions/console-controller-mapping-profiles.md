# Console Controller Mapping Ownership

Status: accepted; amended by the 2026-07-12 Batocera-style controller-setup pivot

Related:

- [Console Controller Identity SDL Gate](console-controller-identity-sdl-gate.md)
- [Console Controller Identity Roadmap](../console-controller-identity-roadmap.md)
- [Emulator Runtime Pattern](../../clients/romd_console/docs/emulator-runtime-pattern.md)

## Decision

ROMD owns a canonical controller layer:

```text
raw physical button / axis / hat
    -> canonical ROMD gameplay pad
    -> runtime/system adapter
    -> emulator-native gameplay controls
```

Players configure and verify a controller on one canonical-pad surface. ROMD then
translates that physical setup for every runtime whose adapter has a proven
native configuration contract. Unsupported adapters remain automatic and fail
closed; they are never presented as receiving a custom mapping.

This supersedes the rejected interpretation of the mapping editor as only a
profile-specific Button-label and In-game-shortcut editor. Shortcuts remain
useful, but they are a secondary behavioral layer rather than the controller
mapping itself.

## Separate Identity And Ownership Spaces

Do not merge these contracts:

| Question | Key / owner |
| --- | --- |
| Which physical unit is seated now? | session-only SDL GUID + serial/provider identity |
| How is this controller mode physically wired? | device-global SDL platform + GUID |
| Which labels does ROMD display? | existing device-wide glyph preference |
| Which ROMD shortcuts does a player prefer? | existing profile/GUID behavioral rules |
| How does a system interpret the canonical pad? | shipped runtime/system adapter; future explicit override |

Serial is excluded from hardware mapping so identical units with one GUID share
one setup. SDL platform participates because raw input numbering and mapping
syntax are platform/backend contracts. USB, Bluetooth, D-input, X-input, and
other modes may expose different GUIDs and therefore different setups.

Physical wiring is not profile-scoped. “Raw button 2 is the bottom face
button” is hardware truth, not a preference belonging to Jan or Andy.

## Canonical Pad

The first version covers:

- D-pad Up, Down, Left, Right;
- bottom, right, left, and top face positions;
- left/right shoulders and triggers;
- left/right stick X/Y axes and stick presses;
- Select/Back, Start/Menu, and optional Guide/Home.

Canonical face controls are positional. Labels such as A/B/X/Y or
Cross/Circle/Square/Triangle remain presentation metadata. Analog axes and
triggers are first-class; they must not be forced into the existing
button-only shortcut model.

## SDL Boundary

The current SDL Gamepad API is already post-mapping and cannot teach ROMD the
raw physical layout. Slice 4 therefore requires the SDL Joystick API as well as
Gamepad:

- enumerate known gamepads and unknown joysticks;
- expose typed raw button, signed/half-axis, and hat events;
- retain one provider/instance identity across inventory and events;
- use one shared SDL event pump so two consumers never race `SDL_PollEvent`;
- expose the detected SDL mapping for known devices;
- generate and validate a deterministic SDL mapping string for custom setups.

Normal navigation continues to consume normalized gamepad events. Live Test
mode and an active Map-mode input listener acquire an exclusive capture lease that synchronously suppresses
gamepad navigation/join actions and delivers raw events only from the selected
controller. Fallback/no-SDL providers report setup unsupported rather than
inventing raw identity.

## Durable Store

Schema 10 adds one table without reinterpreting schema-9 rows:

```text
controller_hardware_mappings(
  sdlPlatform text,
  sdlGuid text,
  displayName text,
  format text,       -- initially sdl3-gamepad-v1
  mapping text,      -- validated SDL mapping string
  createdAt datetime,
  updatedAt datetime,
  primary key(sdlPlatform, sdlGuid)
)
```

The domain exposes typed `RomdPadControl` and `PhysicalControllerInput` values;
only a dedicated codec parses or serializes the SDL string. The codec must
round-trip buttons, axes, inversion/half ranges, and hats, reject duplicate
canonical targets or raw-input conflicts, bound indices/masks, and serialize
deterministically.

Reads, Test mode, and Map-mode drafts create no rows. Explicit Save validates and atomically
upserts the complete mapping. `Use detected setup` deletes only the custom
hardware row. Empty GUIDs are inert. Unknown formats or malformed saved rows
are skipped and retained without passive repair or deletion.

Existing schema-9 tables keep their meanings:

- `controller_preferences_rows`: launcher glyph preference;
- `controller_binding_rules`: legacy device-wide shortcuts;
- `controller_mapping_profiles` and `controller_profile_binding_rules`:
  profile/GUID shortcut metadata and behavioral overlays.

No schema-9 row contains enough information to derive a raw hardware mapping,
so the 9→10 migration creates only the new table and preserves every existing
row.

## Runtime Ownership

`LaunchControllerSetup` carries canonical gameplay mapping separately from
shortcut actions. Each runtime adapter translates the canonical map using its
own proven native syntax.

- DuckStation is the first approved full-gameplay target under the existing
  managed verified-single-controller envelope. Multi-controller order remains
  automatic and uncorrelated.
- RetroArch 1.22.2 Metal on macOS consumes MFi input rather than ROMD's SDL
  provider. The current seven shortcut calibration values are not a complete
  gameplay crosswalk. Custom RetroArch gameplay mapping stays disabled until
  every canonical control is calibrated reproducibly and accepted on hardware.
- PCSX2 remains outside this slice.

Gameplay mapping and runtime hotkeys are separate outputs. A runtime may support
one without the other; UI status must report that distinction.

## UX Contract

Settings > Controllers groups hardware by platform/GUID and reports Detected,
Custom, Needs setup, or Setup unavailable. Duplicate units show one shared
setup. Seating identity attention does not block model/GUID mapping.

Selecting an exact controller model opens one `Controller setup` surface built
around a neutral ROMD canonical pad. The diagram is positional rather than a
copy of an Xbox, PlayStation, Nintendo, Steam, or other vendor shell. It names
Bottom/Right/Left/Top face positions, D-pad, shoulders, triggers, sticks, and
Back/Home/Start so the mapping contract is visible without promising a
family-specific physical layout.

The same surface has two explicit modes:

- `Test` acquires target-only raw input, animates canonical controls and axes in
  place, and shows unmapped raw activity without writing anything;
- `Map` edits an in-memory draft. The player selects a canonical control,
  explicitly chooses `Change`, then supplies one physical button, axis, or hat
  input from the attributed controller. Testing or navigating never changes a
  binding.

Conflicts do not silently steal an input. Clear, detected/custom reset, Cancel,
and atomic Save remain distinct operations. The draft can be tested before
Save. A short guided bootstrap may remain for a controller with no usable
mapping because an unknown device cannot yet provide trustworthy controller
navigation; it uses the same diagram and is a secondary recovery path rather
than the primary editor. Focus returns to the selected canonical control after
listening or dialogs. The experience remains controller/keyboard accessible,
semantically concise, non-color-dependent, reduced-motion safe, and contained
at 1280x720 with 2x text.

Only explicit Save or confirmed custom-map reset writes. Disconnect preserves
the in-memory draft for the current route. Back with changes offers Continue,
Discard, and Save. Test mode verifies the effective draft without writing. The
experience must remain controller/keyboard accessible, semantically concise,
non-color-dependent, reduced-motion safe, and contained at 1280x720 with 2x
text.

## Validation And Release Gate

Required evidence includes:

- fresh schema 10 and lossless 9→10 migration tests;
- codec round trips and rejection tests for buttons, axes, ranges, inversion,
  hats, collisions, malformed/future formats, and deterministic output;
- raw joystick/gamepad enumeration and one-pump event fanout tests;
- attributed capture, ownership, neutral/debounce/dead-zone/release, conflict,
  disconnect, duplicate-model, fallback, zero-write, focus, semantics, and
  responsive widget tests;
- DuckStation full gameplay config bytes and editor-save-to-writer integration;
- unchanged RetroArch and multi-controller suppression bytes;
- `mise run analyze`, `mise run test`, runtime negative searches, and
  `git diff --check`;
- live DuckStation gameplay acceptance. RetroArch support requires a separate
  complete calibration and hardware gate.

## Runtime Decisions

- `artifact resolver: no - canonical controller mapping uses the existing SDL identity/input dependency and existing resolved emulator artifacts; it adds no executable, core, BIOS, artifact, or provisioning requirement.`
- `config writer: yes - ROMD must translate the canonical controller map into supported emulator-native gameplay and hotkey configuration.`

## Current implementation boundary

The current tree implements the unified Test/Map surface above and removes the
separate normalized-input debugger. Exact identity-less raw controllers are
Test-only and perform no repository I/O. Empty maps retain a forward/skip
guided bootstrap on the same diagram, while normal edits are direct and
explicit. Duplicate groups still require disconnecting all but one unit;
hold-to-select, multi-sample axis stability/debounce, paused reconnect/resume,
continuous analog visualization, family-specific shells, and live hardware/
runtime acceptance remain pending. These boundaries must not be described as
automated or hardware-accepted coverage.
