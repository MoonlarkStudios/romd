import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';

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
  test(
    'callback provider exposes the supplied list and event stream',
    () async {
      final events = StreamController<NormalizedGamepadEvent>.broadcast(
        sync: true,
      );
      addTearDown(events.close);
      final provider = CallbackControllerInputProvider(
        listGamepads: () async => <ConnectedGamepad>[
          ConnectedGamepad.fallback(
            id: 'pad-1',
            name: '8BitDo Pro 2',
            order: 0,
          ),
        ],
        events: () => events.stream,
      );

      expect(await provider.listGamepads(), <ConnectedGamepad>[
        ConnectedGamepad.fallback(id: 'pad-1', name: '8BitDo Pro 2', order: 0),
      ]);

      final event = _buttonEvent('pad-1', GamepadButton.leftBumper, 1.0);
      final expectation = expectLater(provider.events(), emits(event));
      events.add(event);

      await expectation;
    },
  );
}
