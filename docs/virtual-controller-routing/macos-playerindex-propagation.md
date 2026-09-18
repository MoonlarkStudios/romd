# macOS GCController playerIndex Propagation Research

Status: executed; classification `perProcess`; forward propagation failed and
this path is closed

Created: 2026-07-10

Executed: 2026-07-11 (UTC)

Related:

- [Console Virtual Controller Routing Roadmap](../console-virtual-controller-routing-roadmap.md)
- [macOS build 25F84 isolation research](macos-isolation-25F84.md)
- [RetroArch macOS 1.22.2 Metal policy research](../runtime-controller-policies/retroarch-macos-1.22.2-metal.md)

## Purpose

Determine whether `GCController.playerIndex` state crosses process boundaries
through the GameController daemon. If it does, ROMD can dictate RetroArch's
MFi slot for a specific controller by setting `playerIndex` in its own process
before launch, then emit matching `input_playerN_joypad_index` values — with no
RetroArch patch, no joypad-driver change, and no virtual devices.

This research targets only the identity/seating problem for the RetroArch MFi
envelope. It does not test, weaken, or reopen the failed Gate 0 physical-input
isolation classification, and it authorizes no production behavior by itself.

## Hypothesis Chain

```text
ROMD seated controller (SDL GUID + serial)
        |
press-correlation during the Change Order ceremony   [separate owner approval]
        |
GCController object in ROMD's process
        |
ROMD sets GCController.playerIndex = N
        |
gamecontrollerd shared state                          [THIS RESEARCH]
        |
RetroArch MFi driver accepts the pre-set index when free   [source-verified]
        |
input_playerN_joypad_index = "N"                      [existing fail-closed writer]
```

Every link except the bracketed two is already established. The ceremony
press-correlation link is out of scope here and requires its own owner decision
under the "never invent exact correlation" contract before any implementation.

## Primary-Source Facts

- At pinned commit `69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576`, the MFi driver
  consumes `[GCController controllers]`, addresses input by
  `GCController.playerIndex`, and at connection accepts that index when free or
  assigns the first free slot from zero through three
  ([mfi_joypad.m](https://github.com/libretro/RetroArch/blob/69a4f0ea1e8aaf442ae4858f2e7f2b31a1776576/input/drivers_joypad/mfi_joypad.m#L500-L565)).
- Required source recheck before the trial: confirm whether the driver writes
  the assigned slot back to `controller.playerIndex` on connect. Direction B
  below depends on that write; Direction A does not.
- Apple documents `playerIndex` as app-managed state that drives the physical
  player LEDs. Cross-process visibility and persistence are undocumented. The
  LEDs are hardware, so some daemon-side state exists; whether the property
  value itself is shared, per-process, or per-connection is the open question.
- The prior V1 probe confirmed both ROMD's probe process and RetroArch observe
  the same physical controller through GameController simultaneously
  (non-exclusive delivery), so both processes hold live `GCController` views of
  one device while the trial runs.

## Trial Directions

- Direction A (ROMD → runtime, the shippable mechanism): the probe sets
  `playerIndex` and holds; RetroArch is launched inside the window; evidence is
  the `[MFI]` connection log adopting the pre-set slot plus observed menu port
  behavior.
- Direction B (runtime → ROMD, corroboration): the probe only observes;
  RetroArch assigns a slot at connect; evidence is a probe
  `playerIndexTransition` record changing away from the baseline value without
  any probe write.

Either direction proving propagation is significant. Direction A passing is the
mechanism ROMD would actually use.

## Probe Support

`tools/macos_controller_isolation_probe` now provides:

```bash
swift run romd-macos-controller-probe player-index \
  --controller-order 0 \
  [--set <-1...3>] \
  --duration-seconds <1...300>
```

Raw values 0...3 correspond to `GCControllerPlayerIndex` players 1...4 and MFi
slots 0...3; -1 is `indexUnset`. The command waits up to 10 seconds for the
selected controller, records a baseline, optionally sets the index once, then
polls for the bounded window and emits a redacted `playerIndexTransition`
record for every observed change with elapsed milliseconds. It records
`propagationClassified: false` and never classifies. A disconnected selected
controller stays absent for the rest of the window because a reconnect creates
a new GameController object; reconnects appear as count transitions and in the
closing inventories.

## Trial Protocol

Environment to freeze at trial time: macOS build, ROMD revision, probe binary,
exact reprovisioned RetroArch 1.22.2 marker check, controller model, transport,
and mode when exposed. Reuse the isolated-config lessons from the V1 report:
pre-create a complete temporary RetroArch config root, expect `--max-frames`
not to bound menu mode, keep a manual watchdog, and treat verbose logs as
containing unrelated identifiers — review or redact before retention.

Single 8BitDo Pro 2 over USB first:

1. T1 baseline observation (Direction B): `player-index --duration-seconds 180`
   with no `--set`; launch RetroArch menu-only mid-window; record whether the
   probe observes any `playerIndexTransition` when RetroArch connects the pad,
   and capture the `[MFI]` connect line.
2. T2 pre-seed (Direction A): `player-index --set 2 --duration-seconds 180`;
   launch RetroArch mid-window; pass requires the `[MFI]` log adopting slot 2
   and no reassignment to slot 0. A slot-0 assignment with the probe still
   holding is a propagation failure or an override; capture which.
3. T3 persistence without a live setter: run `player-index --set 2
   --duration-seconds 5`, let the probe exit, then launch RetroArch; records
   whether daemon state outlives the setting process. ROMD's real launcher
   stays alive, so T3 failing does not block the mechanism; it bounds it.
4. T4 reconnect: during a T2-style window, disconnect and reconnect the pad;
   record the new connect's slot and whether any pre-seeded value survives.

Full matrix before any classification upgrade: families A/B and B/A, reversed
seating, identical pads with and without usable serials, DualSense USB and
Bluetooth, mixed transport, connection before/after RetroArch start,
RetroArch-only restart, probe-only restart.

## Executed Trial Evidence (2026-07-11 UTC)

Environment: macOS 26.5.2 build 25F84 arm64; exact managed RetroArch 1.22.2
Metal (pinned marker per the V1 reprovisioning); 8BitDo Pro 2 over USB, mode
unknown; one controller; probe revision uncommitted at trial time. Each launch
used a fresh temporary config root pinning `input_joypad_driver = "mfi"`.
Verbose output was filtered to `[MFI]`/`[Autoconf]`/joypad-driver lines and raw
logs were deleted.

Two independent T2 executions (owner, then agent reproduction):

- The probe set `playerIndex = 2` with `readBackMatchesRequest: true` and held
  it for the full window (180 s and 120 s respectively).
- RetroArch, launched mid-window, logged `[MFI] Controller given desired index
  0.` and `[Autoconf] 8BitDo Pro 2 configured in port 1.` in both runs. At the
  pinned source, desired index is read from `controller.playerIndex` clamped
  from unset to zero; a propagated value would have produced desired index 2
  and port 3.
- The probe observed zero `playerIndexTransition` records across both windows;
  the agent run's closing record was `transitionCount: 0`,
  `finalPlayerIndex: 2`, `finalPresent: true`.

Forward propagation (ROMD to runtime) therefore failed, and no write-back
(runtime to ROMD) was observed while RetroArch assigned and used slot 0. The
write-back observation carries the standing caveat that the MFi driver writing
`playerIndex` in its own process was never source-verified.

T1, T3, and T4 were not run. With the forward direction disproven twice under
correct procedure, they cannot revive the shippable mechanism and were
dropped.

## Outcome Classification

Assigned from the executed evidence:

- **`perProcess` — selected (2026-07-11).** `GCController.playerIndex` is
  process-local view state; `gamecontrollerd` does not share it between
  clients in either direction. The pre-seeding mechanism cannot work.

The candidate classes considered before execution:

- `propagatesShared`: cross-process visibility in both directions, stable
  across T1-T4.
- `propagatesBounded`: propagation holds for a live setter process but not
  beyond documented bounds (for example T3 fails); mechanism remains viable for
  ROMD's launcher lifecycle with the bounds recorded.
- `perProcess`: no cross-process visibility; this path is dead. Fallbacks are
  the unclassified `input_joypad_driver = "hid"` envelope probe and the
  Linux-first V2 decision (roadmap option 3).

## Fallback Facts Observed During Execution

Read-only driver-activation checks against the exact managed artifact, each
with a fresh isolated config and one 8BitDo Pro 2 over USB:

- `input_joypad_driver = "hid"` activated (`Found HID driver: "iohidmanager"`,
  `Found joypad driver: "hid"`) and enumerated the pad (`[IOHID] Port 0:
  8BitDo Pro 2`) with successful autoconfiguration. No permission prompt was
  observed, though pre-existing grants were not audited.
- `input_joypad_driver = "sdl2"` activated and autoconfigured the pad, with
  haptic fallback to joystick rumble.

Both are config-only switches requiring no artifact change. Each is a separate
unclassified envelope: the `hid` envelope must retest seizure isolation
(including whether the Apple synthetic compatibility device can still carry
input), and the `sdl2` envelope must establish device-reservation and
device-filtering semantics before any correlation claim. Neither activation
fact is isolation or correlation evidence.

## Stop Conditions

- T1 and T2 both show no propagation: classify `perProcess` and stop.
- RetroArch adopts a pre-set slot inconsistently across repeated identical
  runs: record exactly; nondeterministic adoption cannot back a forced writer.
- Any step requiring synthesized identity, name-promoted identity, or ordinal
  trust to interpret results: stop and record the gap instead.

## What This Research Does Not Authorize

- No config-writer change; `input_playerN_joypad_index` emission stays
  suppressed under the existing `uncorrelated` policy.
- No ceremony press-correlation implementation.
- No change to the Gate 0 `notIsolated` classification or virtual-device work.
- No new durable schema, seating rows, or runtime profile changes.

## Invalidation

macOS build, GameController framework behavior, RetroArch artifact or joypad
driver, probe revision, or controller firmware/transport changes invalidate
affected evidence. A passing single-pad trial does not generalize; only the
full matrix supports a policy classification upgrade in the RetroArch policy
document.
