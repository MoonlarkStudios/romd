# ROMD Console Dolphin Runtime Roadmap

Ottercade registers the managed GameCube runtime as the `dolphin:gc:standalone` profile.
See the [Dolphin runtime notes](../emulator-runtime-pattern.md#dolphin-gamecube)
for current behavior and the
[qualification matrix](../../../../docs/architecture/media-status.md#console-runtime-and-controller-scope)
for the exact managed macOS hardware envelope.

## Deferred follow-ups

- Wii support needs a separate Wii Remote, extension, motion, and pointing input
  policy.
- Multiplayer GameCube ports and duplicate-controller correlation need their own
  evidence under the [routing decision](../decisions/virtual-controller-routing.md).
- Physical GameCube controller adapters and native passthrough remain separate
  from the managed controller mapping policy.
- Additional Dolphin hotkeys need runtime-specific qualification.
- Windows and Linux need independently pinned managed artifacts and validation
  of user-directory routing, launch, and cleanup on each platform.
