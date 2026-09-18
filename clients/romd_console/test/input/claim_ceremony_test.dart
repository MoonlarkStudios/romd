import 'package:flutter_test/flutter_test.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/input/claim_ceremony.dart';

NormalizedGamepadEvent _buttonEvent(
  String gamepadId,
  GamepadButton button,
  double value,
) => NormalizedGamepadEvent(
  gamepadId: gamepadId,
  timestamp: 0,
  value: value,
  button: button,
  rawEvent: GamepadEvent(
    gamepadId: gamepadId,
    timestamp: 0,
    type: KeyType.button,
    key: 'raw',
    value: value,
  ),
);

void main() {
  bool chord(ClaimCeremony ceremony, String id) {
    ceremony.onEvent(_buttonEvent(id, GamepadButton.leftBumper, 1.0));
    return ceremony.onEvent(_buttonEvent(id, GamepadButton.rightBumper, 1.0));
  }

  test('a completed chord seats the pad', () {
    final ceremony = ClaimCeremony();

    expect(chord(ceremony, 'pad-a'), isTrue);
    expect(ceremony.seatedIds, <String>['pad-a']);
  });

  test('chord order defines player order', () {
    final ceremony = ClaimCeremony();

    chord(ceremony, 'pad-b');
    chord(ceremony, 'pad-a');

    expect(ceremony.seatedIds, <String>['pad-b', 'pad-a']);
  });

  test('a half-held chord released before completion does not seat', () {
    final ceremony = ClaimCeremony();

    ceremony.onEvent(_buttonEvent('pad-a', GamepadButton.leftBumper, 1.0));
    ceremony.onEvent(_buttonEvent('pad-a', GamepadButton.leftBumper, 0.0));
    final seated = ceremony.onEvent(
      _buttonEvent('pad-a', GamepadButton.rightBumper, 1.0),
    );

    expect(seated, isFalse);
    expect(ceremony.seatedIds, isEmpty);
  });

  test('non-bumper buttons never seat', () {
    final ceremony = ClaimCeremony();

    ceremony.onEvent(_buttonEvent('pad-a', GamepadButton.a, 1.0));
    ceremony.onEvent(_buttonEvent('pad-a', GamepadButton.b, 1.0));

    expect(ceremony.seatedIds, isEmpty);
  });

  test('re-chording a seated pad keeps its position', () {
    final ceremony = ClaimCeremony();

    chord(ceremony, 'pad-a');
    chord(ceremony, 'pad-b');
    expect(chord(ceremony, 'pad-a'), isFalse);

    expect(ceremony.seatedIds, <String>['pad-a', 'pad-b']);
  });

  test(
    'a reconnected provider id replaces the old id without taking a slot',
    () {
      final ceremony = ClaimCeremony();

      chord(ceremony, 'old-id');
      chord(ceremony, 'pad-b');
      chord(ceremony, 'new-id');
      ceremony.replaceReconnectedId(previousId: 'old-id', newId: 'new-id');

      expect(ceremony.seatedIds, <String>['new-id', 'pad-b']);
    },
  );

  test('seating stops at maxSlots', () {
    final ceremony = ClaimCeremony(maxSlots: 2);

    chord(ceremony, 'pad-a');
    chord(ceremony, 'pad-b');
    expect(chord(ceremony, 'pad-c'), isFalse);

    expect(ceremony.seatedIds, <String>['pad-a', 'pad-b']);
  });

  test('bumper state is tracked per pad', () {
    final ceremony = ClaimCeremony();

    ceremony.onEvent(_buttonEvent('pad-a', GamepadButton.leftBumper, 1.0));
    final seated = ceremony.onEvent(
      _buttonEvent('pad-b', GamepadButton.rightBumper, 1.0),
    );

    expect(seated, isFalse);
    expect(ceremony.seatedIds, isEmpty);
  });

  test('a chord requires a fresh press after seating', () {
    final ceremony = ClaimCeremony(maxSlots: 1);

    chord(ceremony, 'pad-a');
    // The completed chord cleared held state; a lone second press on either
    // bumper must not count as a new chord.
    final seated = ceremony.onEvent(
      _buttonEvent('pad-a', GamepadButton.rightBumper, 1.0),
    );

    expect(seated, isFalse);
    expect(ceremony.seatedIds, <String>['pad-a']);
  });
}
