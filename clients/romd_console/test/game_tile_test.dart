import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';
import 'package:romd_console/src/presentation/widgets/console_focusable.dart';
import 'package:romd_console/src/presentation/widgets/game_tile.dart';

void main() {
  ConsoleGame game({
    String platformId = 'snes',
    String platformName = 'SNES',
  }) => ConsoleGame(
    id: 'g',
    platformId: platformId,
    platformName: platformName,
    title: 'Test Title',
    releaseDate: DateTime(1995),
    coverUrl: null,
    genre: 'RPG',
    rating: 8,
    releaseCount: 1,
    defaultReleaseId: 'g-r',
  );

  Future<void> pumpTile(
    WidgetTester tester, {
    required bool installed,
    ConsoleGame? consoleGame,
  }) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Scaffold(
          body: Center(
            child: SizedBox(
              width: 150,
              child: GameTile(
                game: consoleGame ?? game(),
                heroTag: 'tag',
                installed: installed,
                onPressed: () {},
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pump();
  }

  final badge = find.byKey(const ValueKey<String>('tile-installed'));

  testWidgets('an installed title shows the on-device badge', (tester) async {
    await pumpTile(tester, installed: true);
    expect(badge, findsOneWidget);
  });

  testWidgets('a not-installed title shows no badge', (tester) async {
    await pumpTile(tester, installed: false);
    expect(badge, findsNothing);
  });

  testWidgets(
    'uses the shared focus language and a generic platform badge without catalog data',
    (tester) async {
      var opens = 0;
      await tester.pumpWidget(
        MaterialApp(
          theme: RomdSkins.baselineDark(),
          home: Scaffold(
            body: SizedBox(
              width: 150,
              child: GameTile(
                game: game(),
                heroTag: 'focus-language',
                autofocus: true,
                onPressed: () => opens++,
              ),
            ),
          ),
        ),
      );
      await tester.pump();

      expect(find.byType(ConsoleFocusable), findsOneWidget);
      expect(find.byType(ConsoleFocusRing), findsOneWidget);
      expect(
        find.byKey(const ValueKey<String>('platform-corner-logo-snes')),
        findsNothing,
      );
      expect(find.text('SNES'), findsOneWidget);
      expect(find.text('SNES · 1995'), findsOneWidget);

      await tester.sendKeyEvent(LogicalKeyboardKey.numpadEnter);
      await tester.pump();
      expect(opens, 1);
    },
  );

  testWidgets('keeps the compact text badge when no logo is available', (
    tester,
  ) async {
    await pumpTile(
      tester,
      installed: false,
      consoleGame: game(
        platformId: 'custom-console',
        platformName: 'Custom Console',
      ),
    );

    expect(find.text('CC'), findsOneWidget);
    expect(find.text('CC · 1995'), findsOneWidget);
    expect(
      find.byKey(const ValueKey<String>('platform-corner-logo-cc')),
      findsNothing,
    );
  });

  testWidgets(
    'a familiar platform name does not imply a logo or canonical short label',
    (tester) async {
      await pumpTile(
        tester,
        installed: false,
        consoleGame: game(
          platformId: 'opaque-public-id',
          platformName: 'Nintendo 64',
        ),
      );

      expect(
        find.byKey(const ValueKey<String>('platform-corner-logo-n64')),
        findsNothing,
      );
      expect(find.text('N64'), findsNothing);
      expect(find.text('N6'), findsOneWidget);
      expect(find.text('N6 · 1995'), findsOneWidget);
    },
  );
}
