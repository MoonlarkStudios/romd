import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:gamepads/gamepads.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/consumer_host_health.dart';
import 'package:romd_console/src/domain/device_authorization.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/input/gamepad_navigator.dart';
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
import 'package:romd_console/src/play/session/domain/profile_play_history.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';
import 'package:romd_console/src/presentation/catalog/catalog_operation_context.dart';
import 'package:romd_console/src/presentation/console_home_screen.dart';
import 'package:romd_console/src/presentation/launcher/launcher_primitives.dart';
import 'package:romd_console/src/presentation/launcher/launcher_surface_screen.dart';
import 'package:romd_console/src/presentation/theme/console_theme_extensions.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_focusable.dart';
import 'package:romd_console/src/presentation/widgets/game_detail.dart';
import 'package:romd_console/src/presentation/widgets/game_tile.dart';

import 'helpers/in_memory_controller_preferences.dart';
import 'support/fake_profile_local_library_repository.dart';

void main() {
  // Discover is designed around the shipped typefaces; synthetic test fonts
  // are substantially wider and would misreport the assembled header layout.
  setUpAll(() async {
    Future<ByteData> fontData(String path) async =>
        ByteData.sublistView(await File(path).readAsBytes());
    final archivo = FontLoader('Archivo')
      ..addFont(fontData('assets/fonts/Archivo-Variable.ttf'));
    final plexMono = FontLoader('IBM Plex Mono')
      ..addFont(fontData('assets/fonts/IBMPlexMono-Regular.ttf'))
      ..addFont(fontData('assets/fonts/IBMPlexMono-Medium.ttf'))
      ..addFont(fontData('assets/fonts/IBMPlexMono-SemiBold.ttf'));
    await archivo.load();
    await plexMono.load();
  });

  Future<void> settle(WidgetTester tester) async {
    for (var index = 0; index < 10; index++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
  }

  Future<void> pumpHome(
    WidgetTester tester, {
    ProfileLocalLibraryRepository? localLibrary,
    InstallService? install,
    ConsumerApiClient? client,
    VoidCallback? onSwitchProfile,
    VoidCallback? onExitApp,
    VoidCallback? onConnect,
    VoidCallback? onOpenControllers,
    VoidCallback? onOpenSettings,
    bool offline = false,
    bool connecting = false,
    TextScaler textScaler = TextScaler.noScaling,
    bool disableAnimations = false,
    CatalogOperationContext? catalogOperation,
    ValueNotifier<ThemeMode>? themeMode,
    ValueNotifier<HomeSurfaceContext?>? surfaceContextOverride,
  }) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    FocusManager.instance.highlightStrategy =
        FocusHighlightStrategy.alwaysTraditional;
    addTearDown(
      () => FocusManager.instance.highlightStrategy =
          FocusHighlightStrategy.automatic,
    );

    final apiClient = client ?? _FakeApi();
    final playServices = _playServices(
      localLibrary ??
          FakeProfileLocalLibraryRepository(
            library: ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
              _title('a', 'Alpha', playedAt: DateTime.utc(2026, 7, 2)),
              _title('b', 'Beta', playedAt: DateTime.utc(2026, 7)),
            ]),
          ),
      install: install,
    );
    final surfaceContext =
        surfaceContextOverride ??
        ValueNotifier<HomeSurfaceContext?>(
          HomeSurfaceContext(
            localProfileId: _profile.id,
            consumerApiClient: apiClient,
            playServices: playServices,
            catalogOperation: catalogOperation,
          ),
        );
    if (surfaceContextOverride == null) {
      addTearDown(surfaceContext.dispose);
    }

    Widget buildApp(ThemeMode? activeThemeMode) => MaterialApp(
      theme: activeThemeMode == null
          ? RomdSkins.baselineDark()
          : RomdSkins.seamTestTheme(),
      darkTheme: activeThemeMode == null ? null : RomdSkins.baselineDark(),
      themeMode: activeThemeMode,
      themeAnimationDuration: Duration.zero,
      // MediaQuery sits above the Navigator so pushed surfaces (Library,
      // Catalog) inherit the same text scale and motion preferences.
      builder: (context, child) => MediaQuery(
        data: MediaQuery.of(context).copyWith(
          textScaler: textScaler,
          disableAnimations: disableAnimations,
        ),
        child: child!,
      ),
      home: ConsoleHomeScreen(
        consumerApiClient: apiClient,
        playServices: playServices,
        localProfile: _profile,
        session: offline ? null : _session,
        connecting: connecting,
        surfaceContext: surfaceContext,
        onSwitchProfile: onSwitchProfile ?? () {},
        onExitApp: onExitApp ?? () {},
        onConnect: onConnect ?? () {},
        onOpenControllers: onOpenControllers ?? () {},
        onOpenSettings: onOpenSettings ?? () {},
      ),
    );

    await tester.pumpWidget(
      themeMode == null
          ? buildApp(null)
          : ValueListenableBuilder<ThemeMode>(
              valueListenable: themeMode,
              builder: (context, value, _) => buildApp(value),
            ),
    );
    await settle(tester);
  }

  Future<void> press(WidgetTester tester, LogicalKeyboardKey key) async {
    await tester.sendKeyEvent(key);
    await tester.pump();
    await tester.pump();
  }

  String? focusedLabel(WidgetTester tester) =>
      tester.binding.focusManager.primaryFocus?.debugLabel;

  Future<void> openLibrary(WidgetTester tester) async {
    await tester.tap(find.byKey(const ValueKey<String>('dock-library')));
    await settle(tester);
  }

  Future<void> openCatalog(WidgetTester tester) async {
    await tester.tap(find.byKey(const ValueKey<String>('dock-catalog')));
    await settle(tester);
  }

  Future<void> selectDiscoverSection(
    WidgetTester tester,
    String section,
  ) async {
    await tester.tap(find.byKey(ValueKey<String>('discover-section-$section')));
    await settle(tester);
    await press(tester, LogicalKeyboardKey.arrowDown);
    await settle(tester);
  }

  Future<void> openDiscoverSearch(WidgetTester tester) async {
    await tester.tap(find.byKey(const ValueKey<String>('discover-search')));
    await settle(tester);
  }

  // Design contract §14.1–2: no user-visible "store" text and no storefront
  // icon anywhere in the surface, dock, or status panels.
  void expectCatalogNaming(WidgetTester tester) {
    final storePattern = RegExp('store', caseSensitive: false);
    for (final text in tester.widgetList<Text>(
      find.byType(Text, skipOffstage: false),
    )) {
      final value = text.data ?? text.textSpan?.toPlainText() ?? '';
      expect(
        storePattern.hasMatch(value),
        isFalse,
        reason: 'No user-visible text may contain "store": "$value"',
      );
    }
    expect(
      find.byIcon(Icons.storefront_outlined, skipOffstage: false),
      findsNothing,
    );
  }

  testWidgets('Home renders local state without any Consumer request', (
    tester,
  ) async {
    final api = _FakeApi();
    await pumpHome(tester, client: api);

    // The focused game announces itself in the label lane above the rail;
    // tiles without cached reference data carry a readable text badge.
    expect(focusedLabel(tester), 'home-rail-a');
    expect(find.text('Alpha'), findsOneWidget);
    expect(
      find.byKey(const ValueKey<String>('platform-corner-logo-snes')),
      findsNothing,
    );
    expect(find.text('SNES'), findsNWidgets(2));
    expect(find.textContaining('Last played'), findsNothing);
    await press(tester, LogicalKeyboardKey.arrowRight);
    expect(find.text('Beta'), findsOneWidget);
    expect(api.calls, isEmpty);
  });

  testWidgets('Home remains overflow-free at 1.5x text scale', (tester) async {
    await pumpHome(tester, textScaler: const TextScaler.linear(1.5));

    expect(tester.takeException(), isNull);
    expect(find.text('Alpha'), findsOneWidget);
  });

  testWidgets('Home starts on the most recent playable game', (tester) async {
    await pumpHome(tester);

    expect(focusedLabel(tester), 'home-rail-a');
    expect(find.text('Alpha'), findsOneWidget);
  });

  testWidgets('focused title floats centered above its tile', (tester) async {
    await pumpHome(tester);

    Finder tile(int index) => find.byWidgetPredicate(
      (widget) => widget is Hero && widget.tag == 'home-local#card$index',
    );

    // The first tile sits close to the screen edge; its label may overhang
    // the content gutter (Switch-style) but never the lenient edge inset.
    expect(
      tester.getTopLeft(find.text('Alpha')).dx,
      greaterThanOrEqualTo(
        Theme.of(
          tester.element(find.byType(ConsoleHomeScreen)),
        ).extension<ConsoleLayoutTheme>()!.xl,
      ),
    );

    // Further in, the label centers exactly over the focused tile.
    await press(tester, LogicalKeyboardKey.arrowRight);
    await settle(tester);
    expect(
      tester.getCenter(find.text('Beta')).dx,
      closeTo(tester.getCenter(tile(1)).dx, 1.0),
    );
  });

  testWidgets('horizontal traversal clamps at the rail edges', (tester) async {
    await pumpHome(tester);

    await press(tester, LogicalKeyboardKey.arrowRight);
    expect(focusedLabel(tester), 'home-rail-b');
    await press(tester, LogicalKeyboardKey.arrowRight);
    expect(focusedLabel(tester), 'home-rail-b');
    await press(tester, LogicalKeyboardKey.arrowLeft);
    expect(focusedLabel(tester), 'home-rail-a');
    await press(tester, LogicalKeyboardKey.arrowLeft);
    expect(focusedLabel(tester), 'home-rail-a');
  });

  testWidgets('empty rail shows skeleton slots and lands focus on the dock', (
    tester,
  ) async {
    await pumpHome(
      tester,
      localLibrary: FakeProfileLocalLibraryRepository(
        library: ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
          _title('a', 'Alpha'),
        ]),
      ),
    );

    expect(
      find.byKey(const ValueKey<String>('home-rail-skeleton')),
      findsNWidgets(5),
    );
    // The empty shelf is silent: still vessels, no copy. The invitation is
    // the dock's Catalog action, which takes first-run focus.
    expect(find.textContaining('Games you play'), findsNothing);
    expect(focusedLabel(tester), 'dock-catalog');
  });

  testWidgets('vessels breathe while loading and hold still once empty', (
    tester,
  ) async {
    Animation<double> vesselOpacity() => tester
        .widget<FadeTransition>(
          find
              .ancestor(
                of: find.byKey(const ValueKey<String>('home-rail-skeleton')),
                matching: find.byType(FadeTransition),
              )
              .first,
        )
        .opacity;

    // No library result yet: the vessels pulse.
    await pumpHome(
      tester,
      localLibrary: FakeProfileLocalLibraryRepository(
        watchLibraryOverride: (_) =>
            const Stream<ProfileLocalLibraryResult>.empty(),
      ),
    );
    expect(vesselOpacity().isAnimating, isTrue);

    // A known-empty shelf: the same vessels hold still at full register.
    await pumpHome(tester);
    expect(vesselOpacity().isAnimating, isFalse);
    expect(vesselOpacity().value, 1);
  });

  testWidgets('mounted home dock and rail re-resolve their skin at runtime', (
    tester,
  ) async {
    final themeMode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    addTearDown(themeMode.dispose);
    await pumpHome(
      tester,
      themeMode: themeMode,
      localLibrary: FakeProfileLocalLibraryRepository(
        library: ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
          _title('a', 'Alpha'),
        ]),
      ),
    );

    // The focused dock action (Catalog takes first-run focus on an empty
    // shelf) carries the skin-resolved focus fill this test inspects.
    final dock = find.byKey(const ValueKey<String>('dock-catalog'));
    final skeleton = find
        .byKey(const ValueKey<String>('home-rail-skeleton'))
        .first;
    final profileLabel = find.byKey(
      const ValueKey<String>('home-profile-label'),
    );
    final avatar = find.byKey(const ValueKey<String>('home-profile-avatar'));
    final dockElement = tester.element(dock);
    final skeletonElement = tester.element(skeleton);
    final profileElement = tester.element(profileLabel);
    final dockBefore = tester.widget<AnimatedContainer>(dock);
    final dockDecorationBefore = dockBefore.decoration! as BoxDecoration;
    final skeletonBefore = tester.widget<Container>(skeleton);
    final skeletonDecorationBefore =
        skeletonBefore.decoration! as BoxDecoration;
    final labelBefore = tester.widget<Text>(find.text('Catalog'));
    final profileBefore = tester.widget<AnimatedDefaultTextStyle>(profileLabel);
    final avatarSizeBefore = tester.getSize(avatar);

    themeMode.value = ThemeMode.light;
    await tester.pump();

    final dockAfter = tester.widget<AnimatedContainer>(dock);
    final dockDecorationAfter = dockAfter.decoration! as BoxDecoration;
    final skeletonAfter = tester.widget<Container>(skeleton);
    final skeletonDecorationAfter = skeletonAfter.decoration! as BoxDecoration;
    final labelAfter = tester.widget<Text>(find.text('Catalog'));
    final profileAfter = tester.widget<AnimatedDefaultTextStyle>(profileLabel);
    final avatarSizeAfter = tester.getSize(avatar);
    expect(identical(dockElement, tester.element(dock)), isTrue);
    expect(identical(skeletonElement, tester.element(skeleton)), isTrue);
    expect(identical(profileElement, tester.element(profileLabel)), isTrue);
    expect(dockAfter.constraints, isNot(dockBefore.constraints));
    expect(dockAfter.duration, isNot(dockBefore.duration));
    expect(dockDecorationAfter.shape, isNot(dockDecorationBefore.shape));
    expect(dockDecorationAfter.color, isNot(dockDecorationBefore.color));
    expect(
      skeletonDecorationAfter.color,
      isNot(skeletonDecorationBefore.color),
    );
    expect(
      skeletonDecorationAfter.borderRadius,
      isNot(skeletonDecorationBefore.borderRadius),
    );
    expect(labelAfter.style!.fontSize, isNot(labelBefore.style!.fontSize));
    expect(labelAfter.style!.color, isNot(labelBefore.style!.color));
    expect(profileAfter.duration, isNot(profileBefore.duration));
    expect(
      profileAfter.style.fontFamily,
      isNot(profileBefore.style.fontFamily),
    );
    expect(avatarSizeAfter, isNot(avatarSizeBefore));
  });

  testWidgets('dock keeps session switch and app exit distinct', (
    tester,
  ) async {
    var controllerOpens = 0;
    var settingsOpens = 0;
    var profileSwitches = 0;
    var appExits = 0;
    await pumpHome(
      tester,
      onOpenControllers: () => controllerOpens++,
      onOpenSettings: () => settingsOpens++,
      onSwitchProfile: () => profileSwitches++,
      onExitApp: () => appExits++,
    );

    await press(tester, LogicalKeyboardKey.arrowDown);
    expect(focusedLabel(tester), 'dock-library');
    await press(tester, LogicalKeyboardKey.arrowRight);
    expect(focusedLabel(tester), 'dock-catalog');
    await press(tester, LogicalKeyboardKey.arrowRight);
    expect(focusedLabel(tester), 'dock-controllers');
    await press(tester, LogicalKeyboardKey.enter);
    expect(controllerOpens, 1);
    await press(tester, LogicalKeyboardKey.arrowRight);
    await press(tester, LogicalKeyboardKey.enter);
    expect(settingsOpens, 1);
    await press(tester, LogicalKeyboardKey.arrowRight);
    await press(tester, LogicalKeyboardKey.enter);
    expect(profileSwitches, 1);
    expect(appExits, 0);
    await press(tester, LogicalKeyboardKey.arrowRight);
    await press(tester, LogicalKeyboardKey.enter);
    expect(profileSwitches, 1);
    expect(appExits, 1);
  });

  testWidgets('focused dock action names itself in the label lane', (
    tester,
  ) async {
    await pumpHome(tester);

    expect(find.text('Switch Profile'), findsNothing);
    await press(tester, LogicalKeyboardKey.arrowDown);
    expect(find.text('Library'), findsOneWidget);
    await press(tester, LogicalKeyboardKey.arrowRight);
    expect(find.text('Library'), findsNothing);
    expect(find.text('Catalog'), findsOneWidget);
  });

  testWidgets('home hints omit Back; content round-trips through the dock', (
    tester,
  ) async {
    await pumpHome(tester);
    expect(find.text('Navigate'), findsOneWidget);
    expect(find.text('Open'), findsOneWidget);
    expect(find.text('Back'), findsNothing);

    final contentFocus = tester.binding.focusManager.primaryFocus;
    await press(tester, LogicalKeyboardKey.arrowDown);
    expect(focusedLabel(tester), 'dock-library');
    await press(tester, LogicalKeyboardKey.arrowUp);
    expect(tester.binding.focusManager.primaryFocus, same(contentFocus));
    expect(focusedLabel(tester), 'home-rail-a');
  });

  testWidgets('launcher chrome uses access-aware status copy', (tester) async {
    final semantics = tester.ensureSemantics();
    await pumpHome(tester, offline: true);

    expect(find.text('Offline'), findsOneWidget);
    expect(
      find.bySemanticsLabel('Offline — saved access applies'),
      findsOneWidget,
    );
    expect(find.textContaining('installed games still play'), findsNothing);
    semantics.dispose();
  });

  testWidgets('launcher chrome is silent while connected', (tester) async {
    final semantics = tester.ensureSemantics();
    await pumpHome(tester);

    expect(find.text('Connected'), findsNothing);
    expect(
      find.byKey(const ValueKey<String>('connection-status-connected')),
      findsNothing,
    );
    expect(
      find.byKey(const ValueKey<String>('home-profile-connection-connected')),
      findsOneWidget,
    );
    expect(
      find.bySemanticsLabel('John. Connected to ROMD. Open profile settings.'),
      findsOneWidget,
    );
    semantics.dispose();
  });

  testWidgets('profile ring reports an in-flight connection', (tester) async {
    final semantics = tester.ensureSemantics();
    await pumpHome(tester, connecting: true);

    expect(find.text('Connecting…'), findsNothing);
    expect(
      find.byKey(const ValueKey<String>('connection-status-connecting')),
      findsNothing,
    );
    expect(
      find.byKey(const ValueKey<String>('home-profile-connection-connecting')),
      findsOneWidget,
    );
    expect(
      find.bySemanticsLabel('John. Connecting to ROMD. Open profile settings.'),
      findsOneWidget,
    );
    semantics.dispose();
  });

  testWidgets('dock remains focus-visible at 2x text', (tester) async {
    await pumpHome(tester, textScaler: const TextScaler.linear(2));

    await press(tester, LogicalKeyboardKey.arrowDown);
    for (final action in <String>[
      'library',
      'catalog',
      'controllers',
      'settings',
      'switchProfile',
      'exit',
    ]) {
      expect(focusedLabel(tester), 'dock-$action');
      await settle(tester);
      final rect = tester.getRect(find.byKey(ValueKey<String>('dock-$action')));
      expect(rect.left, greaterThanOrEqualTo(0));
      expect(rect.right, lessThanOrEqualTo(1280));
      if (action != 'exit') {
        await press(tester, LogicalKeyboardKey.arrowRight);
      }
    }
    expect(tester.takeException(), isNull);
  });

  testWidgets('focused revoked game leaves Home and focus moves safely', (
    tester,
  ) async {
    final changes = StreamController<ProfileLocalLibraryResult>.broadcast();
    addTearDown(changes.close);
    await pumpHome(
      tester,
      localLibrary: FakeProfileLocalLibraryRepository(
        watchLibraryOverride: (_) => changes.stream,
      ),
    );
    changes.add(
      ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
        _title('a', 'Alpha', playedAt: DateTime.utc(2026, 7, 2)),
        _title('b', 'Beta', playedAt: DateTime.utc(2026, 7)),
      ]),
    );
    await settle(tester);
    expect(focusedLabel(tester), 'home-rail-a');

    changes.add(
      ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
        _title('b', 'Beta', playedAt: DateTime.utc(2026, 7)),
        _title(
          'a',
          'Alpha',
          playedAt: DateTime.utc(2026, 7, 2),
          authorization: ProfileGameAuthorization.revoked,
        ),
      ]),
    );
    await settle(tester);

    expect(find.text('Alpha'), findsNothing);
    expect(focusedLabel(tester), 'home-rail-b');
    expect(
      find.byWidgetPredicate(
        (widget) =>
            widget is Semantics &&
            widget.properties.label == 'A game is no longer available on Home.',
      ),
      findsOneWidget,
    );
  });

  testWidgets('no selected server exposes an honest local-first action', (
    tester,
  ) async {
    var connectRequested = false;
    await pumpHome(
      tester,
      offline: true,
      onConnect: () => connectRequested = true,
      localLibrary: FakeProfileLocalLibraryRepository(
        library: const ProfileLocalLibraryNoServer(),
      ),
    );

    expect(
      find.text(
        'Connect this profile to a ROMD server to install and play games.',
      ),
      findsOneWidget,
    );
    expect(focusedLabel(tester), 'home-rail-connect');
    await press(tester, LogicalKeyboardKey.enter);
    expect(connectRequested, isTrue);

    // The Library surface carries the same honest action.
    connectRequested = false;
    await openLibrary(tester);
    expect(focusedLabel(tester), 'library-status-action');
    await press(tester, LogicalKeyboardKey.enter);
    expect(connectRequested, isTrue);
  });

  testWidgets('navigation animation honors reduced motion', (tester) async {
    await pumpHome(tester, disableAnimations: true);
    final dockButton = tester.widget<AnimatedContainer>(
      find.byKey(const ValueKey<String>('dock-settings')),
    );
    expect(dockButton.duration, Duration.zero);
  });

  testWidgets('Back on home returns focus to the rail, never exits', (
    tester,
  ) async {
    var profileSwitches = 0;
    await pumpHome(tester, onSwitchProfile: () => profileSwitches++);

    await press(tester, LogicalKeyboardKey.arrowDown);
    expect(focusedLabel(tester), 'dock-library');
    await press(tester, LogicalKeyboardKey.escape);
    expect(focusedLabel(tester), 'home-rail-a');
    expect(profileSwitches, 0);

    await press(tester, LogicalKeyboardKey.escape);
    expect(focusedLabel(tester), 'home-rail-a');
    expect(profileSwitches, 0);
  });

  testWidgets('avatar corner opens profile settings', (tester) async {
    var settingsOpens = 0;
    await pumpHome(tester, onOpenSettings: () => settingsOpens++);

    await press(tester, LogicalKeyboardKey.arrowUp);
    expect(focusedLabel(tester), 'home-avatar');
    await press(tester, LogicalKeyboardKey.enter);
    expect(settingsOpens, 1);
    await press(tester, LogicalKeyboardKey.arrowDown);
    expect(focusedLabel(tester), 'home-rail-a');
  });

  testWidgets('Library surface opens the profile-scoped Local Library', (
    tester,
  ) async {
    await pumpHome(tester);
    await openLibrary(tester);

    expect(find.text('Library'), findsOneWidget);
    expect(find.text('2 LOCAL GAMES'), findsOneWidget);
    expect(find.text('RECENTLY PLAYED · ALL SYSTEMS'), findsOneWidget);
    expect(find.byKey(const ValueKey<String>('library-grid')), findsOneWidget);
    expect(
      find.byKey(const ValueKey<String>('library-inspector')),
      findsOneWidget,
    );
    expect(find.text('Alpha'), findsNWidgets(2));
    expect(find.text('Beta'), findsOneWidget);
    expect(find.text('Back'), findsOneWidget);
  });

  testWidgets('connected Library enriches the focused game metadata', (
    tester,
  ) async {
    final api = _FakeApi();
    await pumpHome(tester, client: api, catalogOperation: _operation());
    await openLibrary(tester);

    expect(api.detailCalls, 1);
    expect(find.text('Former authority description'), findsOneWidget);
    expect(
      find.byKey(const ValueKey<String>('library-inspector-description')),
      findsOneWidget,
    );
  });

  testWidgets('rich Library metadata stays inside the inspector', (
    tester,
  ) async {
    const title = 'Teenage Mutant Ninja Turtles IV - Turtles in Time';
    final api = _FakeApi(
      detailFutures: <String, Future<ConsoleGameDetail>>{
        'a': Future<ConsoleGameDetail>.value(
          _detail(
            'a',
            title,
            description:
                'Teenage Mutant Ninja Turtles IV: Turtles in Time, developed '
                'by Konami, is a side-scrolling beat em up for up to two '
                'players. It is the fourth game in the series, following '
                'Teenage Mutant Ninja Turtles III: The Manhattan Project.',
            genre: "Hack and slash/Beat 'em up",
            players: 2,
          ),
        ),
      },
    );
    await pumpHome(
      tester,
      client: api,
      catalogOperation: _operation(),
      localLibrary: FakeProfileLocalLibraryRepository(
        library: ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
          _title('a', title),
        ]),
      ),
    );
    await openLibrary(tester);

    expect(tester.takeException(), isNull);
    expect(find.text('2 PLAYERS'), findsOneWidget);
    expect(find.text('READY TO PLAY'), findsOneWidget);
    expect(find.text('OPEN DETAILS'), findsOneWidget);
  });

  testWidgets('connected Library detail route refreshes metadata', (
    tester,
  ) async {
    final api = _FakeApi();
    await pumpHome(tester, client: api, catalogOperation: _operation());
    await openLibrary(tester);

    tester
        .widget<GameTile>(
          find.byWidgetPredicate(
            (widget) => widget is GameTile && widget.game.title == 'Alpha',
          ),
        )
        .onPressed();
    await settle(tester);

    expect(api.detailCalls, 2);
    expect(find.text('Former authority description'), findsOneWidget);
  });

  testWidgets('offline Library keeps local fallback without metadata calls', (
    tester,
  ) async {
    final api = _FakeApi();
    await pumpHome(tester, client: api);
    await openLibrary(tester);

    expect(api.detailCalls, 0);
    expect(find.text('Former authority description'), findsNothing);
    expect(find.text('Alpha'), findsNWidgets(2));
  });

  testWidgets('Library rejects metadata that completes after invalidation', (
    tester,
  ) async {
    final detailGate = Completer<ConsoleGameDetail>();
    final changes = _CatalogChanges();
    var current = true;
    final api = _FakeApi(detailGate: detailGate);
    await pumpHome(
      tester,
      client: api,
      catalogOperation: _operation(isCurrent: () => current, changes: changes),
    );
    await openLibrary(tester);
    expect(api.detailCalls, 1);

    current = false;
    changes.invalidate();
    detailGate.complete(_detail('a', 'Former Alpha'));
    await settle(tester);

    expect(find.text('Former authority description'), findsNothing);
    expect(find.text('Alpha'), findsNWidgets(2));
  });

  testWidgets('Library ignores superseded focused-game metadata', (
    tester,
  ) async {
    final alpha = Completer<ConsoleGameDetail>();
    final beta = Completer<ConsoleGameDetail>();
    final api = _FakeApi(
      detailFutures: <String, Future<ConsoleGameDetail>>{
        'a': alpha.future,
        'b': beta.future,
      },
    );
    await pumpHome(tester, client: api, catalogOperation: _operation());
    await openLibrary(tester);
    expect(api.detailCalls, 1);

    await press(tester, LogicalKeyboardKey.arrowRight);
    await settle(tester);
    expect(api.detailCalls, 2);

    alpha.complete(_detail('a', 'Alpha', description: 'Alpha metadata'));
    await settle(tester);
    expect(find.text('Alpha metadata'), findsNothing);

    beta.complete(_detail('b', 'Beta', description: 'Beta metadata'));
    await settle(tester);
    expect(find.text('Beta metadata'), findsOneWidget);
  });

  testWidgets('Library rejects metadata for a foreign title', (tester) async {
    final api = _FakeApi(
      detailFutures: <String, Future<ConsoleGameDetail>>{
        'a': Future<ConsoleGameDetail>.value(
          _detail('foreign', 'Foreign', description: 'Foreign metadata'),
        ),
      },
    );
    await pumpHome(tester, client: api, catalogOperation: _operation());
    await openLibrary(tester);

    expect(api.detailCalls, 1);
    expect(find.text('Foreign metadata'), findsNothing);
    expect(find.text('Alpha'), findsNWidgets(2));
  });

  testWidgets('Back pops the Library surface and returns focus to the dock', (
    tester,
  ) async {
    await pumpHome(tester);
    await openLibrary(tester);

    await press(tester, LogicalKeyboardKey.escape);
    await settle(tester);

    expect(find.text('2 LOCAL GAMES'), findsNothing);
    // Opening via the dock left focus there; the pop restores it.
    expect(focusedLabel(tester), 'dock-library');
    await press(tester, LogicalKeyboardKey.arrowUp);
    expect(focusedLabel(tester), 'home-rail-a');
  });

  testWidgets('Local Library retains learned revokes as Access required', (
    tester,
  ) async {
    await pumpHome(
      tester,
      localLibrary: FakeProfileLocalLibraryRepository(
        library: ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
          _title('a', 'Alpha', authorization: ProfileGameAuthorization.revoked),
        ]),
      ),
    );
    await openLibrary(tester);

    expect(find.text('Alpha'), findsNWidgets(2));
    expect(find.text('Access required'), findsOneWidget);
    expect(find.text('ACCESS REQUIRED'), findsOneWidget);
    expect(find.byKey(const ValueKey<String>('tile-installed')), findsNothing);
  });

  testWidgets('learned-revoked local detail is honest before launch', (
    tester,
  ) async {
    await pumpHome(
      tester,
      localLibrary: FakeProfileLocalLibraryRepository(
        library: ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
          _title('a', 'Alpha', authorization: ProfileGameAuthorization.revoked),
        ]),
      ),
    );
    await openLibrary(tester);
    tester
        .widget<GameTile>(
          find.byWidgetPredicate(
            (widget) => widget is GameTile && widget.game.title == 'Alpha',
          ),
        )
        .onPressed();
    await settle(tester);

    expect(find.text('ACCESS REQUIRED'), findsOneWidget);
    expect(find.text('Check access'), findsOneWidget);
  });

  testWidgets('Local Library sort selection re-subscribes explicitly', (
    tester,
  ) async {
    final repository = FakeProfileLocalLibraryRepository(
      watchLibraryOverride: (sort) => Stream<ProfileLocalLibraryResult>.value(
        ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
          _title('a', 'Alpha'),
        ]),
      ),
    );
    await pumpHome(tester, localLibrary: repository);
    await openLibrary(tester);
    await tester.tap(
      find.byKey(const ValueKey<String>('library-filter-trigger')),
    );
    await settle(tester);
    await tester.tap(find.byKey(const ValueKey<String>('library-filter-sort')));
    await settle(tester);
    await tester.tap(
      find.byKey(const ValueKey<String>('library-filter-sort-title')),
    );
    await settle(tester);

    expect(
      repository.watchedSorts,
      contains(ProfileLocalLibrarySort.recentPlay),
    );
    expect(repository.watchedSorts, contains(ProfileLocalLibrarySort.title));
  });

  testWidgets('Local Library System filter updates the grid and inspector', (
    tester,
  ) async {
    await pumpHome(
      tester,
      localLibrary: FakeProfileLocalLibraryRepository(
        library: ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
          _title('a', 'Alpha'),
          _title(
            'b',
            'Beta',
            platformId: 'genesis',
            platformName: 'Genesis',
            platformShortName: 'genesis',
          ),
        ]),
      ),
    );
    await openLibrary(tester);

    await tester.tap(
      find.byKey(const ValueKey<String>('library-filter-trigger')),
    );
    await settle(tester);
    await tester.tap(
      find.byKey(const ValueKey<String>('library-filter-system')),
    );
    await settle(tester);
    await tester.tap(
      find.byKey(const ValueKey<String>('library-filter-system-genesis')),
    );
    await settle(tester);
    await press(tester, LogicalKeyboardKey.escape);
    await settle(tester);

    expect(find.text('RECENTLY PLAYED · GENESIS'), findsOneWidget);
    expect(find.text('Alpha'), findsNothing);
    expect(find.text('Beta'), findsNWidgets(2));
    expect(focusedLabel(tester), 'local-library-b');
  });

  testWidgets('D-pad traverses Library grid, header, and filter menu', (
    tester,
  ) async {
    await pumpHome(tester);
    await openLibrary(tester);

    expect(focusedLabel(tester), 'local-library-a');
    await press(tester, LogicalKeyboardKey.arrowUp);
    expect(focusedLabel(tester), 'library-filter-trigger');
    // The band has no focusable chrome; up from the top row stays put.
    await press(tester, LogicalKeyboardKey.arrowUp);
    expect(focusedLabel(tester), 'library-filter-trigger');
    await press(tester, LogicalKeyboardKey.enter);
    expect(focusedLabel(tester), 'library-filter-sort');
    await press(tester, LogicalKeyboardKey.arrowRight);
    expect(focusedLabel(tester), 'library-filter-option-sort-recentPlay');
    await press(tester, LogicalKeyboardKey.arrowDown);
    expect(focusedLabel(tester), 'library-filter-option-sort-recentInstall');
    await press(tester, LogicalKeyboardKey.enter);
    await settle(tester);

    expect(find.text('RECENTLY INSTALLED · ALL SYSTEMS'), findsOneWidget);
  });

  testWidgets('Local Library controls remain overflow-free at 2x text', (
    tester,
  ) async {
    await pumpHome(tester, textScaler: const TextScaler.linear(2));
    await openLibrary(tester);

    expect(tester.takeException(), isNull);
    expect(find.text('RECENTLY PLAYED · ALL SYSTEMS'), findsOneWidget);
    await tester.tap(
      find.byKey(const ValueKey<String>('library-filter-trigger')),
    );
    await settle(tester);
    expect(tester.takeException(), isNull);
    expect(find.text('SORT & FILTER'), findsOneWidget);
    expect(find.text('Sort by'), findsOneWidget);
    expect(find.text('System'), findsOneWidget);
  });

  testWidgets('Discover assembles Featured, Browse, system rail, and Search', (
    tester,
  ) async {
    final api = _FakeApi(
      topRated: <ConsoleGame>[_game('top', 'Top Pick')],
      allGames: <ConsoleGame>[_game('all', 'All Game')],
      collections: const <ConsoleCollection>[
        ConsoleCollection(
          id: 'curated',
          name: 'Curated Shelf',
          description: 'Hand picked',
          platformId: null,
          platformName: null,
          coverUrl: null,
          heroUrl: null,
          itemCount: 1,
        ),
      ],
      collectionGames: <ConsoleGame>[_game('curated', 'Curated Game')],
      platforms: const <ConsolePlatform>[
        ConsolePlatform(
          id: 'snes',
          name: 'Super Nintendo',
          shortName: 'snes',
          manufacturer: 'Nintendo',
          titleCount: 2,
          coverUrl: null,
        ),
      ],
    );
    await pumpHome(
      tester,
      client: api,
      install: _FakeInstall(<String>{'top-r'}),
      catalogOperation: _operation(),
    );
    await openCatalog(tester);

    expect(find.text('Catalog'), findsOneWidget);
    expect(find.text('Featured'), findsOneWidget);
    expect(find.text('Browse'), findsOneWidget);
    expect(find.text('Systems'), findsNothing);
    expect(
      find.byKey(const ValueKey<String>('discover-search')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('discover-section-featured')),
      findsOneWidget,
    );
    expect(find.text('Top Pick'), findsWidgets);
    expect(
      find.byKey(const ValueKey<String>('featured-spotlight-readiness')),
      findsOneWidget,
    );
    await selectDiscoverSection(tester, 'featured');
    await press(tester, LogicalKeyboardKey.arrowDown);
    await settle(tester);
    expect(find.text('Curated Shelf'), findsOneWidget);
    expect(find.text('Curated Game', skipOffstage: false), findsOneWidget);

    await selectDiscoverSection(tester, 'allGames');
    expect(
      find.byKey(const ValueKey<String>('all-games-grid')),
      findsOneWidget,
    );
    expect(find.text('All Game'), findsWidgets);
    expect(find.text('Filters'), findsOneWidget);
    await tester.sendKeyEvent(LogicalKeyboardKey.keyF);
    await tester.pump();
    expect(focusedLabel(tester), 'all-games-filter-sort');

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pump();
    expect(
      find.byKey(const ValueKey<String>('all-games-filter-menu')),
      findsNothing,
    );
    expect(
      find.byKey(const ValueKey<String>('browse-system-rail')),
      findsOneWidget,
    );
    expect(
      find.descendant(
        of: find.byKey(const ValueKey<String>('browse-system-rail')),
        matching: find.text('SN'),
      ),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('browse-system-snes')),
      findsOneWidget,
    );
    expect(
      _isSemanticallySelected(
        tester,
        find.byKey(const ValueKey<String>('discover-section-allGames')),
      ),
      isTrue,
    );

    await openDiscoverSearch(tester);
    expect(find.text('Search the Catalog'), findsOneWidget);
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await settle(tester);
    expect(find.text('Catalog'), findsOneWidget);
    expect(
      find.descendant(
        of: find.byKey(const ValueKey<String>('browse-system-rail')),
        matching: find.text('SN'),
      ),
      findsOneWidget,
    );
    expect(find.text('Search this ROMD Library'), findsNothing);
    expect(focusedLabel(tester), 'discover-search-action');
    expect(
      _isSemanticallySelected(
        tester,
        find.byKey(const ValueKey<String>('discover-section-allGames')),
      ),
      isTrue,
    );
  });

  testWidgets('pushed Search rejects a stale result after authority changes', (
    tester,
  ) async {
    final formerApi = _FakeApi(
      allGames: <ConsoleGame>[_game('result', 'Search Result')],
    );
    final liveApi = _FakeApi();
    final formerPlayServices = _playServices(
      FakeProfileLocalLibraryRepository(),
    );
    final livePlayServices = _playServices(FakeProfileLocalLibraryRepository());
    final formerOperation = _operation();
    final liveOperation = _operation(epoch: 2);
    final surfaceContext = ValueNotifier<HomeSurfaceContext?>(
      HomeSurfaceContext(
        localProfileId: _profile.id,
        consumerApiClient: formerApi,
        playServices: formerPlayServices,
        catalogOperation: formerOperation,
      ),
    );
    addTearDown(surfaceContext.dispose);
    addTearDown(formerPlayServices.close);
    addTearDown(livePlayServices.close);

    await pumpHome(
      tester,
      client: formerApi,
      catalogOperation: formerOperation,
      surfaceContextOverride: surfaceContext,
    );
    await openCatalog(tester);
    await openDiscoverSearch(tester);
    await tester.sendKeyDownEvent(LogicalKeyboardKey.keyA, character: 'a');
    await tester.sendKeyUpEvent(LogicalKeyboardKey.keyA);
    await tester.pump(const Duration(milliseconds: 350));
    await settle(tester);
    final result = tester.widget<ConsoleFocusable>(
      find.descendant(
        of: find.byKey(const ValueKey<String>('search-result-result')),
        matching: find.byType(ConsoleFocusable),
      ),
    );

    surfaceContext.value = HomeSurfaceContext(
      localProfileId: _profile.id,
      consumerApiClient: liveApi,
      playServices: livePlayServices,
      catalogOperation: liveOperation,
    );
    await tester.pump();
    result.onPressed();
    await settle(tester);

    expect(
      find.byKey(const ValueKey<String>('catalog-privacy-shield')),
      findsOneWidget,
    );
    expect(find.byType(GameDetailScreen), findsNothing);
    expect(formerApi.detailCalls, 0);
    expect(liveApi.detailCalls, 0);
  });

  testWidgets('offline Catalog is explicit and makes no Consumer request', (
    tester,
  ) async {
    final api = _FakeApi();
    var connectRequested = false;
    await pumpHome(
      tester,
      client: api,
      offline: true,
      onConnect: () => connectRequested = true,
    );
    await openCatalog(tester);

    expect(find.text('Catalog is offline'), findsOneWidget);
    expect(find.text('Choose server'), findsOneWidget);
    expect(api.calls, isEmpty);
    expect(focusedLabel(tester), 'catalog-featured-status-action');
    await press(tester, LogicalKeyboardKey.enter);
    expect(connectRequested, isTrue);
  });

  testWidgets('status panels inset their content from the panel border', (
    tester,
  ) async {
    await pumpHome(tester, offline: true);
    await openCatalog(tester);

    final panel = find
        .descendant(
          of: find.byType(LauncherStatusPanel),
          matching: find.byType(Container),
        )
        .first;
    final panelRect = tester.getRect(panel);
    final contentRect = tester.getRect(
      find.descendant(of: panel, matching: find.byType(Column)).first,
    );
    expect(contentRect.left - panelRect.left, greaterThanOrEqualTo(16));
    expect(panelRect.right - contentRect.right, greaterThanOrEqualTo(16));
    expect(contentRect.top - panelRect.top, greaterThanOrEqualTo(16));
    expect(panelRect.bottom - contentRect.bottom, greaterThanOrEqualTo(16));
  });

  testWidgets('Catalog offline state and home chrome carry no storefront '
      'naming', (tester) async {
    await pumpHome(tester, offline: true);

    await press(tester, LogicalKeyboardKey.arrowDown);
    await press(tester, LogicalKeyboardKey.arrowRight);
    expect(focusedLabel(tester), 'dock-catalog');
    expect(find.text('Catalog'), findsOneWidget);
    expectCatalogNaming(tester);

    await openCatalog(tester);
    expect(find.text('Catalog'), findsOneWidget);
    expect(find.text('Catalog is offline'), findsOneWidget);
    expect(
      find.text('Choose and sign in to a ROMD server to browse its Catalog.'),
      findsOneWidget,
    );
    expectCatalogNaming(tester);
  });

  testWidgets('Catalog loading and shield states carry no storefront naming', (
    tester,
  ) async {
    var current = true;
    final changes = _CatalogChanges();
    addTearDown(changes.dispose);
    await pumpHome(
      tester,
      client: _FakeApi(topRatedGate: Completer<ConsoleGamePage>()),
      catalogOperation: _operation(isCurrent: () => current, changes: changes),
    );
    await openCatalog(tester);

    expect(find.text('Opening Featured'), findsOneWidget);
    expect(find.text('Reading the selected ROMD Catalog.'), findsOneWidget);
    expectCatalogNaming(tester);

    current = false;
    changes.invalidate();
    await tester.pump();

    expect(
      find.byKey(const ValueKey<String>('catalog-privacy-shield')),
      findsOneWidget,
    );
    expect(find.text('Connection changed'), findsOneWidget);
    expectCatalogNaming(tester);
  });

  testWidgets('Catalog failed and empty states carry no storefront naming', (
    tester,
  ) async {
    await pumpHome(
      tester,
      client: _FakeApi(failCatalog: true),
      catalogOperation: _operation(),
    );
    await openCatalog(tester);
    expect(find.text('Catalog unavailable'), findsOneWidget);
    expectCatalogNaming(tester);
    await press(tester, LogicalKeyboardKey.escape);
    await settle(tester);

    await pumpHome(tester, client: _FakeApi(), catalogOperation: _operation());
    await openCatalog(tester);
    expect(find.text('Nothing featured yet'), findsOneWidget);
    expect(
      find.text('This ROMD Catalog has no featured games right now.'),
      findsOneWidget,
    );
    expectCatalogNaming(tester);

    await selectDiscoverSection(tester, 'allGames');
    expect(
      find.text('This ROMD Catalog has no visible games.'),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('browse-system-all')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('browse-system-snes')),
      findsNothing,
    );
    expectCatalogNaming(tester);
  });

  testWidgets('Featured empty state routes into All Games', (tester) async {
    await pumpHome(
      tester,
      client: _FakeApi(allGames: <ConsoleGame>[_game('all', 'All Game')]),
      catalogOperation: _operation(),
    );
    await openCatalog(tester);

    expect(find.text('Nothing featured yet'), findsOneWidget);
    expect(focusedLabel(tester), 'catalog-featured-status-action');
    await press(tester, LogicalKeyboardKey.enter);
    await settle(tester);

    expect(find.text('All Game'), findsWidgets);
    expect(
      _isSemanticallySelected(
        tester,
        find.byKey(const ValueKey<String>('discover-section-allGames')),
      ),
      isTrue,
    );
    expect(focusedLabel(tester), 'browse-system-all');
  });

  testWidgets(
    'header previews without activation and restores cached section focus',
    (tester) async {
      final api = _FakeApi(
        topRated: <ConsoleGame>[
          for (var index = 0; index < 4; index++)
            _game('top-$index', 'Top $index'),
        ],
        platforms: const <ConsolePlatform>[
          ConsolePlatform(
            id: 'snes',
            name: 'Super Nintendo',
            shortName: 'SNES',
            manufacturer: 'Nintendo',
            titleCount: 2,
            coverUrl: null,
          ),
        ],
      );
      await pumpHome(tester, client: api, catalogOperation: _operation());
      await openCatalog(tester);

      // Discover opens directly on the first content card. Move to a stable id.
      await press(tester, LogicalKeyboardKey.arrowRight);
      await press(tester, LogicalKeyboardKey.arrowRight);
      await settle(tester);
      expect(focusedLabel(tester), 'catalog-featured-0-2');

      // Header focus previews another tab without changing the active surface.
      await press(tester, LogicalKeyboardKey.arrowUp);
      expect(focusedLabel(tester), 'discover-tab-featured');
      await press(tester, LogicalKeyboardKey.arrowRight);
      expect(focusedLabel(tester), 'discover-tab-browse');
      expect(
        tester
            .getSemantics(
              find.byKey(const ValueKey<String>('featured-card-top-0')),
            )
            .label,
        startsWith('Top 0'),
      );
      expect(
        find.byKey(const ValueKey<String>('all-games-grid')),
        findsNothing,
      );
      await press(tester, LogicalKeyboardKey.enter);
      await press(tester, LogicalKeyboardKey.arrowDown);
      expect(focusedLabel(tester), 'browse-system-all');
      await press(tester, LogicalKeyboardKey.arrowRight);
      expect(focusedLabel(tester), 'browse-system-snes');
      await press(tester, LogicalKeyboardKey.enter);
      await settle(tester);
      expect(
        find.descendant(
          of: find.byKey(const ValueKey<String>('browse-system-rail')),
          matching: find.text('SN'),
        ),
        findsOneWidget,
      );
      final callsAfterSystems = List<String>.of(api.calls);

      // Return to Featured: cards render from cache without repeated requests.
      await press(tester, LogicalKeyboardKey.arrowUp);
      expect(focusedLabel(tester), 'discover-tab-browse');
      await press(tester, LogicalKeyboardKey.arrowLeft);
      await press(tester, LogicalKeyboardKey.enter);
      expect(find.text('Opening Featured'), findsNothing);
      expect(
        tester
            .getSemantics(
              find.byKey(const ValueKey<String>('featured-card-top-0')),
            )
            .label,
        startsWith('Top 0'),
      );
      expect(api.calls, callsAfterSystems);

      // Down from the active header restores the remembered card.
      await press(tester, LogicalKeyboardKey.arrowDown);
      await settle(tester);
      expect(focusedLabel(tester), 'catalog-featured-0-2');
    },
  );

  testWidgets(
    'Catalog failure uses product copy and preserves local launcher',
    (tester) async {
      final api = _FakeApi(failCatalog: true);
      await pumpHome(tester, client: api, catalogOperation: _operation());
      await openCatalog(tester);

      expect(find.text('Catalog unavailable'), findsOneWidget);
      expect(
        find.text(
          'The selected ROMD server could not provide this view. '
          'Your local games are unaffected.',
        ),
        findsOneWidget,
      );
      expect(find.textContaining('StateError'), findsNothing);

      await press(tester, LogicalKeyboardKey.escape);
      await settle(tester);
      expect(find.text('Catalog unavailable'), findsNothing);
      expect(
        find.byKey(const ValueKey<String>('dock-library')),
        findsOneWidget,
      );
    },
  );

  testWidgets('D-pad selects a system filter and enters its game grid', (
    tester,
  ) async {
    await pumpHome(
      tester,
      client: _FakeApi(
        platforms: const <ConsolePlatform>[
          ConsolePlatform(
            id: 'snes',
            name: 'Super Nintendo',
            shortName: 'SNES',
            manufacturer: 'Nintendo',
            titleCount: 2,
            coverUrl: null,
          ),
        ],
        allGames: <ConsoleGame>[_game('system-game', 'System Game')],
      ),
      catalogOperation: _operation(),
    );
    await openCatalog(tester);

    await selectDiscoverSection(tester, 'allGames');
    expect(focusedLabel(tester), 'browse-system-all');
    await press(tester, LogicalKeyboardKey.arrowRight);
    expect(focusedLabel(tester), 'browse-system-snes');
    await press(tester, LogicalKeyboardKey.enter);
    await settle(tester);
    expect(focusedLabel(tester), 'browse-system-snes');
    await press(tester, LogicalKeyboardKey.arrowDown);
    expect(focusedLabel(tester), 'all-games-filter-trigger');
    await press(tester, LogicalKeyboardKey.arrowDown);
    expect(find.text('System Game'), findsWidgets);
    expect(focusedLabel(tester), 'all-games-system-game');
  });

  testWidgets(
    'gamepad and hardware input open Search result and restore focus',
    (tester) async {
      final events = StreamController<NormalizedGamepadEvent>();
      final pad = ConnectedGamepad.fallback(
        id: 'pad-1',
        name: 'Test Pad',
        order: 0,
      );
      final navigator = GamepadNavigator(
        onInputModeChanged: (_) {},
        controllerInputProvider: CallbackControllerInputProvider(
          listGamepads: () async => <ConnectedGamepad>[pad],
          events: () => events.stream,
        ),
      );
      addTearDown(navigator.dispose);
      addTearDown(events.close);

      Future<void> gamepadPress(GamepadButton button) async {
        events.add(_gamepadButton(button, 1));
        await tester.pump();
        events.add(_gamepadButton(button, 0));
        await tester.pump();
      }

      await pumpHome(
        tester,
        client: _FakeApi(
          topRated: <ConsoleGame>[_game('featured', 'Featured Game')],
          allGames: <ConsoleGame>[_game('result', 'Search Result')],
        ),
        catalogOperation: _operation(),
      );

      await gamepadPress(GamepadButton.dpadDown);
      expect(focusedLabel(tester), 'dock-library');
      await gamepadPress(GamepadButton.dpadRight);
      expect(focusedLabel(tester), 'dock-catalog');
      await gamepadPress(GamepadButton.a);
      await settle(tester);
      expect(focusedLabel(tester), 'catalog-featured-0-0');
      final featuredFocus = tester.binding.focusManager.primaryFocus;
      await gamepadPress(GamepadButton.dpadUp);
      expect(focusedLabel(tester), 'discover-tab-featured');
      await gamepadPress(GamepadButton.dpadDown);
      expect(tester.binding.focusManager.primaryFocus, same(featuredFocus));
      await gamepadPress(GamepadButton.x);
      await settle(tester);

      // Gamepad A activates the focused on-screen A key; a physical key
      // then proves both input paths feed the same query.
      await gamepadPress(GamepadButton.a);
      await tester.sendKeyDownEvent(LogicalKeyboardKey.keyB, character: 'b');
      await tester.sendKeyUpEvent(LogicalKeyboardKey.keyB);
      await tester.pump(const Duration(milliseconds: 350));
      await settle(tester);
      expect(find.text('ab'), findsOneWidget);
      expect(find.text('Search Result'), findsOneWidget);

      // RB enters the result zone without disturbing the typed query.
      await gamepadPress(GamepadButton.rightBumper);
      expect(focusedLabel(tester), 'search-result-result');
      final resultFocus = tester.binding.focusManager.primaryFocus;

      await gamepadPress(GamepadButton.a);
      await settle(tester);
      expect(find.byType(GameDetailScreen), findsOneWidget);

      await gamepadPress(GamepadButton.b);
      await settle(tester);
      expect(find.text('Search Result'), findsOneWidget);
      expect(tester.binding.focusManager.primaryFocus, same(resultFocus));

      await gamepadPress(GamepadButton.b);
      await settle(tester);
      expect(find.text('Catalog'), findsOneWidget);
      expect(focusedLabel(tester), 'catalog-featured-0-0');
    },
  );

  testWidgets('Catalog clears preloaded metadata on notifier invalidation', (
    tester,
  ) async {
    var current = true;
    final changes = _CatalogChanges();
    addTearDown(changes.dispose);
    await pumpHome(
      tester,
      client: _FakeApi(topRated: <ConsoleGame>[_game('top', 'Former Pick')]),
      catalogOperation: _operation(isCurrent: () => current, changes: changes),
    );
    await openCatalog(tester);
    expect(find.text('Former Pick'), findsWidgets);

    current = false;
    changes.invalidate();
    await tester.pump();

    expect(
      find.byKey(const ValueKey<String>('catalog-privacy-shield')),
      findsOneWidget,
    );
    expect(find.text('Former Pick'), findsNothing);
  });

  testWidgets(
    'nested Catalog detail is privacy-shielded before route teardown',
    (tester) async {
      var current = true;
      final changes = _CatalogChanges();
      addTearDown(changes.dispose);
      final api = _FakeApi(allGames: <ConsoleGame>[_game('a', 'Allowed Card')]);
      await pumpHome(
        tester,
        client: api,
        catalogOperation: _operation(
          isCurrent: () => current,
          changes: changes,
        ),
      );
      await openCatalog(tester);
      await selectDiscoverSection(tester, 'allGames');
      tester
          .widget<GameTile>(
            find.byWidgetPredicate(
              (widget) =>
                  widget is GameTile && widget.game.title == 'Allowed Card',
            ),
          )
          .onPressed();
      await settle(tester);
      await press(tester, LogicalKeyboardKey.arrowUp);
      await press(tester, LogicalKeyboardKey.enter);
      await settle(tester);
      expect(find.text('ABOUT'), findsOneWidget);
      expect(find.text('Former authority description'), findsWidgets);

      current = false;
      changes.invalidate();
      await tester.pump();

      expect(
        find.byKey(const ValueKey<String>('catalog-privacy-shield')),
        findsWidgets,
      );
      expect(find.text('Former authority description'), findsNothing);
      await settle(tester);
      expect(find.byType(GameDetailScreen), findsNothing);
    },
  );

  testWidgets('stale detail cancellation completes the active install stream', (
    tester,
  ) async {
    var current = true;
    final changes = _CatalogChanges();
    final cancelled = Completer<void>();
    late StreamController<InstallProgress> controller;
    controller = StreamController<InstallProgress>(
      onListen: () => controller.add(const InstallStarted()),
      onCancel: () {
        if (!cancelled.isCompleted) cancelled.complete();
      },
    );
    addTearDown(changes.dispose);
    addTearDown(controller.close);
    final install = _FakeInstall(const <String>{}, controller.stream);
    await pumpHome(
      tester,
      client: _FakeApi(allGames: <ConsoleGame>[_game('a', 'Allowed Card')]),
      install: install,
      catalogOperation: _operation(isCurrent: () => current, changes: changes),
    );
    await openCatalog(tester);
    await selectDiscoverSection(tester, 'allGames');
    tester
        .widget<GameTile>(
          find.byWidgetPredicate(
            (widget) =>
                widget is GameTile && widget.game.title == 'Allowed Card',
          ),
        )
        .onPressed();
    await settle(tester);
    await tester.tap(find.text('Install'));
    await tester.pump();
    expect(install.installCalls, 1);

    current = false;
    changes.invalidate();
    await tester.pump();
    await cancelled.future;
    await tester.pump();

    expect(
      find.byKey(const ValueKey<String>('catalog-privacy-shield')),
      findsWidgets,
    );
    expect(tester.takeException(), isNull);
  });

  testWidgets(
    'foreign Catalog detail identity is rejected before acquisition',
    (tester) async {
      final install = _FakeInstall();
      await pumpHome(
        tester,
        client: _FakeApi(
          allGames: <ConsoleGame>[_game('a', 'Allowed Card')],
          detailGate: Completer<ConsoleGameDetail>()
            ..complete(_detail('foreign', 'Foreign Title')),
        ),
        install: install,
        catalogOperation: _operation(),
      );
      await openCatalog(tester);
      await selectDiscoverSection(tester, 'allGames');
      tester
          .widget<GameTile>(
            find.byWidgetPredicate(
              (widget) =>
                  widget is GameTile && widget.game.title == 'Allowed Card',
            ),
          )
          .onPressed();
      await settle(tester);

      expect(find.text('Foreign Title'), findsNothing);
      expect(find.text('Former authority description'), findsNothing);
      await tester.tap(find.text('Install'));
      await settle(tester);
      expect(install.installCalls, 0);
    },
  );

  testWidgets('Browse system rail follows every platform cursor', (
    tester,
  ) async {
    final api = _FakeApi(
      platformPages: const <ConsolePlatformPage>[
        ConsolePlatformPage(
          items: <ConsolePlatform>[
            ConsolePlatform(
              id: 'snes',
              name: 'Super Nintendo',
              shortName: 'SNES',
              manufacturer: 'Nintendo',
              titleCount: 100,
              coverUrl: null,
            ),
          ],
          nextCursor: 'next',
          hasNextPage: true,
        ),
        ConsolePlatformPage(
          items: <ConsolePlatform>[
            ConsolePlatform(
              id: 'psx',
              name: 'PlayStation',
              shortName: 'PSX',
              manufacturer: 'Sony',
              titleCount: 20,
              coverUrl: null,
            ),
          ],
          nextCursor: null,
          hasNextPage: false,
        ),
      ],
    );
    await pumpHome(tester, client: api, catalogOperation: _operation());
    await openCatalog(tester);
    await selectDiscoverSection(tester, 'allGames');

    expect(
      find.byKey(const ValueKey<String>('browse-system-snes')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('browse-system-psx')),
      findsOneWidget,
    );
    expect(api.platformPageCalls, 2);
  });

  testWidgets(
    'stale Catalog response never renders former authority metadata',
    (tester) async {
      var current = true;
      final gated = Completer<ConsoleGamePage>();
      final api = _FakeApi(topRatedGate: gated);
      await pumpHome(
        tester,
        client: api,
        catalogOperation: _operation(isCurrent: () => current),
      );
      await tester.tap(find.byKey(const ValueKey<String>('dock-catalog')));
      await tester.pump();
      current = false;
      gated.complete(
        ConsoleGamePage(
          items: <ConsoleGame>[_game('restricted', 'Former Library Game')],
          nextCursor: null,
          hasNextPage: false,
        ),
      );
      await settle(tester);

      expect(find.text('Former Library Game'), findsNothing);
    },
  );

  testWidgets('stale Catalog detail closes before late metadata can render', (
    tester,
  ) async {
    var current = true;
    final detail = Completer<ConsoleGameDetail>();
    final api = _FakeApi(
      allGames: <ConsoleGame>[_game('a', 'Allowed Card')],
      detailGate: detail,
    );
    await pumpHome(
      tester,
      client: api,
      catalogOperation: _operation(isCurrent: () => current),
    );
    await openCatalog(tester);
    await selectDiscoverSection(tester, 'allGames');
    expect(find.text('Allowed Card'), findsWidgets);
    tester
        .widget<GameTile>(
          find.byWidgetPredicate(
            (widget) =>
                widget is GameTile && widget.game.title == 'Allowed Card',
          ),
        )
        .onPressed();
    await settle(tester);
    expect(find.byType(GameDetailScreen), findsOneWidget);

    current = false;
    detail.complete(_detail('a', 'Former detail'));
    await tester.pump(const Duration(milliseconds: 300));
    await settle(tester);

    expect(find.byType(GameDetailScreen), findsNothing);
    expect(find.text('Former detail'), findsNothing);
  });

  testWidgets('surfaces close when the profile context is cleared', (
    tester,
  ) async {
    final api = _FakeApi();
    final playServices = _playServices(
      FakeProfileLocalLibraryRepository(
        library: ProfileLocalLibraryReady(<ProfileLocalLibraryTitle>[
          _title('a', 'Alpha', playedAt: DateTime.utc(2026, 7, 2)),
        ]),
      ),
    );
    final surfaceContext = ValueNotifier<HomeSurfaceContext?>(
      HomeSurfaceContext(
        localProfileId: _profile.id,
        consumerApiClient: api,
        playServices: playServices,
      ),
    );
    addTearDown(surfaceContext.dispose);
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: ConsoleHomeScreen(
          consumerApiClient: api,
          playServices: playServices,
          localProfile: _profile,
          session: null,
          surfaceContext: surfaceContext,
          onSwitchProfile: () {},
          onExitApp: () {},
          onConnect: () {},
          onOpenControllers: () {},
          onOpenSettings: () {},
        ),
      ),
    );
    await settle(tester);
    await tester.tap(find.byKey(const ValueKey<String>('dock-library')));
    await settle(tester);
    expect(find.text('1 LOCAL GAME'), findsOneWidget);

    surfaceContext.value = null;
    await settle(tester);

    expect(find.text('1 LOCAL GAME'), findsNothing);
    expect(find.byKey(const ValueKey<String>('dock-library')), findsOneWidget);
  });
}

NormalizedGamepadEvent _gamepadButton(GamepadButton button, double value) =>
    NormalizedGamepadEvent(
      gamepadId: 'pad-1',
      timestamp: 0,
      value: value,
      button: button,
      rawEvent: GamepadEvent(
        gamepadId: 'pad-1',
        timestamp: 0,
        type: KeyType.button,
        key: 'raw',
        value: value,
      ),
    );

bool _isSemanticallySelected(WidgetTester tester, Finder control) => tester
    .widgetList<Semantics>(
      find.descendant(of: control, matching: find.byType(Semantics)),
    )
    .any((semantics) => semantics.properties.selected == true);

final _profile = LocalProfile(
  id: 'p',
  displayName: 'John',
  avatarKey: 'orbit',
  accentColor: 0xff1fbf8f,
  romdServerOrigin: Uri.parse('https://shelf.local'),
  entryMode: LocalProfileEntryMode.open,
  createdAt: DateTime(2026),
  updatedAt: DateTime(2026),
);

final _session = ConsumerLoginSession(
  token: 't',
  tokenType: 'Bearer',
  refreshToken: 'r',
  expiresAt: DateTime(2999),
  account: const ConsumerAccount(id: 'u', username: 'j', email: 'j@x'),
);

final _instanceId = RomdServerInstanceId.tryParse(
  '11111111-1111-4111-8111-111111111111',
)!;

CatalogOperationContext _operation({
  bool Function()? isCurrent,
  Listenable? changes,
  int epoch = 1,
}) => CatalogOperationContext(
  authority: InstallAuthorityContext(
    localProfileId: _profile.id,
    connection: ProfileServerConnection(
      instanceId: _instanceId,
      origin: Uri.parse('https://shelf.local'),
      firstSeenAt: DateTime.utc(2026),
      lastSeenAt: DateTime.utc(2026),
    ),
    generation: 1,
  ),
  accessToken: _session.token,
  epoch: epoch,
  isCurrent: isCurrent ?? () => true,
  changes: changes,
);

final class _CatalogChanges extends ChangeNotifier {
  void invalidate() => notifyListeners();
}

ConsoleGame _game(String id, String title) => ConsoleGame(
  id: id,
  platformId: 'snes',
  platformName: 'SNES',
  title: title,
  releaseDate: DateTime.utc(1995),
  coverUrl: null,
  genre: 'RPG',
  rating: 9,
  releaseCount: 1,
  defaultReleaseId: '$id-r',
);

ConsoleGameDetail _detail(
  String id,
  String title, {
  String description = 'Former authority description',
  String genre = 'RPG',
  int? players,
}) => ConsoleGameDetail(
  id: id,
  platformId: 'snes',
  platformName: 'SNES',
  title: title,
  description: description,
  publisher: null,
  developer: null,
  genre: genre,
  releaseDate: null,
  players: players,
  rating: 9,
  media: const <ConsoleMediaRef>[],
  releases: <ConsoleRelease>[
    ConsoleRelease(
      id: '$id-r',
      name: title,
      revision: null,
      regions: const <String>[],
      languages: const <String>[],
      sizeBytes: 5,
      isComplete: true,
    ),
  ],
  defaultReleaseId: '$id-r',
);

ProfileLocalLibraryTitle _title(
  String id,
  String name, {
  DateTime? playedAt,
  ProfileGameAuthorization authorization = ProfileGameAuthorization.authorized,
  InstallState state = InstallState.installed,
  String platformId = 'snes',
  String platformName = 'SNES',
  String platformShortName = 'snes',
}) => ProfileLocalLibraryTitle(
  install: _install(
    id,
    name,
    state: state,
    platformId: platformId,
    platformName: platformName,
    platformShortName: platformShortName,
  ),
  authorization: authorization,
  state: authorization == ProfileGameAuthorization.revoked
      ? ProfileLocalLibraryState.accessRequired
      : switch (state) {
          InstallState.installed => ProfileLocalLibraryState.ready,
          InstallState.corrupt ||
          InstallState.installFailed => ProfileLocalLibraryState.repairRequired,
          _ => ProfileLocalLibraryState.notReady,
        },
  releaseCount: 1,
  acquiredAt: DateTime.utc(2026, 6, id == 'a' ? 30 : 29),
  lastPlayedAt: playedAt,
  playCount: playedAt == null ? 0 : 1,
);

LocalInstall _install(
  String id,
  String name, {
  InstallState state = InstallState.installed,
  String platformId = 'snes',
  String platformName = 'SNES',
  String platformShortName = 'snes',
}) => LocalInstall(
  serverInstanceId: _instanceId.value,
  releaseId: '$id-r',
  titleId: id,
  titleName: name,
  platformId: platformId,
  platformName: platformName,
  platformShortName: platformShortName,
  coverUrl: null,
  releaseName: name,
  releaseRevision: null,
  contentRoot: '/content/$id',
  launchRelativePath: '$id.sfc',
  sizeBytes: 5,
  primarySha256: null,
  manifestFingerprint: 'fp-$id',
  state: state,
  installMode: 'permanent',
  items: <InstalledItem>[
    InstalledItem(relativePath: '$id.sfc', sizeBytes: 5, sha256: null),
  ],
  installedAt: DateTime.utc(2026, 6, id == 'a' ? 30 : 29),
  lastPlayedAt: null,
);

PlayServices _playServices(
  ProfileLocalLibraryRepository localLibrary, {
  InstallService? install,
}) => PlayServices(
  install: install ?? _FakeInstall(),
  localLibrary: localLibrary,
  playHistory: const _FakeHistory(),
  coordinator: _FakeCoordinator(),
  runtimeResolver: const LocalRuntimeResolver(),
  runtimeRules: const _NoopRules(),
  activeLaunchSession: ActiveLaunchSession(),
  controllerPreferences: InMemoryControllerPreferences(),
  controllerSlotClaims: SessionControllerSlotClaims(),
);

final class _FakeInstall implements InstallService {
  _FakeInstall([
    this.installedReleaseIds = const <String>{},
    this.installStream,
  ]);

  final Set<String> installedReleaseIds;
  final Stream<InstallProgress>? installStream;
  int installCalls = 0;

  @override
  Future<LocalInstall?> findInstall(String releaseId) async => null;
  @override
  Future<List<LocalInstall>> listInstalled() async => const <LocalInstall>[];
  @override
  Future<List<LocalInstall>> listInstalledForTitle(String titleId) async =>
      const <LocalInstall>[];
  @override
  Stream<List<LocalInstall>> watchInstalled() =>
      Stream<List<LocalInstall>>.value(const <LocalInstall>[]);
  @override
  Stream<List<LocalInstall>> watchInstalledForTitle(String titleId) =>
      Stream<List<LocalInstall>>.value(const <LocalInstall>[]);
  @override
  Stream<Set<String>> watchInstalledReleaseIds() =>
      Stream<Set<String>>.value(installedReleaseIds);
  @override
  Stream<InstallProgress> install(
    PlayTarget target, {
    required InstallOperationLease operation,
    String? accessToken,
  }) {
    installCalls++;
    return installStream ?? const Stream<InstallProgress>.empty();
  }

  @override
  Future<void> uninstall(
    String releaseId, {
    InstallOperationLease? operation,
  }) async {}
}

final class _FakeHistory implements ProfilePlayHistoryRepository {
  const _FakeHistory();

  @override
  Future<ProfilePlayHistoryReadResult> find({
    required String localProfileId,
    required RomdServerInstanceId serverInstanceId,
    required RomdPublicId titleId,
  }) async => const ProfilePlayHistoryNotFound();
  @override
  Future<ProfilePlayHistoryRecordResult> recordEnded({
    required ResolvedPlayTarget target,
    required DateTime endedAt,
  }) async => ProfilePlayHistoryRecordResult.recorded;
  @override
  Stream<ProfileRecentGamesResult> watchRecent() =>
      Stream<ProfileRecentGamesResult>.value(
        const ProfileRecentGamesReady(<ProfileRecentGame>[]),
      );
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

final class _FakeApi implements ConsumerApiClient {
  @override
  Future<void> revokeSession({required String refreshToken}) async {}

  _FakeApi({
    this.topRated = const <ConsoleGame>[],
    this.allGames = const <ConsoleGame>[],
    this.collections = const <ConsoleCollection>[],
    this.collectionGames = const <ConsoleGame>[],
    this.platforms = const <ConsolePlatform>[],
    this.platformPages,
    this.topRatedGate,
    this.detailGate,
    this.detailFutures = const <String, Future<ConsoleGameDetail>>{},
    this.failCatalog = false,
  });

  final List<String> calls = <String>[];
  final List<ConsoleGame> topRated;
  final List<ConsoleGame> allGames;
  final List<ConsoleCollection> collections;
  final List<ConsoleGame> collectionGames;
  final List<ConsolePlatform> platforms;
  final List<ConsolePlatformPage>? platformPages;
  final Completer<ConsoleGamePage>? topRatedGate;
  final Completer<ConsoleGameDetail>? detailGate;
  final Map<String, Future<ConsoleGameDetail>> detailFutures;
  final bool failCatalog;
  int platformPageCalls = 0;
  int detailCalls = 0;

  @override
  Future<ConsumerHostHealth> getHealth() async {
    calls.add('health');
    return const ConsumerHostHealth(status: 'Healthy');
  }

  @override
  Future<ConsoleGamePage> searchCatalog({
    required String accessToken,
    String? query,
    String? platformId,
    String? completeness,
    String? sortBy,
    String? cursor,
    int limit = 48,
  }) async {
    calls.add('catalog');
    if (failCatalog) throw StateError('sensitive transport details');
    if (sortBy == 'rating' && topRatedGate != null) {
      return topRatedGate!.future;
    }
    return ConsoleGamePage(
      items: sortBy == 'rating' ? topRated : allGames,
      nextCursor: null,
      hasNextPage: false,
    );
  }

  @override
  Future<ConsolePlatformPage> listPlatforms({
    required String accessToken,
    String? cursor,
    int limit = 50,
  }) async {
    calls.add('platforms');
    final pages = platformPages;
    if (pages != null) {
      final index = platformPageCalls++;
      return pages[index.clamp(0, pages.length - 1).toInt()];
    }
    return ConsolePlatformPage(
      items: platforms,
      nextCursor: null,
      hasNextPage: false,
    );
  }

  @override
  Future<List<ConsoleCollection>> listCollections({
    required String accessToken,
    String? platformId,
  }) async {
    calls.add('collections');
    return collections;
  }

  @override
  Future<ConsoleGamePage> listCollectionTitles({
    required String accessToken,
    required String collectionId,
    String? cursor,
    int limit = 24,
  }) async {
    calls.add('collectionTitles');
    return ConsoleGamePage(
      items: collectionGames,
      nextCursor: null,
      hasNextPage: false,
    );
  }

  @override
  Future<ConsoleGameDetail> getTitle({
    required String accessToken,
    required String titleId,
  }) async {
    detailCalls++;
    return detailFutures[titleId] ??
        detailGate?.future ??
        _detail(titleId, 'Title $titleId');
  }

  @override
  Future<ConsoleReleaseManifest> issueReleaseManifest({
    required String accessToken,
    required String releaseId,
  }) async => throw StateError('not used');
  @override
  Future<List<BiosFileListing>> listPlatformBios({
    required String accessToken,
    required String platformShortName,
  }) async => const <BiosFileListing>[];
  @override
  Future<DeviceAuthorizationResult> requestDeviceAuthorization() async =>
      const DeviceAuthorizationFailure('not used');
  @override
  Future<DeviceTokenResult> redeemDeviceCode({
    required String deviceCode,
  }) async => const DeviceTokenFailure('not used');
  @override
  Future<ConsumerLoginResult> refreshSession({
    required String refreshToken,
  }) async => const ConsumerLoginFailure('not used');
  @override
  void close() {}
}
