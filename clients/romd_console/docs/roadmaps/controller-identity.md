# Console Controller Identity Roadmap

[Controller identity](../decisions/controller-identity.md) owns session
assignment and exact device identity. [Mapping ownership](../decisions/controller-mapping-ownership.md)
owns the device-global map and runtime output. [Virtual controller routing](../decisions/virtual-controller-routing.md)
owns the deferred routing boundary. ROMD assigns local profiles to controllers
for a session. The emulator assigns its own gameplay ports.

## Open work

- Audit Players, Change Order, and pre-launch copy for claims about emulator
  port order. Keep controller assignment and emulator port selection clear.
- Qualify navigation before setup. Connection order can seed ephemeral session
  order within the [usable-before-setup rules](../decisions/controller-identity.md#usable-before-setup).
- Confirm runtime-native shortcuts and return-to-ROMD behavior on each supported
  device envelope. Any multi-pad shortcut policy or broader RetroArch emission
  needs separately reproduced evidence and an owner decision.
- Define Nintendo-native action semantics separately from physical face-button
  positions.
- Keep controller-family icons, explicit device-to-profile affinity, and guest
  play-history attribution as independent backlog items.
- Revisit PCSX2 custom mapping only when the actual process exposes a reliable
  input contract. The [PCSX2 adapter research](../research/runtime-controller-policies/pcsx2-macos-2.6.3.md)
  records the rejected approaches. Kiosk port ordering would need a separate
  routing decision.

## Validation

Use the [console development checks](../development.md) for source changes.
The [runtime controller research](../research/runtime-controller-policies/README.md)
records each adapter's measured inputs and limits. Release claims follow the
[console hardware qualification matrix](../../../../docs/architecture/media-status.md#console-runtime-and-controller-scope).
