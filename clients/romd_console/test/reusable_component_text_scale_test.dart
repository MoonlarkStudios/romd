import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_hint_bar.dart';
import 'package:romd_console/src/presentation/widgets/game_tile.dart';
import 'package:romd_console/src/presentation/widgets/meta_chip.dart';
import 'package:romd_console/src/presentation/widgets/on_screen_keyboard.dart';
import 'package:romd_console/src/presentation/widgets/platform_badge.dart';

void main() {
  Future<void> pumpScaled(
    WidgetTester tester,
    Widget child, {
    required Size size,
    required double scale,
  }) async {
    await tester.binding.setSurfaceSize(size);
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: MediaQueryData(textScaler: TextScaler.linear(scale)),
          child: Scaffold(body: child),
        ),
      ),
    );
    await tester.pump();
    expect(tester.takeException(), isNull);
  }

  testWidgets('footer hints reflow at 2x text on a compact surface', (
    tester,
  ) async {
    await pumpScaled(
      tester,
      const Align(
        alignment: Alignment.bottomCenter,
        child: ConsoleFooterBar(
          context: 'PRESERVATION LAYER · RECORD',
          horizontalPadding: 24,
          hints: <ConsoleHint>[
            ConsoleHint(glyph: 'D-PAD', label: 'Navigate'),
            ConsoleHint(glyph: 'Enter', label: 'Open'),
            ConsoleHint(glyph: 'Esc', label: 'Back'),
          ],
        ),
      ),
      size: const Size(640, 480),
      scale: 2,
    );

    expect(find.text('PRESERVATION LAYER · RECORD'), findsOneWidget);
    expect(find.text('Navigate'), findsOneWidget);
  });

  testWidgets('game tile allocates title height from the active text scaler', (
    tester,
  ) async {
    await pumpScaled(
      tester,
      Center(
        child: SizedBox(
          width: 170,
          child: GameTile(
            game: _game,
            heroTag: 'scaled-game',
            onPressed: () {},
          ),
        ),
      ),
      size: const Size(480, 640),
      scale: 2,
    );

    expect(find.text(_game.title), findsOneWidget);
  });

  testWidgets('game tile focus animation resolves to zero for reduced motion', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: const MediaQueryData(disableAnimations: true),
          child: Scaffold(
            body: Center(
              child: SizedBox(
                width: 170,
                child: GameTile(
                  game: _game,
                  heroTag: 'reduced-motion-game',
                  autofocus: true,
                  onPressed: () {},
                ),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    expect(
      tester.widget<AnimatedScale>(find.byType(AnimatedScale)).duration,
      Duration.zero,
    );
    for (final opacity in tester.widgetList<AnimatedOpacity>(
      find.byType(AnimatedOpacity),
    )) {
      expect(opacity.duration, Duration.zero);
    }
  });

  testWidgets('keyboard and metadata remain usable at 1.5x text', (
    tester,
  ) async {
    await pumpScaled(
      tester,
      SingleChildScrollView(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            OnScreenKeyboard(
              onChar: (_) {},
              onBackspace: () {},
              onClear: () {},
            ),
            const SizedBox(height: 16),
            const Wrap(
              spacing: 8,
              runSpacing: 8,
              children: <Widget>[
                PlatformBadge(label: 'SUPER NINTENDO'),
                MetaChip(label: 'ROLE-PLAYING GAME', icon: Icons.category),
              ],
            ),
          ],
        ),
      ),
      size: const Size(440, 720),
      scale: 1.5,
    );

    expect(find.text('CLEAR'), findsOneWidget);
    expect(find.text('SUPER NINTENDO'), findsOneWidget);
  });

  testWidgets('keyboard focus animation resolves to zero for reduced motion', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: const MediaQueryData(disableAnimations: true),
          child: Scaffold(
            body: OnScreenKeyboard(
              onChar: (_) {},
              onBackspace: () {},
              onClear: () {},
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    final keys = tester.widgetList<AnimatedContainer>(
      find.byType(AnimatedContainer),
    );
    expect(keys, isNotEmpty);
    expect(keys.every((key) => key.duration == Duration.zero), isTrue);
  });
}

final _game = ConsoleGame(
  id: 'chrono-trigger',
  platformId: 'snes',
  platformName: 'Super Nintendo',
  title: 'A Very Long Game Title That Needs Two Lines',
  releaseDate: DateTime(1995),
  coverUrl: null,
  genre: 'RPG',
  rating: 9.8,
  releaseCount: 1,
  defaultReleaseId: 'chrono-trigger-us',
);
