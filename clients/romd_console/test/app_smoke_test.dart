import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/app/romd_console_app.dart';
import 'package:romd_console/src/config/romd_environment.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/local_profiles/local_profile_repository.dart';
import 'package:romd_console/src/data/local_profiles/profile_server_connection_repository.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/data/refresh_token_store.dart';
import 'package:romd_console/src/data/server_discovery_api_client.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/consumer_host_health.dart';
import 'package:romd_console/src/domain/device_authorization.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/input/console_input_mode.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/content/domain/profile_local_library.dart';
import 'package:romd_console/src/play/controllers/data/gamepad_devices.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/play_coordinator.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';
import 'package:romd_console/src/presentation/entry_stage.dart';
import 'package:romd_console/src/presentation/glyph_family_scope.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_ambient_background.dart';

import 'helpers/in_memory_controller_preferences.dart';
import 'helpers/play_fakes.dart';
import 'support/fake_profile_local_library_repository.dart';

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

final RomdServerInstanceId _serverInstanceId = RomdServerInstanceId.tryParse(
  '11111111-1111-4111-8111-111111111111',
)!;

ServerDiscoveryApiClient _successfulDiscoveryFactory(Uri origin) =>
    _FakeServerDiscoveryApiClient(
      result: Future<ServerDiscoveryResult>.value(
        ServerDiscoverySuccess(_serverInstanceId),
      ),
    );

Future<void> _selectFirstProfile(WidgetTester tester) async {
  await tester.tap(find.text('Press L + R to Start'));
  await tester.pump();
  await tester.pump(const Duration(milliseconds: 500));
  await tester.pump();
  await tester.pump();
  await tester.tap(find.text('Player One'));
  await _pumpFrames(tester);
}

Future<void> _pumpFrames(WidgetTester tester, [int count = 20]) async {
  for (var i = 0; i < count; i++) {
    await tester.pump(const Duration(milliseconds: 50));
  }
}

Future<void> _openAccountSettings(WidgetTester tester) async {
  final account = find.byKey(const ValueKey<String>('settings-account-row'));
  final profileCategory = find.byKey(const ValueKey<Object>('profile'));
  if (account.evaluate().isEmpty && profileCategory.evaluate().isEmpty) {
    final settings = find.byKey(const ValueKey<String>('dock-settings'));
    await tester.ensureVisible(settings);
    await tester.tap(settings);
    await _pumpFrames(tester, 4);
  }
  if (account.evaluate().isNotEmpty) {
    await tester.ensureVisible(account);
    await tester.tap(account);
    await _pumpFrames(tester, 4);
  }
}

Future<void> _selectAccountCategory(
  WidgetTester tester,
  String categoryId,
) async {
  final category = find.byKey(ValueKey<Object>(categoryId));
  if (category.evaluate().isEmpty) {
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await _pumpFrames(tester, 4);
  }
  await tester.ensureVisible(category);
  await tester.tap(category);
  await _pumpFrames(tester, 4);
}

Future<void> _tapSettingAction(WidgetTester tester, String label) async {
  final categoryId = switch (label) {
    'Add ROMD server' ||
    'Change server' ||
    'Remove server' ||
    'Connect ROMD' ||
    'Sign out' => 'account',
    _ => null,
  };
  if (categoryId != null && find.text(label).hitTestable().evaluate().isEmpty) {
    if (find.byKey(ValueKey<Object>(categoryId)).evaluate().isEmpty) {
      await _openAccountSettings(tester);
    }
    await _selectAccountCategory(tester, categoryId);
  }
  final action = find.text(label);
  await Scrollable.ensureVisible(tester.element(action), alignment: 0.5);
  await tester.pump();
  await tester.tap(action);
}

Future<void> _confirmRemoveServer(WidgetTester tester) async {
  await _tapSettingAction(tester, 'Remove server');
  await _pumpFrames(tester, 4);
  await tester.tap(find.widgetWithText(TextButton, 'Remove'));
  await _pumpFrames(tester, 4);
}

final Finder _connectControl = find.byWidgetPredicate(
  (widget) => widget is Semantics && widget.properties.label == 'Connect',
  description: 'launcher Connect control',
);

enum _StaleBootstrapPhase { cleanup, discovery, tokenRead, refresh }

void main() {
  testWidgets('controller hint scopes live above the app navigator', (
    tester,
  ) async {
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: Uri.parse(
            RomdEnvironment.defaultConsumerApiOrigin,
          ),
        ),
        consumerApiClientFactory: FakeConsumerApiClientFactory().create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );
    await tester.pump();

    final navigator = find.byType(Navigator);
    expect(
      find.ancestor(
        of: navigator,
        matching: find.byType(ConsoleInputModeScope),
      ),
      findsOneWidget,
    );
    expect(
      find.ancestor(of: navigator, matching: find.byType(GlyphFamilyScope)),
      findsOneWidget,
    );
  });

  testWidgets('controllers route uses the play service gamepad lister', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    var listCount = 0;
    Future<List<ConnectedGamepad>> listGamepads() async {
      listCount++;
      return const <ConnectedGamepad>[
        ConnectedGamepad(
          id: '7',
          order: 0,
          identity: ControllerIdentity(
            displayName: 'SDL Exact Pad',
            sdlGuid: '030000004c050000e60c000000006800',
            serial: 'serial-a',
          ),
        ),
      ];
    }

    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: Uri.parse(
            RomdEnvironment.defaultConsumerApiOrigin,
          ),
        ),
        consumerApiClientFactory: FakeConsumerApiClientFactory().create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        gamepadLister: listGamepads,
        enableGamepad: false,
      ),
    );
    await tester.pump();

    await tester.tap(find.text('Press L + R to Start'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));
    await tester.pump();
    await tester.pump();
    await tester.tap(find.text('Player One'));
    for (var i = 0; i < 20; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    await tester.tap(find.byKey(const ValueKey<String>('dock-settings')));
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    await tester.tap(find.text('Controllers'));
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(
      find.byKey(const ValueKey<String>('controllers-settings-screen')),
      findsOneWidget,
    );
    expect(
      find.ancestor(
        of: find.byKey(const ValueKey<String>('controllers-settings-screen')),
        matching: find.byType(ConsoleAmbientBackground),
      ),
      findsOneWidget,
    );
    expect(find.text('Connected controllers'), findsOneWidget);
    expect(find.text('Button prompts'), findsOneWidget);
    expect(listCount, greaterThan(0));
  });

  testWidgets('Switch Profile returns to entry while Exit closes the app', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    var exits = 0;
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: Uri.parse(
            RomdEnvironment.defaultConsumerApiOrigin,
          ),
        ),
        consumerApiClientFactory: FakeConsumerApiClientFactory().create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
        exitApplication: () async => exits++,
      ),
    );
    await tester.pump();
    await _selectFirstProfile(tester);

    await tester.tap(find.byKey(const ValueKey<String>('dock-controllers')));
    await _pumpFrames(tester, 8);
    expect(
      find.byKey(const ValueKey<String>('controllers-settings-screen')),
      findsOneWidget,
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await _pumpFrames(tester, 8);
    expect(find.byKey(const ValueKey<String>('dock-library')), findsOneWidget);
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'dock-controllers',
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(exits, 1);
    expect(find.byKey(const ValueKey<String>('dock-library')), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await _pumpFrames(tester);
    expect(exits, 1);
    expect(find.text('Player One'), findsOneWidget);
    expect(find.byKey(const ValueKey<String>('dock-library')), findsNothing);
  });

  testWidgets('controller opens settings from home and B returns home', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final refreshGate = Completer<ConsumerLoginResult>();
    final tokenStore = FakeRefreshTokenStore();
    await tokenStore.write(
      profileId: 'profile-1',
      serverInstanceId: _serverInstanceId,
      refreshToken: 'stored-token',
    );
    final pad = ConnectedGamepad.fallback(
      id: 'pad-1',
      name: '8BitDo Pro 2',
      order: 0,
    );

    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: Uri.parse(
            RomdEnvironment.defaultConsumerApiOrigin,
          ),
        ),
        consumerApiClientFactory: FakeConsumerApiClientFactory(
          refreshGate: refreshGate,
        ).create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        gamepadLister: () async => <ConnectedGamepad>[pad],
        controllerInputProvider: CallbackControllerInputProvider(
          listGamepads: () async => <ConnectedGamepad>[pad],
          events: () => events.stream,
        ),
      ),
    );
    await tester.pump();

    Future<void> settle([int frames = 12]) async {
      for (var i = 0; i < frames; i++) {
        await tester.pump(const Duration(milliseconds: 50));
      }
    }

    Future<void> press(GamepadButton button) async {
      events.add(_buttonEvent('pad-1', button, 1.0));
      await tester.pump();
      events.add(_buttonEvent('pad-1', button, 0.0));
      await tester.pump();
    }

    expect(find.text('Press L + R to Start'), findsOneWidget);
    final sharedClock = find.byKey(
      const ValueKey<String>('shared-console-clock'),
    );
    expect(sharedClock, findsOneWidget);
    final entryClockElement = tester.element(sharedClock);
    final entryClockPosition = tester.getTopLeft(sharedClock);
    events.add(_buttonEvent('pad-1', GamepadButton.leftBumper, 1.0));
    events.add(_buttonEvent('pad-1', GamepadButton.rightBumper, 1.0));
    await settle();
    expect(find.text('Who’s using 8BitDo Pro 2?'), findsOneWidget);
    expect(find.text('This controller will be Player 1.'), findsOneWidget);
    expect(
      find.byKey(const ValueKey<String>('players-session-cluster')),
      findsNothing,
    );

    final selectionAvatarCenter = tester.getCenter(
      find.byKey(const ValueKey<String>('profile-avatar-profile-1')),
    );
    await press(GamepadButton.a);
    final sharedAvatar = find.byKey(
      const ValueKey<String>('shared-profile-avatar'),
    );
    expect(sharedAvatar, findsOneWidget);
    final sharedConnectionRing = find.byKey(
      const ValueKey<String>('shared-profile-connection'),
    );
    expect(sharedConnectionRing, findsOneWidget);
    expect(
      find.byKey(const ValueKey<String>('home-profile-connection-connecting')),
      findsNothing,
    );
    expect(
      (tester.getCenter(sharedAvatar) - selectionAvatarCenter).distance,
      lessThan(1),
    );
    expect(
      tester.getCenter(sharedConnectionRing),
      tester.getCenter(sharedAvatar),
    );
    final avatarStartSize = tester.getSize(sharedAvatar);

    await tester.pump();
    await tester.pump(const Duration(milliseconds: 180));
    expect(tester.getCenter(sharedAvatar), isNot(selectionAvatarCenter));
    expect(
      tester.getCenter(sharedConnectionRing),
      tester.getCenter(sharedAvatar),
    );
    expect(tester.getSize(sharedAvatar).width, lessThan(avatarStartSize.width));
    expect(
      find.byKey(const ValueKey<String>('home-profile-avatar-placeholder')),
      findsOneWidget,
    );

    refreshGate.complete(ConsumerLoginSuccess(FakeConsumerApiClient.session));
    await settle(30);
    expect(find.byKey(const ValueKey<String>('dock-library')), findsOneWidget);
    expect(find.text('Chrono Trigger'), findsNothing);
    expect(sharedAvatar, findsNothing);
    expect(
      find.byKey(const ValueKey<String>('home-profile-avatar')),
      findsOneWidget,
    );
    expect(sharedClock, findsOneWidget);
    expect(identical(tester.element(sharedClock), entryClockElement), isTrue);
    expect(tester.getTopLeft(sharedClock), isNot(entryClockPosition));

    // The home dock is controller-complete. The empty rail lands focus on
    // the dock's Catalog action; enter the Catalog surface and back out.
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'dock-catalog',
    );
    await press(GamepadButton.a);
    await settle();
    expect(find.text('Featured'), findsOneWidget);

    await press(GamepadButton.b);
    await settle();
    expect(find.text('Featured'), findsNothing);
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'dock-catalog',
    );

    // Open Settings from the dock.
    await press(GamepadButton.dpadRight);
    await press(GamepadButton.dpadRight);
    await press(GamepadButton.a);
    await settle();

    expect(find.text('Settings'), findsOneWidget);
    expect(find.text('Profile & Account'), findsOneWidget);

    await press(GamepadButton.dpadDown);
    await press(GamepadButton.dpadDown);
    await press(GamepadButton.a);
    await settle();

    expect(
      find.byKey(const ValueKey<String>('controllers-settings-screen')),
      findsOneWidget,
    );

    await press(GamepadButton.b);
    await settle();

    expect(find.text('Profile & Account'), findsOneWidget);

    await press(GamepadButton.b);
    await settle();

    expect(find.byKey(const ValueKey<String>('dock-library')), findsOneWidget);
    expect(find.text('Profile & Account'), findsNothing);
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'dock-settings',
    );

    // The global controller shortcut remains live after returning Home.
    await press(GamepadButton.start);
    await settle();
    expect(find.text('PLAYERS'), findsOneWidget);

    await press(GamepadButton.b);
    await settle();
    expect(find.text('PLAYERS'), findsNothing);
    expect(
      find.byKey(const ValueKey<String>('players-session-cluster')),
      findsOneWidget,
    );
  });

  testWidgets('controller start chord seats P1 after profile selection', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();
    final starter = ConnectedGamepad.fallback(
      id: 'pad-1',
      name: '8BitDo Pro 2',
      order: 0,
    );
    Future<List<ConnectedGamepad>> listGamepads() async => <ConnectedGamepad>[
      starter,
    ];

    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: Uri.parse(
            RomdEnvironment.defaultConsumerApiOrigin,
          ),
        ),
        consumerApiClientFactory: FakeConsumerApiClientFactory().create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        controllerSlotClaims: slotClaims,
        gamepadLister: listGamepads,
        controllerInputProvider: CallbackControllerInputProvider(
          listGamepads: listGamepads,
          events: () => events.stream,
        ),
      ),
    );
    await tester.pump();

    expect(find.text('Press L + R to Start'), findsOneWidget);
    events.add(_buttonEvent('pad-1', GamepadButton.a, 1.0));
    await tester.pump();
    expect(find.text('Press L + R to Start'), findsOneWidget);
    events.add(_buttonEvent('pad-1', GamepadButton.dpadRight, 1.0));
    await tester.pump();
    expect(find.text('Press L + R to Start'), findsOneWidget);

    events.add(_buttonEvent('pad-1', GamepadButton.leftBumper, 1.0));
    events.add(_buttonEvent('pad-1', GamepadButton.rightBumper, 1.0));
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(find.text('Who’s using 8BitDo Pro 2?'), findsOneWidget);
    expect(slotClaims.claims.every((claim) => claim == null), isTrue);

    await tester.tap(find.text('Player One'));
    for (var i = 0; i < 20; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(await slotClaims.loadClaims(), <ControllerSlotClaim?>[
      ControllerSlotClaim.fromGamepad(starter),
      null,
      null,
      null,
    ]);
    expect(
      find.text('8BitDo Pro 2 is Player 1 for this session.'),
      findsNothing,
    );
    expect(find.text('Change players'), findsNothing);
    expect(
      find.byKey(const ValueKey<String>('players-session-cluster')),
      findsOneWidget,
    );

    events.add(_buttonEvent('pad-1', GamepadButton.start, 1.0));
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.text('PLAYERS'), findsOneWidget);
    expect(find.text('Playing as Player One'), findsOneWidget);
    expect(find.text('Switch primary profile'), findsOneWidget);
  });

  testWidgets('controller start chord stores exact P1 from active lister', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();
    final fallbackStarter = ConnectedGamepad.fallback(
      id: 'pad-1',
      name: '8BitDo Pro 2',
      order: 0,
    );
    const exactStarter = ConnectedGamepad(
      id: 'pad-1',
      order: 0,
      identity: ControllerIdentity(
        displayName: '8BitDo Pro 2',
        sdlGuid: '03000000c82d000012ab000000006800',
        serial: 'starter-serial',
      ),
    );

    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: Uri.parse(
            RomdEnvironment.defaultConsumerApiOrigin,
          ),
        ),
        consumerApiClientFactory: FakeConsumerApiClientFactory().create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        controllerSlotClaims: slotClaims,
        gamepadLister: () async => const <ConnectedGamepad>[exactStarter],
        enableGamepad: false,
        controllerInputProvider: CallbackControllerInputProvider(
          listGamepads: () async => <ConnectedGamepad>[fallbackStarter],
          events: () => events.stream,
        ),
      ),
    );
    await tester.pump();

    events.add(_buttonEvent('pad-1', GamepadButton.leftBumper, 1.0));
    events.add(_buttonEvent('pad-1', GamepadButton.rightBumper, 1.0));
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(find.text('Who’s using 8BitDo Pro 2?'), findsOneWidget);
    expect(slotClaims.claims.every((claim) => claim == null), isTrue);

    await tester.tap(find.text('Player One'));
    for (var i = 0; i < 20; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(await slotClaims.loadClaims(), <ControllerSlotClaim?>[
      ControllerSlotClaim.fromGamepad(exactStarter),
      null,
      null,
      null,
    ]);
    expect(
      find.text('8BitDo Pro 2 is Player 1 for this session.'),
      findsNothing,
    );
  });

  testWidgets('backing out after controller start discards the pending claim', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    final slotClaims = SessionControllerSlotClaims();
    final starter = ConnectedGamepad.fallback(
      id: 'pad-1',
      name: '8BitDo Pro 2',
      order: 0,
    );
    Future<List<ConnectedGamepad>> listGamepads() async => <ConnectedGamepad>[
      starter,
    ];

    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: Uri.parse(
            RomdEnvironment.defaultConsumerApiOrigin,
          ),
        ),
        consumerApiClientFactory: FakeConsumerApiClientFactory().create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        controllerSlotClaims: slotClaims,
        gamepadLister: listGamepads,
        enableGamepad: false,
        controllerInputProvider: CallbackControllerInputProvider(
          listGamepads: listGamepads,
          events: () => events.stream,
        ),
      ),
    );
    await tester.pump();

    events.add(_buttonEvent('pad-1', GamepadButton.leftBumper, 1.0));
    events.add(_buttonEvent('pad-1', GamepadButton.rightBumper, 1.0));
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.text('Who’s using 8BitDo Pro 2?'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.text('Press L + R to Start'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.space);
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    await tester.tap(find.text('Player One'));
    for (var i = 0; i < 20; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(slotClaims.claims.every((claim) => claim == null), isTrue);
  });

  testWidgets('signs in through the device flow and renders the home shell', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    final environment = RomdEnvironment(
      consumerApiOrigin: Uri.parse(RomdEnvironment.defaultConsumerApiOrigin),
    );
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: environment,
        consumerApiClientFactory: FakeConsumerApiClientFactory().create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );
    await tester.pump();

    // Attract screen: any tap (or key) begins the session.
    expect(find.text('Press L + R to Start'), findsOneWidget);
    await tester.tap(find.text('Press L + R to Start'));
    await tester.pump(); // apply onStart, begin the Quiet Handoff
    await tester.pump(const Duration(milliseconds: 500)); // finish the handoff
    await tester.pump(); // resolve the profiles future
    await tester.pump(); // build the profile grid

    expect(find.text("Who's playing?"), findsOneWidget);
    await tester.tap(find.text('Player One'));

    // Local-first: selecting a profile lands straight in the launcher with no
    // session. Nothing is installed and no token is stored, so it stays offline
    // and the empty state invites (does not force) a connection.
    for (var i = 0; i < 20; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(
      find.byKey(const ValueKey<String>('home-rail-skeleton')),
      findsNWidgets(5),
    );
    expect(find.textContaining('Games you play'), findsNothing);
    expect(
      find.byKey(const ValueKey<String>('connection-status-connected')),
      findsNothing,
    );

    // Connect explicitly from the launcher: device authorization -> the 1ms poll
    // succeeds -> account link, while Home remains local-first.
    await tester.tap(_connectControl);
    for (var i = 0; i < 30; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(
      find.byKey(const ValueKey<String>('connection-status-connected')),
      findsNothing,
    );
    expect(_connectControl, findsNothing);
    expect(find.byKey(const ValueKey<String>('dock-library')), findsOneWidget);
    expect(find.text('Chrono Trigger'), findsNothing);
  });

  testWidgets('returning user: silent restore keeps Home local-first', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    final environment = RomdEnvironment(
      consumerApiOrigin: Uri.parse(RomdEnvironment.defaultConsumerApiOrigin),
    );

    // A stored refresh token for this profile's server triggers a silent
    // restore on selection; the gate holds it in flight so we can observe the
    // launcher while connecting.
    final refreshGate = Completer<ConsumerLoginResult>();
    final tokenStore = FakeRefreshTokenStore();
    await tokenStore.write(
      profileId: 'profile-1',
      serverInstanceId: _serverInstanceId,
      refreshToken: 'stored-token',
    );

    await tester.pumpWidget(
      RomdConsoleApp(
        environment: environment,
        consumerApiClientFactory: FakeConsumerApiClientFactory(
          refreshGate: refreshGate,
        ).create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );
    await tester.pump();

    await tester.tap(find.text('Press L + R to Start'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));
    await tester.pump();
    await tester.pump();
    await tester.tap(find.text('Player One'));
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    // While restore is in flight the local Home remains available; connecting
    // status must not replace its empty state.
    expect(
      find.byKey(const ValueKey<String>('home-rail-skeleton')),
      findsNWidgets(5),
    );
    expect(find.textContaining('Games you play'), findsNothing);
    expect(find.byKey(const ValueKey<String>('dock-library')), findsOneWidget);

    // Resolve the restore: the session appears in place without replacing Home
    // with online discovery.
    refreshGate.complete(ConsumerLoginSuccess(FakeConsumerApiClient.session));
    for (var i = 0; i < 30; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(
      find.byKey(const ValueKey<String>('connection-status-connected')),
      findsNothing,
    );
    expect(_connectControl, findsNothing);
    expect(find.byKey(const ValueKey<String>('dock-library')), findsOneWidget);
    expect(find.text('Chrono Trigger'), findsNothing);
  });

  testWidgets('bootstrap failures cannot reach device authorization', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async => tester.binding.setSurfaceSize(null));

    final tokenStore = FakeRefreshTokenStore()
      ..legacyCleanupError = StateError('secure storage unavailable');
    final api = FakeConsumerApiClientFactory();
    var discoveryCount = 0;
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: (origin) {
          discoveryCount++;
          return _successfulDiscoveryFactory(origin);
        },
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    expect(discoveryCount, 0);
    expect(api.deviceAuthorizationCount, 0);
    expect(find.textContaining('Secure sign-in cleanup'), findsOneWidget);

    await tester.tap(_connectControl);
    await _pumpFrames(tester);
    expect(discoveryCount, 0);
    expect(api.deviceAuthorizationCount, 0);
    expect(find.text('Preparing secure sign-in…'), findsNothing);
  });

  testWidgets('discovery failure keeps fresh login unreachable', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async => tester.binding.setSurfaceSize(null));

    final tokenStore = FakeRefreshTokenStore();
    final api = FakeConsumerApiClientFactory();
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: (_) => _FakeServerDiscoveryApiClient(
          result: Future<ServerDiscoveryResult>.value(
            const ServerDiscoveryFailure(ServerDiscoveryFailureKind.transport),
          ),
        ),
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    await tester.tap(_connectControl);
    await _pumpFrames(tester);

    expect(tokenStore.calls.where((call) => call.startsWith('read:')), isEmpty);
    expect(api.deviceAuthorizationCount, 0);
    expect(find.text('Preparing secure sign-in…'), findsNothing);
    expect(
      find.textContaining('identity could not be verified'),
      findsOneWidget,
    );
  });

  testWidgets('explicit Connect bootstraps before fresh device flow', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async => tester.binding.setSurfaceSize(null));

    final events = <String>[];
    final tokenStore = FakeRefreshTokenStore(calls: events);
    final api = FakeConsumerApiClientFactory(events: events);
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: (origin) =>
            _FakeServerDiscoveryApiClient(
              result: Future<ServerDiscoveryResult>.value(
                ServerDiscoverySuccess(_serverInstanceId),
              ),
              onDiscover: () => events.add('discover:$origin'),
            ),
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    expect(api.refreshCount, 0);
    expect(api.deviceAuthorizationCount, 0);
    events.clear();

    await tester.tap(_connectControl);
    await _pumpFrames(tester);

    expect(events.take(4), <String>[
      'cleanup:profile-1:${RomdServerOrigins.defaultUri}',
      'discover:${RomdServerOrigins.defaultUri}',
      'read:profile-1:${_serverInstanceId.value}',
      'device-authorization',
    ]);
    expect(api.deviceAuthorizationCount, 1);
  });

  testWidgets('server A to B restore uses a fresh B-bound client graph', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async => tester.binding.setSurfaceSize(null));

    final originA = RomdServerOrigins.defaultUri;
    final originB = Uri.parse('https://library-b.example');
    final instanceB = RomdServerInstanceId.tryParse(
      '22222222-2222-4222-8222-222222222222',
    )!;
    final tokenStore = FakeRefreshTokenStore();
    await tokenStore.write(
      profileId: 'profile-1',
      serverInstanceId: instanceB,
      refreshToken: 'token-b',
    );
    tokenStore.calls.clear();
    final refreshGate = Completer<ConsumerLoginResult>()
      ..complete(ConsumerLoginSuccess(FakeConsumerApiClient.session));
    final api = FakeConsumerApiClientFactory(refreshGate: refreshGate);
    final serviceOrigins = <Uri>[];
    final authorities = <InstallAuthorityContext?>[];
    final serviceClosed = <bool>[];
    final activeSessions = <ActiveLaunchSession>[];

    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(consumerApiOrigin: originA),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: (origin) =>
            _FakeServerDiscoveryApiClient(
              result: Future<ServerDiscoveryResult>.value(
                ServerDiscoverySuccess(
                  origin == originB ? instanceB : _serverInstanceId,
                ),
              ),
            ),
        playServicesFactory:
            (
              origin,
              consumerApiClient,
              accessTokenProvider,
              installAuthority,
              activeLaunchSession,
              controllerSlotClaims,
              gamepadLister,
            ) {
              expect(
                (consumerApiClient as FakeConsumerApiClient).origin,
                origin,
              );
              serviceOrigins.add(origin);
              authorities.add(installAuthority);
              activeSessions.add(activeLaunchSession);
              final index = serviceClosed.length;
              serviceClosed.add(false);
              return _fakePlayServices(
                activeLaunchSession: activeLaunchSession,
                controllerSlotClaims: controllerSlotClaims,
                gamepadLister: gamepadLister,
                onClose: () => serviceClosed[index] = true,
              );
            },
        serverOriginPrompt: (_, _) async => originB,
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    await tester.tap(find.byIcon(Icons.settings_outlined));
    await _pumpFrames(tester, 4);
    await _openAccountSettings(tester);
    await _tapSettingAction(tester, 'Change server');
    await _pumpFrames(tester);

    expect(serviceOrigins, <Uri>[originA, originA, originB]);
    expect(authorities.map((authority) => authority?.localProfileId), <String?>[
      'profile-1',
      'profile-1',
      'profile-1',
    ]);
    expect(authorities.map((authority) => authority?.generation), <int?>[
      0,
      2,
      4,
    ]);
    expect(
      authorities.map((authority) => authority?.connection.instanceId),
      <RomdServerInstanceId?>[_serverInstanceId, _serverInstanceId, instanceB],
    );
    expect(serviceClosed, <bool>[true, true, false]);
    expect(activeSessions, hasLength(3));
    expect(
      activeSessions.every(
        (session) => identical(session, activeSessions.first),
      ),
      isTrue,
    );
    expect(api.clients, hasLength(3));
    expect(api.clients.first.origin, originA);
    expect(api.clients.first.refreshTokens, isEmpty);
    expect(api.clients.first.closeCount, 1);
    expect(api.clients.last.origin, originB);
    expect(api.clients.last.refreshTokens, <String>['token-b']);
    expect(api.clients.last.closeCount, 0);
  });

  testWidgets('server A to B fresh login stays on the B-bound client', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async => tester.binding.setSurfaceSize(null));

    final originA = RomdServerOrigins.defaultUri;
    final originB = Uri.parse('https://library-b.example');
    final instanceB = RomdServerInstanceId.tryParse(
      '22222222-2222-4222-8222-222222222222',
    )!;
    final api = FakeConsumerApiClientFactory();
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(consumerApiOrigin: originA),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: (origin) =>
            _FakeServerDiscoveryApiClient(
              result: Future<ServerDiscoveryResult>.value(
                ServerDiscoverySuccess(
                  origin == originB ? instanceB : _serverInstanceId,
                ),
              ),
            ),
        playServicesFactory: _fakePlayServicesFactory(),
        serverOriginPrompt: (_, _) async => originB,
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    await tester.tap(find.byIcon(Icons.settings_outlined));
    await _pumpFrames(tester, 4);
    await _openAccountSettings(tester);
    await _tapSettingAction(tester, 'Change server');
    await _pumpFrames(tester);

    expect(api.clients, hasLength(3));
    expect(api.clients.first.deviceAuthorizationCount, 0);
    expect(api.clients.first.closeCount, 1);
    expect(api.clients.last.origin, originB);
    expect(api.clients.last.deviceAuthorizationCount, 1);
  });

  testWidgets('same server instance moved to B still replaces the A client', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async => tester.binding.setSurfaceSize(null));

    final originA = RomdServerOrigins.defaultUri;
    final originB = Uri.parse('https://moved-library.example');
    final tokenStore = FakeRefreshTokenStore();
    await tokenStore.write(
      profileId: 'profile-1',
      serverInstanceId: _serverInstanceId,
      refreshToken: 'portable-instance-token',
    );
    final refreshGate = Completer<ConsumerLoginResult>()
      ..complete(ConsumerLoginSuccess(FakeConsumerApiClient.session));
    final api = FakeConsumerApiClientFactory(refreshGate: refreshGate);
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(consumerApiOrigin: originA),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: (_) =>
            _successfulDiscoveryFactory(originA),
        playServicesFactory: _fakePlayServicesFactory(),
        serverOriginPrompt: (_, _) async => originB,
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    await tester.tap(find.byIcon(Icons.settings_outlined));
    await _pumpFrames(tester, 4);
    await _openAccountSettings(tester);
    await _tapSettingAction(tester, 'Change server');
    await _pumpFrames(tester);

    expect(api.clients, hasLength(3));
    expect(api.clients.first.origin, originA);
    expect(api.clients[1].refreshTokens, <String>['portable-instance-token']);
    expect(api.clients.first.closeCount, 1);
    expect(api.clients.last.origin, originB);
    expect(api.clients.last.refreshTokens, <String>['refresh-token']);
  });

  testWidgets('same-origin server replacement reads only its new partition', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async => tester.binding.setSurfaceSize(null));

    final replacementInstance = RomdServerInstanceId.tryParse(
      '22222222-2222-4222-8222-222222222222',
    )!;
    final tokenStore = FakeRefreshTokenStore();
    await tokenStore.write(
      profileId: 'profile-1',
      serverInstanceId: replacementInstance,
      refreshToken: 'replacement-token',
    );
    tokenStore.calls.clear();
    final api = FakeConsumerApiClientFactory();
    var discoveryCount = 0;
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: (_) => _FakeServerDiscoveryApiClient(
          result: Future<ServerDiscoveryResult>.value(
            ServerDiscoverySuccess(
              discoveryCount++ == 0 ? _serverInstanceId : replacementInstance,
            ),
          ),
        ),
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    await tester.tap(_connectControl);
    await _pumpFrames(tester);

    expect(api.clients, hasLength(3));
    expect(api.deviceAuthorizationCount, 1);
    expect(tokenStore.calls.where((call) => call.startsWith('read:')), <String>[
      'read:profile-1:${_serverInstanceId.value}',
      'read:profile-1:${replacementInstance.value}',
    ]);
  });

  for (final promptCase in <String>['cancel', 'same origin']) {
    testWidgets('Change server $promptCase recovers a pending silent restore', (
      tester,
    ) async {
      await tester.binding.setSurfaceSize(const Size(1280, 720));
      addTearDown(() async => tester.binding.setSurfaceSize(null));

      final staleRefresh = Completer<ConsumerLoginResult>();
      final tokenStore = FakeRefreshTokenStore();
      await tokenStore.write(
        profileId: 'profile-1',
        serverInstanceId: _serverInstanceId,
        refreshToken: 'stored-token',
      );
      final api = FakeConsumerApiClientFactory(
        refreshResults: <Future<ConsumerLoginResult>>[staleRefresh.future],
      );
      await tester.pumpWidget(
        RomdConsoleApp(
          environment: RomdEnvironment(
            consumerApiOrigin: RomdServerOrigins.defaultUri,
          ),
          consumerApiClientFactory: api.create,
          localProfileRepository: FakeLocalProfileRepository(),
          refreshTokenStore: tokenStore,
          serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
          playServicesFactory: _fakePlayServicesFactory(),
          serverOriginPrompt: (_, _) async =>
              promptCase == 'cancel' ? null : RomdServerOrigins.defaultUri,
          enableGamepad: false,
        ),
      );

      await _selectFirstProfile(tester);
      expect(api.refreshCount, 1);
      final settings = find.byKey(const ValueKey<String>('dock-settings'));
      await tester.ensureVisible(settings);
      await tester.pump();
      await tester.tap(settings);
      await _pumpFrames(tester, 4);
      await _openAccountSettings(tester);
      await _tapSettingAction(tester, 'Change server');
      await _pumpFrames(tester, 8);
      expect(api.refreshCount, 1);

      staleRefresh.complete(
        ConsumerLoginSuccess(FakeConsumerApiClient.session),
      );
      await _pumpFrames(tester, 8);

      expect(find.text('Change server'), findsOneWidget);
      await _selectAccountCategory(tester, 'account');
      expect(find.text('Sign out'), findsOneWidget);
      expect(
        tokenStore.tokenFor('profile-1', _serverInstanceId),
        'refresh-token',
      );
      await tester.sendKeyEvent(LogicalKeyboardKey.escape);
      await _pumpFrames(tester, 4);
      await tester.sendKeyEvent(LogicalKeyboardKey.escape);
      await _pumpFrames(tester, 4);
      expect(find.text('Connecting…'), findsNothing);
      expect(
        find.byKey(const ValueKey<String>('dock-library')),
        findsOneWidget,
      );
    });
  }

  for (final failureCase in <String>['prompt', 'repository update']) {
    testWidgets('Change server recovers when $failureCase throws and retries', (
      tester,
    ) async {
      await tester.binding.setSurfaceSize(const Size(1280, 720));
      addTearDown(() async => tester.binding.setSurfaceSize(null));

      final originB = Uri.parse('https://recovered.example');
      final staleRefresh = Completer<ConsumerLoginResult>();
      final recoveryRefresh = Completer<ConsumerLoginResult>();
      final tokenStore = FakeRefreshTokenStore();
      await tokenStore.write(
        profileId: 'profile-1',
        serverInstanceId: _serverInstanceId,
        refreshToken: 'stored-token',
      );
      final repository = FakeLocalProfileRepository();
      final api = FakeConsumerApiClientFactory(
        refreshResults: <Future<ConsumerLoginResult>>[
          staleRefresh.future,
          recoveryRefresh.future,
        ],
      );
      var promptCount = 0;
      await tester.pumpWidget(
        RomdConsoleApp(
          environment: RomdEnvironment(
            consumerApiOrigin: RomdServerOrigins.defaultUri,
          ),
          consumerApiClientFactory: api.create,
          localProfileRepository: repository,
          refreshTokenStore: tokenStore,
          serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
          playServicesFactory: _fakePlayServicesFactory(),
          serverOriginPrompt: (_, _) async {
            promptCount++;
            if (failureCase == 'prompt' && promptCount == 1) {
              throw StateError('prompt unavailable');
            }
            return originB;
          },
          enableGamepad: false,
        ),
      );

      await _selectFirstProfile(tester);
      if (failureCase == 'repository update') {
        repository.updateOriginErrors.add(StateError('repository unavailable'));
      }
      await tester.tap(find.byIcon(Icons.settings_outlined));
      await _pumpFrames(tester, 4);
      await _openAccountSettings(tester);
      await _tapSettingAction(tester, 'Change server');
      await _pumpFrames(tester, 8);
      expect(api.refreshCount, 1);

      recoveryRefresh.complete(
        const ConsumerLoginFailure('Recovery refresh failed.'),
      );
      await _pumpFrames(tester, 8);
      staleRefresh.complete(
        ConsumerLoginSuccess(FakeConsumerApiClient.session),
      );
      await _pumpFrames(tester, 8);

      expect(
        find.text(
          failureCase == 'prompt'
              ? 'ROMD server settings could not be opened. Try again.'
              : 'ROMD server selection could not be saved. Try again.',
        ),
        findsOneWidget,
      );
      expect(find.text('Change server'), findsOneWidget);
      expect(api.clients, hasLength(2));

      await _tapSettingAction(tester, 'Change server');
      await _pumpFrames(tester, 16);

      expect(api.clients, hasLength(3));
      expect(
        api.clients.take(2).every((client) => client.closeCount == 1),
        isTrue,
      );
      expect(api.clients.last.origin, originB);
      expect(api.clients.last.refreshTokens, hasLength(1));
      expect(api.clients.last.deviceAuthorizationCount, 1);
    });
  }

  testWidgets('remove-server update failure stays coherent and retryable', (
    tester,
  ) async {
    final repository = FakeLocalProfileRepository();
    final api = FakeConsumerApiClientFactory();
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: repository,
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    repository.updateOriginErrors.add(StateError('repository unavailable'));
    await tester.tap(find.byIcon(Icons.settings_outlined));
    await _pumpFrames(tester, 4);
    await _openAccountSettings(tester);
    await _confirmRemoveServer(tester);
    await _pumpFrames(tester, 8);

    expect(
      find.text('ROMD server could not be removed. Try again.'),
      findsOneWidget,
    );
    expect(find.text('Remove server'), findsOneWidget);
    expect(api.clients, hasLength(2));
    expect(api.clients.last.closeCount, 0);

    await _confirmRemoveServer(tester);
    await _pumpFrames(tester, 12);
    expect(api.clients, hasLength(3));
    expect(
      api.clients.take(2).every((client) => client.closeCount == 1),
      isTrue,
    );
    await tester.tap(find.byIcon(Icons.settings_outlined));
    await _pumpFrames(tester, 4);
    await _openAccountSettings(tester);
    await _selectAccountCategory(tester, 'account');
    expect(find.text('Add ROMD server'), findsOneWidget);
  });

  testWidgets('remove server publishes neutral graph without credentials', (
    tester,
  ) async {
    final tokenStore = FakeRefreshTokenStore();
    await tokenStore.write(
      profileId: 'profile-1',
      serverInstanceId: _serverInstanceId,
      refreshToken: 'stored-token',
    );
    final refreshGate = Completer<ConsumerLoginResult>()
      ..complete(ConsumerLoginSuccess(FakeConsumerApiClient.session));
    final providers = <ProfileSessionCoordinator?>[];
    final valuesAtGraphCreation = <String?>[];

    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: FakeConsumerApiClientFactory(
          refreshGate: refreshGate,
        ).create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory:
            (
              origin,
              consumerApiClient,
              authenticatedSession,
              installAuthority,
              activeLaunchSession,
              controllerSlotClaims,
              gamepadLister,
            ) {
              providers.add(authenticatedSession);
              valuesAtGraphCreation.add(authenticatedSession?.session?.token);
              return _fakePlayServices(
                activeLaunchSession: activeLaunchSession,
                controllerSlotClaims: controllerSlotClaims,
                gamepadLister: gamepadLister,
              );
            },
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    expect(providers, hasLength(2));
    expect(providers.last?.session?.token, FakeConsumerApiClient.session.token);
    await tester.tap(find.byIcon(Icons.settings_outlined));
    await _pumpFrames(tester, 4);
    await _openAccountSettings(tester);
    await _tapSettingAction(tester, 'Sign out');
    await _pumpFrames(tester, 8);
    expect(providers.every((provider) => provider?.session == null), isTrue);
    await tester.tap(find.byIcon(Icons.settings_outlined));
    await _pumpFrames(tester, 4);
    await _openAccountSettings(tester);
    await _confirmRemoveServer(tester);
    await _pumpFrames(tester, 8);

    expect(valuesAtGraphCreation, <String?>[null, null, null]);
    expect(providers.last?.session, isNull);
    expect(tokenStore.tokenFor('profile-1', _serverInstanceId), isNull);
  });

  testWidgets('local-only attach update failure stays coherent and retryable', (
    tester,
  ) async {
    final originB = Uri.parse('https://attached.example');
    final repository = FakeLocalProfileRepository(
      localOnly: true,
      updateOriginErrors: <Object>[StateError('repository unavailable')],
    );
    final api = FakeConsumerApiClientFactory();
    var discoveryCount = 0;
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: repository,
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: (origin) {
          discoveryCount++;
          return _successfulDiscoveryFactory(origin);
        },
        playServicesFactory: _fakePlayServicesFactory(),
        serverOriginPrompt: (_, _) async => originB,
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    await tester.tap(_connectControl);
    await _pumpFrames(tester, 8);

    expect(
      find.text('ROMD server selection could not be saved. Try again.'),
      findsOneWidget,
    );
    expect(_connectControl, findsAtLeastNWidgets(1));
    expect(discoveryCount, 0);
    expect(api.clients, hasLength(1));
    expect(api.clients.single.closeCount, 0);

    await tester.tap(_connectControl);
    await _pumpFrames(tester);
    expect(discoveryCount, 1);
    expect(api.clients, hasLength(2));
    expect(api.clients.first.closeCount, 1);
    expect(api.clients.last.origin, originB);
    expect(api.clients.last.deviceAuthorizationCount, 1);
  });

  testWidgets('local-only prompt failure stays coherent and retryable', (
    tester,
  ) async {
    final originB = Uri.parse('https://prompt-retry.example');
    final api = FakeConsumerApiClientFactory();
    var promptCount = 0;
    var discoveryCount = 0;
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(localOnly: true),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: (origin) {
          discoveryCount++;
          return _successfulDiscoveryFactory(origin);
        },
        playServicesFactory: _fakePlayServicesFactory(),
        serverOriginPrompt: (_, _) async {
          promptCount++;
          if (promptCount == 1) {
            throw StateError('prompt unavailable');
          }
          return originB;
        },
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    await tester.tap(_connectControl);
    await _pumpFrames(tester, 8);

    expect(
      find.text('ROMD server settings could not be opened. Try again.'),
      findsOneWidget,
    );
    expect(discoveryCount, 0);
    expect(api.clients, hasLength(1));
    expect(api.clients.single.closeCount, 0);

    await tester.tap(_connectControl);
    await _pumpFrames(tester);
    expect(discoveryCount, 1);
    expect(api.clients, hasLength(2));
    expect(api.clients.first.closeCount, 1);
    expect(api.clients.last.origin, originB);
    expect(api.clients.last.deviceAuthorizationCount, 1);
  });

  testWidgets('failed refresh deletes only its exact instance partition', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async => tester.binding.setSurfaceSize(null));

    final otherInstance = RomdServerInstanceId.tryParse(
      '22222222-2222-4222-8222-222222222222',
    )!;
    final tokenStore = FakeRefreshTokenStore();
    await tokenStore.write(
      profileId: 'profile-1',
      serverInstanceId: _serverInstanceId,
      refreshToken: 'selected-token',
    );
    await tokenStore.write(
      profileId: 'profile-1',
      serverInstanceId: otherInstance,
      refreshToken: 'other-token',
    );
    tokenStore.calls.clear();
    final api = FakeConsumerApiClientFactory();
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);

    expect(tokenStore.tokenFor('profile-1', _serverInstanceId), isNull);
    expect(tokenStore.tokenFor('profile-1', otherInstance), 'other-token');
    expect(
      tokenStore.calls.where((call) => call.startsWith('delete:')),
      <String>['delete:profile-1:${_serverInstanceId.value}'],
    );
  });

  testWidgets('local-only profile performs no credential bootstrap work', (
    tester,
  ) async {
    final tokenStore = FakeRefreshTokenStore();
    final api = FakeConsumerApiClientFactory();
    var discoveryCount = 0;
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(localOnly: true),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: (origin) {
          discoveryCount++;
          return _successfulDiscoveryFactory(origin);
        },
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);

    expect(tokenStore.calls, isEmpty);
    expect(discoveryCount, 0);
    expect(api.refreshCount, 0);
    expect(api.deviceAuthorizationCount, 0);
  });

  testWidgets('double Connect opens only one server prompt', (tester) async {
    final promptResult = Completer<Uri?>();
    var promptCount = 0;
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: FakeConsumerApiClientFactory().create,
        localProfileRepository: FakeLocalProfileRepository(localOnly: true),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        serverOriginPrompt: (_, _) {
          promptCount++;
          return promptResult.future;
        },
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    final connect = _connectControl;
    await tester.tap(connect);
    await tester.tap(connect);
    await tester.pump();

    expect(promptCount, 1);
    promptResult.complete(null);
    await _pumpFrames(tester, 4);
  });

  testWidgets('returning to profile selection invalidates an open prompt', (
    tester,
  ) async {
    final promptResult = Completer<Uri?>();
    final repository = FakeLocalProfileRepository(localOnly: true);
    final api = FakeConsumerApiClientFactory();
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: repository,
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        serverOriginPrompt: (_, _) => promptResult.future,
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    await tester.tap(_connectControl);
    await tester.pump();
    await tester.tap(find.byKey(const ValueKey<String>('dock-switchProfile')));
    await _pumpFrames(tester, 4);
    promptResult.complete(Uri.parse('https://late.example'));
    await _pumpFrames(tester, 8);

    expect(find.text("Who's playing?"), findsOneWidget);
    expect(repository.updateOriginCount, 0);
    expect(api.clients, hasLength(1));
    expect(api.clients.every((client) => client.closeCount == 1), isTrue);
  });

  testWidgets(
    'later profile selection wins when markLastUsed completes first',
    (tester) async {
      final firstSelectionGate = Completer<void>();
      final repository = FakeLocalProfileRepository(
        localOnly: true,
        markLastUsedGates: <String, Completer<void>>{
          'profile-a': firstSelectionGate,
        },
      );
      final api = FakeConsumerApiClientFactory();
      await tester.pumpWidget(
        RomdConsoleApp(
          environment: RomdEnvironment(
            consumerApiOrigin: RomdServerOrigins.defaultUri,
          ),
          consumerApiClientFactory: api.create,
          localProfileRepository: repository,
          refreshTokenStore: FakeRefreshTokenStore(),
          serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
          playServicesFactory: _fakePlayServicesFactory(),
          enableGamepad: false,
        ),
      );
      await tester.pump();

      final entry = tester.widget<EntryStage>(find.byType(EntryStage));
      final createdAt = DateTime(2026);
      final profileA = LocalProfile(
        id: 'profile-a',
        displayName: 'Profile A',
        avatarKey: 'default',
        accentColor: 0xff1fbf8f,
        romdServerOrigin: null,
        entryMode: LocalProfileEntryMode.open,
        createdAt: createdAt,
        updatedAt: createdAt,
      );
      final profileB = LocalProfile(
        id: 'profile-b',
        displayName: 'Profile B',
        avatarKey: 'default',
        accentColor: 0xff1fbf8f,
        romdServerOrigin: null,
        entryMode: LocalProfileEntryMode.open,
        createdAt: createdAt,
        updatedAt: createdAt,
      );

      entry.onSelected(profileA);
      entry.onSelected(profileB);
      await _pumpFrames(tester, 8);
      expect(api.clients, hasLength(1));
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await _pumpFrames(tester, 4);
      expect(find.text('Profile B'), findsOneWidget);

      firstSelectionGate.complete();
      await _pumpFrames(tester, 8);
      expect(find.text('Profile B'), findsOneWidget);
      expect(find.text('Profile A'), findsNothing);
      expect(api.clients, hasLength(1));
    },
  );

  for (final action in <String>['Change server', 'Remove server']) {
    testWidgets('$action result cannot reactivate after profile return', (
      tester,
    ) async {
      final updateGate = Completer<void>();
      final repository = FakeLocalProfileRepository(
        updateOriginGate: updateGate,
      );
      final api = FakeConsumerApiClientFactory();
      await tester.pumpWidget(
        RomdConsoleApp(
          environment: RomdEnvironment(
            consumerApiOrigin: RomdServerOrigins.defaultUri,
          ),
          consumerApiClientFactory: api.create,
          localProfileRepository: repository,
          refreshTokenStore: FakeRefreshTokenStore(),
          serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
          playServicesFactory: _fakePlayServicesFactory(),
          serverOriginPrompt: (_, _) async =>
              Uri.parse('https://replacement.example'),
          enableGamepad: false,
        ),
      );

      await _selectFirstProfile(tester);
      await tester.tap(find.byIcon(Icons.settings_outlined));
      await _pumpFrames(tester, 4);
      await _openAccountSettings(tester);
      if (action == 'Remove server') {
        await _confirmRemoveServer(tester);
      } else {
        await _tapSettingAction(tester, action);
      }
      await tester.pump();
      await repository.updateOriginStarted.future;
      await tester.sendKeyEvent(LogicalKeyboardKey.escape);
      await _pumpFrames(tester, 4);
      if (find.text("Who's playing?").evaluate().isEmpty) {
        await _openAccountSettings(tester);
        await _selectAccountCategory(tester, 'profile');
        await _pumpFrames(tester, 12);
        final switchProfile = find.text('Switch profile').hitTestable();
        await tester.ensureVisible(switchProfile);
        await tester.tap(switchProfile);
        await _pumpFrames(tester, 4);
      }
      updateGate.complete();
      await _pumpFrames(tester, 8);

      expect(find.text("Who's playing?"), findsOneWidget);
      expect(api.clients, hasLength(1));
      expect(api.clients.single.closeCount, 1);
    });
  }

  testWidgets('sign-out deletion cannot overtake a reconnect token write', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async => tester.binding.setSurfaceSize(null));

    final deleteGate = Completer<void>();
    final tokenStore = FakeRefreshTokenStore(deleteGate: deleteGate);
    await tokenStore.write(
      profileId: 'profile-1',
      serverInstanceId: _serverInstanceId,
      refreshToken: 'old-token',
    );
    final refreshGate = Completer<ConsumerLoginResult>()
      ..complete(ConsumerLoginSuccess(FakeConsumerApiClient.session));
    final api = FakeConsumerApiClientFactory(refreshGate: refreshGate);
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    expect(find.byKey(const ValueKey<String>('dock-library')), findsOneWidget);
    tokenStore.calls.clear();
    await tester.tap(find.byIcon(Icons.settings_outlined));
    await _pumpFrames(tester, 4);
    await _openAccountSettings(tester);
    await _tapSettingAction(tester, 'Sign out');
    await tester.pump();
    await tokenStore.deleteStarted.future;
    await tester.tap(_connectControl);
    await tester.pump();

    expect(tokenStore.calls, <String>[
      'delete:profile-1:${_serverInstanceId.value}',
    ]);
    deleteGate.complete();
    await _pumpFrames(tester, 40);

    expect(api.deviceAuthorizationCount, 1);
    expect(
      tokenStore.tokenFor('profile-1', _serverInstanceId),
      'refresh-token',
    );
    expect(tokenStore.calls, <String>[
      'delete:profile-1:${_serverInstanceId.value}',
      'cleanup:profile-1:${RomdServerOrigins.defaultUri}',
      'read:profile-1:${_serverInstanceId.value}',
      'write:profile-1:${_serverInstanceId.value}',
    ]);
  });

  testWidgets('disposing during refresh cannot enqueue a stale token write', (
    tester,
  ) async {
    final refreshGate = Completer<ConsumerLoginResult>();
    final tokenStore = FakeRefreshTokenStore();
    await tokenStore.write(
      profileId: 'profile-1',
      serverInstanceId: _serverInstanceId,
      refreshToken: 'old-token',
    );
    tokenStore.calls.clear();
    final api = FakeConsumerApiClientFactory(refreshGate: refreshGate);
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: RomdEnvironment(
          consumerApiOrigin: RomdServerOrigins.defaultUri,
        ),
        consumerApiClientFactory: api.create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: tokenStore,
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        enableGamepad: false,
      ),
    );

    await _selectFirstProfile(tester);
    expect(api.refreshCount, 1);
    await tester.pumpWidget(const SizedBox.shrink());
    refreshGate.complete(ConsumerLoginSuccess(FakeConsumerApiClient.session));
    await tester.pump();

    expect(
      tokenStore.calls.where((call) => call.startsWith('write:')),
      isEmpty,
    );
    expect(api.clients.every((client) => client.closeCount == 1), isTrue);
  });

  for (final stalePhase in _StaleBootstrapPhase.values) {
    testWidgets('profile switch drops stale ${stalePhase.name} result', (
      tester,
    ) async {
      final events = <String>[];
      final cleanupGate = stalePhase == _StaleBootstrapPhase.cleanup
          ? Completer<void>()
          : null;
      final readGate = stalePhase == _StaleBootstrapPhase.tokenRead
          ? Completer<String?>()
          : null;
      final discoveryGate = stalePhase == _StaleBootstrapPhase.discovery
          ? Completer<ServerDiscoveryResult>()
          : null;
      final refreshGate = stalePhase == _StaleBootstrapPhase.refresh
          ? Completer<ConsumerLoginResult>()
          : null;
      final tokenStore = FakeRefreshTokenStore(
        calls: events,
        cleanupGate: cleanupGate,
        readGate: readGate,
      );
      if (readGate == null) {
        await tokenStore.write(
          profileId: 'profile-1',
          serverInstanceId: _serverInstanceId,
          refreshToken: 'stored-token',
        );
      }
      events.clear();
      final api = FakeConsumerApiClientFactory(
        refreshGate: refreshGate,
        events: events,
      );

      await tester.pumpWidget(
        RomdConsoleApp(
          environment: RomdEnvironment(
            consumerApiOrigin: RomdServerOrigins.defaultUri,
          ),
          consumerApiClientFactory: api.create,
          localProfileRepository: FakeLocalProfileRepository(),
          refreshTokenStore: tokenStore,
          serverDiscoveryApiClientFactory: (origin) =>
              _FakeServerDiscoveryApiClient(
                result:
                    discoveryGate?.future ??
                    Future<ServerDiscoveryResult>.value(
                      ServerDiscoverySuccess(_serverInstanceId),
                    ),
                onDiscover: () => events.add('discover:$origin'),
              ),
          playServicesFactory: _fakePlayServicesFactory(),
          enableGamepad: false,
        ),
      );

      await _selectFirstProfile(tester);
      await tester.tap(
        find.byKey(const ValueKey<String>('dock-switchProfile')),
      );
      await _pumpFrames(tester, 8);
      expect(find.text("Who's playing?"), findsOneWidget);

      final replacementInstance = RomdServerInstanceId.tryParse(
        '33333333-3333-4333-8333-333333333333',
      )!;
      if (stalePhase == _StaleBootstrapPhase.refresh) {
        await tokenStore.write(
          profileId: 'profile-1',
          serverInstanceId: replacementInstance,
          refreshToken: 'replacement-token',
        );
        events.clear();
      }

      switch (stalePhase) {
        case _StaleBootstrapPhase.cleanup:
          cleanupGate!.complete();
        case _StaleBootstrapPhase.discovery:
          discoveryGate!.complete(ServerDiscoverySuccess(_serverInstanceId));
        case _StaleBootstrapPhase.tokenRead:
          readGate!.complete('stored-token');
        case _StaleBootstrapPhase.refresh:
          refreshGate!.complete(
            ConsumerLoginSuccess(FakeConsumerApiClient.session),
          );
      }
      await _pumpFrames(tester, 12);

      expect(find.text("Who's playing?"), findsOneWidget);
      expect(find.text('Preparing secure sign-in…'), findsNothing);
      expect(
        find.byKey(const ValueKey<String>('connection-status-connected')),
        findsNothing,
      );
      expect(api.deviceAuthorizationCount, 0);
      switch (stalePhase) {
        case _StaleBootstrapPhase.cleanup:
          expect(
            events.where((event) => event.startsWith('discover:')),
            isEmpty,
          );
        case _StaleBootstrapPhase.discovery:
          expect(events.where((event) => event.startsWith('read:')), isEmpty);
        case _StaleBootstrapPhase.tokenRead:
          expect(api.refreshCount, 0);
        case _StaleBootstrapPhase.refresh:
          expect(events.where((event) => event.startsWith('write:')), isEmpty);
          expect(
            tokenStore.tokenFor('profile-1', replacementInstance),
            'replacement-token',
          );
          expect(events.where((event) => event.startsWith('delete:')), isEmpty);
      }
    });
  }

  testWidgets('offline: a profile with an install plays without connecting', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    final environment = RomdEnvironment(
      consumerApiOrigin: Uri.parse(RomdEnvironment.defaultConsumerApiOrigin),
    );
    await tester.pumpWidget(
      RomdConsoleApp(
        environment: environment,
        consumerApiClientFactory: FakeConsumerApiClientFactory().create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(
          installed: <LocalInstall>[_installRecord],
        ),
        enableGamepad: false,
      ),
    );
    await tester.pump();

    await tester.tap(find.text('Press L + R to Start'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));
    await tester.pump();
    await tester.pump();

    expect(find.text("Who's playing?"), findsOneWidget);
    await tester.tap(find.text('Player One'));

    // No token, no network — Home stays local-first and points installed-only
    // profiles at Library.
    for (var i = 0; i < 20; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(
      find.byKey(const ValueKey<String>('home-rail-skeleton')),
      findsNWidgets(5),
    );
    expect(find.textContaining('Games you play'), findsNothing);
    await tester.tap(find.byKey(const ValueKey<String>('dock-library')));
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.text('Chrono Trigger'), findsAtLeastNWidgets(1));
    expect(
      find.byKey(const ValueKey<String>('connection-status-connected')),
      findsNothing,
    );
  });

  testWidgets('keyboard stays live across attract <-> selection transitions', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    final environment = RomdEnvironment(
      consumerApiOrigin: Uri.parse(RomdEnvironment.defaultConsumerApiOrigin),
    );
    final slotClaims = SessionControllerSlotClaims.fromClaims(
      const <ControllerSlotClaim?>[
        ControllerSlotClaim(displayName: 'Old Player One', providerId: 'old'),
      ],
    );

    await tester.pumpWidget(
      RomdConsoleApp(
        environment: environment,
        consumerApiClientFactory: FakeConsumerApiClientFactory().create,
        localProfileRepository: FakeLocalProfileRepository(),
        refreshTokenStore: FakeRefreshTokenStore(),
        serverDiscoveryApiClientFactory: _successfulDiscoveryFactory,
        playServicesFactory: _fakePlayServicesFactory(),
        controllerSlotClaims: slotClaims,
        enableGamepad: false,
      ),
    );
    await tester.pump();

    // Fixed pumps everywhere — the ambient ticker never settles.
    Future<void> settle() async {
      for (var i = 0; i < 12; i++) {
        await tester.pump(const Duration(milliseconds: 50));
      }
    }

    ConsoleAmbientBackground ambient() =>
        tester.widget<ConsoleAmbientBackground>(
          find.byType(ConsoleAmbientBackground),
        );

    // Attract -> selection via a key press (no tap), proving attract has focus.
    expect(find.text('Press L + R to Start'), findsOneWidget);
    expect(ambient().effectIntensity, 0.25);
    expect(
      find.byKey(const ValueKey<String>('players-status-row')),
      findsNothing,
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.gameButtonA);
    await tester.pump();
    expect(find.text('Press L + R to Start'), findsOneWidget);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    expect(find.text('Press L + R to Start'), findsOneWidget);
    await tester.sendKeyEvent(LogicalKeyboardKey.space);
    await settle();
    expect(find.text("Who's playing?"), findsOneWidget);
    expect(ambient().effectIntensity, 1);
    expect(
      find.byKey(const ValueKey<String>('players-session-cluster')),
      findsNothing,
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.f2);
    await tester.pump();
    expect(find.text('PLAYERS'), findsNothing);
    expect(slotClaims.claimDetails.first, isNotNull);

    // Selection -> attract via Esc with no prior tap. This only works if the
    // freshly-transitioned selection screen grabbed keyboard focus.
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await settle();
    expect(find.text('Press L + R to Start'), findsOneWidget);
    expect(ambient().effectIntensity, 0.25);
    expect(
      find.byKey(const ValueKey<String>('players-session-cluster')),
      findsNothing,
    );
    expect(slotClaims.claimDetails.every((claim) => claim == null), isTrue);

    // Attract -> selection again via a key, proving the re-shown attract screen
    // grabbed focus after the second transition.
    await tester.sendKeyEvent(LogicalKeyboardKey.space);
    await settle();
    expect(find.text("Who's playing?"), findsOneWidget);
    expect(ambient().effectIntensity, 1);
    expect(
      find.byKey(const ValueKey<String>('players-session-cluster')),
      findsNothing,
    );
  });

  testWidgets(
    'entry stage re-resolves artwork, layout, and motion while mounted',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1280, 720));
      addTearDown(() async {
        await tester.binding.setSurfaceSize(null);
      });
      final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
      addTearDown(mode.dispose);

      await tester.pumpWidget(
        ValueListenableBuilder<ThemeMode>(
          valueListenable: mode,
          builder: (context, themeMode, _) => MaterialApp(
            theme: RomdSkins.seamTestTheme(),
            darkTheme: RomdSkins.baselineDark(),
            themeMode: themeMode,
            themeAnimationDuration: Duration.zero,
            home: EntryStage(
              repository: FakeLocalProfileRepository(),
              defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
              onSelected: (_) {},
              onStartedChanged: (_) {},
            ),
          ),
        ),
      );
      await tester.pump();

      final stageElement = tester.element(find.byType(EntryStage));
      final darkPrompt = tester.widget<Text>(
        find.byKey(const ValueKey<String>('entry-start-prompt')),
      );
      final rompFinder = find.byKey(const ValueKey<String>('entry-romp'));
      final darkRompSize = tester.getSize(rompFinder);
      final darkBrandTransform = tester.widget<Transform>(
        find.byKey(const ValueKey<String>('entry-brand-transform')),
      );

      mode.value = ThemeMode.light;
      await tester.pump();

      final testPrompt = tester.widget<Text>(
        find.byKey(const ValueKey<String>('entry-start-prompt')),
      );
      final testRompSize = tester.getSize(rompFinder);
      final testBrandTransform = tester.widget<Transform>(
        find.byKey(const ValueKey<String>('entry-brand-transform')),
      );
      expect(
        identical(stageElement, tester.element(find.byType(EntryStage))),
        isTrue,
      );
      expect(testPrompt.style, isNot(darkPrompt.style));
      expect(testRompSize, isNot(darkRompSize));
      expect(
        testBrandTransform.transform.getTranslation().y,
        isNot(darkBrandTransform.transform.getTranslation().y),
      );

      await tester.tap(
        find.byKey(const ValueKey<String>('entry-start-prompt')),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 600));
      final midTransition = tester.widget<Transform>(
        find.byKey(const ValueKey<String>('entry-profile-prompt-transform')),
      );
      expect(
        midTransition.transform.getTranslation().y,
        isNot(closeTo(-214, 0.01)),
        reason:
            'the mounted controller picked up the 980 ms test-skin duration',
      );

      await tester.pump(const Duration(milliseconds: 400));
      final completedTransition = tester.widget<Transform>(
        find.byKey(const ValueKey<String>('entry-profile-prompt-transform')),
      );
      expect(
        completedTransition.transform.getTranslation().y,
        closeTo(-214, 0.01),
      );
    },
  );

  testWidgets('entry stage reduced motion hands off immediately', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async => tester.binding.setSurfaceSize(null));
    LocalProfile? selected;

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context).copyWith(disableAnimations: true),
          child: child!,
        ),
        home: EntryStage(
          repository: FakeLocalProfileRepository(),
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          onSelected: (profile) => selected = profile,
          onStartedChanged: (_) {},
        ),
      ),
    );
    await tester.pump();
    await tester.pump();

    await tester.tap(find.text('Press L + R to Start'));
    await tester.pump();
    await tester.pump();

    expect(find.text("Who's playing?"), findsOneWidget);
    await tester.tap(find.text('Player One'));
    await tester.pump();
    expect(selected?.displayName, 'Player One');
  });

  testWidgets('controller start chord stays live after backing to attract', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    final events = StreamController<NormalizedGamepadEvent>.broadcast(
      sync: true,
    );
    addTearDown(events.close);
    var startCount = 0;
    final starter = ConnectedGamepad.fallback(
      id: 'pad-1',
      name: '8BitDo Pro 2',
      order: 0,
    );
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: EntryStage(
          repository: FakeLocalProfileRepository(),
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          controllerInputProvider: CallbackControllerInputProvider(
            listGamepads: () async => <ConnectedGamepad>[starter],
            events: () => events.stream,
          ),
          onControllerStarted: (_) => startCount++,
          onSelected: (_) {},
          onStartedChanged: (_) {},
        ),
      ),
    );
    await tester.pump();

    Future<void> settle() async {
      for (var i = 0; i < 12; i++) {
        await tester.pump(const Duration(milliseconds: 50));
      }
    }

    Future<void> pressStartChord() async {
      events.add(_buttonEvent('pad-1', GamepadButton.leftBumper, 1.0));
      events.add(_buttonEvent('pad-1', GamepadButton.rightBumper, 1.0));
      await settle();
    }

    expect(find.text('Press L + R to Start'), findsOneWidget);
    await pressStartChord();
    expect(find.text('Who’s using 8BitDo Pro 2?'), findsOneWidget);
    expect(startCount, 1);

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await settle();
    expect(find.text('Press L + R to Start'), findsOneWidget);

    await pressStartChord();
    expect(find.text('Who’s using 8BitDo Pro 2?'), findsOneWidget);
    expect(startCount, 2);
  });

  testWidgets('entry stage opened in the selection state focuses the carousel', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    // Mirrors backing out of login: the stage remounts already in selection
    // (initialStarted) and must hand keyboard focus to the carousel without any
    // tap — this path is not exercised by the attract <-> selection test.
    LocalProfile? selected;
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: EntryStage(
          repository: FakeLocalProfileRepository(),
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          initialStarted: true,
          onSelected: (profile) => selected = profile,
          onStartedChanged: (_) {},
        ),
      ),
    );

    // Fixed pumps — the attract prompt's pulse ticker never settles.
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.text("Who's playing?"), findsOneWidget);

    // Enter activates the focused profile, proving the carousel holds focus.
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(selected?.displayName, 'Player One');
  });
}

PlayServices _fakePlayServices({
  List<LocalInstall> installed = const <LocalInstall>[],
  SessionControllerSlotClaims? controllerSlotClaims,
  GamepadLister? gamepadLister,
  ActiveLaunchSession? activeLaunchSession,
  VoidCallback? onClose,
}) => PlayServices(
  install: _FakeInstallService(installed),
  localLibrary: FakeProfileLocalLibraryRepository(
    library: ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
      for (final install in installed)
        ProfileLocalLibraryTitle(
          install: install,
          authorization: ProfileGameAuthorization.authorized,
          state: ProfileLocalLibraryState.ready,
          releaseCount: 1,
          acquiredAt: install.installedAt,
          lastPlayedAt: null,
          playCount: 0,
        ),
    ]),
  ),
  playHistory: RecordingProfilePlayHistoryRepository(),
  coordinator: _FakeCoordinator(),
  runtimeResolver: const LocalRuntimeResolver(),
  runtimeRules: const _NoopRules(),
  activeLaunchSession: activeLaunchSession ?? ActiveLaunchSession(),
  controllerPreferences: InMemoryControllerPreferences(),
  controllerSlotClaims: controllerSlotClaims ?? SessionControllerSlotClaims(),
  gamepadLister: gamepadLister ?? (() async => const <ConnectedGamepad>[]),
  onClose: onClose,
);

RomdConsolePlayServicesFactory _fakePlayServicesFactory({
  List<LocalInstall> installed = const <LocalInstall>[],
}) =>
    (
      origin,
      consumerApiClient,
      accessTokenProvider,
      installAuthority,
      activeLaunchSession,
      controllerSlotClaims,
      gamepadLister,
    ) => _fakePlayServices(
      installed: installed,
      activeLaunchSession: activeLaunchSession,
      controllerSlotClaims: controllerSlotClaims,
      gamepadLister: gamepadLister,
    );

final class _NoopRules implements RuntimeOverrideRuleRepository {
  const _NoopRules();

  @override
  Future<void> setRule({
    required RuntimeOverrideScope scope,
    required String scopeValue,
    required RuntimeProfileId profileId,
  }) async {}

  @override
  Future<List<RuntimeOverrideRule>> rulesMatching({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) async => const <RuntimeOverrideRule>[];
}

final class _FakeCoordinator implements PlayCoordinator {
  @override
  Stream<PlayProgress> play(
    PlayRequest request, {
    ReviewedLaunchSnapshot? controllerSnapshot,
  }) => Stream<PlayProgress>.value(const PlayCompleted(LaunchExited(0)));

  @override
  void close() {}
}

final class _FakeInstallService implements InstallService {
  _FakeInstallService([List<LocalInstall> initial = const <LocalInstall>[]])
    : _installed = initial;

  final StreamController<List<LocalInstall>> _changes =
      StreamController<List<LocalInstall>>.broadcast();
  List<LocalInstall> _installed;

  @override
  Future<LocalInstall?> findInstall(String releaseId) async =>
      _findInstall(releaseId);

  @override
  Future<List<LocalInstall>> listInstalled() async => _installed;

  @override
  Future<List<LocalInstall>> listInstalledForTitle(String titleId) async =>
      _installed.where((i) => i.titleId == titleId).toList(growable: false);

  @override
  Stream<List<LocalInstall>> watchInstalled() =>
      _watchInstalled(() => _installed);

  @override
  Stream<List<LocalInstall>> watchInstalledForTitle(String titleId) =>
      _watchInstalled(
        () => _installed
            .where((i) => i.titleId == titleId)
            .toList(growable: false),
      );

  @override
  Stream<Set<String>> watchInstalledReleaseIds() => watchInstalled().map(
    (installs) => installs.map((i) => i.releaseId).toSet(),
  );

  @override
  Stream<InstallProgress> install(
    PlayTarget target, {
    required InstallOperationLease operation,
    String? accessToken,
  }) async* {
    yield const InstallStarted();
    _installed = <LocalInstall>[_installRecord];
    _changes.add(_installed);
    yield const InstallCompleted(_resolved);
  }

  @override
  Future<void> uninstall(
    String releaseId, {
    InstallOperationLease? operation,
  }) async {
    _installed = _installed
        .where((install) => install.releaseId != releaseId)
        .toList(growable: false);
    _changes.add(_installed);
  }

  Stream<List<LocalInstall>> _watchInstalled(
    List<LocalInstall> Function() current,
  ) async* {
    yield current();
    await for (final _ in _changes.stream) {
      yield current();
    }
  }

  LocalInstall? _findInstall(String releaseId) {
    for (final install in _installed) {
      if (install.releaseId == releaseId) {
        return install;
      }
    }
    return null;
  }
}

const _resolved = ResolvedPlayTarget(
  releaseId: 'release-1',
  titleId: 'title-1',
  platformShortName: 'snes',
  displayName: 'Chrono Trigger',
  localProfileId: 'profile-1',
  contentRoot: '/tmp/content',
  launchAbsolutePath: '/tmp/content/chrono.sfc',
  saveRoot: '/tmp/saves',
  stateRoot: '/tmp/states',
  configRoot: '/tmp/config',
);

final _installRecord = LocalInstall(
  serverInstanceId: '11111111-1111-4111-8111-111111111111',
  releaseId: 'release-1',
  titleId: 'title-1',
  titleName: 'Chrono Trigger',
  platformId: 'platform-snes',
  platformName: 'SNES',
  platformShortName: 'snes',
  coverUrl: null,
  releaseName: 'Chrono Trigger',
  releaseRevision: null,
  contentRoot: '/tmp/content',
  launchRelativePath: 'chrono.sfc',
  sizeBytes: 4194304,
  primarySha256: null,
  manifestFingerprint: 'fp',
  state: InstallState.installed,
  installMode: 'permanent',
  items: const <InstalledItem>[
    InstalledItem(relativePath: 'chrono.sfc', sizeBytes: 4194304, sha256: null),
  ],
  installedAt: DateTime.utc(2026, 6, 30),
  lastPlayedAt: null,
);

final class FakeConsumerApiClientFactory {
  FakeConsumerApiClientFactory({
    this.refreshGate,
    List<Future<ConsumerLoginResult>>? refreshResults,
    List<String>? events,
  }) : refreshResults = refreshResults ?? <Future<ConsumerLoginResult>>[],
       events = events ?? <String>[];

  final Completer<ConsumerLoginResult>? refreshGate;
  final List<Future<ConsumerLoginResult>> refreshResults;
  final List<String> events;
  final List<FakeConsumerApiClient> clients = <FakeConsumerApiClient>[];

  ConsumerApiClient create(Uri origin) {
    final client = FakeConsumerApiClient(
      origin: origin,
      refreshGate: refreshGate,
      refreshResults: refreshResults,
      events: events,
    );
    clients.add(client);
    return client;
  }

  FakeConsumerApiClient get latest => clients.last;
  int get deviceAuthorizationCount => clients.fold(
    0,
    (count, client) => count + client.deviceAuthorizationCount,
  );
  int get refreshCount =>
      clients.fold(0, (count, client) => count + client.refreshCount);
}

final class FakeConsumerApiClient implements ConsumerApiClient {
  @override
  Future<void> revokeSession({required String refreshToken}) async {}

  FakeConsumerApiClient({
    required this.origin,
    required this.refreshResults,
    this.refreshGate,
    List<String>? events,
  }) : events = events ?? <String>[];

  /// When set, [refreshSession] returns this gate's future instead of an
  /// immediate failure — lets a test hold a silent restore in flight to observe
  /// the "connecting" launcher state before the session resolves.
  final Uri origin;
  final Completer<ConsumerLoginResult>? refreshGate;
  final List<Future<ConsumerLoginResult>> refreshResults;
  final List<String> events;
  final List<String> refreshTokens = <String>[];
  int deviceAuthorizationCount = 0;
  int refreshCount = 0;
  int closeCount = 0;

  static final _session = ConsumerLoginSession(
    token: 'access-token',
    tokenType: 'Bearer',
    refreshToken: 'refresh-token',
    expiresAt: DateTime(2999),
    account: const ConsumerAccount(
      id: 'romd-user-1',
      username: 'player',
      email: 'player@example.com',
    ),
  );

  static ConsumerLoginSession get session => _session;

  @override
  Future<ConsumerHostHealth> getHealth() async =>
      const ConsumerHostHealth(status: 'Healthy');

  @override
  Future<ConsoleGamePage> searchCatalog({
    required String accessToken,
    String? query,
    String? platformId,
    String? completeness,
    String? sortBy,
    String? cursor,
    int limit = 48,
  }) async => ConsoleGamePage(
    items: <ConsoleGame>[
      ConsoleGame(
        id: 'title-1',
        platformId: 'platform-snes',
        platformName: 'SNES',
        title: 'Chrono Trigger',
        releaseDate: DateTime(1995),
        coverUrl: null,
        genre: 'RPG',
        rating: 9.4,
        releaseCount: 1,
        defaultReleaseId: 'release-1',
      ),
    ],
    nextCursor: null,
    hasNextPage: false,
  );

  @override
  Future<ConsolePlatformPage> listPlatforms({
    required String accessToken,
    String? cursor,
    int limit = 50,
  }) async => const ConsolePlatformPage(
    items: <ConsolePlatform>[
      ConsolePlatform(
        id: 'platform-snes',
        name: 'SNES',
        shortName: 'SNES',
        manufacturer: 'Nintendo',
        titleCount: 1,
        coverUrl: null,
      ),
    ],
    nextCursor: null,
    hasNextPage: false,
  );

  @override
  Future<List<ConsoleCollection>> listCollections({
    required String accessToken,
    String? platformId,
  }) async => const <ConsoleCollection>[
    ConsoleCollection(
      id: 'collection-1',
      name: 'Favorites',
      description: 'Curated RPG picks',
      platformId: null,
      platformName: null,
      coverUrl: null,
      heroUrl: null,
      itemCount: 1,
    ),
  ];

  @override
  Future<ConsoleGamePage> listCollectionTitles({
    required String accessToken,
    required String collectionId,
    String? cursor,
    int limit = 24,
  }) async => ConsoleGamePage(
    items: <ConsoleGame>[
      ConsoleGame(
        id: 'title-1',
        platformId: 'platform-snes',
        platformName: 'SNES',
        title: 'Chrono Trigger',
        releaseDate: DateTime(1995),
        coverUrl: null,
        genre: 'RPG',
        rating: 9.4,
        releaseCount: 1,
        defaultReleaseId: 'release-1',
      ),
    ],
    nextCursor: null,
    hasNextPage: false,
  );

  @override
  Future<ConsoleGameDetail> getTitle({
    required String accessToken,
    required String titleId,
  }) async => ConsoleGameDetail(
    id: titleId,
    platformId: 'platform-snes',
    platformName: 'SNES',
    title: 'Chrono Trigger',
    description: 'A time-spanning RPG adventure.',
    publisher: 'Square',
    developer: 'Square',
    genre: 'RPG',
    releaseDate: DateTime(1995),
    players: 1,
    rating: 9.4,
    media: const <ConsoleMediaRef>[],
    releases: const <ConsoleRelease>[
      ConsoleRelease(
        id: 'release-1',
        name: 'Chrono Trigger',
        revision: null,
        regions: <String>['USA'],
        languages: <String>['English'],
        sizeBytes: 4194304,
        isComplete: true,
      ),
    ],
    defaultReleaseId: 'release-1',
  );

  @override
  Future<ConsoleReleaseManifest> issueReleaseManifest({
    required String accessToken,
    required String releaseId,
  }) async => ConsoleReleaseManifest(
    releaseId: releaseId,
    titleId: 'title-1',
        systemKey: 'snes',
    name: 'Chrono Trigger',
    revision: null,
    isComplete: true,
    runtime: const ConsoleReleaseRuntime(
      contentType: 'single_rom',
      launch: ConsoleLaunchTarget(type: 'file', relativePath: 'chrono.sfc'),
      packaging: 'direct_files',
      minimumInstallBytes: 4194304,
    ),
    items: const <ConsoleReleaseManifestItem>[
      ConsoleReleaseManifestItem(
        relativePath: 'chrono.sfc',
        role: 'rom',
        sizeBytes: 4194304,
        sha256: 'abc123',
        isAvailable: true,
        downloadUrl: null,
      ),
    ],
  );

  @override
  Future<List<BiosFileListing>> listPlatformBios({
    required String accessToken,
    required String platformShortName,
  }) async => const <BiosFileListing>[];

  @override
  Future<DeviceAuthorizationResult> requestDeviceAuthorization() async {
    deviceAuthorizationCount++;
    events.add('device-authorization');
    return DeviceAuthorizationSuccess(
      DeviceAuthorization(
        deviceCode: 'device-code-1',
        userCode: 'WXYZ-1234',
        verificationUri: Uri.parse('http://localhost/connect/verify'),
        interval: const Duration(milliseconds: 1),
        expiresAt: DateTime(2999),
      ),
    );
  }

  @override
  Future<DeviceTokenResult> redeemDeviceCode({
    required String deviceCode,
  }) async => DeviceTokenSuccess(_session);

  @override
  Future<ConsumerLoginResult> refreshSession({required String refreshToken}) {
    refreshCount++;
    refreshTokens.add(refreshToken);
    events.add('refresh');
    if (refreshResults.isNotEmpty) {
      return refreshResults.removeAt(0);
    }
    return refreshGate?.future ??
        Future<ConsumerLoginResult>.value(
          const ConsumerLoginFailure('No stored session.'),
        );
  }

  @override
  void close() => closeCount++;
}

final class FakeRefreshTokenStore implements RefreshTokenStore {
  FakeRefreshTokenStore({
    List<String>? calls,
    this.cleanupGate,
    this.readGate,
    this.deleteGate,
  }) : calls = calls ?? <String>[];

  final Map<String, String> _tokens = <String, String>{};
  final List<String> calls;
  final Completer<void>? cleanupGate;
  final Completer<String?>? readGate;
  final Completer<void>? deleteGate;
  final Completer<void> deleteStarted = Completer<void>();
  Object? legacyCleanupError;

  @override
  Future<String?> read({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {
    calls.add('read:$profileId:${serverInstanceId.value}');
    if (readGate != null) {
      return readGate!.future;
    }
    return _tokens[_key(profileId, serverInstanceId)];
  }

  @override
  Future<void> write({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
    required String refreshToken,
  }) async {
    calls.add('write:$profileId:${serverInstanceId.value}');
    _tokens[_key(profileId, serverInstanceId)] = refreshToken;
  }

  @override
  Future<void> delete({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {
    calls.add('delete:$profileId:${serverInstanceId.value}');
    if (!deleteStarted.isCompleted) {
      deleteStarted.complete();
    }
    await deleteGate?.future;
    _tokens.remove(_key(profileId, serverInstanceId));
  }

  @override
  Future<void> deleteLegacyOriginToken({
    required String profileId,
    required Uri serverOrigin,
  }) async {
    calls.add('cleanup:$profileId:$serverOrigin');
    if (legacyCleanupError case final error?) {
      throw error;
    }
    await cleanupGate?.future;
  }

  String? tokenFor(String profileId, RomdServerInstanceId serverInstanceId) =>
      _tokens[_key(profileId, serverInstanceId)];

  String _key(String profileId, RomdServerInstanceId serverInstanceId) =>
      '$profileId::${serverInstanceId.value}';
}

final class _FakeServerDiscoveryApiClient implements ServerDiscoveryApiClient {
  _FakeServerDiscoveryApiClient({required this.result, this.onDiscover});

  final Future<ServerDiscoveryResult> result;
  final VoidCallback? onDiscover;

  @override
  Future<ServerDiscoveryResult> discover() {
    onDiscover?.call();
    return result;
  }

  @override
  void close() {}
}

final class FakeLocalProfileRepository
    implements
        LocalProfileRepository,
        ServerBoundLocalProfileRepository,
        ProfileServerConnectionRepository {
  FakeLocalProfileRepository({
    bool localOnly = false,
    this.markLastUsedGates = const <String, Completer<void>>{},
    this.updateOriginGate,
    List<Object>? updateOriginErrors,
  }) : updateOriginErrors = updateOriginErrors ?? <Object>[],
       _profile = LocalProfile(
         id: 'profile-1',
         displayName: 'Player One',
         avatarKey: 'default',
         accentColor: 0xff1fbf8f,
         romdServerOrigin: localOnly ? null : RomdServerOrigins.defaultUri,
         entryMode: LocalProfileEntryMode.open,
         createdAt: DateTime(2026),
         updatedAt: DateTime(2026),
       ) {
    final selected = localOnly
        ? null
        : ProfileServerConnection(
            instanceId: _serverInstanceId,
            origin: RomdServerOrigins.defaultUri,
            firstSeenAt: DateTime(2026),
            lastSeenAt: DateTime(2026),
          );
    _serverState = ProfileServerConnectionState(
      localProfileId: _profile.id,
      generation: 0,
      selectedConnection: selected,
      pendingLocator: null,
    );
  }

  LocalProfile _profile;
  late ProfileServerConnectionState _serverState;
  RomdServerInstanceId? _accountServerInstanceId;
  final Map<String, Completer<void>> markLastUsedGates;
  final Completer<void>? updateOriginGate;
  final List<Object> updateOriginErrors;
  final Completer<void> updateOriginStarted = Completer<void>();
  int updateOriginCount = 0;

  @override
  Future<LocalProfile> createProfile(CreateLocalProfileRequest request) async {
    _profile = LocalProfile(
      id: _profile.id,
      displayName: request.displayName,
      avatarKey: request.avatarKey,
      accentColor: request.accentColor,
      romdServerOrigin: request.romdServerOrigin ?? _profile.romdServerOrigin,
      entryMode: request.entryMode,
      createdAt: _profile.createdAt,
      updatedAt: _profile.updatedAt,
    );
    return _profile;
  }

  @override
  Future<LocalProfile> linkRomdAccount({
    required String localProfileId,
    required RomdAccountLink accountLink,
  }) async {
    _accountServerInstanceId = _serverState.selectedConnection?.instanceId;
    _profile = LocalProfile(
      id: _profile.id,
      displayName: _profile.displayName,
      avatarKey: _profile.avatarKey,
      accentColor: _profile.accentColor,
      romdServerOrigin: _profile.romdServerOrigin,
      entryMode: _profile.entryMode,
      createdAt: _profile.createdAt,
      updatedAt: _profile.updatedAt,
      romdAccountLink: accountLink,
    );
    return _profile;
  }

  @override
  Future<LocalProfile> linkRomdAccountToSelectedServer({
    required String localProfileId,
    required int expectedGeneration,
    required RomdServerInstanceId serverInstanceId,
    required RomdAccountLink accountLink,
  }) async {
    if (_serverState.generation != expectedGeneration ||
        _serverState.selectedConnection?.instanceId != serverInstanceId) {
      throw StateError('stale fake account binding');
    }
    _accountServerInstanceId = serverInstanceId;
    return linkRomdAccount(
      localProfileId: localProfileId,
      accountLink: accountLink,
    );
  }

  @override
  Future<LocalProfile> unlinkRomdAccount({
    required String localProfileId,
  }) async {
    _accountServerInstanceId = null;
    _profile = LocalProfile(
      id: _profile.id,
      displayName: _profile.displayName,
      avatarKey: _profile.avatarKey,
      accentColor: _profile.accentColor,
      romdServerOrigin: _profile.romdServerOrigin,
      entryMode: _profile.entryMode,
      createdAt: _profile.createdAt,
      updatedAt: _profile.updatedAt,
      lastUsedAt: _profile.lastUsedAt,
    );
    return _profile;
  }

  @override
  Future<LocalProfile> updateRomdServerOrigin({
    required String localProfileId,
    required Uri? serverOrigin,
  }) async {
    updateOriginCount++;
    if (!updateOriginStarted.isCompleted) {
      updateOriginStarted.complete();
    }
    await updateOriginGate?.future;
    if (updateOriginErrors.isNotEmpty) {
      throw updateOriginErrors.removeAt(0);
    }
    _profile = LocalProfile(
      id: _profile.id,
      displayName: _profile.displayName,
      avatarKey: _profile.avatarKey,
      accentColor: _profile.accentColor,
      romdServerOrigin: serverOrigin,
      entryMode: _profile.entryMode,
      createdAt: _profile.createdAt,
      updatedAt: _profile.updatedAt,
      lastUsedAt: _profile.lastUsedAt,
    );
    return _profile;
  }

  @override
  Future<List<LocalProfile>> listProfiles() async => <LocalProfile>[_profile];

  @override
  Future<void> markLastUsed(String profileId) async {
    await markLastUsedGates[profileId]?.future;
  }

  @override
  Stream<List<LocalProfile>> watchProfiles() =>
      Stream.value(<LocalProfile>[_profile]);

  @override
  Future<ProfileServerConnectionReadResult> read(String localProfileId) async =>
      localProfileId == _profile.id
      ? ProfileServerConnectionFound(_serverState)
      : const ProfileServerConnectionProfileNotFound();

  @override
  Future<ProfileServerMutationResult> setPendingLocator({
    required String localProfileId,
    required Uri origin,
  }) async {
    if (!updateOriginStarted.isCompleted) {
      updateOriginStarted.complete();
    }
    await updateOriginGate?.future;
    if (updateOriginErrors.isNotEmpty) {
      throw updateOriginErrors.removeAt(0);
    }
    if (localProfileId != _profile.id) {
      return const ProfileServerMutationProfileNotFound();
    }
    final normalized = RomdServerOrigins.parse(origin.toString());
    _serverState = ProfileServerConnectionState(
      localProfileId: localProfileId,
      generation: _serverState.generation + 1,
      selectedConnection: _serverState.selectedConnection,
      pendingLocator: PendingProfileServerLocator(
        origin: normalized,
        createdAt: DateTime.now(),
      ),
    );
    return ProfileServerMutationApplied(_serverState.generation);
  }

  @override
  Future<bool> recordPendingAttempt({
    required String localProfileId,
    required int expectedGeneration,
    required Uri expectedOrigin,
  }) async {
    final pending = _serverState.pendingLocator;
    if (localProfileId != _profile.id ||
        _serverState.generation != expectedGeneration ||
        pending?.origin != RomdServerOrigins.parse(expectedOrigin.toString())) {
      return false;
    }
    _serverState = ProfileServerConnectionState(
      localProfileId: localProfileId,
      generation: expectedGeneration,
      selectedConnection: _serverState.selectedConnection,
      pendingLocator: PendingProfileServerLocator(
        origin: pending!.origin,
        createdAt: pending.createdAt,
        lastAttemptAt: DateTime.now(),
      ),
    );
    return true;
  }

  @override
  Future<ProfileServerMutationResult> activateDiscoveredServer({
    required String localProfileId,
    required int expectedGeneration,
    required Uri expectedPendingOrigin,
    required RomdServerInstanceId instanceId,
  }) async {
    final pending = _serverState.pendingLocator;
    final normalized = RomdServerOrigins.parse(
      expectedPendingOrigin.toString(),
    );
    if (localProfileId != _profile.id ||
        _serverState.generation != expectedGeneration ||
        pending?.origin != normalized) {
      return const ProfileServerMutationStale();
    }
    final previous = _serverState.selectedConnection;
    final timestamp = DateTime.now();
    final connection = ProfileServerConnection(
      instanceId: instanceId,
      origin: normalized,
      firstSeenAt: previous?.instanceId == instanceId
          ? previous!.firstSeenAt
          : timestamp,
      lastSeenAt: timestamp,
    );
    _serverState = ProfileServerConnectionState(
      localProfileId: localProfileId,
      generation: expectedGeneration + 1,
      selectedConnection: connection,
      pendingLocator: null,
    );
    if (_accountServerInstanceId != instanceId) {
      _accountServerInstanceId = null;
      _profile = _copyFakeProfile(
        _profile,
        origin: normalized,
        clearAccountLink: true,
      );
    } else {
      _profile = _copyFakeProfile(_profile, origin: normalized);
    }
    return ProfileServerMutationApplied(_serverState.generation);
  }

  @override
  Future<ProfileServerMutationResult> selectKnownServer({
    required String localProfileId,
    required int expectedGeneration,
    required RomdServerInstanceId instanceId,
  }) async {
    final selected = _serverState.selectedConnection;
    if (localProfileId != _profile.id ||
        expectedGeneration != _serverState.generation ||
        selected?.instanceId != instanceId) {
      return const ProfileServerMutationStale();
    }
    _serverState = ProfileServerConnectionState(
      localProfileId: localProfileId,
      generation: expectedGeneration + 1,
      selectedConnection: selected,
      pendingLocator: null,
    );
    return ProfileServerMutationApplied(_serverState.generation);
  }

  @override
  Future<ProfileServerMutationResult> clearSelection({
    required String localProfileId,
    required int expectedGeneration,
  }) async {
    if (!updateOriginStarted.isCompleted) {
      updateOriginStarted.complete();
    }
    await updateOriginGate?.future;
    if (updateOriginErrors.isNotEmpty) {
      throw updateOriginErrors.removeAt(0);
    }
    if (localProfileId != _profile.id ||
        expectedGeneration != _serverState.generation) {
      return const ProfileServerMutationStale();
    }
    _serverState = ProfileServerConnectionState(
      localProfileId: localProfileId,
      generation: expectedGeneration + 1,
      selectedConnection: null,
      pendingLocator: null,
    );
    _profile = _copyFakeProfile(_profile, origin: null, clearAccountLink: true);
    return ProfileServerMutationApplied(_serverState.generation);
  }
}

LocalProfile _copyFakeProfile(
  LocalProfile profile, {
  required Uri? origin,
  bool clearAccountLink = false,
}) => LocalProfile(
  id: profile.id,
  displayName: profile.displayName,
  avatarKey: profile.avatarKey,
  accentColor: profile.accentColor,
  romdServerOrigin: origin,
  entryMode: profile.entryMode,
  createdAt: profile.createdAt,
  updatedAt: profile.updatedAt,
  lastUsedAt: profile.lastUsedAt,
  romdAccountLink: clearAccountLink ? null : profile.romdAccountLink,
);
