# PCSX2 macOS 2.6.3 Controller Experiment

Status: custom ROMD mapping rejected by two owner hardware experiments on
2026-07-14; no active PCSX2 controller policy id; ordinary PCSX2-owned input
remains available

## Frozen Runtime Evidence

- Official immutable asset:
  <https://github.com/PCSX2/pcsx2/releases/download/v2.6.3/pcsx2-v2.6.3-macos-Qt.tar.xz>
- Archive size: 28,960,388 bytes.
- Archive SHA-256:
  `cb7b9e6330f1abf0cf92c94065f7eb983d0fa8affcfe6b0ccb9c2a4ebf067f1a`.
- Release/source: [v2.6.3, commit `bc8151d`](https://github.com/PCSX2/pcsx2/tree/v2.6.3).
- Executable SHA-256:
  `1972341a1bf079e3b5180eeba9c2c233574a9fe38cadd4e5e85ea179e334f13d`.
- Bundled `libSDL3.0.dylib` SHA-256:
  `c97d766b3bc471578fa8e4cfb60c3c5d2fcbe47f739b8ad5f81fc631b4ddd72b`.
- The executable and SDL hashes from the existing ROMD-managed install matched
  files freshly extracted from the official archive.
- The PCSX2 executable and SDL library are x86_64 and run under Rosetta on the
  tested arm64 Mac.

The artifact remains digest-pinned as independent runtime hardening. That pin
does not authorize controller correlation.

## Canonical Invariant

ROMD's hardware editor remains positional. South is always South, regardless
of whether the controller prints A, B, Cross, or another label there. Button
labels are presentation only.

The intended PlayStation 2 projection remains:

| Canonical position | DualShock 2 control |
| --- | --- |
| South / East / West / North | Cross / Circle / Square / Triangle |
| D-pad | Up / Right / Down / Left |
| Left and right shoulders | L1 and R1 |
| Left and right triggers | L2 and R2 |
| Left and right stick presses | L3 and R3 |
| Left and right sticks | matching signed X/Y directions |
| Back / Start | Select / Start |

The rejected experiments do not change this contract or any saved canonical
mapping.

## Experiments And Rejection

Batocera commit
[`965190aa9513bb32ad04597be1ba764591e6426f`](https://github.com/batocera-linux/batocera.linux/tree/965190aa9513bb32ad04597be1ba764591e6426f)
was used as a behavioral reference for PCSX2's symbolic bindings, database
path, and regenerative configuration. ROMD did not copy its GPL generator.

The first implementation incorrectly assumed that a reviewed ROMD SDL GUID and
one connected controller were sufficient to authorize PCSX2 `SDL-0`. Hardware
evidence disproved that assumption:

- arm64 ROMD enumerated the USB 8BitDo Pro 2 through its macOS MFi domain as
  GUID `030001f2c82d00000660000000026800`, with 6 axes and 15 buttons;
- x86_64 PCSX2 under Rosetta loaded ROMD's user controller database but opened
  the same physical unit as `HID`, with 6 axes and 19 buttons;
- PCSX2's bundled macOS Pro 2 entries use different
  `03000000...010000`/`...020000` GUIDs and a different raw-button surface;
- setting `SDLIOKitDriver = false` and `SDLMFIDriver = true` was applied—PCSX2
  logged `IOKit is disabled, MFI is enabled`—but it still opened the controller
  as the 19-button HID device;
- the resulting face controls were reversed from ROMD's canonical positions:
  printed B/South acted as Circle and printed A/East acted as Cross, with the
  other face controls reversed as well.

The second experiment removed the remaining obvious correlation assumptions:

- a Cocoa-hosted x86_64 helper loaded PCSX2's exact bundled SDL 3.2.26 under
  Rosetta and observed one `HID` controller with target GUID
  `0500b7b5ac05000004000000452f6d04`, VID/PID/version
  `05ac:0004:2f45`, 6 axes, 19 buttons, and no hats, path, or serial;
- attributed raw capture established the complete physical translation.
  South/East/West/North were target buttons `3/2/8/7`, while PCSX2's selected
  generic map used `2/3/7/8`;
- a narrowly gated release build re-verified the exact artifact, SDL and
  controller-database fingerprints, host/runtime architecture, hints,
  singleton topology, target identity, capabilities, and selected mapping
  immediately before launch;
- it supplied the corrected target SDL mapping only to that process and
  temporarily wrote the canonical DualShock 2 Pad1 projection;
- the owner then launched a PS2 game with the exact USB Pro 2 envelope. The
  face buttons were still incorrect.

That live failure is definitive. Observing the same SDL library, backend hints,
target device, raw controls, and selected mapping in a helper still does not
prove that PCSX2's complete Qt/input stack will consume ROMD's configuration as
predicted. ROMD cannot honestly own PCSX2 controller mapping through its
current environment/INI/database surfaces.

## Production Behavior

- PCSX2 controller references remain `uncorrelated`.
- No saved ROMD hardware mapping authorizes PCSX2 gameplay emission.
- ROMD does not inject `SDL_GAMECONTROLLERCONFIG` for PCSX2.
- ROMD does not create or replace PCSX2 Pad sections.
- ROMD preserves existing PCSX2-owned `InputSources`, `SDLHints`, `Pad`, and
  `PadN` configuration so ordinary emulator input and owner-created bindings
  remain available.
- The writer removes only the explicitly framed ROMD controller-database entry
  left by the first rejected experiment; unrelated database entries are
  preserved.
- Controller shortcuts and multi-controller port routing remain unapproved.
- No production verifier, helper, target-domain alias, or policy id ships.

## Reconsideration Gate

Do not attempt a third configuration-writer calibration. Reconsider ROMD-owned
PCSX2 mapping only if PCSX2 exposes a supported, deterministic external input
contract that can be verified in the actual emulator process, or if ROMD owns
the emulator integration deeply enough to remove the cross-process ambiguity.
Names, VID/PID, GUIDs, helper observations, backend hints, singleton order, and
raw capture are all insufficient on their own or in combination.

## Remaining Boundaries

- Multi-controller PCSX2 routing remains `uncorrelated`.
- Generic SDL pads do not reproduce DualShock 2 pressure-sensitive buttons.
- PCSX2 memory cards, save states, screenshots, and settings currently remain
  in its shared ROMD runtime user directory. Moving those files requires a
  migration-safe follow-up.
- Automated rollback validation is recorded in the controller management
  roadmap.

- `artifact resolver: yes - the existing PCSX2 2.6.3 managed artifact is cryptographically pinned; the pin does not authorize controller emission.`
- `config writer: yes - the PCSX2 writer removes only ROMD's rejected framed mapping and preserves PCSX2-owned input configuration.`
