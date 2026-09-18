import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/catalog/search/discover_search_boundary.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_ambient_background.dart';

import 'console_golden_harness.dart';

void main() {
  configureConsoleGoldenTests();

  for (final skin in <({String name, ThemeData theme})>[
    (name: 'dark', theme: RomdSkins.baselineDark()),
    (name: 'light', theme: RomdSkins.baselineLight()),
  ]) {
    testWidgets('${skin.name} Search redesign checkpoint', (tester) async {
      await ConsoleGoldenHarness.pump(
        tester,
        theme: skin.theme,
        child: Scaffold(
          backgroundColor: skin.theme.scaffoldBackgroundColor,
          body: Stack(
            children: <Widget>[
              const Positioned.fill(
                child: ConsoleAmbientBackground(dimmed: true),
              ),
              DiscoverSearchBoundary(
                input: DiscoverSearchInput(
                  results: _results,
                  systems: const DiscoverSystemsSnapshot(
                    state: DiscoverLoadState.ready,
                    items: _systems,
                  ),
                  focus: const DiscoverSearchFocusSnapshot(
                    query: 'star',
                    keyboardKeyIndex: 17,
                  ),
                ),
                callbacks: DiscoverSearchCallbacks(
                  onSearch: (_) {},
                  onRetry: (_) {},
                  onLoadMore: () {},
                  onOpenGame: (_, _) {},
                  onBack: () {},
                  onRememberFocus: (_) {},
                ),
              ),
            ],
          ),
        ),
      );

      expect(find.text('SEARCH'), findsOneWidget);
      expect(find.text(' CATALOG'), findsOneWidget);
      expect(find.text('6 RESULTS'), findsOneWidget);
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'search-key-R',
      );
      await expectLater(
        find.byKey(consoleGoldenSurfaceKey),
        matchesGoldenFile('../goldens/search_redesign_${skin.name}/search.png'),
      );
    });
  }
}

const List<ConsolePlatform> _systems = <ConsolePlatform>[
  ConsolePlatform(
    id: 'snes',
    name: 'Super Nintendo Entertainment System',
    shortName: 'SNES',
    manufacturer: 'Nintendo',
    titleCount: 3,
    coverUrl: null,
  ),
  ConsolePlatform(
    id: 'genesis',
    name: 'Sega Genesis',
    shortName: 'GEN',
    manufacturer: 'Sega',
    titleCount: 3,
    coverUrl: null,
  ),
];

final List<ConsoleGame> _games = <ConsoleGame>[
  _game('star-runner', 'Star Runner', 'snes', 'SNES', 1992),
  _game('starlight-rally', 'Starlight Rally', 'genesis', 'Genesis', 1993),
  _game('starbound-tactics', 'Starbound Tactics', 'snes', 'SNES', 1994),
  _game('solar-drift', 'Solar Drift', 'genesis', 'Genesis', 1995),
  _game('astral-quest', 'Astral Quest', 'snes', 'SNES', 1996),
  _game('nova-league', 'Nova League', 'genesis', 'Genesis', 1997),
];

final DiscoverGamePageSnapshot _results = DiscoverGamePageSnapshot(
  state: DiscoverLoadState.ready,
  items: _games,
);

ConsoleGame _game(
  String id,
  String title,
  String platformId,
  String platformName,
  int year,
) => ConsoleGame(
  id: id,
  platformId: platformId,
  platformName: platformName,
  title: title,
  releaseDate: DateTime(year),
  coverUrl: null,
  genre: 'Action',
  rating: 8.5,
  releaseCount: 1,
  defaultReleaseId: '$id-release',
);
