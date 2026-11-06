import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/players_projection.dart';
import 'package:romd_console/src/presentation/launcher/launcher_primitives.dart';
import 'package:romd_console/src/presentation/theme/console_theme_extensions.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_ambient_background.dart';
import 'package:romd_console/src/presentation/widgets/console_circle_button.dart';
import 'package:romd_console/src/presentation/widgets/console_clock.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';
import 'package:romd_console/src/presentation/widgets/controller_setup_workspace.dart';
import 'package:romd_console/src/presentation/widgets/cover_art.dart';
import 'package:romd_console/src/presentation/widgets/game_record.dart';
import 'package:romd_console/src/presentation/widgets/game_tile.dart';
import 'package:romd_console/src/presentation/widgets/guest_profile_assignment_screen.dart';
import 'package:romd_console/src/presentation/widgets/media_viewer_screen.dart';
import 'package:romd_console/src/presentation/widgets/meta_chip.dart';
import 'package:romd_console/src/presentation/widgets/on_screen_keyboard.dart';
import 'package:romd_console/src/presentation/widgets/platform_badge.dart';
import 'package:romd_console/src/presentation/widgets/players_panel.dart';
import 'package:romd_console/src/presentation/widgets/players_session_cluster.dart';
import 'package:romd_console/src/presentation/widgets/profile_avatar.dart';

void main() {
  testWidgets(
    'theme mode updates a mounted focus painter and animation controller',
    (tester) async {
      final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
      addTearDown(mode.dispose);

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
          child: const Scaffold(
            body: Center(
              child: ConsoleFocusRing(
                focused: true,
                child: SizedBox.square(dimension: 96),
              ),
            ),
          ),
        ),
      );
      await tester.pump();

      final focusRing = find.byType(ConsoleFocusRing);
      final animatedBuilder = find.descendant(
        of: focusRing,
        matching: find.byType(AnimatedBuilder),
      );
      final customPaint = find.descendant(
        of: focusRing,
        matching: find.byType(CustomPaint),
      );
      final elementBefore = tester.element(focusRing);
      final controllerBefore =
          tester.widget<AnimatedBuilder>(animatedBuilder).animation
              as AnimationController;
      final painterBefore = tester
          .widget<CustomPaint>(customPaint)
          .foregroundPainter!;

      expect(
        controllerBefore.duration,
        RomdSkins.baselineDark().extension<ConsoleMotionTheme>()!.focusRotation,
      );

      mode.value = ThemeMode.light;
      await tester.pump();

      final elementAfter = tester.element(focusRing);
      final controllerAfter =
          tester.widget<AnimatedBuilder>(animatedBuilder).animation
              as AnimationController;
      final painterAfter = tester
          .widget<CustomPaint>(customPaint)
          .foregroundPainter!;

      expect(identical(elementBefore, elementAfter), isTrue);
      expect(identical(controllerBefore, controllerAfter), isTrue);
      expect(
        controllerAfter.duration,
        RomdSkins.seamTestTheme()
            .extension<ConsoleMotionTheme>()!
            .focusRotation,
      );
      expect(painterAfter.shouldRepaint(painterBefore), isTrue);
    },
  );

  testWidgets('focus motion resolves to zero when motion is disabled', (
    tester,
  ) async {
    late BuildContext context;
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: const MediaQueryData(disableAnimations: true),
          child: Builder(
            builder: (builderContext) {
              context = builderContext;
              return const SizedBox.shrink();
            },
          ),
        ),
      ),
    );

    final motion = Theme.of(context).extension<ConsoleMotionTheme>()!;
    expect(motion.resolve(context, motion.focus), Duration.zero);
  });

  testWidgets('cover tones and catalog accents re-resolve at runtime', (
    tester,
  ) async {
    final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    addTearDown(mode.dispose);

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
        child: const Scaffold(
          body: Center(
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: <Widget>[
                SizedBox(
                  width: 120,
                  height: 160,
                  child: CoverArt(
                    coverUrl: null,
                    platformId: 'snes',
                    platformName: 'Super Nintendo',
                    title: 'Chrono Trigger',
                  ),
                ),
                PlatformBadge(label: 'SNES'),
              ],
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    final cover = find.byType(CoverArt);
    final coverElementBefore = tester.element(cover);
    final coverGradientBefore = _coverGradient(tester);
    final badgeColorBefore = _platformBadgeDecoration(tester).color;

    mode.value = ThemeMode.light;
    await tester.pump();

    expect(identical(coverElementBefore, tester.element(cover)), isTrue);
    final coverGradientAfter = _coverGradient(tester);
    expect(
      coverGradientAfter.colors,
      isNot(orderedEquals(coverGradientBefore.colors)),
    );
    expect(
      coverGradientAfter.stops,
      isNot(orderedEquals(coverGradientBefore.stops!)),
    );
    expect(_platformBadgeDecoration(tester).color, isNot(badgeColorBefore));
  });

  testWidgets('launcher controls re-resolve type, shape, spacing, and motion', (
    tester,
  ) async {
    final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    final node = FocusNode();
    addTearDown(mode.dispose);
    addTearDown(node.dispose);

    await tester.pumpWidget(
      ValueListenableBuilder<ThemeMode>(
        valueListenable: mode,
        builder: (context, themeMode, _) => MaterialApp(
          theme: RomdSkins.seamTestTheme(),
          darkTheme: RomdSkins.baselineDark(),
          themeMode: themeMode,
          themeAnimationDuration: Duration.zero,
          home: Scaffold(
            body: Center(
              child: LauncherNavChip(
                label: 'ALL GAMES',
                node: node,
                selected: true,
                onActivate: () {},
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    final animatedContainer = find.byType(AnimatedContainer);
    final elementBefore = tester.element(animatedContainer);
    final containerBefore = tester.widget<AnimatedContainer>(animatedContainer);
    final decorationBefore = containerBefore.decoration! as BoxDecoration;
    final textBefore = tester.widget<Text>(find.text('ALL GAMES')).style!;

    mode.value = ThemeMode.light;
    await tester.pump();

    final containerAfter = tester.widget<AnimatedContainer>(animatedContainer);
    final decorationAfter = containerAfter.decoration! as BoxDecoration;
    final textAfter = tester.widget<Text>(find.text('ALL GAMES')).style!;
    expect(identical(elementBefore, tester.element(animatedContainer)), isTrue);
    expect(containerAfter.padding, isNot(containerBefore.padding));
    expect(containerAfter.duration, isNot(containerBefore.duration));
    expect(decorationAfter.borderRadius, isNot(decorationBefore.borderRadius));
    expect(decorationAfter.color, isNot(decorationBefore.color));
    expect(textAfter.fontFamily, isNot(textBefore.fontFamily));
    expect(textAfter.fontSize, isNot(textBefore.fontSize));
  });

  testWidgets('game tiles re-resolve shared interaction character', (
    tester,
  ) async {
    final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    addTearDown(mode.dispose);
    final game = ConsoleGame(
      id: 'interaction-theme',
      platformId: 'snes',
      platformName: 'SNES',
      title: 'Interaction Theme',
      releaseDate: DateTime(1995),
      coverUrl: null,
      genre: 'Action',
      rating: 8,
      releaseCount: 1,
      defaultReleaseId: 'interaction-theme-release',
    );

    await tester.pumpWidget(
      ValueListenableBuilder<ThemeMode>(
        valueListenable: mode,
        builder: (context, themeMode, _) => MaterialApp(
          theme: RomdSkins.seamTestTheme(),
          darkTheme: RomdSkins.baselineDark(),
          themeMode: themeMode,
          themeAnimationDuration: Duration.zero,
          home: Scaffold(
            body: SizedBox(
              width: 160,
              child: GameTile(
                game: game,
                heroTag: 'interaction-theme',
                rowActive: true,
                onPressed: () {},
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    final tile = find.byType(GameTile);
    final scaleFinder = find.descendant(
      of: tile,
      matching: find.byType(AnimatedScale),
    );
    final opacityFinder = find.descendant(
      of: tile,
      matching: find.byType(AnimatedOpacity),
    );
    final elementBefore = tester.element(tile);
    final scaleBefore = tester.widget<AnimatedScale>(scaleFinder).scale;
    final opacityBefore = tester.widget<AnimatedOpacity>(opacityFinder).opacity;

    mode.value = ThemeMode.light;
    await tester.pump();

    expect(identical(elementBefore, tester.element(tile)), isTrue);
    expect(tester.widget<AnimatedScale>(scaleFinder).scale, isNot(scaleBefore));
    expect(
      tester.widget<AnimatedOpacity>(opacityFinder).opacity,
      isNot(opacityBefore),
    );
  });

  testWidgets('mounted clock re-resolves typography and chrome motion', (
    tester,
  ) async {
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
          home: ConsoleClock(
            now: () => DateTime(2026, 7, 10, 14, 51),
            compact: true,
          ),
        ),
      ),
    );
    await tester.pump();

    final animatedStyle = find.byType(AnimatedDefaultTextStyle);
    final elementBefore = tester.element(animatedStyle);
    final before = tester.widget<AnimatedDefaultTextStyle>(animatedStyle);

    mode.value = ThemeMode.light;
    await tester.pump();

    final after = tester.widget<AnimatedDefaultTextStyle>(animatedStyle);
    expect(identical(elementBefore, tester.element(animatedStyle)), isTrue);
    expect(after.duration, isNot(before.duration));
    expect(after.style.fontSize, isNot(before.style.fontSize));
    expect(after.style.letterSpacing, isNot(before.style.letterSpacing));
    expect(after.style.color, isNot(before.style.color));
  });

  testWidgets('mounted ambient painter rebuilds from the active artwork skin', (
    tester,
  ) async {
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
          home: const MediaQuery(
            data: MediaQueryData(disableAnimations: true),
            child: ConsoleAmbientBackground(),
          ),
        ),
      ),
    );
    await tester.pump();

    final ambient = find.byType(ConsoleAmbientBackground);
    final paint = find.descendant(
      of: ambient,
      matching: find.byType(CustomPaint),
    );
    final backdrop = find
        .descendant(of: ambient, matching: find.byType(DecoratedBox))
        .first;
    final ambientElement = tester.element(ambient);
    final painterBefore = tester.widget<CustomPaint>(paint).painter!;
    final gradientBefore =
        (tester.widget<DecoratedBox>(backdrop).decoration as BoxDecoration)
                .gradient!
            as RadialGradient;

    mode.value = ThemeMode.light;
    await tester.pump();

    final painterAfter = tester.widget<CustomPaint>(paint).painter!;
    final gradientAfter =
        (tester.widget<DecoratedBox>(backdrop).decoration as BoxDecoration)
                .gradient!
            as RadialGradient;
    expect(identical(ambientElement, tester.element(ambient)), isTrue);
    expect(painterAfter.shouldRepaint(painterBefore), isTrue);
    expect(gradientAfter.colors, isNot(orderedEquals(gradientBefore.colors)));
    expect(gradientAfter.center, isNot(gradientBefore.center));
  });

  testWidgets(
    'shared controls re-resolve shape, elevation, metrics, and type',
    (tester) async {
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
            home: Scaffold(
              body: Row(
                children: <Widget>[
                  ConsoleCircleButton(onTap: () {}, selected: true),
                  const MetaChip(label: '1995', icon: Icons.calendar_today),
                ],
              ),
            ),
          ),
        ),
      );
      await tester.pump();

      final circle = find.byType(AnimatedContainer);
      final circleElement = tester.element(circle);
      final circleBefore = tester.widget<AnimatedContainer>(circle);
      final decorationBefore = circleBefore.decoration! as BoxDecoration;
      final chipTextBefore = tester.widget<Text>(find.text('1995')).style!;

      mode.value = ThemeMode.light;
      await tester.pump();

      final circleAfter = tester.widget<AnimatedContainer>(circle);
      final decorationAfter = circleAfter.decoration! as BoxDecoration;
      final chipTextAfter = tester.widget<Text>(find.text('1995')).style!;
      expect(identical(circleElement, tester.element(circle)), isTrue);
      expect(circleAfter.constraints, isNot(circleBefore.constraints));
      expect(circleAfter.duration, isNot(circleBefore.duration));
      expect(decorationAfter.shape, isNot(decorationBefore.shape));
      expect(decorationAfter.boxShadow, isNot(decorationBefore.boxShadow));
      expect(chipTextAfter.fontSize, isNot(chipTextBefore.fontSize));
      expect(chipTextAfter.letterSpacing, isNot(chipTextBefore.letterSpacing));
    },
  );

  testWidgets('mounted media viewer re-resolves backdrop and counter type', (
    tester,
  ) async {
    final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    addTearDown(mode.dispose);
    final media = <ConsoleMediaRef>[
      ConsoleMediaRef(
        id: 'one',
        type: 'screenshot',
        url: Uri.parse('https://media.invalid/one.png'),
        isPrimary: true,
      ),
      ConsoleMediaRef(
        id: 'two',
        type: 'screenshot',
        url: Uri.parse('https://media.invalid/two.png'),
        isPrimary: false,
      ),
    ];

    await tester.pumpWidget(
      ValueListenableBuilder<ThemeMode>(
        valueListenable: mode,
        builder: (context, themeMode, _) => MaterialApp(
          theme: RomdSkins.seamTestTheme(),
          darkTheme: RomdSkins.baselineDark(),
          themeMode: themeMode,
          themeAnimationDuration: Duration.zero,
          home: MediaViewerScreen(
            media: media,
            initialIndex: 0,
            imageProvider: (_) => MemoryImage(
              base64Decode(
                'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlE'
                'QVQIHWP4z8DwHwAFgAI/ScLkWQAAAABJRU5ErkJggg==',
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    final scaffold = find.byType(Scaffold);
    final counter = find.text('1 / 2');
    final scaffoldElement = tester.element(scaffold);
    final counterElement = tester.element(counter);
    final scaffoldBefore = tester.widget<Scaffold>(scaffold);
    final counterBefore = tester.widget<Text>(counter);

    mode.value = ThemeMode.light;
    await tester.pump();

    final scaffoldAfter = tester.widget<Scaffold>(scaffold);
    final counterAfter = tester.widget<Text>(counter);
    expect(identical(scaffoldElement, tester.element(scaffold)), isTrue);
    expect(identical(counterElement, tester.element(counter)), isTrue);
    expect(
      scaffoldAfter.backgroundColor,
      isNot(scaffoldBefore.backgroundColor),
    );
    expect(counterAfter.style!.fontSize, isNot(counterBefore.style!.fontSize));
    expect(
      counterAfter.style!.letterSpacing,
      isNot(counterBefore.style!.letterSpacing),
    );
    expect(counterAfter.style!.color, isNot(counterBefore.style!.color));
  });

  testWidgets('Material component defaults are skin-owned and runtime-safe', (
    tester,
  ) async {
    final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    addTearDown(mode.dispose);
    late ThemeData observed;

    await tester.pumpWidget(
      ValueListenableBuilder<ThemeMode>(
        valueListenable: mode,
        builder: (context, themeMode, _) => MaterialApp(
          theme: RomdSkins.seamTestTheme(),
          darkTheme: RomdSkins.baselineDark(),
          themeMode: themeMode,
          themeAnimationDuration: Duration.zero,
          home: Builder(
            key: const ValueKey<String>('material-theme-probe'),
            builder: (context) {
              observed = Theme.of(context);
              return const Scaffold(
                body: Column(
                  children: <Widget>[
                    Card(child: Text('Panel')),
                    FilledButton(onPressed: null, child: Text('Action')),
                    CircularProgressIndicator(),
                    Divider(),
                  ],
                ),
              );
            },
          ),
        ),
      ),
    );
    await tester.pump();

    final probe = find.byKey(const ValueKey<String>('material-theme-probe'));
    final builderElement = tester.element(probe);
    final dark = observed;
    mode.value = ThemeMode.light;
    await tester.pump();
    final light = observed;

    expect(identical(builderElement, tester.element(probe)), isTrue);
    expect(light.cardTheme.color, isNot(dark.cardTheme.color));
    expect(
      light.dialogTheme.backgroundColor,
      isNot(dark.dialogTheme.backgroundColor),
    );
    expect(light.iconTheme.size, isNot(dark.iconTheme.size));
    expect(light.dividerTheme.thickness, isNot(dark.dividerTheme.thickness));
    expect(
      light.progressIndicatorTheme.constraints,
      isNot(dark.progressIndicatorTheme.constraints),
    );
    expect(
      light.filledButtonTheme.style!.backgroundColor!.resolve(<WidgetState>{}),
      isNot(
        dark.filledButtonTheme.style!.backgroundColor!.resolve(<WidgetState>{}),
      ),
    );
  });

  testWidgets('mounted on-screen keyboard re-resolves its component contract', (
    tester,
  ) async {
    final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    addTearDown(mode.dispose);

    await tester.pumpWidget(
      ValueListenableBuilder<ThemeMode>(
        valueListenable: mode,
        builder: (context, themeMode, child) => MaterialApp(
          theme: RomdSkins.seamTestTheme(),
          darkTheme: RomdSkins.baselineDark(),
          themeMode: themeMode,
          themeAnimationDuration: Duration.zero,
          home: Scaffold(body: child),
        ),
        child: OnScreenKeyboard(
          onChar: (_) {},
          onBackspace: () {},
          onClear: () {},
        ),
      ),
    );
    await tester.pump();

    final keyboard = find.byType(OnScreenKeyboard);
    final key = find
        .descendant(of: keyboard, matching: find.byType(AnimatedContainer))
        .first;
    final elementBefore = tester.element(key);
    final sizeBefore = tester.getSize(key);
    final containerBefore = tester.widget<AnimatedContainer>(key);
    final decorationBefore = containerBefore.decoration! as BoxDecoration;
    final textBefore = tester.widget<Text>(find.text('A'));

    mode.value = ThemeMode.light;
    await tester.pump();

    final containerAfter = tester.widget<AnimatedContainer>(key);
    final decorationAfter = containerAfter.decoration! as BoxDecoration;
    final textAfter = tester.widget<Text>(find.text('A'));
    await tester.pumpAndSettle();
    final sizeAfter = tester.getSize(key);
    expect(identical(elementBefore, tester.element(key)), isTrue);
    expect(sizeAfter, isNot(sizeBefore));
    expect(containerAfter.duration, isNot(containerBefore.duration));
    expect(decorationAfter.borderRadius, isNot(decorationBefore.borderRadius));
    expect(textAfter.style!.fontSize, isNot(textBefore.style!.fontSize));
  });

  testWidgets('mounted guest picker re-resolves dialog and action roles', (
    tester,
  ) async {
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
          home: Builder(
            builder: (context) => TextButton(
              onPressed: () async {
                await showWhoIsUsingController(
                  context,
                  playerNumber: 2,
                  profiles: const <LocalProfile>[],
                  controllerDisplayName: 'Guest controller',
                );
              },
              child: const Text('Open guest picker'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('Open guest picker'));
    await tester.pumpAndSettle();

    final action = find.ancestor(
      of: find.text('Guest'),
      matching: find.byType(AnimatedContainer),
    );
    final elementBefore = tester.element(action);
    final containerBefore = tester.widget<AnimatedContainer>(action);
    final decorationBefore = containerBefore.decoration! as BoxDecoration;

    mode.value = ThemeMode.light;
    await tester.pump();

    final containerAfter = tester.widget<AnimatedContainer>(action);
    final decorationAfter = containerAfter.decoration! as BoxDecoration;
    expect(identical(elementBefore, tester.element(action)), isTrue);
    expect(containerAfter.duration, isNot(containerBefore.duration));
    expect(decorationAfter.color, isNot(decorationBefore.color));
    expect(decorationAfter.borderRadius, isNot(decorationBefore.borderRadius));
  });

  testWidgets('mounted game record re-resolves frame and typography roles', (
    tester,
  ) async {
    final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    final detail = Completer<ConsoleGameDetail>();
    addTearDown(mode.dispose);

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
        child: GameRecordScreen(
          game: ConsoleGame(
            id: 'chrono-trigger',
            platformId: 'snes',
            platformName: 'Super Nintendo',
            title: 'Chrono Trigger',
            releaseDate: DateTime(1995),
            coverUrl: null,
            genre: 'RPG',
            rating: 9,
            releaseCount: 1,
            defaultReleaseId: 'release',
          ),
          detailFuture: detail.future,
        ),
      ),
    );
    await tester.pump();

    final screen = find.byType(GameRecordScreen);
    final scaffold = find.descendant(
      of: screen,
      matching: find.byType(Scaffold),
    );
    final elementBefore = tester.element(screen);
    final scaffoldBefore = tester.widget<Scaffold>(scaffold);
    final titleBefore = tester.widget<Text>(find.text('Chrono Trigger'));

    mode.value = ThemeMode.light;
    await tester.pump();

    final scaffoldAfter = tester.widget<Scaffold>(scaffold);
    final titleAfter = tester.widget<Text>(find.text('Chrono Trigger'));
    expect(identical(elementBefore, tester.element(screen)), isTrue);
    expect(
      scaffoldAfter.backgroundColor,
      isNot(scaffoldBefore.backgroundColor),
    );
    expect(titleAfter.style!.fontFamily, isNot(titleBefore.style!.fontFamily));
    expect(titleAfter.style!.fontSize, isNot(titleBefore.style!.fontSize));
  });

  testWidgets('mounted Players cluster re-resolves focus chrome and metrics', (
    tester,
  ) async {
    final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    final node = FocusNode();
    final timestamp = DateTime.utc(2026, 7, 19);
    final profile = LocalProfile(
      id: 'profile',
      displayName: 'Player',
      avatarKey: 'default',
      accentColor: 0xff4fe3b0,
      romdServerOrigin: null,
      entryMode: LocalProfileEntryMode.open,
      createdAt: timestamp,
      updatedAt: timestamp,
    );
    addTearDown(mode.dispose);
    addTearDown(node.dispose);

    await tester.pumpWidget(
      ValueListenableBuilder<ThemeMode>(
        valueListenable: mode,
        builder: (context, themeMode, _) => MaterialApp(
          theme: RomdSkins.seamTestTheme(),
          darkTheme: RomdSkins.baselineDark(),
          themeMode: themeMode,
          themeAnimationDuration: Duration.zero,
          home: Scaffold(
            body: PlayersSessionCluster(
              profile: profile,
              focusNode: node,
              onSwitchProfile: () {},
            ),
          ),
        ),
      ),
    );
    await tester.pump();
    node.requestFocus();
    await tester.pump();

    final cluster = find.byKey(
      const ValueKey<String>('players-session-cluster'),
    );
    final elementBefore = tester.element(cluster);
    final sizeBefore = tester.getSize(cluster);
    final before = tester.widget<AnimatedContainer>(cluster);
    final decorationBefore = before.decoration! as BoxDecoration;

    mode.value = ThemeMode.light;
    await tester.pump();

    final after = tester.widget<AnimatedContainer>(cluster);
    final decorationAfter = after.decoration! as BoxDecoration;
    expect(identical(elementBefore, tester.element(cluster)), isTrue);
    expect(tester.getSize(cluster), isNot(sizeBefore));
    expect(after.padding, isNot(before.padding));
    expect(after.duration, isNot(before.duration));
    expect(decorationAfter.color, isNot(decorationBefore.color));
    expect(decorationAfter.borderRadius, isNot(decorationBefore.borderRadius));
  });

  testWidgets(
    'mounted Players panel re-resolves painter, metrics, type, and motion',
    (tester) async {
      final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
      final timestamp = DateTime.utc(2026, 7, 19);
      final profile = LocalProfile(
        id: 'profile',
        displayName: 'Player',
        avatarKey: 'orbit',
        accentColor: 0xff4fe3b0,
        romdServerOrigin: null,
        entryMode: LocalProfileEntryMode.open,
        createdAt: timestamp,
        updatedAt: timestamp,
      );
      final projection = PlayersProjection.resolve(
        claims: const [],
        devices: const [],
      );
      addTearDown(mode.dispose);

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
          child: Scaffold(
            body: PlayersPanel(
              projection: projection,
              activeProfile: profile,
              profileNamesBySlot: const <int, String?>{},
              actions: <Widget>[
                PlayersPanelAction(
                  icon: Icons.swap_horiz,
                  label: 'Change player order',
                  detail: 'Choose who is P1, P2, P3, and P4',
                  onPressed: () {},
                ),
              ],
            ),
          ),
        ),
      );
      await tester.pump();

      final panel = find.byKey(const ValueKey<String>('players-panel'));
      final seat = find.byKey(const ValueKey<String>('player-seat-0'));
      final action = find.descendant(
        of: find.byType(PlayersPanelAction),
        matching: find.byType(AnimatedContainer),
      );
      final avatarPaint = find.descendant(
        of: find.byType(ProfileAvatar),
        matching: find.byType(CustomPaint),
      );
      final elementBefore = tester.element(panel);
      final panelBefore = tester.widget<Container>(panel);
      final panelDecorationBefore = panelBefore.decoration! as BoxDecoration;
      final actionBefore = tester.widget<AnimatedContainer>(action);
      final painterBefore = tester.widget<CustomPaint>(avatarPaint).painter!;
      final titleBefore = tester.widget<Text>(find.text('PLAYERS')).style!;
      final seatSizeBefore = tester.getSize(seat);

      mode.value = ThemeMode.light;
      await tester.pump();

      final panelAfter = tester.widget<Container>(panel);
      final panelDecorationAfter = panelAfter.decoration! as BoxDecoration;
      final actionAfter = tester.widget<AnimatedContainer>(action);
      final painterAfter = tester.widget<CustomPaint>(avatarPaint).painter!;
      final titleAfter = tester.widget<Text>(find.text('PLAYERS')).style!;

      expect(identical(elementBefore, tester.element(panel)), isTrue);
      expect(panelDecorationAfter.color, isNot(panelDecorationBefore.color));
      expect(
        panelDecorationAfter.borderRadius,
        isNot(panelDecorationBefore.borderRadius),
      );
      expect(tester.getSize(seat), isNot(seatSizeBefore));
      expect(actionAfter.duration, isNot(actionBefore.duration));
      expect(titleAfter.fontFamily, isNot(titleBefore.fontFamily));
      expect(painterAfter.shouldRepaint(painterBefore), isTrue);
    },
  );

  testWidgets(
    'mounted controller diagram re-resolves painter, shape, and control state',
    (tester) async {
      final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
      final focusNodes = <CanonicalGamepadControl, FocusNode>{};
      addTearDown(mode.dispose);
      addTearDown(() {
        for (final node in focusNodes.values) {
          node.dispose();
        }
      });

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
          child: Scaffold(
            body: CanonicalControllerDiagram(
              mapping: const {},
              selected: CanonicalGamepadControl.faceSouth,
              active: const <CanonicalGamepadControl>{
                CanonicalGamepadControl.faceNorth,
              },
              enabled: true,
              focusNodes: focusNodes,
              onSelected: (_) {},
              controlLabel: (control) => control.name,
            ),
          ),
        ),
      );
      await tester.pump();

      final diagram = find.byType(CanonicalControllerDiagram);
      final frame = find.byKey(
        const ValueKey<String>('controller-diagram-frame'),
      );
      final painter = find.byKey(
        const ValueKey<String>('controller-silhouette-painter'),
      );
      final activeIcon = find.descendant(
        of: find.byKey(const ValueKey<String>('control-faceNorth')),
        matching: find.byIcon(Icons.bolt),
      );
      final elementBefore = tester.element(diagram);
      final frameBefore = tester.widget<DecoratedBox>(frame);
      final decorationBefore = frameBefore.decoration as BoxDecoration;
      final painterBefore = tester.widget<CustomPaint>(painter).painter!;
      final iconSizeBefore = tester.widget<Icon>(activeIcon).size;

      mode.value = ThemeMode.light;
      await tester.pump();

      final frameAfter = tester.widget<DecoratedBox>(frame);
      final decorationAfter = frameAfter.decoration as BoxDecoration;
      final painterAfter = tester.widget<CustomPaint>(painter).painter!;
      final iconSizeAfter = tester.widget<Icon>(activeIcon).size;

      expect(identical(elementBefore, tester.element(diagram)), isTrue);
      expect(decorationAfter.color, isNot(decorationBefore.color));
      expect(
        decorationAfter.borderRadius,
        isNot(decorationBefore.borderRadius),
      );
      expect(decorationAfter.border, isNot(decorationBefore.border));
      expect(iconSizeAfter, isNot(iconSizeBefore));
      expect(painterAfter.shouldRepaint(painterBefore), isTrue);
    },
  );
}

LinearGradient _coverGradient(WidgetTester tester) {
  final decorations = tester.widgetList<DecoratedBox>(
    find.descendant(
      of: find.byType(CoverArt),
      matching: find.byType(DecoratedBox),
    ),
  );
  final coverDecoration = decorations
      .map((widget) => widget.decoration)
      .whereType<BoxDecoration>()
      .firstWhere((decoration) => decoration.gradient != null);
  return coverDecoration.gradient! as LinearGradient;
}

BoxDecoration _platformBadgeDecoration(WidgetTester tester) =>
    tester
            .widget<DecoratedBox>(
              find.descendant(
                of: find.byType(PlatformBadge),
                matching: find.byType(DecoratedBox),
              ),
            )
            .decoration
        as BoxDecoration;
