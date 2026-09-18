import 'package:flutter_test/flutter_test.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/input/session_join_tracker.dart';

NormalizedGamepadEvent event(String id, GamepadButton button, double value) =>
    NormalizedGamepadEvent(
      gamepadId: id,
      timestamp: 0,
      value: value,
      button: button,
      rawEvent: GamepadEvent(
        gamepadId: id,
        timestamp: 0,
        type: KeyType.button,
        key: 'raw',
        value: value,
      ),
    );

void main() {
  test('L plus R completes once per controller and ignores other buttons', () {
    final tracker = SessionJoinTracker();

    expect(tracker.onEvent(event('a', GamepadButton.a, 1)), isNull);
    expect(tracker.onEvent(event('a', GamepadButton.leftBumper, 1)), isNull);
    expect(tracker.onEvent(event('b', GamepadButton.rightBumper, 1)), isNull);
    expect(tracker.onEvent(event('a', GamepadButton.rightBumper, 1)), 'a');
  });

  test('recognized chord stays latched until both bumpers release', () {
    final tracker = SessionJoinTracker();

    expect(tracker.onEvent(event('a', GamepadButton.leftBumper, 1)), isNull);
    expect(tracker.onEvent(event('a', GamepadButton.rightBumper, 1)), 'a');
    expect(tracker.onEvent(event('a', GamepadButton.rightBumper, 1)), isNull);
    expect(tracker.onEvent(event('a', GamepadButton.leftBumper, 0)), isNull);
    expect(tracker.onEvent(event('a', GamepadButton.leftBumper, 1)), isNull);
    expect(tracker.onEvent(event('a', GamepadButton.rightBumper, 0)), isNull);
    expect(tracker.onEvent(event('a', GamepadButton.leftBumper, 0)), isNull);

    expect(tracker.onEvent(event('a', GamepadButton.rightBumper, 1)), isNull);
    expect(tracker.onEvent(event('a', GamepadButton.leftBumper, 1)), 'a');
  });
}
