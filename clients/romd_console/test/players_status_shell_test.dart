import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/data/local_profiles/local_profile_repository.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/input/console_intents.dart';
import 'package:romd_console/src/input/gamepad_navigator.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_raw_input.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/presentation/theme/console_theme_extensions.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/players_session_cluster.dart';
import 'package:romd_console/src/presentation/widgets/players_status_shell.dart';

final class _CaptureGateProvider
    implements ControllerInputProvider, ControllerInputCaptureGate {
  _CaptureGateProvider(this.controller);

  final StreamController<NormalizedGamepadEvent> controller;
  bool suppressed = false;

  @override
  bool get isNavigationSuppressed => suppressed;

  @override
  void addCaptureStateListener(VoidCallback listener) {}

  @override
  void removeCaptureStateListener(VoidCallback listener) {}

  @override
  Stream<NormalizedGamepadEvent> events() => controller.stream;

  @override
  Future<List<ConnectedGamepad>> listGamepads() async => const [];
}

void main() {
  Future<void> pumpShell(
    WidgetTester tester, {
    required SessionControllerSlotClaims claims,
    required GamepadLister lister,
    Duration pollInterval = const Duration(seconds: 2),
    VoidCallback? onSwitchProfile,
    bool visible = true,
    LocalProfile? activeProfile,
    LocalProfileRepository? localProfileRepository,
    FocusNode? routeButtonFocusNode,
    bool useRealHeader = false,
    Stream<NormalizedGamepadEvent>? controllerEvents,
    ControllerInputProvider? controllerInputProvider,
    ControllerNavigationGate? navigationGate,
    ActiveLaunchSession? activeLaunchSession,
    MediaQueryData? mediaQueryData,
  }) async {
    final profile = activeProfile ?? _profile;
    final navigatorKey = GlobalKey<NavigatorState>();
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        navigatorKey: navigatorKey,
        builder: (context, child) {
          final shell = PlayersStatusShell(
            navigatorKey: navigatorKey,
            slotClaims: claims,
            gamepadLister: lister,
            activeProfile: profile,
            localProfileRepository: localProfileRepository,
            onSwitchProfile: onSwitchProfile ?? () {},
            visible: visible,
            navigationGate: navigationGate,
            activeLaunchSession: activeLaunchSession,
            pollInterval: pollInterval,
            controllerInputProvider:
                controllerInputProvider ??
                CallbackControllerInputProvider(
                  listGamepads: lister,
                  events: () =>
                      controllerEvents ??
                      const Stream<NormalizedGamepadEvent>.empty(),
                ),
            child: child!,
          );
          return mediaQueryData == null
              ? shell
              : MediaQuery(data: mediaQueryData, child: shell);
        },
        home: Builder(
          builder: (context) => Scaffold(
            body: Column(
              children: <Widget>[
                if (useRealHeader)
                  // Home-chrome register: the cluster sits in a padded corner
                  // row, as on the console home screen's bottom-left.
                  Padding(
                    padding: const EdgeInsets.all(24),
                    child: Row(
                      children: <Widget>[
                        const SizedBox(width: 100, child: Text('HOME')),
                        const Spacer(),
                        PlayersSessionCluster(
                          profile: profile,
                          onSwitchProfile: onSwitchProfile ?? () {},
                        ),
                      ],
                    ),
                  )
                else
                  PlayersSessionCluster(
                    profile: profile,
                    onSwitchProfile: onSwitchProfile ?? () {},
                  ),
                Expanded(
                  child: Center(
                    child: FilledButton(
                      focusNode: routeButtonFocusNode,
                      onPressed: () => Navigator.of(context).push<void>(
                        MaterialPageRoute<void>(
                          builder: (_) => const Scaffold(
                            body: Center(child: Text('Pushed route')),
                          ),
                        ),
                      ),
                      child: const Text('Push route'),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
    await tester.pump();
  }

  testWidgets(
    'embedded cluster opens Players and global shortcut covers pushed routes',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1280, 720));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      final pad = ConnectedGamepad.fallback(
        id: 'pad-1',
        name: 'DualSense',
        order: 0,
      );

      await pumpShell(
        tester,
        claims: SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
          ControllerSlotClaim.fromGamepad(pad),
        ]),
        lister: () async => <ConnectedGamepad>[pad],
      );
      await tester.pump();

      expect(
        find.byKey(const ValueKey<String>('players-session-cluster')),
        findsOneWidget,
      );
      expect(
        tester
            .getSize(
              find.byKey(const ValueKey<String>('players-session-cluster')),
            )
            .width,
        48,
      );
      expect(
        find.byKey(const ValueKey<String>('players-status-row')),
        findsNothing,
      );
      expect(
        find.byKey(const ValueKey<String>('joined-controller-icon-0')),
        findsOneWidget,
      );
      expect(
        tester
            .widget<Icon>(
              find.byKey(const ValueKey<String>('joined-controller-icon-0')),
            )
            .color,
        Theme.of(
          tester.element(
            find.byKey(const ValueKey<String>('joined-controller-icon-0')),
          ),
        ).extension<ConsoleColors>()!.focusBorder,
      );
      await tester.tap(find.text('Push route'));
      await tester.pumpAndSettle();
      expect(find.text('Pushed route'), findsOneWidget);
      expect(
        find.byKey(const ValueKey<String>('players-session-cluster')),
        findsNothing,
      );

      final events = StreamController<NormalizedGamepadEvent>.broadcast(
        sync: true,
      );
      addTearDown(events.close);
      final navigator = GamepadNavigator(
        onInputModeChanged: (_) {},
        controllerInputProvider: CallbackControllerInputProvider(
          listGamepads: () async => <ConnectedGamepad>[pad],
          events: () => events.stream,
        ),
      );
      addTearDown(navigator.dispose);
      events.add(_buttonEvent(GamepadButton.start, 1));
      await tester.pumpAndSettle();
      expect(find.text('PLAYERS'), findsOneWidget);
      expect(
        find.text('Player order is for this session only.'),
        findsOneWidget,
      );

      events.add(_buttonEvent(GamepadButton.b, 1));
      await tester.pumpAndSettle();
      expect(find.text('PLAYERS'), findsNothing);
      expect(find.text('Pushed route'), findsOneWidget);
      expect(
        find.byKey(const ValueKey<String>('players-session-cluster')),
        findsNothing,
      );

      Navigator.of(tester.element(find.text('Pushed route'))).pop();
      await tester.pumpAndSettle();
      events.add(_buttonEvent(GamepadButton.start, 1));
      await tester.pumpAndSettle();
      expect(find.text('PLAYERS'), findsOneWidget);
      expect(find.text('Playing as John'), findsOneWidget);
    },
  );

  testWidgets('available controller is quiet until L plus R joins P1', (
    tester,
  ) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final claims = SessionControllerSlotClaims();
    final pad = ConnectedGamepad.fallback(
      id: 'pad-1',
      name: 'DualSense',
      order: 0,
    );
    await pumpShell(
      tester,
      claims: claims,
      lister: () async => <ConnectedGamepad>[pad],
      controllerEvents: events.stream,
    );
    await tester.pump();

    expect(
      find.byKey(const ValueKey<String>('available-controller-icon-0')),
      findsOneWidget,
    );
    expect(
      tester
          .widget<Icon>(
            find.byKey(const ValueKey<String>('available-controller-icon-0')),
          )
          .color,
      Theme.of(
        tester.element(
          find.byKey(const ValueKey<String>('available-controller-icon-0')),
        ),
      ).extension<ConsoleColors>()!.textFaint,
    );
    expect(
      find.bySemanticsLabel(RegExp('1 controller is available to join')),
      findsOneWidget,
    );
    expect(claims.claimDetails.every((claim) => claim == null), isTrue);

    events.add(_buttonEvent(GamepadButton.leftBumper, 1));
    events.add(_buttonEvent(GamepadButton.rightBumper, 1));
    await tester.pumpAndSettle();

    expect(claims.claimDetails[0]?.providerId, 'pad-1');
    expect(claims.claimDetails.skip(1).every((claim) => claim == null), isTrue);
  });

  testWidgets('active raw capture cannot join or prime the join chord', (
    tester,
  ) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final provider = _CaptureGateProvider(events)..suppressed = true;
    final claims = SessionControllerSlotClaims();
    final pad = ConnectedGamepad.fallback(
      id: 'pad-1',
      name: 'DualSense',
      order: 0,
    );
    await pumpShell(
      tester,
      claims: claims,
      lister: () async => <ConnectedGamepad>[pad],
      controllerInputProvider: provider,
    );

    events.add(_buttonEvent(GamepadButton.leftBumper, 1));
    events.add(_buttonEvent(GamepadButton.rightBumper, 1));
    await tester.pumpAndSettle();
    expect(claims.claimDetails.every((claim) => claim == null), isTrue);

    provider.suppressed = false;
    events.add(_buttonEvent(GamepadButton.rightBumper, 1));
    await tester.pumpAndSettle();
    expect(claims.claimDetails.every((claim) => claim == null), isTrue);

    events.add(_buttonEvent(GamepadButton.leftBumper, 1));
    await tester.pumpAndSettle();
    expect(claims.claimDetails.first?.providerId, 'pad-1');
  });

  testWidgets('Home roster distinguishes empty and mixed physical inventory', (
    tester,
  ) async {
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => const <ConnectedGamepad>[],
    );
    await tester.pump();
    expect(
      find.byKey(const ValueKey<String>('empty-players-icon')),
      findsOneWidget,
    );
    expect(
      tester
          .getSize(
            find.byKey(const ValueKey<String>('players-session-cluster')),
          )
          .width,
      48,
    );
    expect(
      find.bySemanticsLabel(RegExp('No controllers are connected or joined')),
      findsOneWidget,
    );

    final joined = ConnectedGamepad.fallback(
      id: 'joined',
      name: '8BitDo',
      order: 0,
    );
    const attention = ConnectedGamepad(
      id: 'attention',
      order: 1,
      identity: ControllerIdentity(
        displayName: 'DualSense',
        sdlGuid: 'guid-attention',
        serial: 'serial-attention',
      ),
    );
    final available = ConnectedGamepad.fallback(
      id: 'available',
      name: 'Xbox',
      order: 2,
    );
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
        ControllerSlotClaim.fromGamepad(joined),
        ControllerSlotClaim.fallback('DualSense'),
      ]),
      lister: () async => <ConnectedGamepad>[joined, attention, available],
    );
    await tester.pump();

    expect(
      find.byKey(const ValueKey<String>('joined-controller-icon-0')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('available-controller-icon-0')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('attention-controller-icon-0')),
      findsOneWidget,
    );
    expect(
      tester
          .widget<Icon>(
            find.byKey(const ValueKey<String>('attention-controller-icon-0')),
          )
          .color,
      Theme.of(
        tester.element(
          find.byKey(const ValueKey<String>('attention-controller-icon-0')),
        ),
      ).extension<ConsoleColors>()!.warning,
    );
    final attentionBadge = find.byKey(
      const ValueKey<String>('attention-controller-badge-0'),
    );
    expect(attentionBadge, findsOneWidget);
    final badgeDecoration =
        tester.widget<Container>(attentionBadge).decoration! as BoxDecoration;
    expect(
      badgeDecoration.color,
      Theme.of(
        tester.element(attentionBadge),
      ).extension<ConsoleColors>()!.warning,
    );
    expect(badgeDecoration.shape, BoxShape.circle);
    expect(
      find.descendant(
        of: attentionBadge,
        matching: find.byIcon(Icons.priority_high_rounded),
      ),
      findsOneWidget,
    );
    expect(
      tester
          .getSize(
            find.byKey(const ValueKey<String>('players-session-cluster')),
          )
          .width,
      120,
    );
    await tester.tap(
      find.byKey(const ValueKey<String>('players-session-cluster')),
    );
    await tester.pumpAndSettle();
    expect(find.text('Controller ready — Hold L + R to join'), findsOneWidget);
    expect(
      find.bySemanticsLabel(
        RegExp('connected controller needs identity attention'),
      ),
      findsOneWidget,
    );
  });

  testWidgets('Home roster gives four controllers full literal width', (
    tester,
  ) async {
    final pads = <ConnectedGamepad>[
      for (var index = 0; index < 4; index++)
        ConnectedGamepad.fallback(id: 'pad-$index', name: 'Pad', order: index),
    ];
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => pads,
    );
    await tester.pump();

    for (var index = 0; index < 4; index++) {
      expect(
        find.byKey(ValueKey<String>('available-controller-icon-$index')),
        findsOneWidget,
      );
    }
    expect(
      tester
          .getSize(
            find.byKey(const ValueKey<String>('players-session-cluster')),
          )
          .width,
      156,
    );
  });

  testWidgets('P2 joins through controller-only identity chooser', (
    tester,
  ) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final gate = ControllerNavigationGate();
    final navigator = GamepadNavigator(
      onInputModeChanged: (_) {},
      controllerNavigationGate: gate,
      controllerInputProvider: CallbackControllerInputProvider(
        listGamepads: () async => const <ConnectedGamepad>[],
        events: () => events.stream,
      ),
    );
    addTearDown(navigator.dispose);
    final guest = LocalProfile(
      id: 'sam',
      displayName: 'Sam',
      avatarKey: 'orbit',
      accentColor: 0xff336699,
      romdServerOrigin: null,
      entryMode: LocalProfileEntryMode.open,
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );
    final claims = SessionControllerSlotClaims.fromClaims(
      const <ControllerSlotClaim?>[
        ControllerSlotClaim(displayName: 'Pad One', providerId: 'p1'),
      ],
    );
    final pads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(id: 'p1', name: 'Wireless Pad', order: 0),
      ConnectedGamepad.fallback(id: 'p2', name: 'Wireless Pad', order: 1),
    ];
    await pumpShell(
      tester,
      claims: claims,
      lister: () async => pads,
      controllerEvents: events.stream,
      navigationGate: gate,
      localProfileRepository: _ReadOnlyProfileRepository(<LocalProfile>[
        _profile,
        guest,
      ]),
    );
    await tester.pump();

    for (final button in <GamepadButton>[
      GamepadButton.a,
      GamepadButton.start,
      GamepadButton.x,
      GamepadButton.y,
    ]) {
      events.add(_buttonEvent(button, 1, gamepadId: 'p2'));
    }
    await tester.pump();
    expect(find.text('Pushed route'), findsNothing);
    expect(claims.claimDetails[1], isNull);

    events.add(_buttonEvent(GamepadButton.leftBumper, 1, gamepadId: 'p2'));
    events.add(_buttonEvent(GamepadButton.rightBumper, 1, gamepadId: 'p2'));
    await tester.pumpAndSettle();
    expect(find.text("Who's using this controller?"), findsOneWidget);
    expect(find.text('Wireless Pad #2'), findsOneWidget);
    expect(
      find.text('Uses the primary profile’s controls for this session.'),
      findsOneWidget,
    );

    events.add(_buttonEvent(GamepadButton.dpadDown, 1, gamepadId: 'p2'));
    events.add(_buttonEvent(GamepadButton.dpadDown, 0, gamepadId: 'p2'));
    await tester.pump();
    events.add(_buttonEvent(GamepadButton.a, 1, gamepadId: 'p2'));
    await tester.pumpAndSettle();

    expect(claims.claimDetails[1]?.providerId, 'p2');
    expect(claims.claimDetails[1]?.localProfileId, 'sam');
  });

  testWidgets('simultaneous joins are FIFO after cancel then accept', (
    tester,
  ) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final gate = ControllerNavigationGate();
    final navigator = GamepadNavigator(
      onInputModeChanged: (_) {},
      controllerNavigationGate: gate,
      controllerInputProvider: CallbackControllerInputProvider(
        listGamepads: () async => const <ConnectedGamepad>[],
        events: () => events.stream,
      ),
    );
    addTearDown(navigator.dispose);
    final alternate = LocalProfile(
      id: 'alternate',
      displayName: 'Sam',
      avatarKey: 'orbit',
      accentColor: 0xff336699,
      romdServerOrigin: null,
      entryMode: LocalProfileEntryMode.open,
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );
    final claims = SessionControllerSlotClaims.fromClaims(
      const <ControllerSlotClaim?>[
        ControllerSlotClaim(displayName: 'P1 Pad', providerId: 'p1'),
      ],
    );
    final pads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(id: 'p1', name: 'P1 Pad', order: 0),
      ConnectedGamepad.fallback(id: 'p2', name: 'Second Pad', order: 1),
      ConnectedGamepad.fallback(id: 'p3', name: 'Third Pad', order: 2),
    ];
    await pumpShell(
      tester,
      claims: claims,
      lister: () async => pads,
      controllerEvents: events.stream,
      navigationGate: gate,
      localProfileRepository: _ReadOnlyProfileRepository(<LocalProfile>[
        _profile,
        alternate,
      ]),
    );
    await tester.pump();

    for (final id in <String>['p2', 'p3']) {
      events.add(_buttonEvent(GamepadButton.leftBumper, 1, gamepadId: id));
      events.add(_buttonEvent(GamepadButton.rightBumper, 1, gamepadId: id));
    }
    await tester.pumpAndSettle();
    expect(find.text('Second Pad'), findsOneWidget);
    events.add(_buttonEvent(GamepadButton.b, 1, gamepadId: 'p2'));
    await tester.pumpAndSettle();
    expect(find.text('Third Pad'), findsOneWidget);
    events.add(_buttonEvent(GamepadButton.a, 1, gamepadId: 'p3'));
    await tester.pumpAndSettle();

    expect(claims.claimDetails[1]?.providerId, 'p3');
    expect(claims.claimDetails[2], isNull);
  });

  testWidgets('emulator-active shell suppresses join and navigation', (
    tester,
  ) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final active = ActiveLaunchSession()..hold(const _FakeLaunchSession());
    final gate = ControllerNavigationGate();
    final navigator = GamepadNavigator(
      onInputModeChanged: (_) {},
      controllerNavigationGate: gate,
      controllerInputProvider: CallbackControllerInputProvider(
        listGamepads: () async => const <ConnectedGamepad>[],
        events: () => events.stream,
      ),
    );
    addTearDown(navigator.dispose);
    final claims = SessionControllerSlotClaims.fromClaims(
      const <ControllerSlotClaim?>[
        ControllerSlotClaim(displayName: 'P1 Pad', providerId: 'p1'),
      ],
    );
    final pads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(id: 'p1', name: 'P1 Pad', order: 0),
      ConnectedGamepad.fallback(id: 'p2', name: 'Second Pad', order: 1),
    ];
    await pumpShell(
      tester,
      claims: claims,
      lister: () async => pads,
      controllerEvents: events.stream,
      navigationGate: gate,
      activeLaunchSession: active,
    );
    await tester.pump();

    events.add(_buttonEvent(GamepadButton.a, 1, gamepadId: 'p1'));
    events.add(_buttonEvent(GamepadButton.leftBumper, 1, gamepadId: 'p2'));
    events.add(_buttonEvent(GamepadButton.rightBumper, 1, gamepadId: 'p2'));
    await tester.pump();

    expect(find.text('Pushed route'), findsNothing);
    expect(claims.claimDetails[1], isNull);
  });

  testWidgets('inventory failure is not reported as disconnect', (
    tester,
  ) async {
    var inventoryFails = false;
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final claims = SessionControllerSlotClaims.fromClaims(
      const <ControllerSlotClaim?>[
        ControllerSlotClaim(displayName: 'P1 Pad', providerId: 'p1'),
      ],
    );
    final pads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(id: 'p1', name: 'P1 Pad', order: 0),
      ConnectedGamepad.fallback(id: 'p2', name: 'Second Pad', order: 1),
    ];
    await pumpShell(
      tester,
      claims: claims,
      lister: () async =>
          inventoryFails ? throw StateError('inventory unavailable') : pads,
      controllerEvents: events.stream,
      pollInterval: const Duration(milliseconds: 50),
      localProfileRepository: _ReadOnlyProfileRepository(<LocalProfile>[
        _profile,
        _alternateProfile,
      ]),
    );
    await tester.pump();
    events.add(_buttonEvent(GamepadButton.leftBumper, 1, gamepadId: 'p2'));
    events.add(_buttonEvent(GamepadButton.rightBumper, 1, gamepadId: 'p2'));
    await tester.pumpAndSettle();
    expect(find.text("Who's using this controller?"), findsOneWidget);

    inventoryFails = true;
    await tester.pump(const Duration(milliseconds: 120));
    await tester.pumpAndSettle();
    expect(
      find.text('Controller status unavailable. Join cancelled.'),
      findsOneWidget,
    );
    expect(find.textContaining('disconnected'), findsNothing);
    expect(claims.claimDetails[1], isNull);
  });

  testWidgets('physical disconnect cancels an active chooser truthfully', (
    tester,
  ) async {
    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final claims = SessionControllerSlotClaims.fromClaims(
      const <ControllerSlotClaim?>[
        ControllerSlotClaim(displayName: 'P1 Pad', providerId: 'p1'),
      ],
    );
    var pads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(id: 'p1', name: 'P1 Pad', order: 0),
      ConnectedGamepad.fallback(id: 'p2', name: 'Second Pad', order: 1),
    ];
    await pumpShell(
      tester,
      claims: claims,
      lister: () async => pads,
      controllerEvents: events.stream,
      localProfileRepository: _ReadOnlyProfileRepository(<LocalProfile>[
        _profile,
        _alternateProfile,
      ]),
    );
    await tester.pump();
    events.add(_buttonEvent(GamepadButton.leftBumper, 1, gamepadId: 'p2'));
    events.add(_buttonEvent(GamepadButton.rightBumper, 1, gamepadId: 'p2'));
    await tester.pumpAndSettle();
    expect(find.text("Who's using this controller?"), findsOneWidget);

    pads = <ConnectedGamepad>[pads.first];
    await PlayersStatusScope.maybeOf(
      tester.element(find.text('Push route')),
    )!.controller.refresh();
    await tester.pumpAndSettle();

    expect(
      find.text('Controller disconnected. Join cancelled.'),
      findsOneWidget,
    );
    expect(claims.claimDetails[1], isNull);
  });

  testWidgets(
    'guest profile assignment is session-only and shown on the seat',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1280, 720));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      final guest = LocalProfile(
        id: 'guest-profile',
        displayName: 'Sam',
        avatarKey: 'default',
        accentColor: 0xff336699,
        romdServerOrigin: null,
        entryMode: LocalProfileEntryMode.pin,
        createdAt: DateTime(2026),
        updatedAt: DateTime(2026),
      );
      final repository = _ReadOnlyProfileRepository(<LocalProfile>[
        _profile,
        guest,
      ]);
      final claims =
          SessionControllerSlotClaims.fromClaims(const <ControllerSlotClaim?>[
            ControllerSlotClaim(displayName: 'Pad One', providerId: 'p1'),
            ControllerSlotClaim(displayName: 'Pad Two', providerId: 'p2'),
          ]);
      final pads = <ConnectedGamepad>[
        ConnectedGamepad.fallback(id: 'p1', name: 'Pad One', order: 0),
        ConnectedGamepad.fallback(id: 'p2', name: 'Pad Two', order: 1),
      ];

      await pumpShell(
        tester,
        claims: claims,
        lister: () async => pads,
        localProfileRepository: repository,
      );
      await tester.pump();
      await tester.tap(
        find.byKey(const ValueKey<String>('players-session-cluster')),
      );
      await tester.pumpAndSettle();

      expect(find.text('John'), findsWidgets);
      expect(find.text('Guest'), findsOneWidget);
      await tester.tap(find.text('Assign guest profiles'));
      await tester.pumpAndSettle();
      expect(find.text('P2 · Pad Two'), findsOneWidget);
      expect(find.text('Anonymous guest'), findsOneWidget);

      await tester.tap(find.text('P2 · Pad Two'));
      await tester.pumpAndSettle();
      expect(find.text('Sam'), findsOneWidget);
      Actions.invoke(tester.element(find.text('Sam')), const DismissIntent());
      await tester.pumpAndSettle();
      expect(claims.claimDetails[1]!.localProfileId, isNull);

      await tester.tap(find.text('P2 · Pad Two'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Sam'));
      await tester.pumpAndSettle();

      expect(find.text('Sam'), findsOneWidget);
      expect(claims.claimDetails[1]!.localProfileId, 'guest-profile');
      expect(repository.writeCalls, 0);

      await tester.sendKeyEvent(LogicalKeyboardKey.escape);
      await tester.pumpAndSettle();
      expect(find.text('Sam'), findsOneWidget);
      expect(find.text('Playing as John'), findsOneWidget);
    },
  );

  testWidgets('global Players action is suppressed before profile activation', (
    tester,
  ) async {
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => const <ConnectedGamepad>[],
      visible: false,
    );

    final context = tester.element(find.text('Push route'));
    expect(Actions.maybeInvoke(context, const ShowPlayersIntent()), isNull);
    await tester.pumpAndSettle();
    expect(find.text('PLAYERS'), findsNothing);
  });

  testWidgets('cluster exposes Change Order through Players', (tester) async {
    final pads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(id: 'pad-1', name: 'DualSense', order: 0),
      ConnectedGamepad.fallback(id: 'pad-2', name: 'Xbox', order: 1),
    ];
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
        for (final pad in pads) ControllerSlotClaim.fromGamepad(pad),
      ]),
      lister: () async => pads,
    );
    await tester.pump();

    expect(
      find.byKey(const ValueKey<String>('joined-controller-icon-0')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('joined-controller-icon-1')),
      findsOneWidget,
    );
    expect(
      tester
          .getSize(
            find.byKey(const ValueKey<String>('players-session-cluster')),
          )
          .width,
      84,
    );

    await tester.tap(
      find.byKey(const ValueKey<String>('players-session-cluster')),
    );
    await tester.pumpAndSettle();
    expect(find.text('PLAYERS'), findsOneWidget);
    expect(find.text('Playing as John'), findsOneWidget);
    expect(find.text('Switch primary profile'), findsOneWidget);
    expect(find.text('Change player order'), findsOneWidget);

    await tester.tap(find.text('Change player order'));
    await tester.pumpAndSettle();

    expect(find.text('CHANGE PLAYER ORDER'), findsOneWidget);
    expect(
      find.byKey(const ValueKey<String>('players-status-row')),
      findsNothing,
    );
  });

  testWidgets('controller dismiss intent leaves Players', (tester) async {
    final pad = ConnectedGamepad.fallback(
      id: 'pad-1',
      name: 'DualSense',
      order: 0,
    );
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
        ControllerSlotClaim.fromGamepad(pad),
      ]),
      lister: () async => <ConnectedGamepad>[pad],
    );
    await tester.pump();

    await tester.tap(
      find.byKey(const ValueKey<String>('players-session-cluster')),
    );
    await tester.pumpAndSettle();
    expect(find.text('PLAYERS'), findsOneWidget);

    Actions.invoke(
      tester.element(find.text('Change player order')),
      const DismissIntent(),
    );
    await tester.pumpAndSettle();

    expect(find.text('PLAYERS'), findsNothing);
    expect(
      find.byKey(const ValueKey<String>('players-session-cluster')),
      findsOneWidget,
    );
  });

  testWidgets('panel preserves route context and restores prior focus', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final routeFocus = FocusNode(debugLabel: 'route-button');
    addTearDown(routeFocus.dispose);
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => const <ConnectedGamepad>[],
      routeButtonFocusNode: routeFocus,
    );
    routeFocus.requestFocus();
    await tester.pump();
    expect(routeFocus.hasFocus, isTrue);

    await tester.sendKeyEvent(LogicalKeyboardKey.f2);
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey<String>('players-panel')), findsOneWidget);
    expect(find.text('Push route'), findsOneWidget);
    expect(routeFocus.hasFocus, isFalse);

    Actions.invoke(
      tester.element(find.text('Change player order')),
      const DismissIntent(),
    );
    await tester.pumpAndSettle();
    await tester.pump();
    expect(find.byKey(const ValueKey<String>('players-panel')), findsNothing);
    expect(routeFocus.hasFocus, isTrue);
  });

  testWidgets('nested player-order route returns to the same Players panel', (
    tester,
  ) async {
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => const <ConnectedGamepad>[],
    );
    await tester.tap(
      find.byKey(const ValueKey<String>('players-session-cluster')),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Change player order'));
    await tester.pumpAndSettle();
    expect(find.text('CHANGE PLAYER ORDER'), findsOneWidget);
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey<String>('players-panel')), findsOneWidget);
  });

  testWidgets('long profile and controller names fit the 720p panel', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final longProfile = LocalProfile(
      id: 'profile-long',
      displayName:
          'Alexandria Very Long Household Profile Name For The Living Room',
      avatarKey: 'orbit',
      accentColor: 0xff1fbf8f,
      romdServerOrigin: null,
      entryMode: LocalProfileEntryMode.open,
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );
    final pads = <ConnectedGamepad>[
      for (var index = 0; index < 4; index++)
        ConnectedGamepad.fallback(
          id: 'pad-$index',
          name:
              'Wireless Controller With An Exceptionally Long Product Name $index',
          order: index,
        ),
    ];
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
        for (final pad in pads) ControllerSlotClaim.fromGamepad(pad),
      ]),
      lister: () async => pads,
      activeProfile: longProfile,
      useRealHeader: true,
    );
    await tester.pump();
    await tester.tap(
      find.byKey(const ValueKey<String>('players-session-cluster')),
    );
    for (var frame = 0; frame < 8; frame++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    final panel = tester.getRect(
      find.byKey(const ValueKey<String>('players-panel')),
    );
    expect(panel.width, lessThanOrEqualTo(900));
    expect(panel.height, lessThanOrEqualTo(660));
    expect(panel.left, greaterThanOrEqualTo(0));
    expect(panel.right, lessThanOrEqualTo(1280));
    expect(panel.top, greaterThanOrEqualTo(0));
    expect(panel.bottom, lessThanOrEqualTo(720));
    expect(tester.takeException(), isNull);
  });

  testWidgets('Players seat text remains inside every seat at 2x', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final pads = <ConnectedGamepad>[
      for (var index = 0; index < 4; index++)
        ConnectedGamepad.fallback(
          id: 'pad-$index',
          name: 'Long Controller Name $index',
          order: index,
        ),
    ];
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
        for (final pad in pads) ControllerSlotClaim.fromGamepad(pad),
      ]),
      lister: () async => pads,
      mediaQueryData: const MediaQueryData(textScaler: TextScaler.linear(2)),
    );
    await tester.pump();
    await tester.tap(
      find.byKey(const ValueKey<String>('players-session-cluster')),
    );
    await tester.pumpAndSettle();

    final panel = find.byKey(const ValueKey<String>('players-panel'));
    for (var slot = 0; slot < 4; slot++) {
      final seat = find.byKey(ValueKey<String>('player-seat-$slot'));
      _expectContained(tester, seat, panel);
      _expectContained(
        tester,
        find.byKey(ValueKey<String>('player-seat-number-$slot')),
        seat,
      );
      _expectContained(
        tester,
        find.byKey(ValueKey<String>('player-seat-name-$slot')),
        seat,
      );
      _expectContained(
        tester,
        find.byKey(ValueKey<String>('player-seat-status-$slot')),
        seat,
      );
    }
  });

  testWidgets('offline claims do not enter the physical controller roster', (
    tester,
  ) async {
    final claims = SessionControllerSlotClaims();
    await pumpShell(tester, claims: claims, lister: () async => const []);
    await tester.pump();
    expect(
      find.byKey(const ValueKey<String>('players-session-cluster')),
      findsOneWidget,
    );

    await claims.replaceAllClaims(const <ControllerSlotClaim?>[
      ControllerSlotClaim(
        displayName: 'DualSense',
        sdlGuid: 'guid-a',
        serial: 'serial-a',
      ),
    ]);
    await tester.pump();
    await tester.pump();

    expect(
      tester
          .getSemantics(
            find.byKey(const ValueKey<String>('players-session-cluster')),
          )
          .label,
      contains('No controllers are connected or joined'),
    );
  });

  testWidgets('offline P2 reservations stay out of the controller roster', (
    tester,
  ) async {
    final claims =
        SessionControllerSlotClaims.fromClaims(const <ControllerSlotClaim?>[
          null,
          ControllerSlotClaim(
            displayName: 'DualSense',
            sdlGuid: 'guid-a',
            serial: 'serial-a',
          ),
        ]);
    await pumpShell(tester, claims: claims, lister: () async => const []);
    await tester.pump();

    expect(
      tester
          .getSemantics(
            find.byKey(const ValueKey<String>('players-session-cluster')),
          )
          .label,
      contains('No controllers are connected or joined'),
    );
  });

  testWidgets('profile switching is a separate Players action', (tester) async {
    var switchCount = 0;
    final pad = ConnectedGamepad.fallback(
      id: 'pad-1',
      name: 'DualSense',
      order: 0,
    );
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => <ConnectedGamepad>[pad],
      onSwitchProfile: () => switchCount++,
    );
    await tester.pump();

    await tester.tap(
      find.byKey(const ValueKey<String>('players-session-cluster')),
    );
    await tester.pumpAndSettle();
    expect(find.text('Playing as John'), findsOneWidget);
    expect(find.text('Switch primary profile'), findsOneWidget);

    await tester.tap(find.text('Change player order'));
    await tester.pumpAndSettle();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();
    expect(switchCount, 0);

    await tester.tap(find.text('Switch primary profile'));
    await tester.pumpAndSettle();
    expect(switchCount, 1);
  });

  testWidgets('connection polling refreshes the status without claim writes', (
    tester,
  ) async {
    var pads = <ConnectedGamepad>[];
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => pads,
      pollInterval: const Duration(milliseconds: 20),
    );
    await tester.pump();
    expect(
      find.byKey(const ValueKey<String>('players-session-cluster')),
      findsOneWidget,
    );

    pads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(
        id: 'pad-2',
        name: 'Xbox Wireless Controller',
        order: 0,
      ),
    ];
    await tester.pump(const Duration(milliseconds: 25));
    await tester.pump();

    expect(
      find.byKey(const ValueKey<String>('players-session-cluster')),
      findsOneWidget,
    );
  });

  testWidgets('healthy single-player state proceeds without prompting', (
    tester,
  ) async {
    final pad = ConnectedGamepad.fallback(
      id: 'pad-1',
      name: 'DualSense',
      order: 0,
    );
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
        ControllerSlotClaim.fromGamepad(pad),
      ]),
      lister: () async => <ConnectedGamepad>[pad],
    );
    await tester.pump();

    final snapshot = await reviewPlayersBeforePlay(
      tester.element(find.text('Push route')),
    );
    expect(snapshot, isNotNull);
    expect(snapshot!.devices, <ConnectedGamepad>[pad]);
    expect(snapshot.slotResolution.controllerSlots.single.playerSlot, 0);
    expect(find.text('Use this player order?'), findsNothing);
  });

  testWidgets('multiple clear players receive a concise launch review', (
    tester,
  ) async {
    final pads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(id: 'pad-1', name: 'DualSense', order: 0),
      ConnectedGamepad.fallback(id: 'pad-2', name: 'Xbox', order: 1),
    ];
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => pads,
    );
    await tester.pump();

    final review = reviewPlayersBeforePlay(
      tester.element(find.text('Push route')),
    );
    await tester.pumpAndSettle();
    expect(find.text('PLAYERS'), findsOneWidget);
    expect(find.text('Continue to game'), findsOneWidget);
    expect(
      find.text('Game controller order may differ from Ottercade Players'),
      findsOneWidget,
    );
    expect(find.text('Playing as John'), findsOneWidget);
    expect(find.text('Switch primary profile'), findsOneWidget);

    await tester.tap(find.text('Continue to game'));
    await tester.pumpAndSettle();
    expect(await review, isNotNull);
  });

  testWidgets('pre-launch panel returns an explicit result and snapshot', (
    tester,
  ) async {
    final pads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(id: 'pad-1', name: 'DualSense', order: 0),
      ConnectedGamepad.fallback(id: 'pad-2', name: 'Xbox', order: 1),
    ];
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => pads,
    );
    await tester.pump();
    final context = tester.element(find.text('Push route'));
    final scope = PlayersStatusScope.maybeOf(context)!;

    final resultFuture = scope.openPlayers(forLaunch: true);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Continue to game'));
    await tester.pumpAndSettle();
    final result = await resultFuture;

    expect(result.disposition, PlayersPanelDisposition.approved);
    expect(result.snapshot, isNotNull);
    expect(result.snapshot!.devices, pads);
  });

  testWidgets('controller Back returns an explicit dismissed result', (
    tester,
  ) async {
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => const <ConnectedGamepad>[],
    );
    final context = tester.element(find.text('Push route'));
    final resultFuture = PlayersStatusScope.maybeOf(
      context,
    )!.openPlayers(forLaunch: true);
    await tester.pumpAndSettle();

    Actions.invoke(
      tester.element(find.text('Change player order')),
      const DismissIntent(),
    );
    await tester.pumpAndSettle();
    final result = await resultFuture;

    expect(result.disposition, PlayersPanelDisposition.dismissed);
    expect(result.snapshot, isNull);
  });

  testWidgets('uncertain player state routes into setup instead of launching', (
    tester,
  ) async {
    final claims =
        SessionControllerSlotClaims.fromClaims(const <ControllerSlotClaim?>[
          ControllerSlotClaim(
            displayName: 'DualSense',
            sdlGuid: 'guid-a',
            serial: 'serial-a',
          ),
        ]);
    await pumpShell(tester, claims: claims, lister: () async => const []);
    await tester.pump();

    final review = reviewPlayersBeforePlay(
      tester.element(find.text('Push route')),
    );
    await tester.pumpAndSettle();
    expect(find.text('PLAYERS'), findsOneWidget);
    expect(find.text('Play anyway'), findsOneWidget);
    expect(
      find.text(
        'P1 is waiting for DualSense. Reconnect it or change player order. '
        'Other controllers won’t take P1.',
      ),
      findsOneWidget,
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pumpAndSettle();
    expect(await review, isNull);
  });

  testWidgets('listing failure enters setup but preserves fallback launch', (
    tester,
  ) async {
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => throw StateError('backend unavailable'),
    );
    await tester.pump();

    final review = reviewPlayersBeforePlay(
      tester.element(find.text('Push route')),
    );
    await tester.pumpAndSettle();
    expect(find.text('PLAYERS'), findsOneWidget);
    expect(
      find.text(
        'Controller status is temporarily unavailable. Confirm player order before playing.',
      ),
      findsOneWidget,
    );
    expect(find.text('Play anyway'), findsOneWidget);

    await tester.tap(find.text('Play anyway'));
    await tester.pumpAndSettle();
    final snapshot = await review;
    expect(snapshot, isNotNull);
    expect(snapshot!.inventoryAvailable, isFalse);
  });

  testWidgets('reserved P1 can launch without promoting an available pad', (
    tester,
  ) async {
    final claims =
        SessionControllerSlotClaims.fromClaims(const <ControllerSlotClaim?>[
          ControllerSlotClaim(
            displayName: 'DualSense',
            sdlGuid: 'guid-a',
            serial: 'serial-a',
          ),
        ]);
    final p2 = ConnectedGamepad.fallback(id: 'pad-2', name: 'Xbox', order: 0);
    await pumpShell(
      tester,
      claims: claims,
      lister: () async => <ConnectedGamepad>[p2],
    );
    await tester.pump();

    final review = reviewPlayersBeforePlay(
      tester.element(find.text('Push route')),
    );
    await tester.pumpAndSettle();
    expect(find.text('Waiting'), findsOneWidget);
    expect(find.text('Controller ready — Hold L + R to join'), findsOneWidget);
    expect(find.text('Play anyway'), findsOneWidget);

    await tester.tap(find.text('Play anyway'));
    await tester.pumpAndSettle();
    final snapshot = await review;
    expect(snapshot, isNotNull);
    expect(snapshot!.slotResolution.reservedPlayerSlots, <int>{0});
    expect(snapshot.slotResolution.controllerSlots, isEmpty);
    expect(snapshot.slotResolution.availableControllers, <ConnectedGamepad>[
      p2,
    ]);
  });

  testWidgets('Play awaits an in-flight refresh before choosing its route', (
    tester,
  ) async {
    final listing = Completer<List<ConnectedGamepad>>();
    final pads = <ConnectedGamepad>[
      ConnectedGamepad.fallback(id: 'pad-1', name: 'DualSense', order: 0),
      ConnectedGamepad.fallback(id: 'pad-2', name: 'Xbox', order: 1),
    ];
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () => listing.future,
    );

    var completed = false;
    final review =
        reviewPlayersBeforePlay(tester.element(find.text('Push route'))).then((
          value,
        ) {
          completed = true;
          return value;
        });
    await tester.pump();
    expect(completed, isFalse);

    listing.complete(pads);
    await tester.pumpAndSettle();
    expect(find.text('PLAYERS'), findsOneWidget);
    expect(completed, isFalse);

    await tester.tap(find.text('Continue to game'));
    await tester.pumpAndSettle();
    expect(await review, isNotNull);
  });

  testWidgets('an in-flight refresh cannot notify after shell disposal', (
    tester,
  ) async {
    final listing = Completer<List<ConnectedGamepad>>();
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () => listing.future,
    );

    await tester.pumpWidget(const SizedBox.shrink());
    listing.complete(const <ConnectedGamepad>[]);
    await tester.pump();

    expect(tester.takeException(), isNull);
  });

  testWidgets('Players keeps device settings out of session setup', (
    tester,
  ) async {
    await pumpShell(
      tester,
      claims: SessionControllerSlotClaims(),
      lister: () async => const <ConnectedGamepad>[],
    );
    await tester.pump();

    await tester.sendKeyEvent(LogicalKeyboardKey.f2);
    await tester.pumpAndSettle();
    expect(find.text('PLAYERS'), findsOneWidget);
    expect(find.text('Change player order'), findsOneWidget);
    expect(find.text('Controller settings'), findsNothing);
  });
}

void _expectContained(WidgetTester tester, Finder child, Finder parent) {
  final childRect = tester.getRect(child);
  final parentRect = tester.getRect(parent);
  expect(childRect.left, greaterThanOrEqualTo(parentRect.left));
  expect(childRect.top, greaterThanOrEqualTo(parentRect.top));
  expect(childRect.right, lessThanOrEqualTo(parentRect.right));
  expect(childRect.bottom, lessThanOrEqualTo(parentRect.bottom));
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

final _profile = LocalProfile(
  id: 'profile-1',
  displayName: 'John',
  avatarKey: 'orbit',
  accentColor: 0xff1fbf8f,
  romdServerOrigin: null,
  entryMode: LocalProfileEntryMode.open,
  createdAt: DateTime(2026),
  updatedAt: DateTime(2026),
);

final _alternateProfile = LocalProfile(
  id: 'alternate-profile',
  displayName: 'Sam',
  avatarKey: 'orbit',
  accentColor: 0xff336699,
  romdServerOrigin: null,
  entryMode: LocalProfileEntryMode.open,
  createdAt: DateTime(2026),
  updatedAt: DateTime(2026),
);

final class _FakeLaunchSession implements LaunchSession {
  const _FakeLaunchSession();

  @override
  Future<LaunchResult> get completed async => const LaunchExited(0);

  @override
  LaunchForegroundControl? get foreground => null;

  @override
  LaunchTermination? get termination => null;
}

final class _ReadOnlyProfileRepository implements LocalProfileRepository {
  _ReadOnlyProfileRepository(this.profiles);

  final List<LocalProfile> profiles;
  int writeCalls = 0;

  @override
  Future<List<LocalProfile>> listProfiles() async => profiles;

  @override
  Stream<List<LocalProfile>> watchProfiles() => Stream.value(profiles);

  @override
  Future<LocalProfile> createProfile(CreateLocalProfileRequest request) {
    writeCalls++;
    throw UnimplementedError();
  }

  @override
  Future<LocalProfile> linkRomdAccount({
    required String localProfileId,
    required RomdAccountLink accountLink,
  }) {
    writeCalls++;
    throw UnimplementedError();
  }

  @override
  Future<LocalProfile> unlinkRomdAccount({required String localProfileId}) {
    writeCalls++;
    throw UnimplementedError();
  }

  @override
  Future<LocalProfile> updateRomdServerOrigin({
    required String localProfileId,
    required Uri? serverOrigin,
  }) {
    writeCalls++;
    throw UnimplementedError();
  }

  @override
  Future<void> markLastUsed(String profileId) async {
    writeCalls++;
  }
}
