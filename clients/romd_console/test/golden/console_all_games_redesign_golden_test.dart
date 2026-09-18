import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/catalog/all_games/discover_all_games_boundary.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/catalog/discover_shell.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';

import 'console_golden_harness.dart';

void main() {
  configureConsoleGoldenTests();

  for (final skin in <({String name, ThemeData theme})>[
    (name: 'dark', theme: RomdSkins.baselineDark()),
    (name: 'light', theme: RomdSkins.baselineLight()),
  ]) {
    testWidgets('${skin.name} All Games living catalog checkpoint', (
      tester,
    ) async {
      await ConsoleGoldenHarness.pump(
        tester,
        theme: skin.theme,
        child: const _GoldenAllGamesSurface(),
      );

      expect(find.text('Browse'), findsOneWidget);
      expect(find.text('ALL GAMES'), findsNothing);
      expect(find.text('Chrono Trigger'), findsWidgets);
      expect(
        find.byKey(const ValueKey<String>('all-games-inspector')),
        findsOneWidget,
      );
      await expectLater(
        find.byKey(consoleGoldenSurfaceKey),
        matchesGoldenFile(
          '../goldens/all_games_redesign_${skin.name}/all_games.png',
        ),
      );
    });

    testWidgets('${skin.name} All Games filter menu checkpoint', (
      tester,
    ) async {
      await ConsoleGoldenHarness.pump(
        tester,
        theme: skin.theme,
        child: const _GoldenAllGamesSurface(),
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.keyF);
      await tester.pump();
      expect(
        find.byKey(const ValueKey<String>('all-games-filter-menu')),
        findsOneWidget,
      );
      await expectLater(
        find.byKey(consoleGoldenSurfaceKey),
        matchesGoldenFile(
          '../goldens/all_games_redesign_${skin.name}/'
          'all_games_filter_menu.png',
        ),
      );
    });
  }
}

final class _GoldenAllGamesSurface extends StatefulWidget {
  const _GoldenAllGamesSurface();

  @override
  State<_GoldenAllGamesSurface> createState() => _GoldenAllGamesSurfaceState();
}

final class _GoldenAllGamesSurfaceState extends State<_GoldenAllGamesSurface> {
  final DiscoverShellController _controller = DiscoverShellController(
    initialSection: DiscoverSection.allGames,
  );
  final DiscoverFocusHandoff _focusHandoff = DiscoverFocusHandoff();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    body: DiscoverShell(
      controller: _controller,
      focusHandoff: _focusHandoff,
      onSearch: () {},
      onBack: () {},
      slots: DiscoverFeatureSlots(
        featured: (_) => const SizedBox.shrink(),
        allGames: (_) => DiscoverAllGamesBoundary(
          focusHandoff: _focusHandoff,
          input: DiscoverAllGamesInput(
            snapshot: DiscoverGamePageSnapshot(
              state: DiscoverLoadState.ready,
              items: _goldenGames,
            ),
            focus: const DiscoverFocusSnapshot(itemId: 'chrono-trigger'),
            sort: DiscoverCatalogSort.title,
            systems: const DiscoverSystemsSnapshot(
              state: DiscoverLoadState.ready,
              items: _goldenPlatforms,
            ),
            installedReleaseIds: const <String>{'chrono-trigger-release'},
            operationAvailable: true,
            active: true,
            autofocusNavigation: true,
          ),
          callbacks: DiscoverAllGamesCallbacks(
            navigation: DiscoverFeatureNavigation(
              onSelectSection: (_) {},
              onSearch: () {},
              onConnect: () {},
            ),
            onOpenGame: (_, _) {},
            onSelectSort: (_) {},
            onSelectPlatform: (_) {},
            onClearFilters: () {},
            onLoadMore: () {},
            onRetry: () {},
            onRetrySystems: () {},
            onRememberFocus: ({required itemId, required scrollOffset}) {},
            detailFor: (_) => null,
            loadDetail: (_) async => null,
          ),
        ),
      ),
    ),
  );
}

const List<ConsolePlatform> _goldenPlatforms = <ConsolePlatform>[
  ConsolePlatform(
    id: 'genesis',
    name: 'Genesis',
    shortName: 'gen',
    manufacturer: 'Sega',
    titleCount: 2,
    coverUrl: null,
  ),
  ConsolePlatform(
    id: 'snes',
    name: 'Super Nintendo',
    shortName: 'snes',
    manufacturer: 'Nintendo',
    titleCount: 6,
    coverUrl: null,
  ),
];

final List<ConsoleGame> _goldenGames = <ConsoleGame>[
  _goldenGame(
    'chrono-trigger',
    'Chrono Trigger',
    'snes',
    'SNES',
    1995,
    9.4,
    'Role-playing',
  ),
  _goldenGame(
    'battletoads',
    'Battletoads',
    'genesis',
    'Genesis',
    1993,
    8.7,
    'Action',
  ),
  _goldenGame(
    'zelda',
    'The Legend of Zelda',
    'snes',
    'SNES',
    1991,
    9.2,
    'Adventure',
  ),
  _goldenGame(
    'metroid',
    'Super Metroid',
    'snes',
    'SNES',
    1994,
    9.6,
    'Adventure',
  ),
  _goldenGame('fzero', 'F-Zero', 'snes', 'SNES', 1990, 8.4, 'Racing'),
  _goldenGame(
    'castlevania',
    'Super Castlevania IV',
    'snes',
    'SNES',
    1991,
    9,
    'Action',
  ),
  _goldenGame('contra', 'Contra III', 'snes', 'SNES', 1992, 8.9, 'Action'),
  _goldenGame(
    'earthbound',
    'EarthBound',
    'snes',
    'SNES',
    1994,
    9.1,
    'Role-playing',
  ),
];

ConsoleGame _goldenGame(
  String id,
  String title,
  String platformId,
  String platformName,
  int year,
  double rating,
  String genre,
) => ConsoleGame(
  id: id,
  platformId: platformId,
  platformName: platformName,
  title: title,
  releaseDate: DateTime.utc(year),
  coverUrl: null,
  genre: genre,
  rating: rating,
  releaseCount: 1,
  defaultReleaseId: '$id-release',
);
