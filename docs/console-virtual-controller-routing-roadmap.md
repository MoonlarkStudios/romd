# Console Virtual Controller Routing Roadmap

Status: parked 2026-07-11 by owner decision; retained as historical record

Created: 2026-07-10

Parked: 2026-07-11

## Parked By Owner Decision (2026-07-11)

Two product constraints close this roadmap on every platform:

1. ROMD supports emulators beyond RetroArch, so an in-process libretro embed
   is not the runtime strategy and cannot substitute for routing.
2. The Linux kiosk must run with stock input configuration; no `uinput`
   broker, `EVIOCGRAB` acquisition, udev/systemd device policy, or device
   namespace layer will be required for normal operation.

Combined with the macOS evidence — Gate 0 `notIsolated` under both the MFi and
iohidmanager envelopes, and `playerIndex` classified `perProcess` — no
platform remains where the virtual-routing data path can be built under the
product constraints. Slices V2 through V8 are closed unstarted; V0's
platform-neutral contracts and V1's evidence remain valid historical research.

Deterministic per-player port routing is therefore out of scope for ROMD.
Emulators assign ports; ROMD owns identity, profiles, and mappings. The active
continuation is the
[Console Controller Identity Roadmap](console-controller-identity-roadmap.md).

One future flag: if multi-pad port order ever becomes a real complaint on a
kiosk deployment, the only remaining mechanism is best-effort ordinal seating
(the Batocera approach), which was rejected while a correct alternative
existed on this roadmap. Reconsidering it is a separate owner decision and
must be recorded against this section.

Related:

- [Console Controller Management Roadmap](console-controller-management-roadmap.md)
- [Controller Identity SDL Gate](decisions/console-controller-identity-sdl-gate.md)
- [Controller Mapping Profiles](decisions/console-controller-mapping-profiles.md)
- [Runtime Controller Adapter Policies](runtime-controller-policies/README.md)
- [Emulator Runtime Pattern](../clients/romd_console/docs/emulator-runtime-pattern.md)

## Purpose

This roadmap defines the next controller-routing architecture for ROMD Console.
It preserves the product, identity, seating, mapping, review, and fail-closed
contracts established by the original roadmap while changing how ROMD delivers
controller input to external emulators.

The original roadmap remains authoritative for landed Slices 1-6 and for the
controller UX and persistence contracts. This document supersedes its planned
Slices 7-9 as the active runtime-routing sequence. PCSX2 mapping remains
unapproved until this roadmap's isolation and virtual-routing gates are met.

## Why ROMD Is Diverging

The original roadmap assumed ROMD might prove a narrow adapter policy that
correlated its physical-controller identity with each emulator's runtime-local
device reference. Slices 5 and 6 tested that assumption against exact managed
artifacts and matching primary source.

The results were negative:

- DuckStation `SDL-N` is a process-local player id derived from SDL player
  state or a reusable first-free slot. It is not a GUID, serial, path, or SDL
  instance id, and DuckStation's inventory can include joysticks ROMD excludes.
- RetroArch 1.22.2 Metal uses Apple GameController/MFi rather than ROMD's SDL 3
  provider. Its joypad index is `GCController.playerIndex` or a reusable MFi
  slot, and its MFi reservation surface exposes no shared exact identity.
- Provider ordinals may align in common cases but cannot prove physical
  identity across independent processes, provider stacks, filters, hotplug
  timing, reconnects, or identical devices.
- A successful trial cannot repair a missing shared identity key. Treating an
  ordinal as identity would make ROMD confidently route mappings or P1
  shortcuts to the wrong physical controller.

The detailed evidence is preserved in:

- [DuckStation macOS v0.1-10998 research](runtime-controller-policies/duckstation-macos-v0.1-10998.md)
- [RetroArch macOS 1.22.2 Metal research](runtime-controller-policies/retroarch-macos-1.22.2-metal.md)

ROMD is therefore changing the runtime boundary. Instead of translating a
physical controller into an emulator's opaque device ordinal, ROMD should own
stable virtual P1-P4 devices and route seated physical input into them.

## What The Previous Work Still Provides

This is not a reset of controller management. The following landed work becomes
the control plane for virtual routing:

- exact session seating by SDL GUID plus serial when both are available;
- honest fallback and ambiguity behavior when exact identity is unavailable;
- session-only claims, reserved holes, and reconnect/provider-id replacement;
- profile/GUID mapping persistence and capability filtering;
- canonical `ControllerSlotResolution` and `LaunchControllerSetup` models;
- revision-checked reviewed launch snapshots;
- controller-first Players and Change Order UX;
- explicit correlation confidence and fail-closed config writers;
- suppression of forced references and P1 shortcuts when routing is unknown.

The divergence replaces only the final physical-to-runtime routing mechanism.

## Target Product Boundary

ROMD should behave like a console shell or Steam Big Picture-style session,
without requiring ownership of the complete operating-system distribution.
The long-term deployment target is a Linux kiosk system, while macOS remains a
first-class development platform and possible first virtual-routing backend.
Windows remains an intended platform behind the same contract.

The target data path is:

```text
Physical controllers
        |
Platform physical-input provider
        |
ROMD identity, seating, mappings, and reviewed launch snapshot
        |
Native controller router/broker
        |
ROMD Virtual Gamepad P1 / P2 / P3 / P4
        |
Isolated emulator-visible device surface
        |
Unmodified emulator runtime
```

The emulator-visible identity is stable and owned by ROMD. Changing or
reconnecting a physical controller must not change the virtual P1-P4 identity.

## Hard Gate: Physical Device Isolation

Virtual device creation is not sufficient. If an emulator can also read the
physical controllers, it may receive duplicate input, count physical devices
before virtual devices, bind automatic ports incorrectly, or attach shortcuts
to the wrong device.

Before production virtual-controller work begins on a platform, ROMD must prove:

> A launched emulator receives input from the assigned ROMD virtual devices and
> cannot receive input from the corresponding physical controllers.

Two isolation strengths may satisfy the gate:

1. The physical devices are not enumerated by the emulator.
2. The physical devices may be enumerated, but the emulator cannot open or
   receive input from them and its virtual-device selection remains stable.

Enumeration without reads is acceptable only when each supported emulator's
device numbering and selection behavior is proven. One duplicate physical
input, unexplained port shift, or crash that leaves devices captured blocks the
platform backend.

## Cross-Platform Architecture

### Control Plane And Data Plane

Flutter remains the controller UX and policy control plane. A small native
broker owns the real-time input data plane.

Flutter owns:

- local profiles and mapping intent;
- Players, Change Order, review, and launch disposition;
- canonical seating and immutable reviewed snapshots;
- runtime selection and orchestration;
- user-facing failure and fallback behavior.

The native broker owns:

- physical device discovery and input reports;
- exclusive acquisition or platform isolation;
- persistent virtual P1-P4 lifecycle;
- high-frequency input transformation and forwarding;
- neutral-state guarantees;
- output reports such as rumble where supported;
- watchdog cleanup after Flutter or emulator failure;
- read-only diagnostic inventories.

High-frequency controller reports should not traverse Dart. Flutter supplies a
validated route/mapping snapshot; the broker applies it until explicitly
replaced or cleared.

### Platform-Neutral Contracts

The common broker protocol must model:

```text
VirtualControllerBackend
  capabilities
  ensurePlayers(P1..P4)
  applyRouteSnapshot(snapshot)
  clearPlayer(player)
  clearAll()
  getPhysicalInventory()
  getVirtualInventory()
  getIsolationStatus()
  receiveOutput(player)
  shutdown()
```

Normalized input reports must use ROMD's physical-position model rather than a
platform button-label convention:

```text
VirtualGamepadReport
  sequence
  timestamp
  connected
  buttons
  leftX / leftY
  rightX / rightY
  leftTrigger / rightTrigger
```

Sticks normalize to `-1.0...1.0`, triggers to `0.0...1.0`, and buttons to
`GamepadButtonPosition`. Unsupported controls remain explicit capabilities.

### Platform Backends

| Platform | Virtual output | Isolation candidate | Initial status |
| --- | --- | --- | --- |
| macOS | CoreHID `HIDVirtualDevice`; HIDDriverKit fallback | IOHID exclusive seizure plus runtime-specific filtering if needed | Gate 0 required |
| Linux | `uinput`, preferably through `libevdev` | `EVIOCGRAB`, permissions, cgroups/systemd policy, and device namespace | Gate 0 required; primary kiosk target |
| Windows | signed Virtual HID Framework source driver | signed HID filter/hiding driver plus runtime policy | Gate 0 required |

The shared contract must not contain CoreHID references, Linux event codes,
Windows device handles, emulator ordinals, or platform-specific paths.

## Non-Negotiable Contracts

- Player seating remains ephemeral; do not add durable P1-P4 seating rows.
- Exact ROMD seating identity remains `sdlGuid + serial` when both exist until
  an approved provider migration defines an equal or stronger replacement.
- Mapping persistence remains `localProfileId + sdlGuid`.
- Never invent GUIDs, serials, paths, transports, or exact correlation.
- Passive enumeration creates no profile/GUID rows or mapping intent.
- Exact offline reservations remain holes.
- Virtual P1-P4 devices remain stable and non-compacting; an empty player is a
  present neutral device when the backend supports persistent devices.
- Every route mutation sends a neutral report before reassignment.
- Broker crash, Flutter crash, emulator exit, and shutdown must release
  physical grabs and leave virtual devices neutral.
- Physical devices must never double-feed a launched emulator.
- Provider ordinal remains diagnostic-only.
- Runtime profile ids remain unchanged.
- Commands remain structured with `runInShell: false`.
- Save, state, and config paths remain under ROMD-owned roots.
- No PCSX2 mapping output is authorized by this roadmap alone.

## Runtime Decisions

- `artifact resolver: yes - isolation and routing conclusions apply only to exact managed emulator artifacts, broker binaries, platform APIs, and virtual-device descriptors.`
- `config writer: yes - each emulator must be configured to consume only approved ROMD virtual devices, with fail-closed fallback when isolation or backend prerequisites are absent.`

## Sequenced Roadmap

### Slice V0 - Cross-Platform Isolation Contract

Status: documented in this milestone; pending owner review/freeze

Purpose: define what it means for a platform/runtime pair to be isolated before
writing a production virtual-controller backend.

Scope:

- define physical and virtual inventory records;
- define enumerated/opened/input-received distinctions;
- define isolation states and failure reasons;
- define neutralization and crash cleanup requirements;
- define permission, privilege, entitlement, and installation evidence;
- define latency and output-feedback measurements;
- define the emulator observation matrix;
- keep the contract independent of Flutter and any one OS API.

Exit criteria:

- one reusable isolation research template exists;
- a platform cannot report ready from partial or mixed isolation;
- runtime and broker upgrades invalidate evidence;
- no production device creation or mapping emission is enabled.

Evidence (2026-07-10): the platform-neutral inventory, observation-state,
classification, stable-identity, route-snapshot, neutralization/watchdog,
permission, performance, output, invalidation, redaction, and evidence-lifecycle
contracts are documented in the [virtual-controller isolation research
template](virtual-controller-routing/templates/platform-isolation-research-template.md).
No production or diagnostic device code was added.

### Slice V1 - macOS Gate 0 Isolation Probe

Status: `notIsolated` for IOHID seizure with exact RetroArch 1.22.2 MFi and
8BitDo Pro 2 USB; production work stopped

Purpose: determine whether macOS can prevent physical controller input from
reaching managed DuckStation, RetroArch, and PCSX2 processes while ROMD retains
input for forwarding.

Probe phases:

1. Capture IORegistry/CoreHID/IOHID/GameController/SDL and emulator inventories.
2. Open physical HID devices using the supported exclusive/seize option.
3. Repeat enumeration, open, input, hotplug, and output-report observations.
4. Create one minimal CoreHID virtual gamepad.
5. Forward input while the physical device is seized.
6. Prove each runtime receives exactly one virtual input and no physical input.
7. Exercise normal exit, forced broker exit, Flutter exit, emulator exit, and
   disconnect/reconnect cleanup.

Minimum hardware:

- 8BitDo Pro 2 D-input and X-input;
- Xbox-style controller;
- DualSense USB and Bluetooth;
- two identical controllers;
- mixed USB/Bluetooth;
- fallback/no-SDL path.

Required observations per case:

- physical and synthetic registry identities;
- whether each API/runtime enumerates the device;
- whether open succeeds;
- whether input is received;
- whether virtual input is received exactly once;
- runtime device order and selected port;
- permissions/prompts;
- cleanup and neutral state;
- latency and output-feedback behavior.

Classification:

- `isolated`: every required runtime/hardware case prevents physical input and
  reliably consumes the intended virtual device;
- `runtimeScoped`: only named frozen runtimes meet the gate through documented
  runtime filtering; requires a narrow policy per runtime;
- `notIsolated`: physical input or unstable numbering remains possible.

One duplicate event or unexplained device-order shift blocks `isolated`.

Planning evidence (2026-07-10): the [macOS build 25F84 research
report](virtual-controller-routing/macos-isolation-25F84.md) records primary
source analysis, the phased probe design, and exact missing evidence. Read-only
host/SDK checks ran, but no physical controller was acquired, no virtual device
was created, no managed runtime received attributed input, and the complete
hardware matrix was untested at that milestone. Gate 0 was then `unclassified`.

Probe foundation (2026-07-10): a development-only Swift CLI now emits redacted
IORegistry, CoreHID, IOHID joystick/gamepad, and GameController inventories and
can explicitly seize one reviewed IOHID candidate for a bounded window. It
creates no virtual device, makes no ROMD writes, and never classifies isolation.
Build, format lint, and argument/redaction/filter tests passed; a one-second
inventory run returned empty candidate sets from all four observers. The
existing ROMD SDL probe independently reported zero gamepads against the exact
managed library. Explicit seizure now counts attributed IOHID callbacks without
retaining raw control values. Local runtime discovery found DuckStation,
RetroArch, and PCSX2 installations, but DuckStation is a later rolling build
than the frozen policy and RetroArch installed provenance is incomplete; they
were not launched at that milestone. Runtime observers, hardware seizure, and
cleanup/crash trials were still untested.

Runtime provenance follow-up (2026-07-10): installed-artifact discovery exposed
a RetroArch provisioner gap: an existing app bundle bypassed the pinned archive
fingerprint. The provisioner now replaces markerless/stale frontends, verifies
the archive SHA-256 before unpacking, and writes a frontend marker only after a
successful install. Focused tests, full console analysis, and all 509 console
tests passed. Existing user runtime directories were not mutated, and no
emulator was launched in that provenance milestone; Gate 0 was still
`unclassified`.

Empty-runtime baseline (2026-07-10): after reprovisioning, DuckStation and
RetroArch markers and versions matched their frozen envelopes. A menu-only
RetroArch run confirmed joypad driver `mfi`, while all independent controller
inventories remained empty and no controller records appeared. The attempted
frame bound did not terminate menu mode, and verbose startup exposed unrelated
user/device metadata, so the process was manually stopped and raw logs were not
retained. Future runtime observation requires an isolated root, streaming
redaction, and an external watchdog. No hardware case had run at that point.

Gate 0 hardware evidence (2026-07-10): ROMD SDL, IORegistry, CoreHID, IOHID,
and GameController independently observed an 8BitDo Pro 2 over USB; mode was
not exposed and remains unknown. macOS also exposed a separate marked synthetic
HID representation. Exclusive seizure of the physical IOHID candidate
succeeded while ROMD received attributed input. With exact RetroArch 1.22.2
MFi active, ROMD received 42 physical callbacks, RetroArch assigned MFi index 0
/ port 1, and the owner directly observed its menu react during seizure.
Physical input therefore reached a managed runtime (state 4). Classification is
`notIsolated`; Phase C and production macOS virtual-controller work stop here
unless an alternate physical-hiding boundary receives a separate owner decision.

### Slice V2 - macOS Go/No-Go Decision

Status: decided 2026-07-11 - retain the automatic-order fallback on macOS
(option 3); deterministic macOS routing work is stopped

Purpose: decide whether macOS is a supported deterministic-routing platform.

Owner direction (2026-07-10): before deciding V2, run the [GCController
playerIndex propagation research](virtual-controller-routing/macos-playerindex-propagation.md).
If cross-process propagation holds, RetroArch seating on macOS may be
achievable by pre-seeding `GCController.playerIndex` from ROMD's process and
emitting reviewed `input_playerN_joypad_index` values — no virtual devices,
runtime patches, or joypad-driver changes. That would resolve identity/seating
for the MFi envelope only; it does not satisfy Gate 0 isolation and does not
reopen macOS virtual-device work. If propagation fails (`perProcess`), the
remaining V2 candidates are the unclassified `input_joypad_driver = "hid"`
envelope probe and the Linux-first fallback below.

Result (2026-07-11 UTC): `perProcess` — the propagation path failed and is
closed. Two independent T2 executions showed RetroArch reading desired index 0
while the probe verifiably held `playerIndex = 2`, with zero cross-process
transitions in either direction. During execution, read-only checks confirmed
the exact managed artifact activates `input_joypad_driver = "hid"`
(iohidmanager backend, pad enumerated) and `"sdl2"` (pad autoconfigured) as
config-only switches.

The `hid`-envelope seizure retest then also failed (2026-07-11): with the
physical candidate exclusively seized, the exact frontend under
`input_joypad_driver = "hid"` enumerated the pad, autoconfigured port 1, and
visibly responded to button input while the probe attributed 225 physical
callbacks. Evidence is recorded in the [isolation research
report](virtual-controller-routing/macos-isolation-25F84.md).

Decision (2026-07-11): option 3. macOS retains the automatic-order fallback,
suppressed uncorrelated P1 shortcuts, and fail-closed writers. No macOS broker,
runtime patch, or further cross-process identity probing is scheduled. The
Linux slices (V5, V6) remain defined but unscheduled pending a future owner
decision; the platform-neutral contracts from V0 remain the basis for any such
future work.

Possible decisions:

- proceed with CoreHID when isolation, entitlements, permissions, cleanup, and
  distribution are reproducible;
- support only named runtime-scoped macOS policies;
- retain automatic-order fallback on macOS and continue development against
  the platform-neutral contract plus Linux integration environment.

Do not treat development-machine success, disabled security, root-only setup,
or an ungrantable entitlement as production viability.

### Slice V3 - macOS Virtual Controller Broker

Status: not approved; requires V2 go decision

Purpose: implement persistent ROMD Virtual P1-P4 devices and the native broker.

Requirements:

- fixed ROMD vendor/product, serial, unique, and product identities;
- fixed creation order;
- neutral devices for unoccupied/reserved players;
- atomic immutable route snapshot application;
- capability-filtered physical-to-virtual transformation;
- exclusive physical acquisition;
- watchdog and crash-safe cleanup;
- diagnostics outside normal UI;
- output-report capture with explicit unsupported capabilities.

No normal emulator launch uses the broker until V4 policies are approved.

### Slice V4 - macOS Runtime Policies Over Virtual Devices

Status: not approved; requires V3

Purpose: configure exact managed DuckStation, RetroArch, and later PCSX2
artifacts to consume only ROMD virtual devices.

Each runtime requires its own frozen envelope, adversarial hardware matrix,
fail-closed prerequisites, and upgrade invalidation. DuckStation ordinal use is
acceptable only if isolation proves its visible/openable inventory and stable
virtual creation order. RetroArch reservation is acceptable only when it
selects unique ROMD virtual identity. PCSX2 remains research-first.

### Slice V5 - Linux Gate 0 And `uinput` Spike

Status: may begin after V0; primary deployment path

Purpose: prove the architecture on the intended Linux kiosk target.

Scope:

- physical inventory through evdev/SDL;
- exclusive `EVIOCGRAB` behavior;
- one standards-compliant `uinput` virtual gamepad;
- udev identity and stable `/dev/input/by-id` behavior;
- emulator process user/group and systemd device policy;
- optional device namespace exposing only ROMD virtual nodes;
- DuckStation, RetroArch, and PCSX2 visibility/input evidence;
- crash cleanup, neutralization, latency, and rumble.

The spike must work from an installed non-root kiosk session. Root-only manual
commands are development evidence, not an exit condition.

### Slice V6 - Linux Four-Player Broker And Kiosk Integration

Status: not approved; requires V5

Purpose: deliver the primary production virtual-routing backend.

Requirements include persistent P1-P4, reversed seating, identical pads,
offline holes, reconnect/provider replacement, systemd lifecycle, compositor
integration, emulator launch isolation, watchdog cleanup, and complete hardware
evidence.

### Slice V7 - Windows Isolation And VHF Feasibility

Status: future

Purpose: prove that signed Windows components can create ROMD virtual gamepads
and hide/block physical input from managed runtimes.

Do not standardize on an abandoned third-party driver without a separate
security, signing, maintenance, and redistribution review. Prefer Windows VHF
and a narrowly scoped signed filter architecture when feasible.

### Slice V8 - Phase 6B Virtual Routing Release Gate

Status: blocked on at least one complete platform backend and runtime policy

Purpose: release per-player routing only where ROMD owns a proven isolated
virtual device surface.

Exit criteria:

- reviewed ROMD seating deterministically reaches virtual P1-P4;
- virtual identities and order survive restarts and physical reconnects;
- exact reserved holes remain neutral and non-compacted;
- each physical device's effective mapping reaches its assigned virtual player;
- P1 alone owns shortcuts;
- physical input never double-feeds runtimes;
- supported runtimes pass their frozen-envelope hardware matrix;
- unsupported platforms/runtimes remain honestly automatic and uncorrelated;
- no passive durable writes or runtime profile renames;
- full touched-platform validation passes.

## Isolation Evidence Matrix

Every platform/runtime case must record:

| Field | Required evidence |
| --- | --- |
| Environment | OS/build, architecture, broker/runtime hashes, provider/backend |
| Hardware | model, mode, transport, available GUID/serial/path/VID/PID |
| Sequence | exact connect, seize/grab, virtual create, launch, reconnect order |
| Inventories | physical, synthetic compatibility, virtual, runtime-visible |
| Access | enumerated, open result, input received, output received |
| Routing | physical source, ROMD player, virtual target, runtime port |
| Duplication | raw event counts and source attribution |
| Failure | broker/UI/runtime crash, disconnect, permission denial |
| Cleanup | physical release, virtual neutral state, next-launch recovery |
| Performance | forwarding latency and dropped/coalesced reports |
| Result | pass, mismatch, untested, and exact blocker |

Missing fields remain missing. Never synthesize identity or convert unavailable
hardware into a passing case.

## Risk Map

| Risk | Boundary | Mitigation |
| --- | --- | --- |
| Physical and virtual duplicate input | platform isolation | Gate 0 before production routing; event-source evidence |
| Emulator counts blocked physical devices | runtime inventory | per-runtime visible/openable inventory and port tests |
| Broker crash leaves controls seized | native lifecycle | watchdog, OS-owned handle cleanup, forced-exit tests |
| Stuck buttons after route change | report lifecycle | neutral report before every clear/reassign/shutdown |
| Privileged component expands attack surface | broker/driver | minimal protocol, least privilege, input validation, signed artifacts |
| Platform API leaks into Flutter | architecture | platform-neutral IPC and report contracts |
| Mapping applied twice | router/writer | virtual device carries mapped layout; runtime writers avoid physical remap |
| Rumble targets wrong physical pad | output routing | virtual-player output correlated through active route snapshot |
| Runtime update changes device behavior | artifact resolver | exact hashes and invalidated policies |
| Linux solution requires root | deployment | udev/systemd permissions and installed non-root validation |
| macOS entitlement cannot ship | distribution | V1/V2 go/no-go before broker integration |
| Windows driver cannot be maintained | release/security | defer until signed VHF/filter feasibility review |

## Validation Strategy

Documentation-only slices require link/path checks, diff review, and explicit
untested evidence. Native broker slices require unit tests for report encoding,
mapping, neutralization, malformed IPC, lifecycle, and watchdog behavior plus
platform integration tests.

Console changes continue to require, from `clients/romd_console`:

```bash
mise run analyze
mise run test
```

Runtime work also requires the negative searches from the console runtime skill.
Unexpected validation failures must be interpreted through
`docs/known-issues.md` before behavior changes or retries.

Platform hardware validation is not replaceable by mocks. Automated tests can
prove contract and state-machine behavior; they cannot prove OS isolation.

## Parallelization

After V0 freezes the shared contract, macOS and Linux isolation probes may run
in parallel when they do not edit the same protocol or roadmap files. Runtime
inventory research can run independently per exact artifact.

Do not parallelize:

- shared IPC/report contract edits;
- Flutter launch snapshot integration and broker snapshot parsing;
- physical input ownership and virtual output lifecycle in the same backend;
- emulator writer changes before that runtime's isolation evidence is reviewed;
- production routing and the Gate 0 probe intended to authorize it.

## Explicit Non-Goals

- owning or replacing the complete Linux distribution;
- requiring containers or a VM for normal emulator execution;
- durable player seating;
- exposing physical controller identity directly to emulators;
- trusting connection order as cross-process identity;
- enabling virtual devices before physical isolation is proven;
- forcing every platform to ship deterministic routing simultaneously;
- live reseating of an already-running emulator in the first release;
- full motion/touchpad/adaptive-trigger parity in the first virtual descriptor;
- PCSX2 mapping before isolation, research, and explicit implementation approval;
- renaming existing runtime profile ids.

## Open Decisions And Approvals

Required before implementation:

1. Approve the cross-platform broker repository/package location after V0.
2. Approve macOS CoreHID production work only after V1/V2 evidence.
3. Approve Linux broker production work only after V5 isolation evidence.
4. Approve any runtime-scoped policy that cannot meet platform-wide isolation.
5. Approve PCSX2 mapping only after virtual-routing Phase 6B and separate
   runtime research.
6. Approve Windows driver/signing scope after VHF/filter feasibility.

No `acceptedBestEffort` physical-to-runtime ordinal policy is authorized by
this roadmap.

## Amendment Protocol

For every landed slice:

1. update its status and record exact commit evidence;
2. record exact automated and manual validation without overclaiming;
3. list untested hardware and platform/runtime boundaries;
4. preserve the original roadmap's UX, identity, persistence, and fail-closed
   contracts unless an explicit owner decision changes them;
5. update runtime policy documents when evidence changes;
6. add to `docs/known-issues.md` only for newly confirmed durable environment
   facts, not ordinary implementation history.
