import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/catalog/discover_shell.dart';
import 'package:romd_console/src/presentation/catalog/featured/discover_featured_boundary.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_ambient_background.dart';

import 'console_golden_harness.dart';

void main() {
  configureConsoleGoldenTests();

  testWidgets('living catalog Featured dark visual checkpoint', (tester) async {
    await _pumpGoldenFeatured(tester, theme: RomdSkins.baselineDark());

    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile(
        '../goldens/living_catalog_featured_dark/featured_1280x720.png',
      ),
    );
  });

  testWidgets('living catalog Featured light visual checkpoint', (
    tester,
  ) async {
    await _pumpGoldenFeatured(tester, theme: RomdSkins.baselineLight());

    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile(
        '../goldens/living_catalog_featured_light/featured_1280x720.png',
      ),
    );
  });

  testWidgets('living catalog Featured dark deep-rail visual checkpoint', (
    tester,
  ) async {
    await _pumpGoldenFeatured(tester, theme: RomdSkins.baselineDark());
    await _focusDeepRail(tester);

    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile(
        '../goldens/living_catalog_featured_dark/'
        'featured_deep_rail_1280x720.png',
      ),
    );
  });

  testWidgets('living catalog Featured light deep-rail visual checkpoint', (
    tester,
  ) async {
    await _pumpGoldenFeatured(tester, theme: RomdSkins.baselineLight());
    await _focusDeepRail(tester);

    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile(
        '../goldens/living_catalog_featured_light/'
        'featured_deep_rail_1280x720.png',
      ),
    );
  });
}

Future<void> _pumpGoldenFeatured(
  WidgetTester tester, {
  required ThemeData theme,
}) async {
  final controller = DiscoverShellController();
  final handoff = DiscoverFocusHandoff();
  addTearDown(controller.dispose);

  await ConsoleGoldenHarness.pump(
    tester,
    theme: theme,
    child: ConsoleAmbientBackground(
      dimmed: true,
      child: DiscoverShell(
        controller: controller,
        focusHandoff: handoff,
        onSearch: () {},
        onBack: () {},
        slots: DiscoverFeatureSlots(
          featured: (_) => DiscoverFeaturedBoundary(
            focusHandoff: handoff,
            input: DiscoverFeaturedInput(
              snapshot: DiscoverFeaturedSnapshot(
                state: DiscoverLoadState.ready,
                shelves: _goldenShelves,
              ),
              focus: const DiscoverFocusSnapshot(itemId: 'super-metroid'),
              installedReleaseIds: const <String>{'release-super-metroid'},
              operationAvailable: true,
              active: true,
              autofocusNavigation: true,
              imageProvider: _goldenImageProvider,
            ),
            callbacks: DiscoverFeaturedCallbacks(
              navigation: DiscoverFeatureNavigation(
                onSelectSection: (_) {},
                onSearch: () {},
                onConnect: () {},
              ),
              onOpenGame: (_, _) {},
              onRetry: () {},
              onRememberFocus: ({required itemId, required scrollOffset}) {},
              detailFor: (id) =>
                  id == 'super-metroid' ? _spotlightDetail : null,
              loadDetail: (_) async => null,
              onHeroChromeProgress: controller.updateFeaturedChromeProgress,
            ),
          ),
          allGames: (_) => const SizedBox.shrink(),
        ),
      ),
    ),
  );
  await _settleGoldenArtwork(tester);
  await tester.pump();
}

Future<void> _settleGoldenArtwork(WidgetTester tester) async {
  final context = tester.element(find.byKey(consoleGoldenSurfaceKey));
  await tester.runAsync(
    () => Future.wait(
      _goldenArtwork.values.map((image) => precacheImage(image, context)),
    ),
  );
}

Future<void> _focusDeepRail(WidgetTester tester) async {
  await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
  await tester.pump();
  await tester.pump(const Duration(seconds: 1));
  await tester.pump();
}

final List<ConsoleGame> _goldenGames = <ConsoleGame>[
  _goldenGame('battletoads', 'Battletoads', 'GEN', 1993, 8.7),
  _goldenGame('super-metroid', 'Super Metroid', 'SNES', 1994, 9.6),
  _goldenGame('zelda-link', 'The Legend of Zelda', 'SNES', 1991, 9.4),
  _goldenGame('nhl-96', 'NHL 96', 'SNES', 1995, 8.2),
  _goldenGame('contra-iii', 'Contra III', 'SNES', 1992, 8.9),
  _goldenGame('mega-man-x', 'Mega Man X', 'SNES', 1993, 9.1),
  _goldenGame('streets-rage', 'Streets of Rage 2', 'GEN', 1992, 9),
  _goldenGame('gunstar', 'Gunstar Heroes', 'GEN', 1993, 8.8),
  _goldenGame('shinobi', 'Shinobi III', 'GEN', 1993, 8.5),
  _goldenGame('earthworm-jim', 'Earthworm Jim', 'GEN', 1994, 8.4),
];

final List<DiscoverFeaturedShelf> _goldenShelves = <DiscoverFeaturedShelf>[
  DiscoverFeaturedShelf(
    id: 'top-rated',
    eyebrow: 'FEATURED',
    title: 'Top Rated',
    games: _goldenGames.take(5).toList(growable: false),
  ),
  DiscoverFeaturedShelf(
    id: 'action-heroes',
    eyebrow: 'Featured collection',
    title: 'Action heroes',
    games: _goldenGames.skip(5).toList(growable: false),
  ),
];

ConsoleGame _goldenGame(
  String id,
  String title,
  String platform,
  int year,
  double rating,
) => ConsoleGame(
  id: id,
  platformId: platform.toLowerCase(),
  platformName: platform,
  title: title,
  releaseDate: DateTime.utc(year),
  coverUrl: _goldenArtworkUri(
    id == 'super-metroid' ? 'super-metroid-cover' : id,
  ),
  genre: id == 'super-metroid' ? 'Adventure' : 'Action',
  rating: rating,
  releaseCount: 1,
  defaultReleaseId: 'release-$id',
);

final ConsoleGameDetail _spotlightDetail = ConsoleGameDetail(
  id: 'super-metroid',
  platformId: 'snes',
  platformName: 'SNES',
  title: 'Super Metroid',
  description: 'A solitary mission into the deepest chambers of planet Zebes.',
  publisher: 'Nintendo',
  developer: 'Nintendo R&D1',
  genre: 'Adventure',
  releaseDate: DateTime.utc(1994),
  players: 1,
  rating: 9.6,
  media: <ConsoleMediaRef>[
    ConsoleMediaRef(
      id: 'super-metroid-background',
      url: _goldenArtworkUri('hero'),
      type: 'background',
      isPrimary: true,
    ),
  ],
  releases: const <ConsoleRelease>[],
  defaultReleaseId: 'release-super-metroid',
);

Uri _goldenArtworkUri(String id) =>
    Uri.parse('https://golden.invalid/featured/$id.png');

ImageProvider<Object> _goldenImageProvider(Uri uri) {
  final image = _goldenArtwork[uri];
  if (image == null) {
    throw StateError('Missing Featured golden artwork fixture for $uri.');
  }
  return image;
}

final Map<Uri, MemoryImage> _goldenArtwork = <Uri, MemoryImage>{
  for (final id in <String>[
    'hero',
    'battletoads',
    'super-metroid-cover',
    'zelda-link',
    'nhl-96',
    'contra-iii',
    'mega-man-x',
    'streets-rage',
    'gunstar',
    'shinobi',
    'earthworm-jim',
  ])
    _goldenArtworkUri(id): MemoryImage(
      File('test/golden/fixtures/featured_artwork/$id.png').readAsBytesSync(),
    ),
};
