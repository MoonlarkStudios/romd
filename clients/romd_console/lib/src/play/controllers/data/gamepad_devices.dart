import 'dart:async';

import 'package:gamepads/gamepads.dart';

import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';

typedef ControllerEventStream = Stream<NormalizedGamepadEvent> Function();

/// Controller input boundary: listed ids and event ids must share the same
/// provider identity space so session claims can resolve without guessing.
abstract interface class ControllerInputProvider {
  Future<List<ConnectedGamepad>> listGamepads();
  Stream<NormalizedGamepadEvent> events();
}

final class CallbackControllerInputProvider implements ControllerInputProvider {
  const CallbackControllerInputProvider({
    required GamepadLister listGamepads,
    required ControllerEventStream events,
  }) : _listGamepads = listGamepads,
       _events = events;

  final GamepadLister _listGamepads;
  final ControllerEventStream _events;

  @override
  Future<List<ConnectedGamepad>> listGamepads() => _listGamepads();

  @override
  Stream<NormalizedGamepadEvent> events() => _events();
}

final class GamepadsControllerInputProvider implements ControllerInputProvider {
  const GamepadsControllerInputProvider();

  @override
  Future<List<ConnectedGamepad>> listGamepads() => listConnectedGamepads();

  @override
  Stream<NormalizedGamepadEvent> events() => Gamepads.normalizedEvents;
}

/// Lists connected pads via the gamepads plugin, mapped to the domain shape.
/// This is the fallback lister and the canonical event-id source for the
/// controller ceremony.
Future<List<ConnectedGamepad>> listConnectedGamepads() async {
  final controllers = await Gamepads.list();
  return <ConnectedGamepad>[
    for (final (order, controller) in controllers.indexed)
      ConnectedGamepad.fallback(
        id: controller.id,
        name: controller.name,
        order: order,
      ),
  ];
}
