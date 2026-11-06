# ROMD macOS Controller Isolation Probe

Development-only Gate 0 diagnostic. It is not linked to Flutter, normal ROMD
launches, persistence, runtime config writers, or a virtual-device backend. It
never classifies isolation.

Build and test:

```bash
swift build
swift test
```

Capture independent IORegistry, CoreHID, IOHID, and GameController baseline
observations:

```bash
swift run romd-macos-controller-probe inventory --duration-seconds 5
```

The JSONL output lists an ephemeral `candidateId` for each joystick/gamepad HID
candidate. Review the inventory and then explicitly seize exactly one candidate:

```bash
swift run romd-macos-controller-probe seize \
  --candidate-id hid-0123456789abcdef \
  --duration-seconds 5
```

Seizure is released on normal completion and Swift error unwinding. Forced
termination and runtime isolation still require manual evidence. A successful
`seizeOpen` record does not prove isolation.

Observe one GameController's `playerIndex`, optionally setting it first:

```bash
swift run romd-macos-controller-probe player-index \
  --controller-order 0 \
  --set 2 \
  --duration-seconds 180
```

Raw values 0...3 correspond to `GCControllerPlayerIndex` players 1...4 and to
RetroArch MFi slot numbering; -1 is `indexUnset`. During the bounded window the
probe polls the selected controller and emits a `playerIndexTransition` record
for every observed change, including changes made by another process. It sets
`playerIndex` only when `--set` is passed, and it never classifies propagation.
If the selected controller disconnects, it remains absent for the rest of the
window even if the physical device reconnects, because the replacement is a new
GameController object; reconnects appear as controller-count transitions and in
the closing inventory instead.

During the bounded seizure window the probe counts attributed IOHID input-value
callbacks and records only event count plus first/last monotonic timestamps. It
does not retain element identifiers or raw control values. A nonzero count
proves only that the probe received physical input while seized; emulator input
and isolation must be observed independently.

Serial, unique, registry, and location values are emitted only as short SHA-256
fingerprints. Product, manufacturer, VID/PID, transport, usage, synthetic
marker, GameController display fields, and player index remain evidence fields.
Inspect output before sharing because controller names may still be identifying.
The IORegistry observer excludes unrelated HID services and retains only
joystick, gamepad, multi-axis controller, and marked synthetic representations.

This milestone does not yet provide ROMD SDL inventory, managed-emulator
inventory/log collection, input-event attribution, output feedback, or
virtual-device creation. Those remain required before Gate 0 can be classified.
