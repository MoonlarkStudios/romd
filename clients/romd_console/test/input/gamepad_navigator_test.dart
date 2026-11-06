import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/input/console_intents.dart';
import 'package:romd_console/src/input/gamepad_navigator.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';

final class _CaptureGateProvider
    implements ControllerInputProvider, ControllerInputCaptureGate {
  final eventsController = StreamController<NormalizedGamepadEvent>.broadcast(
    sync: true,
  );
  final listeners = <VoidCallback>{};
  var suppressed = false;

  @override
  Future<List<ConnectedGamepad>> listGamepads() async =>
      const <ConnectedGamepad>[];

  @override
  Stream<NormalizedGamepadEvent> events() => eventsController.stream;

  @override
  bool get isNavigationSuppressed => suppressed;

  @override
  void addCaptureStateListener(VoidCallback listener) =>
      listeners.add(listener);

  @override
  void removeCaptureStateListener(VoidCallback listener) =>
      listeners.remove(listener);

  void setSuppressed(bool value) {
    suppressed = value;
    for (final listener in List<VoidCallback>.of(listeners)) {
      listener();
    }
  }
}

NormalizedGamepadEvent _buttonEvent(
  GamepadButton button,
  double value, {
  String gamepadId = 'pad-1',
}) => NormalizedGamepadEvent(
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
  testWidgets('exclusive capture suppresses intents and clears repeat', (
    tester,
  ) async {
    final provider = _CaptureGateProvider();
    addTearDown(provider.eventsController.close);
    var directions = 0;
    var activations = 0;
    final navigator = GamepadNavigator(
      onInputModeChanged: (_) {},
      controllerInputProvider: provider,
    );
    addTearDown(navigator.dispose);
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Actions(
          actions: <Type, Action<Intent>>{
            DirectionalFocusIntent: CallbackAction<DirectionalFocusIntent>(
              onInvoke: (_) => directions++,
            ),
            ActivateIntent: CallbackAction<ActivateIntent>(
              onInvoke: (_) => activations++,
            ),
          },
          child: const Focus(autofocus: true, child: SizedBox.shrink()),
        ),
      ),
    );
    await tester.pump();

    provider.eventsController.add(_buttonEvent(GamepadButton.dpadDown, 1));
    await tester.pump(const Duration(milliseconds: 500));
    expect(directions, greaterThan(1));

    provider.setSuppressed(true);
    final beforeCapture = directions;
    provider.eventsController.add(_buttonEvent(GamepadButton.a, 1));
    await tester.pump(const Duration(milliseconds: 500));
    expect(directions, beforeCapture);
    expect(activations, 0);

    provider.setSuppressed(false);
    provider.eventsController.add(_buttonEvent(GamepadButton.a, 1));
    await tester.pump();
    expect(activations, 1);
  });

  test('controller gate admits only joined pads in the active shell', () {
    final gate = ControllerNavigationGate()
      ..configure(
        enabled: true,
        requireJoined: true,
        ownershipAllowed: true,
        joiningControllerId: null,
        joinedControllerIds: const <String>{'joined'},
      );

    expect(
      gate.allows(
        _buttonEvent(GamepadButton.start, 1, gamepadId: 'available'),
        GamepadNavigationAction.players,
      ),
      isFalse,
    );
    expect(
      gate.allows(
        _buttonEvent(GamepadButton.start, 1, gamepadId: 'joined'),
        GamepadNavigationAction.players,
      ),
      isTrue,
    );
  });

  test('chooser gate limits navigation to joining pad D-pad A and B', () {
    final gate = ControllerNavigationGate()
      ..configure(
        enabled: true,
        requireJoined: true,
        ownershipAllowed: true,
        joiningControllerId: 'joining',
        joinedControllerIds: const <String>{},
      );
    final event = _buttonEvent(GamepadButton.a, 1, gamepadId: 'joining');

    expect(gate.allows(event, GamepadNavigationAction.direction), isTrue);
    expect(gate.allows(event, GamepadNavigationAction.activate), isTrue);
    expect(gate.allows(event, GamepadNavigationAction.dismiss), isTrue);
    expect(
      gate.allows(event, GamepadNavigationAction.analogDirection),
      isFalse,
    );
    expect(gate.allows(event, GamepadNavigationAction.players), isFalse);
    expect(
      gate.allows(
        _buttonEvent(GamepadButton.a, 1, gamepadId: 'other'),
        GamepadNavigationAction.activate,
      ),
      isFalse,
    );
  });

  testWidgets(
    'direction repeat stops synchronously when chooser or launch ownership changes',
    (tester) async {
      final events = StreamController<NormalizedGamepadEvent>.broadcast(
        sync: true,
      );
      addTearDown(events.close);
      final gate = ControllerNavigationGate()
        ..configure(
          enabled: true,
          requireJoined: true,
          ownershipAllowed: true,
          joiningControllerId: null,
          joinedControllerIds: const <String>{'pad-a'},
        );
      var directions = 0;
      final navigator = GamepadNavigator(
        onInputModeChanged: (_) {},
        controllerNavigationGate: gate,
        controllerInputProvider: CallbackControllerInputProvider(
          listGamepads: () async => const <ConnectedGamepad>[],
          events: () => events.stream,
        ),
      );
      addTearDown(navigator.dispose);
      await tester.pumpWidget(
        MaterialApp(
          theme: RomdSkins.baselineDark(),
          home: Actions(
            actions: <Type, Action<Intent>>{
              DirectionalFocusIntent: CallbackAction<DirectionalFocusIntent>(
                onInvoke: (_) => directions++,
              ),
            },
            child: const Focus(autofocus: true, child: SizedBox.shrink()),
          ),
        ),
      );
      await tester.pump();

      events.add(_buttonEvent(GamepadButton.dpadDown, 1, gamepadId: 'pad-a'));
      await tester.pump(const Duration(milliseconds: 500));
      expect(directions, greaterThan(1));

      gate.configure(
        enabled: true,
        requireJoined: true,
        ownershipAllowed: true,
        joiningControllerId: 'pad-b',
        joinedControllerIds: const <String>{'pad-a'},
      );
      final afterChooserSwitch = directions;
      await tester.pump(const Duration(milliseconds: 500));
      expect(directions, afterChooserSwitch);

      gate.configure(
        enabled: true,
        requireJoined: true,
        ownershipAllowed: true,
        joiningControllerId: null,
        joinedControllerIds: const <String>{'pad-a'},
      );
      events.add(_buttonEvent(GamepadButton.dpadDown, 1, gamepadId: 'pad-a'));
      await tester.pump(const Duration(milliseconds: 500));
      expect(directions, greaterThan(afterChooserSwitch));
      gate.configure(
        enabled: true,
        requireJoined: true,
        ownershipAllowed: false,
        joiningControllerId: null,
        joinedControllerIds: const <String>{'pad-a'},
      );
      final afterLaunch = directions;
      await tester.pump(const Duration(milliseconds: 500));
      expect(directions, afterLaunch);
    },
  );

  testWidgets('Start dispatches Players while Y remains Details', (
    tester,
  ) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    var players = 0;
    var details = 0;
    var activations = 0;
    final navigator = GamepadNavigator(
      onInputModeChanged: (_) {},
      controllerInputProvider: CallbackControllerInputProvider(
        listGamepads: () async => const <ConnectedGamepad>[],
        events: () => events.stream,
      ),
    );
    addTearDown(navigator.dispose);

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Actions(
          actions: <Type, Action<Intent>>{
            ShowPlayersIntent: CallbackAction<ShowPlayersIntent>(
              onInvoke: (_) => players++,
            ),
            ShowDetailsIntent: CallbackAction<ShowDetailsIntent>(
              onInvoke: (_) => details++,
            ),
            ActivateIntent: CallbackAction<ActivateIntent>(
              onInvoke: (_) => activations++,
            ),
          },
          child: const Scaffold(
            body: Focus(autofocus: true, child: Text('Focused game')),
          ),
        ),
      ),
    );
    await tester.pump();

    events.add(_buttonEvent(GamepadButton.start, 1));
    await tester.pump();
    expect(players, 1);
    expect(details, 0);
    expect(activations, 0);

    events.add(_buttonEvent(GamepadButton.y, 1));
    await tester.pump();
    expect(players, 1);
    expect(details, 1);
    expect(activations, 0);
  });

  testWidgets('a lone bumper dispatches its section intent on release', (
    tester,
  ) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    var previous = 0;
    var next = 0;
    final navigator = GamepadNavigator(
      onInputModeChanged: (_) {},
      controllerInputProvider: CallbackControllerInputProvider(
        listGamepads: () async => const <ConnectedGamepad>[],
        events: () => events.stream,
      ),
    );
    addTearDown(navigator.dispose);
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Actions(
          actions: <Type, Action<Intent>>{
            PreviousCatalogSectionIntent:
                CallbackAction<PreviousCatalogSectionIntent>(
                  onInvoke: (_) => previous++,
                ),
            NextCatalogSectionIntent: CallbackAction<NextCatalogSectionIntent>(
              onInvoke: (_) => next++,
            ),
          },
          child: const Focus(autofocus: true, child: SizedBox.shrink()),
        ),
      ),
    );
    await tester.pump();

    events.add(_buttonEvent(GamepadButton.leftBumper, 1));
    await tester.pump();
    expect(previous, 0);
    events.add(_buttonEvent(GamepadButton.leftBumper, 0));
    await tester.pump();
    expect(previous, 1);

    events.add(_buttonEvent(GamepadButton.rightBumper, 1));
    await tester.pump();
    expect(next, 0);
    events.add(_buttonEvent(GamepadButton.rightBumper, 0));
    await tester.pump();
    expect(next, 1);
  });

  testWidgets(
    'an arbitrarily slow overlapping bumper chord suppresses both actions',
    (tester) async {
      final events = StreamController<NormalizedGamepadEvent>.broadcast(
        sync: true,
      );
      addTearDown(events.close);
      var sections = 0;
      final navigator = GamepadNavigator(
        onInputModeChanged: (_) {},
        controllerInputProvider: CallbackControllerInputProvider(
          listGamepads: () async => const <ConnectedGamepad>[],
          events: () => events.stream,
        ),
      );
      addTearDown(navigator.dispose);
      await tester.pumpWidget(
        MaterialApp(
          theme: RomdSkins.baselineDark(),
          home: Actions(
            actions: <Type, Action<Intent>>{
              PreviousCatalogSectionIntent:
                  CallbackAction<PreviousCatalogSectionIntent>(
                    onInvoke: (_) => sections++,
                  ),
              NextCatalogSectionIntent:
                  CallbackAction<NextCatalogSectionIntent>(
                    onInvoke: (_) => sections++,
                  ),
            },
            child: const Focus(autofocus: true, child: SizedBox.shrink()),
          ),
        ),
      );
      await tester.pump();

      events.add(_buttonEvent(GamepadButton.leftBumper, 1));
      await tester.pump(const Duration(seconds: 5));
      events.add(_buttonEvent(GamepadButton.rightBumper, 1));
      await tester.pump();
      events.add(_buttonEvent(GamepadButton.leftBumper, 0));
      events.add(_buttonEvent(GamepadButton.rightBumper, 0));
      await tester.pump();
      expect(sections, 0);
    },
  );

  testWidgets('duplicate shoulder events never leak an action', (tester) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    var next = 0;
    final navigator = GamepadNavigator(
      onInputModeChanged: (_) {},
      controllerInputProvider: CallbackControllerInputProvider(
        listGamepads: () async => const <ConnectedGamepad>[],
        events: () => events.stream,
      ),
    );
    addTearDown(navigator.dispose);
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Actions(
          actions: <Type, Action<Intent>>{
            NextCatalogSectionIntent: CallbackAction<NextCatalogSectionIntent>(
              onInvoke: (_) => next++,
            ),
          },
          child: const Focus(autofocus: true, child: SizedBox.shrink()),
        ),
      ),
    );
    await tester.pump();

    events.add(_buttonEvent(GamepadButton.rightBumper, 1));
    events.add(_buttonEvent(GamepadButton.rightBumper, 1));
    events.add(_buttonEvent(GamepadButton.rightBumper, 0));
    events.add(_buttonEvent(GamepadButton.rightBumper, 0));
    await tester.pump();
    expect(next, 1);
  });

  testWidgets('shoulder dispatch rechecks capture and ownership gates', (
    tester,
  ) async {
    final provider = _CaptureGateProvider();
    addTearDown(provider.eventsController.close);
    final gate = ControllerNavigationGate()
      ..configure(
        enabled: true,
        requireJoined: true,
        ownershipAllowed: true,
        joiningControllerId: null,
        joinedControllerIds: const <String>{'pad-1'},
      );
    var previous = 0;
    final navigator = GamepadNavigator(
      onInputModeChanged: (_) {},
      controllerInputProvider: provider,
      controllerNavigationGate: gate,
    );
    addTearDown(navigator.dispose);
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Actions(
          actions: <Type, Action<Intent>>{
            PreviousCatalogSectionIntent:
                CallbackAction<PreviousCatalogSectionIntent>(
                  onInvoke: (_) => previous++,
                ),
          },
          child: const Focus(autofocus: true, child: SizedBox.shrink()),
        ),
      ),
    );
    await tester.pump();

    provider.eventsController.add(_buttonEvent(GamepadButton.leftBumper, 1));
    gate.configure(
      enabled: true,
      requireJoined: true,
      ownershipAllowed: false,
      joiningControllerId: null,
      joinedControllerIds: const <String>{'pad-1'},
    );
    provider.eventsController.add(_buttonEvent(GamepadButton.leftBumper, 0));
    await tester.pump();
    expect(previous, 0);

    gate.configure(
      enabled: true,
      requireJoined: true,
      ownershipAllowed: true,
      joiningControllerId: 'pad-1',
      joinedControllerIds: const <String>{'pad-1'},
    );
    provider.eventsController.add(_buttonEvent(GamepadButton.leftBumper, 1));
    provider.eventsController.add(_buttonEvent(GamepadButton.leftBumper, 0));
    await tester.pump();
    expect(previous, 0);

    gate.configure(
      enabled: true,
      requireJoined: true,
      ownershipAllowed: true,
      joiningControllerId: null,
      joinedControllerIds: const <String>{'pad-1'},
    );
    provider.eventsController.add(_buttonEvent(GamepadButton.leftBumper, 1));
    provider.setSuppressed(true);
    provider.eventsController.add(_buttonEvent(GamepadButton.leftBumper, 0));
    await tester.pump();
    expect(previous, 0);
  });
}
