import 'package:gamepads/gamepads.dart';

/// Pure L+R chord recognizer for session joining. It owns no UI or mutations;
/// callers serialize accepted ids and resolve them against the live inventory.
final class SessionJoinTracker {
  final Map<String, _JoinShoulderState> _states =
      <String, _JoinShoulderState>{};

  String? onEvent(NormalizedGamepadEvent event) {
    final button = event.button;
    if (button != GamepadButton.leftBumper &&
        button != GamepadButton.rightBumper) {
      return null;
    }
    final state = _states.putIfAbsent(event.gamepadId, _JoinShoulderState.new);
    final isLeft = button == GamepadButton.leftBumper;
    if (event.value < 0.5) {
      if (isLeft) {
        state.leftDown = false;
      } else {
        state.rightDown = false;
      }
      if (!state.leftDown && !state.rightDown) {
        _states.remove(event.gamepadId);
      }
      return null;
    }
    if (isLeft) {
      if (state.leftDown) return null;
      state.leftDown = true;
    } else {
      if (state.rightDown) return null;
      state.rightDown = true;
    }
    if (!state.leftDown || !state.rightDown || state.latched) {
      return null;
    }
    state.latched = true;
    return event.gamepadId;
  }

  void forget(String gamepadId) => _states.remove(gamepadId);

  void clear() => _states.clear();
}

final class _JoinShoulderState {
  bool leftDown = false;
  bool rightDown = false;
  bool latched = false;
}
