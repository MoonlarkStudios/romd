import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_controller_input_provider.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_gamepad_enumerator.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/change_order_screen.dart';

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

final List<ConnectedGamepad> _twoPads = <ConnectedGamepad>[
  ConnectedGamepad.fallback(
    id: '0',
    name: 'Xbox Wireless Controller',
    order: 0,
  ),
  ConnectedGamepad.fallback(id: '1', name: 'DualSense', order: 1),
];

const ConnectedGamepad _dualSenseExact = ConnectedGamepad(
  id: '7',
  order: 0,
  identity: ControllerIdentity(
    displayName: 'DualSense',
    sdlGuid: '030000004c050000e60c000000006800',
    serial: 'serial-a',
  ),
);

final class _FakeSdlHandle extends SdlGamepadHandle {
  const _FakeSdlHandle(this.instanceId);

  final int instanceId;
}

final class _FakeSdlGamepadApi implements SdlGamepadApi {
  _FakeSdlGamepadApi(this._events);

  final List<SdlPolledEvent> _events;
  var _eventIndex = 0;

  @override
  bool initGamepad() => true;

  @override
  void quitGamepad() {}

  @override
  void setGamepadEventsEnabled(bool enabled) {}

  @override
  List<int> gamepadInstanceIds() => const <int>[7];

  @override
  String? gamepadNameForId(int instanceId) => 'DualSense';

  @override
  String? gamepadGuidForId(int instanceId) =>
      '030000004c050000e60c000000006800';

  @override
  SdlGamepadHandle? openGamepad(int instanceId) => _FakeSdlHandle(instanceId);

  @override
  String? gamepadSerial(SdlGamepadHandle handle) => 'serial-a';

  @override
  void closeGamepad(SdlGamepadHandle handle) {}

  @override
  SdlPolledEvent? pollEvent() {
    if (_eventIndex >= _events.length) {
      return null;
    }
    return _events[_eventIndex++];
  }
}

void main() {
  testWidgets('route establishes its background before ceremony content', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () => ChangeOrderScreen.show(
              context,
              slotClaims: SessionControllerSlotClaims(),
              controllerInputProvider: CallbackControllerInputProvider(
                listGamepads: () async => _twoPads,
                events: () => const Stream<NormalizedGamepadEvent>.empty(),
              ),
            ),
            child: const Text('open'),
          ),
        ),
      ),
    );

    await tester.tap(find.text('open'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 50));

    final background = tester.widget<FadeTransition>(
      find.byKey(const ValueKey<String>('change-order-transition-background')),
    );
    final content = tester.widget<FadeTransition>(
      find.byKey(const ValueKey<String>('change-order-transition-content')),
    );
    expect(background.opacity.value, greaterThan(content.opacity.value));

    await tester.pumpAndSettle();
    expect(background.opacity.value, 1);
    expect(content.opacity.value, 1);
    expect(find.text('CHANGE PLAYER ORDER'), findsOneWidget);
  });

  Future<void> pumpCeremony(
    WidgetTester tester, {
    required SessionControllerSlotClaims slotClaims,
    required StreamController<NormalizedGamepadEvent> events,
    List<ConnectedGamepad> Function()? pads,
    Map<String, String> profileNamesById = const <String, String>{},
  }) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: ChangeOrderScreen(
          slotClaims: slotClaims,
          profileNamesById: profileNamesById,
          controllerInputProvider: CallbackControllerInputProvider(
            listGamepads: () async => pads?.call() ?? _twoPads,
            events: () => events.stream,
          ),
        ),
      ),
    );
    await tester.pump();
    await tester.pump();
  }

  Future<void> pressChord(
    WidgetTester tester,
    StreamController<NormalizedGamepadEvent> events,
    String gamepadId,
  ) async {
    events.add(_buttonEvent(gamepadId, GamepadButton.leftBumper, 1.0));
    events.add(_buttonEvent(gamepadId, GamepadButton.rightBumper, 1.0));
    await tester.pump();
    await tester.pump();
  }

  testWidgets('starts unseated with numbered tiles', (tester) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    await pumpCeremony(
      tester,
      slotClaims: SessionControllerSlotClaims(),
      events: events,
    );

    expect(find.text('CHANGE PLAYER ORDER'), findsOneWidget);
    expect(
      find.text('Choose Player 1, or select Ready to clear the joined roster.'),
      findsOneWidget,
    );
    expect(find.text('AVAILABLE CONTROLLERS'), findsOneWidget);
    expect(find.text('Xbox Wireless Controller  •  DualSense'), findsOneWidget);
    for (var slot = 1; slot <= ControllerAssignments.maxSlots; slot++) {
      expect(find.text('$slot'), findsOneWidget);
    }
    expect(find.text('P1'), findsNothing);
  });

  testWidgets(
    'mounted ceremony re-resolves route content keycaps tiles and motion',
    (tester) async {
      final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
      final events = StreamController<NormalizedGamepadEvent>.broadcast(
        sync: true,
      );
      addTearDown(mode.dispose);
      addTearDown(events.close);

      await tester.pumpWidget(
        ValueListenableBuilder<ThemeMode>(
          valueListenable: mode,
          builder: (context, themeMode, child) => MaterialApp(
            theme: RomdSkins.seamTestTheme(),
            darkTheme: RomdSkins.baselineDark(),
            themeMode: themeMode,
            themeAnimationDuration: Duration.zero,
            home: child,
          ),
          child: ChangeOrderScreen(
            slotClaims: SessionControllerSlotClaims(),
            controllerInputProvider: CallbackControllerInputProvider(
              listGamepads: () async => _twoPads,
              events: () => events.stream,
            ),
          ),
        ),
      );
      await tester.pump();
      await tester.pump();

      final screen = find.byType(ChangeOrderScreen);
      final tile = find.byType(AnimatedContainer).first;
      final keycap = find.ancestor(
        of: find.text('L'),
        matching: find.byType(DecoratedBox),
      );
      final elementBefore = tester.element(screen);
      final headerBefore = tester
          .widget<Text>(find.text('CHANGE PLAYER ORDER'))
          .style!;
      final tileBefore = tester.widget<AnimatedContainer>(tile);
      final tileDecorationBefore = tileBefore.decoration! as BoxDecoration;
      final keycapBefore =
          tester.widget<DecoratedBox>(keycap).decoration as BoxDecoration;

      mode.value = ThemeMode.light;
      await tester.pump();

      final headerAfter = tester
          .widget<Text>(find.text('CHANGE PLAYER ORDER'))
          .style!;
      final tileAfter = tester.widget<AnimatedContainer>(tile);
      final tileDecorationAfter = tileAfter.decoration! as BoxDecoration;
      final keycapAfter =
          tester.widget<DecoratedBox>(keycap).decoration as BoxDecoration;

      expect(identical(elementBefore, tester.element(screen)), isTrue);
      expect(headerAfter.fontFamily, isNot(headerBefore.fontFamily));
      expect(tileAfter.duration, isNot(tileBefore.duration));
      expect(
        tileDecorationAfter.borderRadius,
        isNot(tileDecorationBefore.borderRadius),
      );
      expect(tileDecorationAfter.color, isNot(tileDecorationBefore.color));
      expect(keycapAfter.color, isNot(keycapBefore.color));
      expect(keycapAfter.border, isNot(keycapBefore.border));
    },
  );

  testWidgets('a chord seats the pad in the next tile', (tester) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    await pumpCeremony(
      tester,
      slotClaims: SessionControllerSlotClaims(),
      events: events,
    );

    await pressChord(tester, events, '1');

    expect(find.text('P1'), findsOneWidget);
    expect(find.text('DualSense'), findsOneWidget);
    expect(find.text('DualSense is Player 1. Next: Player 2.'), findsOneWidget);
    expect(find.text('Xbox Wireless Controller'), findsOneWidget);
    expect(find.text('Xbox Wireless Controller  •  DualSense'), findsNothing);
  });

  testWidgets('ceremony shows the profile attached to a guest controller', (
    tester,
  ) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final claims =
        SessionControllerSlotClaims.fromClaims(const <ControllerSlotClaim?>[
          null,
          ControllerSlotClaim(
            displayName: 'Xbox Wireless Controller',
            providerId: 'stale-provider-id',
            localProfileId: 'guest-profile',
          ),
        ]);
    await pumpCeremony(
      tester,
      slotClaims: claims,
      events: events,
      profileNamesById: const <String, String>{'guest-profile': 'Sam'},
    );

    await pressChord(tester, events, '1');
    await pressChord(tester, events, '0');

    expect(find.text('P2'), findsOneWidget);
    expect(find.text('Sam'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(claims.claimDetails[1]!.providerId, '0');
    expect(claims.claimDetails[1]!.localProfileId, 'guest-profile');
  });

  testWidgets('a seated pad explains that it already has a player spot', (
    tester,
  ) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    await pumpCeremony(
      tester,
      slotClaims: SessionControllerSlotClaims(),
      events: events,
    );

    await pressChord(tester, events, '1');
    await pressChord(tester, events, '1');

    expect(find.text('DualSense is already Player 1.'), findsOneWidget);
    expect(find.text('P2'), findsNothing);
  });

  testWidgets('roster reports when every connected controller is seated', (
    tester,
  ) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    await pumpCeremony(
      tester,
      slotClaims: SessionControllerSlotClaims(),
      events: events,
    );

    await pressChord(tester, events, '1');
    await pressChord(tester, events, '0');

    expect(
      find.text('Xbox Wireless Controller is Player 2. Next: Player 3.'),
      findsOneWidget,
    );
    expect(find.text('All connected controllers are seated.'), findsOneWidget);
  });

  testWidgets('confirm records chord order as the whole assignment', (
    tester,
  ) async {
    final slotClaims = SessionControllerSlotClaims(<String?>[
      null,
      null,
      'Stale Reservation',
      null,
    ]);
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    await pumpCeremony(tester, slotClaims: slotClaims, events: events);

    await pressChord(tester, events, '1');
    await pressChord(tester, events, '0');
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(slotClaims.claims, <String?>[
      'DualSense',
      'Xbox Wireless Controller',
      null,
      null,
    ]);
  });

  testWidgets('confirm replaces a first-use startup claim', (tester) async {
    final slotClaims = SessionControllerSlotClaims.fromClaims(
      const <ControllerSlotClaim?>[
        ControllerSlotClaim(displayName: 'Starter Pad', providerId: 'starter'),
      ],
    );
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    await pumpCeremony(tester, slotClaims: slotClaims, events: events);

    await pressChord(tester, events, '1');
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(slotClaims.claims, <String?>['DualSense', null, null, null]);
  });

  testWidgets('confirm stores exact identity for identity-aware pads', (
    tester,
  ) async {
    const dualSense = ConnectedGamepad(
      id: '7',
      order: 0,
      identity: ControllerIdentity(
        displayName: 'DualSense',
        sdlGuid: '030000004c050000e60c000000006800',
        serial: 'serial-a',
      ),
    );
    final slotClaims = SessionControllerSlotClaims();
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    await pumpCeremony(
      tester,
      slotClaims: slotClaims,
      events: events,
      pads: () => const <ConnectedGamepad>[dualSense],
    );

    await pressChord(tester, events, '7');
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(await slotClaims.loadClaims(), <ControllerSlotClaim?>[
      ControllerSlotClaim.fromGamepad(dualSense),
      null,
      null,
      null,
    ]);
    expect(slotClaims.claims, <String?>['DualSense', null, null, null]);
  });

  testWidgets('SDL provider ids seat exact identity in the ceremony', (
    tester,
  ) async {
    final slotClaims = SessionControllerSlotClaims();
    final api = _FakeSdlGamepadApi(const <SdlPolledEvent>[
      SdlGamepadButtonInputEvent(
        instanceId: 7,
        button: SdlGamepadButtonIds.leftShoulder,
        pressed: true,
        timestampMillis: 1,
      ),
      SdlGamepadButtonInputEvent(
        instanceId: 7,
        button: SdlGamepadButtonIds.rightShoulder,
        pressed: true,
        timestampMillis: 2,
      ),
    ]);
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: ChangeOrderScreen(
          slotClaims: slotClaims,
          controllerInputProvider: SdlControllerInputProvider(
            api: api,
            pollInterval: const Duration(milliseconds: 1),
          ),
        ),
      ),
    );

    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 2));
    }

    expect(find.text('P1'), findsOneWidget);
    expect(find.text('DualSense'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(await slotClaims.loadClaims(), const <ControllerSlotClaim?>[
      ControllerSlotClaim(
        displayName: 'DualSense',
        providerId: '7',
        sdlGuid: '030000004c050000e60c000000006800',
        serial: 'serial-a',
      ),
      null,
      null,
      null,
    ]);
  });

  testWidgets('cancel leaves stored exact claims untouched', (tester) async {
    final slotClaims = SessionControllerSlotClaims.fromClaims(
      <ControllerSlotClaim?>[ControllerSlotClaim.fromGamepad(_dualSenseExact)],
    );
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    await pumpCeremony(tester, slotClaims: slotClaims, events: events);

    await pressChord(tester, events, '0');
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pump();

    expect(await slotClaims.loadClaims(), <ControllerSlotClaim?>[
      ControllerSlotClaim.fromGamepad(_dualSenseExact),
      null,
      null,
      null,
    ]);
  });

  testWidgets('DismissIntent cancels without touching stored claims', (
    tester,
  ) async {
    final slotClaims = SessionControllerSlotClaims(<String?>[
      'DualSense',
      null,
      null,
      null,
    ]);
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => ChangeOrderScreen(
                  slotClaims: slotClaims,
                  controllerInputProvider: CallbackControllerInputProvider(
                    listGamepads: () async => _twoPads,
                    events: () => events.stream,
                  ),
                ),
              ),
            ),
            child: const Text('open'),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    expect(find.byType(ChangeOrderScreen), findsOneWidget);

    await pressChord(tester, events, '0');
    final context = tester.binding.focusManager.primaryFocus?.context;
    expect(context, isNotNull);
    Actions.maybeInvoke(context!, const DismissIntent());
    await tester.pumpAndSettle();

    expect(find.byType(ChangeOrderScreen), findsNothing);
    expect(slotClaims.claims, <String?>['DualSense', null, null, null]);
  });

  testWidgets('confirm with nothing seated clears every exact claim', (
    tester,
  ) async {
    final slotClaims =
        SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
          ControllerSlotClaim.fromGamepad(_dualSenseExact),
          ControllerSlotClaim.fromGamepad(_twoPads.first),
        ]);
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    await pumpCeremony(tester, slotClaims: slotClaims, events: events);

    expect(
      find.text('Choose Player 1, or select Ready to clear the joined roster.'),
      findsOneWidget,
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(
      (await slotClaims.loadClaims()).every((claim) => claim == null),
      isTrue,
    );
  });

  testWidgets('confirm pops the ceremony route', (tester) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => ChangeOrderScreen(
                  slotClaims: slotClaims,
                  controllerInputProvider: CallbackControllerInputProvider(
                    listGamepads: () async => _twoPads,
                    events: () => events.stream,
                  ),
                ),
              ),
            ),
            child: const Text('open'),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    expect(find.byType(ChangeOrderScreen), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();

    expect(find.byType(ChangeOrderScreen), findsNothing);
  });

  testWidgets('a chord from a freshly connected pad refreshes and seats', (
    tester,
  ) async {
    var pads = <ConnectedGamepad>[];
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();
    await pumpCeremony(
      tester,
      slotClaims: slotClaims,
      events: events,
      pads: () => pads,
    );

    // The pad connects and chords before the next poll tick.
    pads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(id: '7', name: '8BitDo Pro 2', order: 0),
    ];
    await pressChord(tester, events, '7');
    await tester.pump();

    expect(find.text('8BitDo Pro 2'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(slotClaims.claims, <String?>['8BitDo Pro 2', null, null, null]);
  });

  testWidgets('confirm refreshes a freshly connected exact pad before commit', (
    tester,
  ) async {
    var pads = <ConnectedGamepad>[];
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();
    await pumpCeremony(
      tester,
      slotClaims: slotClaims,
      events: events,
      pads: () => pads,
    );

    pads = const <ConnectedGamepad>[_dualSenseExact];
    events.add(_buttonEvent('7', GamepadButton.leftBumper, 1.0));
    events.add(_buttonEvent('7', GamepadButton.rightBumper, 1.0));
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(await slotClaims.loadClaims(), <ControllerSlotClaim?>[
      ControllerSlotClaim.fromGamepad(_dualSenseExact),
      null,
      null,
      null,
    ]);
  });

  testWidgets('same-model pads seat with distinct ordinals', (tester) async {
    final duplicatePads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(id: '0', name: '8BitDo Pro 2', order: 0),
      ConnectedGamepad.fallback(id: '1', name: '8BitDo Pro 2', order: 1),
    ];
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();
    await pumpCeremony(
      tester,
      slotClaims: slotClaims,
      events: events,
      pads: () => duplicatePads,
    );

    await pressChord(tester, events, '1');
    await pressChord(tester, events, '0');

    expect(find.text('8BitDo Pro 2 #2'), findsOneWidget);
    expect(find.text('8BitDo Pro 2 #1'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(slotClaims.claims, <String?>[
      '8BitDo Pro 2',
      '8BitDo Pro 2',
      null,
      null,
    ]);
  });

  testWidgets('rapid Ready activation commits only once', (tester) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();
    await pumpCeremony(tester, slotClaims: slotClaims, events: events);

    await pressChord(tester, events, '1');
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();

    expect(slotClaims.revision, 1);
    expect(slotClaims.claims.first, 'DualSense');
  });

  testWidgets('disconnecting seated P1 does not promote seated P2', (
    tester,
  ) async {
    var pads = <ConnectedGamepad>[_dualSenseExact, _twoPads.first];
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();
    await pumpCeremony(
      tester,
      slotClaims: slotClaims,
      events: events,
      pads: () => pads,
    );

    await pressChord(tester, events, '7');
    await pressChord(tester, events, '0');
    pads = <ConnectedGamepad>[_twoPads.first];
    await tester.pump(const Duration(seconds: 1));
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();

    final claims = await slotClaims.loadClaims();
    expect(claims[0], ControllerSlotClaim.fromGamepad(_dualSenseExact));
    expect(claims[1], ControllerSlotClaim.fromGamepad(_twoPads.first));
  });

  testWidgets('reconnected exact P1 resolves back to its intended seat', (
    tester,
  ) async {
    var pads = <ConnectedGamepad>[_dualSenseExact, _twoPads.first];
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();
    await pumpCeremony(
      tester,
      slotClaims: slotClaims,
      events: events,
      pads: () => pads,
    );

    await pressChord(tester, events, '7');
    await pressChord(tester, events, '0');
    const reconnected = ConnectedGamepad(
      id: '9',
      order: 1,
      identity: ControllerIdentity(
        displayName: 'DualSense',
        sdlGuid: '030000004c050000e60c000000006800',
        serial: 'serial-a',
      ),
    );
    pads = <ConnectedGamepad>[_twoPads.first, reconnected];
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();

    final resolution = ControllerAssignments.resolveSlotResolution(
      claims: await slotClaims.loadClaims(),
      devices: pads,
    );
    expect(resolution.controllerSlots[0].playerSlot, 0);
    expect(resolution.controllerSlots[0].controller.id, '9');
    expect(resolution.controllerSlots[1].playerSlot, 1);
    expect(resolution.controllerSlots[1].controller.id, '0');
  });

  testWidgets('provider id reuse cannot replace a captured exact seat', (
    tester,
  ) async {
    var pads = <ConnectedGamepad>[_dualSenseExact, _twoPads.first];
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();
    await pumpCeremony(
      tester,
      slotClaims: slotClaims,
      events: events,
      pads: () => pads,
    );

    await pressChord(tester, events, '7');
    const replacement = ConnectedGamepad(
      id: '7',
      order: 0,
      identity: ControllerIdentity(
        displayName: 'Different Controller',
        sdlGuid: 'replacement-guid',
        serial: 'replacement-serial',
      ),
    );
    pads = const <ConnectedGamepad>[replacement];
    await tester.pump(const Duration(seconds: 1));
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();

    expect(
      (await slotClaims.loadClaims()).first,
      ControllerSlotClaim.fromGamepad(_dualSenseExact),
    );
  });

  testWidgets('reconnected exact P1 cannot claim P2 with its new provider id', (
    tester,
  ) async {
    var pads = <ConnectedGamepad>[_dualSenseExact];
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();
    await pumpCeremony(
      tester,
      slotClaims: slotClaims,
      events: events,
      pads: () => pads,
    );

    await pressChord(tester, events, '7');
    expect(find.text('P1'), findsOneWidget);

    pads = const <ConnectedGamepad>[];
    await tester.pump(const Duration(seconds: 1));
    const reconnected = ConnectedGamepad(
      id: '9',
      order: 0,
      identity: ControllerIdentity(
        displayName: 'DualSense',
        sdlGuid: '030000004c050000e60c000000006800',
        serial: 'serial-a',
      ),
    );
    pads = const <ConnectedGamepad>[reconnected];
    await tester.pump(const Duration(seconds: 1));
    await pressChord(tester, events, '9');

    expect(find.text('P1'), findsOneWidget);
    expect(find.text('P2'), findsNothing);
    expect(find.text('DualSense is already Player 1.'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();
    final claims = await slotClaims.loadClaims();
    expect(claims[0], ControllerSlotClaim.fromGamepad(reconnected));
    expect(claims[1], isNull);

    final resolution = ControllerAssignments.resolveSlotResolution(
      claims: claims,
      devices: pads,
    );
    expect(resolution.controllerSlots.single.playerSlot, 0);
    expect(resolution.reservedPlayerSlots, isEmpty);
  });
}
