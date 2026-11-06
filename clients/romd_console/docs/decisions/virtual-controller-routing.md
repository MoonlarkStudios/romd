# Console Virtual Controller Routing

Related decisions: [controller identity](controller-identity.md),
[controller mapping ownership](controller-mapping-ownership.md). The
[controller identity roadmap](../roadmaps/controller-identity.md) holds the
active continuation, and the evidence is in the
[virtual controller routing research](../research/virtual-controller-routing/README.md),
including the [macOS 25F84 isolation report](../research/virtual-controller-routing/macos-isolation-25F84.md)
and the [`GCController.playerIndex` propagation research](../research/virtual-controller-routing/macos-playerindex-propagation.md).

## Context

ROMD Console identifies and seats physical controllers through SDL, by GUID
plus serial where available, but external emulators assign their own
runtime-local device references. DuckStation's `SDL-N` and RetroArch's MFi
slots are process-local ordinals, not shared identity, and treating an ordinal
as identity would route mappings or P1 shortcuts to the wrong physical
controller.

Deterministic per-player routing would need ROMD to own virtual P1–P4 devices,
route seated physical input into them, and prove that each launched emulator
cannot also read the physical controllers. On macOS that isolation failed:
with the physical controller exclusively seized through IOHID, RetroArch 1.22.2
still received physical input under both the MFi and iohidmanager drivers, and
`GCController.playerIndex` set in ROMD's process does not propagate to
RetroArch.

## Decisions

### Emulators Assign Ports

- Deterministic per-player port routing is out of scope for ROMD on every
  platform. Emulators assign ports.
- ROMD owns controller identity, seating, profiles and mappings.
- No physical-to-runtime ordinal policy is authorized.

### macOS Behavior

- macOS keeps the automatic-order fallback.
- P1 shortcuts that cannot be correlated with the seated controller are
  suppressed.
- Config writers fail closed rather than guess a correlation.

### Product Constraints

These two constraints, together with the macOS findings, leave no platform
where the routing data path can be built:

1. ROMD supports emulators beyond RetroArch, so an in-process libretro embed
   cannot substitute for routing.
2. The Linux kiosk runs with stock input configuration. Normal operation needs
   no `uinput` broker, `EVIOCGRAB` acquisition, udev or systemd device policy,
   or device-namespace layer.

### Re-decision Triggers

Reopening this decision needs a new owner decision. These triggers would
justify reconsidering it:

- multi-pad port order becomes a real complaint on a kiosk deployment. The
  remaining candidate is best-effort ordinal seating, the Batocera approach.
- either product constraint changes
- a platform gains a mechanism that passes physical-device isolation, under
  the [platform isolation template](../research/virtual-controller-routing/templates/platform-isolation-research-template.md),
  for every required managed runtime

## Trade-offs

- **Port order is the emulator's.** With several pads, player order follows the
  emulator's own assignment and can differ between sessions.
- **Fewer shortcuts on macOS.** P1 shortcuts are unavailable whenever the
  seated controller cannot be correlated.

## Rejected Alternatives

- **Virtual-controller routing.** Rejected: macOS physical-device isolation
  failed, and the product constraints close every other platform. The planned
  macOS broker, Linux `uinput` broker, Windows VHF feasibility work and
  virtual-routing release gate were closed unstarted.
- **An in-process libretro embed.** Rejected: ROMD supports emulators beyond
  RetroArch.
- **Best-effort ordinal seating.** Rejected while a correct alternative was
  still being evaluated. It remains the first candidate if this decision
  reopens.

## Guardrails

- **`play/retroarch_config_writer_test.dart` and
  `play/emulator_launch_plan_test.dart`:** uncorrelated shortcuts are
  suppressed, and writers emit no guessed correlation.
- **Review:** no production or diagnostic virtual-device code is added without
  a new decision.
