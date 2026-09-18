import 'package:gamepads/gamepads.dart';

import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';

/// Seating logic for the Change Order ceremony: holding **L + R** on a pad
/// takes the next open player slot, so chord order *is* player order — no
/// slot picking, no arming. Pure events-in / seats-out, testable without
/// widgets; the screen owns id → device-name resolution.
final class ClaimCeremony {
  ClaimCeremony({this.maxSlots = ControllerAssignments.maxSlots});

  final int maxSlots;
  final Map<String, Set<GamepadButton>> _held = <String, Set<GamepadButton>>{};
  final List<String> _seated = <String>[];

  /// Seated gamepad ids in chord order — element `k` is player `k+1`.
  List<String> get seatedIds => List<String>.unmodifiable(_seated);

  /// Replaces the ephemeral provider id for an already-seated physical
  /// controller without changing its player slot. The screen calls this only
  /// after exact identity proves [newId] is the same controller as [previousId].
  void replaceReconnectedId({
    required String previousId,
    required String newId,
  }) {
    final playerSlot = _seated.indexOf(previousId);
    if (playerSlot < 0 || previousId == newId) {
      return;
    }
    _seated.remove(newId);
    _seated[playerSlot] = newId;
    _held.remove(previousId);
  }

  /// Feeds one normalized event; returns true when the seating changed.
  ///
  /// A completed chord always resets that pad's held set (fresh chord per
  /// seat); re-chording an already-seated pad keeps its position, and chords
  /// beyond [maxSlots] are ignored.
  bool onEvent(NormalizedGamepadEvent event) {
    final button = event.button;
    if (button != GamepadButton.leftBumper &&
        button != GamepadButton.rightBumper) {
      return false;
    }
    final held = _held.putIfAbsent(event.gamepadId, () => <GamepadButton>{});
    if (event.value < 0.5) {
      held.remove(button);
      return false;
    }
    held.add(button!);
    if (held.length < 2) {
      return false;
    }
    held.clear();
    if (_seated.contains(event.gamepadId) || _seated.length >= maxSlots) {
      return false;
    }
    _seated.add(event.gamepadId);
    return true;
  }
}
