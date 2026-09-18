# ROMD Console Dolphin Runtime Roadmap

Status: GameCube automated acceptance passed; owner hardware acceptance
pending. Wii is deferred pending a separate input contract.

## Product contract

ROMD will add Dolphin as a managed standalone runtime for Nintendo GameCube
content (`gc`) first. Dolphin also emulates Wii, but Wii input is not a second
GameCube profile: Wii Remote orientation, pointing, motion, extensions, and
game-specific layouts require their own product and runtime policy. The first
slice must not claim Wii support.

The shipped GameCube profile id is append-only:
`dolphin:gc:standalone`. Dolphin is the default and only GameCube runtime until
an alternate is deliberately added.

ROMD owns:

- provisioning the exact managed Dolphin build;
- structured direct launch in batch/fullscreen mode;
- a shared ROMD-owned Dolphin user directory;
- native GameCube saves under `saveRoot` and save states under `stateRoot`;
- a fail-closed, canonical-position GameCube pad configuration inside the
  approved controller policy.

ROMD does not own Dolphin graphics tuning, compatibility overrides, Wii input,
real GameCube adapters, passthrough Wii Remotes, multiplayer port routing, or
arbitrary Dolphin hotkeys in this slice.

Settings → Emulators exposes an explicit `Open Dolphin settings` action. It
provisions the same managed build, then launches Dolphin's own desktop UI with
the shared ROMD user directory and without a game, batch mode, or fullscreen
override. ROMD supervises that process and reclaims foreground when it exits.
The screen warns that keyboard or mouse may be required and keeps controller
mapping in Settings → Controllers; it does not imply that Dolphin's desktop UI
is a controller-first surface.

Dolphin batch mode has no in-game emulator menu or overlay. ROMD must not
describe pause, windowing, or process exit as a Dolphin menu. Inside the same
approved singleton SDL3 policy used for gameplay, ROMD translates its standard
modifier + Menu chord to Dolphin's `Stop` hotkey. In batch mode, Stop performs
a clean emulation shutdown and exits Dolphin, returning control to ROMD. If the
user has explicitly bound ROMD's Quit action, that chord takes precedence over
the Menu chord. An unbound modifier, unbound action, modifier collision, or any
ineligible controller policy emits no Dolphin controller hotkey.

## Runtime and artifact contract

The managed macOS artifact is Dolphin 2606 from the official release URL:

`https://dl.dolphin-emu.org/releases/2606/dolphin-2606-universal.dmg`

Verified archive SHA-256:

`908f60ddcccec46507f2ed629ad0cd82f4065e801efa7828676ae89274cc740a`

Verified inner `Dolphin.app/Contents/MacOS/Dolphin` SHA-256:

`354f1f443e419393086e1b6b3aa58fa407d695f87acca4c90754c013ae69fcc2`

The downloaded app passed strict deep code-signature verification. Its Dolphin
executable is a universal x86_64/arm64 Mach-O and reports version 2606. ROMD
launches the inner `Dolphin.app/Contents/MacOS/Dolphin` executable with a
structured argument list and never through a shell. Managed readiness verifies
the inner executable digest on every provision check; a changed app is replaced
from the pinned archive before settings or gameplay may use it.

Dolphin's documented `-u` option scopes the complete user directory. ROMD uses
`<runtimesRoot>/dolphin/user` so graphics, audio, and other ordinary emulator
preferences persist across titles. Immediately before a game launch, its `GC`
save surface is linked to the active profile and title's `saveRoot`, and
`StateSaves` is linked to that profile and title's `stateRoot`. ROMD may
retarget only links it owns. Dolphin's settings UI may create data-free `GC`
region folders and an empty `StateSaves` directory; ROMD safely adopts only
those empty directory trees with its managed links. It renames and revalidates
the displaced tree, removes directories only with non-recursive empty-directory
operations, and restores the original structure if adoption fails. Any file,
nested link, or other data at either boundary fails closed and remains
untouched. The content directory remains only the gameplay working directory
and game-image location.

Opening settings creates the shared user-directory structure and reasserts the
managed update boundary. It does not select a title, repoint save/state links,
write controller configuration, or mark any game played. A running game blocks
settings launch, and while settings are open the normal active-session
supervision prevents ROMD from treating the desktop UI as an unsupervised child
process.

ROMD merges, rather than replaces, shared native configuration. It disables
Dolphin's update track because an in-place self-update would invalidate the
pinned runtime contract. At gameplay launch it reasserts GameCube Slot A as a
folder memory card and Slot B as absent. Dolphin's default regional hierarchy,
`GC/<region>/Card A`, resolves through ROMD's managed `GC` link into the active
title's `saveRoot`; ROMD must not flatten that hierarchy with a custom GCI
folder override. ROMD shipped no external build with the obsolete override, so
the final writer simply never emits it and carries no migration branch. Raw
memory-card paths remain inside `saveRoot` but are inactive while Slot A uses
GCI-folder mode. ROMD owns only the hotkey device plus Stop and optional Pause
keys. Other Dolphin hotkeys and ordinary graphics/audio preferences are
preserved.

Settings-session ownership is reserved atomically before provisioning begins
and replaced by the live settings process after start. Gameplay-session
ownership is reserved after dependency preparation but before the launch
provider may create a process, then replaced by the live gameplay session. A
competing launch never overwrites the active-session handle.

## GameCube controller contract

Dolphin 2606 source tag `2606` is authoritative for the first policy. Its
macOS build enables the SDL 3 controller backend and names canonical inputs
`Button S`, `Button E`, `Button W`, `Button N`, `Left X/Y`, `Right X/Y`,
`Trigger L/R`, `Shoulder L/R`, `Pad N/S/W/E`, `Back`, and `Start`.

The first production policy is
`dolphin-macos-2606-single-controller-sdl3-gameplay-v1`. It is eligible only
when all of the following are true:

- the operating system is macOS;
- the Dolphin executable is the ROMD-managed, digest-pinned artifact;
- launch uses one inventory-reviewed controller;
- that controller has exact SDL identity;
- a saved hardware mapping exists for the same SDL platform and GUID;
- only GameCube player one is emitted.

Under that envelope, ROMD supplies the saved mapping through the
process-scoped `SDL_GAMECONTROLLERCONFIG` environment variable and writes
`GCPadNew.ini` against Dolphin's SDL canonical names. The provider ordinal is
not treated as a Dolphin port. A single same-name SDL device receives Dolphin
device id `0` by the pinned runtime's source contract; multi-controller
emission remains suppressed.

Canonical face positions map to the emulated GameCube controller as follows:

| ROMD canonical position | GameCube control |
| --- | --- |
| South | A |
| East | B |
| West | X |
| North | Y |

The left stick maps to the GameCube control stick, the right stick to the
C-stick, triggers to analog and digital L/R, right shoulder to Z, D-pad to
D-pad, and Start to Start. Printed controller labels never change this
positional mapping.

Missing, malformed, stale, mismatched, fallback-identity, external-runtime, or
multi-controller input suppresses ROMD-authored gameplay configuration. ROMD
must not guess a Dolphin device from display name, VID/PID, or provider order.

## Persistence and schema

No SQLite migration is required. Dolphin consumes the existing schema-10
device-global hardware map keyed by `(sdlPlatform, sdlGuid)`. Runtime profile
registration adds an immutable profile id but no new persisted table or enum.

## Acceptance

Automated acceptance requires:

- profile selection and ordering for platform `gc`;
- pinned catalog URL, kind, install path, and digest;
- generic DMG app discovery without RetroArch-specific archive assumptions;
- managed provision, stale replacement, digest rejection, and unsupported-OS
  behavior;
- installed-executable mutation detection and pinned replacement;
- exact structured command arguments and inner app executable resolution;
- a supervised settings launch with shared `-u`, no `-b`, no `-e`, no content
  boot, foreground restoration, and active-session exclusion;
- save/state/config isolation with no durable path under `contentRoot`;
- deterministic `GCPadNew.ini` bytes and SDL mapping environment bytes inside
  the approved singleton policy;
- deterministic `Hotkeys.ini` return-to-ROMD bytes inside that same policy,
  preservation of unrelated native hotkeys, and no false in-game-menu claim;
- `Dolphin.ini` auto-update suppression and fail-closed save-path reassertion;
- suppression for missing map, external runtime, fallback identity, and every
  multi-controller case;
- launch-provider propagation from the persisted hardware mapping to the
  Dolphin adapter;
- clean analysis, focused tests, full console tests, runtime negative searches,
  and `git diff --check`.

Owner hardware acceptance requires one managed Dolphin 2606 GameCube launch
with the mapped 8BitDo Pro 2: all face positions, D-pad, both sticks, both
triggers, shoulders/Z, Start, persistence after ROMD restart, native in-game
save persistence, and clean return to ROMD. It also requires opening Settings →
Emulators → Dolphin, changing a harmless graphics preference, closing Dolphin,
confirming ROMD regains focus, and reopening settings to confirm persistence.
The policy remains provisional until those runs pass.

Owner acceptance completed on 2026-07-15 for the current managed macOS
envelope. The mapped 8BitDo Pro 2 controls, native save persistence, Settings
UI persistence, both return-to-ROMD lifecycles, and two-profile isolation all
passed. The Super Mario Sunshine recovery run loaded the imported regional GCI
progress, saved it under `USA/Card A`, and exited cleanly; the obsolete flat GCI
file remained unchanged.

## Validation evidence

Validated on 2026-07-14:

- `mise run analyze`: no issues.
- focused settings, adapter, profile, catalog, unpacker, provider, runtime-
  integration, and session-ownership tests: 150 passed.
- `mise run test`: 715 passed.
- `git diff --check`: clean.
- runtime shell-string search: only the established structured process-runner,
  SDL dependency probe, and documentation references matched; no shell launch
  was added.
- durable-path search: no save, state, or config path under `contentRoot`.
- `AppDatabase.schemaVersion`: remains 10; no Drift declaration, migration, or
  generated file changed.

Automated bytes and the owner hardware acceptance above complete the current
managed macOS singleton policy. Deferred platforms, Wii input, and multiplayer
remain outside that policy.

## Required runtime decisions

- `artifact resolver: yes - opening Dolphin settings must provision or resolve the managed Dolphin 2606 executable on demand.`
- `config writer: yes - shared Dolphin configuration must remain ROMD-scoped while per-game saves and states are routed to each target’s durable roots.`

## Deferred follow-ups

- Wii and Wii Remote/classic-controller input policy.
- Multiplayer GameCube ports and duplicate-model device correlation.
- Real GameCube controller adapters.
- Additional Dolphin hotkeys beyond return-to-ROMD and optional pause.
- Windows and Linux managed artifacts and filesystem-specific user-directory
  routing.
