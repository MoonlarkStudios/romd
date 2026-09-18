import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/catalog/all_games/discover_all_games_boundary.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/catalog/discover_shell.dart';
import 'package:romd_console/src/presentation/theme/console_theme_extensions.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';

void main() {
  testWidgets('wide viewports add columns instead of stretching covers', (
    tester,
  ) async {
    final harness = _AllGamesHarness(games: _games());
    await _pumpHarness(tester, harness, size: const Size(2400, 1000));

    final rail = RomdSkins.baselineDark()
        .extension<ConsoleLayoutTheme>()!
        .gameRail;
    final cardFinder = find.byWidgetPredicate(
      (widget) => switch (widget.key) {
        ValueKey<String>(:final value) => value.startsWith('all-games-card-'),
        _ => false,
      },
    );
    final cards = <Rect>[
      for (final element in cardFinder.evaluate())
        tester.getRect(find.byWidget(element.widget)),
    ];

    // Covers stay inside the designed size band…
    for (final card in cards) {
      expect(card.width, lessThanOrEqualTo(rail.gridTileMaxWidth + 0.01));
      expect(card.width, greaterThanOrEqualTo(rail.strip.tileWidth - 0.01));
    }
    // …because the grid gains columns beyond the four-column design count.
    final firstRowTop = cards.first.top;
    final firstRowCount = cards
        .where((card) => (card.top - firstRowTop).abs() < 1)
        .length;
    expect(firstRowCount, greaterThan(4));
  });

  testWidgets('renders a four-column grid, toolbar, and contextual inspector', (
    tester,
  ) async {
    final harness = _AllGamesHarness(games: _games());
    await _pumpHarness(tester, harness);

    expect(find.byKey(const ValueKey<String>('catalog-sidebar')), findsNothing);
    expect(
      find.byKey(const ValueKey<String>('catalog-letter-rail')),
      findsNothing,
    );
    expect(
      find.byKey(const ValueKey<String>('all-games-filter-trigger')),
      findsOneWidget,
    );
    expect(find.text('TITLE'), findsOneWidget);
    expect(
      find.byKey(const ValueKey<String>('browse-system-all')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('browse-system-genesis')),
      findsOneWidget,
    );
    expect(
      find.descendant(
        of: find.byKey(const ValueKey<String>('browse-system-genesis')),
        matching: find.text('GENE'),
      ),
      findsOneWidget,
    );
    expect(
      find.descendant(
        of: find.byKey(const ValueKey<String>('browse-system-snes')),
        matching: find.text('SN'),
      ),
      findsOneWidget,
    );
    expect(
      tester
          .getSize(find.byKey(const ValueKey<String>('browse-system-rail')))
          .height,
      lessThan(80),
    );
    expect(
      tester
          .renderObject<RenderParagraph>(find.text('All Systems'))
          .didExceedMaxLines,
      isFalse,
    );
    expect(find.text('Any Release'), findsNothing);
    expect(find.text('Complete'), findsNothing);
    expect(find.text('Partial'), findsNothing);
    expect(find.text('Chrono Trigger'), findsWidgets);
    expect(find.text('SNES · 1995 · Role-playing'), findsOneWidget);
    expect(find.text('9.4'), findsOneWidget);
    expect(find.text('READY TO PLAY'), findsOneWidget);
    expect(
      find.byKey(const ValueKey<String>('all-games-inspector-media-fallback')),
      findsOneWidget,
    );

    final first = tester.getTopLeft(
      find.byKey(const ValueKey<String>('all-games-card-chrono')),
    );
    final fourth = tester.getTopLeft(
      find.byKey(const ValueKey<String>('all-games-card-metroid')),
    );
    final fifth = tester.getTopLeft(
      find.byKey(const ValueKey<String>('all-games-card-fzero')),
    );
    expect(fourth.dy, closeTo(first.dy, 0.1));
    expect(fourth.dx, greaterThan(first.dx));
    expect(fifth.dy, greaterThan(first.dy));
    expect(fifth.dx, closeTo(first.dx, 0.1));
  });

  testWidgets('system rail and controller mini-menu compose selections', (
    tester,
  ) async {
    final harness = _AllGamesHarness(games: _games());
    await _pumpHarness(tester, harness);

    final genesisLeft = tester.getTopLeft(
      find.byKey(const ValueKey<String>('browse-system-genesis')),
    );
    final snesLeft = tester.getTopLeft(
      find.byKey(const ValueKey<String>('browse-system-snes')),
    );
    expect(genesisLeft.dx, lessThan(snesLeft.dx));
    await tester.tap(
      find.byKey(const ValueKey<String>('browse-system-genesis')),
    );
    await tester.pump();

    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-filter-trigger')),
    );
    await tester.pump();
    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-filter-sort')),
    );
    await tester.pump();
    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-filter-sort-rating')),
    );
    await tester.pump();

    expect(find.text('RATING'), findsOneWidget);
    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-filter-reset')),
    );
    await tester.pump();

    expect(harness.sorts, <DiscoverCatalogSort>[DiscoverCatalogSort.rating]);
    expect(harness.platforms, <String?>['genesis']);
    expect(harness.clearFilterCalls, hasLength(1));
  });

  testWidgets(
    'filtered empty results retain system filter and clear recovery',
    (tester) async {
      final input = ValueNotifier<DiscoverAllGamesInput>(
        _input(games: const <ConsoleGame>[], platformId: 'genesis'),
      );
      addTearDown(input.dispose);
      final harness = _AllGamesHarness(input: input);
      await _pumpHarness(tester, harness);

      expect(
        find.byKey(const ValueKey<String>('all-games-filter-trigger')),
        findsOneWidget,
      );
      expect(find.text('No games match these filters'), findsOneWidget);
      expect(
        find.byKey(const ValueKey<String>('all-games-clear-filters')),
        findsOneWidget,
      );

      harness.focusHandoff.restoreContent(DiscoverSection.allGames);
      await tester.pump();
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'browse-system-genesis',
      );
      await tester.sendKeyEvent(LogicalKeyboardKey.keyF);
      await tester.pump();
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'all-games-filter-sort',
      );
      await tester.sendKeyEvent(LogicalKeyboardKey.escape);
      await tester.pump();
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'browse-system-genesis',
      );

      await tester.tap(
        find.byKey(const ValueKey<String>('all-games-clear-filters')),
      );
      expect(harness.clearFilterCalls, hasLength(1));
    },
  );

  testWidgets(
    'arrow and controller contract reaches filter, header, and traps menu focus',
    (tester) async {
      final harness = _AllGamesHarness(games: _games());
      await _pumpHarness(tester, harness);

      await tester.tap(
        find.byKey(const ValueKey<String>('all-games-card-chrono')),
      );
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump();
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'all-games-filter-trigger',
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump();
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'browse-system-all',
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump();
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'discover-tab-browse',
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pump();
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'all-games-chrono',
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.keyF);
      await tester.pump();
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'all-games-filter-sort',
      );
      expect(
        tester
            .widget<ExcludeFocus>(
              find.byKey(
                const ValueKey<String>('all-games-content-focus-boundary'),
              ),
            )
            .excluding,
        isTrue,
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pump();
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'all-games-filter-option-sort-title',
      );
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pump();
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'all-games-filter-option-sort-rating',
      );
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pump();
      expect(harness.sorts, <DiscoverCatalogSort>[DiscoverCatalogSort.rating]);
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'all-games-filter-sort',
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.escape);
      await tester.pump();
      expect(
        FocusManager.instance.primaryFocus?.debugLabel,
        'all-games-chrono',
      );
      expect(
        find.byKey(const ValueKey<String>('all-games-filter-menu')),
        findsNothing,
      );
    },
  );

  testWidgets('shortcut menu close restores the previously focused game', (
    tester,
  ) async {
    final harness = _AllGamesHarness(games: _games());
    await _pumpHarness(tester, harness);

    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-card-metroid')),
    );
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'all-games-metroid');

    await tester.sendKeyEvent(LogicalKeyboardKey.keyF);
    await tester.pump();
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'all-games-filter-sort',
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pump();
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'all-games-metroid');
  });

  testWidgets('filter menu survives request loading and restores by game id', (
    tester,
  ) async {
    final input = ValueNotifier<DiscoverAllGamesInput>(_input(games: _games()));
    addTearDown(input.dispose);
    final harness = _AllGamesHarness(input: input);
    await _pumpHarness(tester, harness);

    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-card-metroid')),
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.keyF);
    await tester.pump();
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'all-games-filter-sort',
    );

    input.value = _input(
      games: const <ConsoleGame>[],
      focus: const DiscoverFocusSnapshot(itemId: 'metroid'),
      state: DiscoverLoadState.loading,
    );
    await tester.pump();
    expect(
      find.byKey(const ValueKey<String>('all-games-filter-menu')),
      findsOneWidget,
    );
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'all-games-filter-sort',
    );

    input.value = _input(
      games: _games(),
      focus: const DiscoverFocusSnapshot(itemId: 'metroid'),
    );
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pump();
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'all-games-metroid');
  });

  testWidgets(
    'operation change replaces platform choices with the complete new list',
    (tester) async {
      final input = ValueNotifier<DiscoverAllGamesInput>(
        _input(
          games: _games(),
          platforms: const <ConsolePlatform>[
            ConsolePlatform(
              id: 'old-system',
              name: 'Old System',
              shortName: 'old',
              manufacturer: null,
              titleCount: 1,
              coverUrl: null,
            ),
          ],
        ),
      );
      addTearDown(input.dispose);
      final harness = _AllGamesHarness(input: input);
      await _pumpHarness(tester, harness);

      input.value = _input(
        games: _games(),
        operationEpoch: 2,
        platforms: const <ConsolePlatform>[
          ConsolePlatform(
            id: 'zeta',
            name: 'Zeta System',
            shortName: 'zeta',
            manufacturer: null,
            titleCount: 1,
            coverUrl: null,
          ),
          ConsolePlatform(
            id: 'alpha',
            name: 'Alpha System',
            shortName: 'alpha',
            manufacturer: null,
            titleCount: 1,
            coverUrl: null,
          ),
        ],
      );
      await tester.pump();
      await tester.tap(
        find.byKey(const ValueKey<String>('browse-system-alpha')),
      );

      expect(harness.platforms, <String?>['alpha']);
      expect(harness.platforms, isNot(contains('old-system')));
      expect(harness.platforms, isNot(contains('snes')));
    },
  );

  testWidgets('platform failure preserves All Systems and exposes retry', (
    tester,
  ) async {
    final harness = _AllGamesHarness(
      input: ValueNotifier<DiscoverAllGamesInput>(
        _input(
          games: _games(),
          platforms: const <ConsolePlatform>[],
          systemsState: DiscoverLoadState.failed,
        ),
      ),
    );
    addTearDown(harness.input.dispose);
    await _pumpHarness(tester, harness);

    expect(
      find.byKey(const ValueKey<String>('browse-system-all')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('browse-system-retry')),
      findsOneWidget,
    );

    harness.focusHandoff.restoreContent(DiscoverSection.allGames);
    await tester.pump();
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'browse-system-all');
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'browse-system-retry',
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    expect(harness.retrySystemCalls, hasLength(1));
  });

  testWidgets('last-row focus prefetches once and duplicate ids are ignored', (
    tester,
  ) async {
    final harness = _AllGamesHarness(
      games: <ConsoleGame>[..._games(), _games().first],
      nextCursor: 'page-2',
    );
    await _pumpHarness(tester, harness);

    expect(
      find.byKey(const ValueKey<String>('all-games-card-chrono')),
      findsOneWidget,
    );
    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-card-chrono')),
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    await tester.pump();
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'all-games-fzero');
    final grid = tester.widget<CustomScrollView>(
      find.byKey(const ValueKey<String>('all-games-grid')),
    );
    expect(grid.controller!.offset, greaterThan(0));
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'all-games-castlevania',
    );
    expect(harness.loadMoreCalls, hasLength(1));
  });

  testWidgets('paging failure exposes a focusable explicit retry', (
    tester,
  ) async {
    final harness = _AllGamesHarness(
      games: _games(),
      nextCursor: 'page-2',
      pagingFailed: true,
    );
    await _pumpHarness(tester, harness);

    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-card-chrono')),
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    expect(harness.loadMoreCalls, isEmpty);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'all-games-paging-action',
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(harness.loadMoreCalls, hasLength(1));
  });

  testWidgets('append keeps the focused stable id and header handoff works', (
    tester,
  ) async {
    final input = ValueNotifier<DiscoverAllGamesInput>(
      _input(games: _games().take(4).toList()),
    );
    addTearDown(input.dispose);
    final harness = _AllGamesHarness(input: input);
    await _pumpHarness(tester, harness);

    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-card-zelda')),
    );
    await tester.pump();
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'all-games-zelda');

    input.value = _input(games: _games());
    await tester.pump();
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'all-games-zelda');

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
    await tester.pump();
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'all-games-filter-trigger',
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
    await tester.pump();
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'browse-system-all');
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
    await tester.pump();
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'discover-tab-browse',
    );
  });

  testWidgets('leaving All Games dismisses the retained filter menu', (
    tester,
  ) async {
    final input = ValueNotifier<DiscoverAllGamesInput>(_input(games: _games()));
    addTearDown(input.dispose);
    final harness = _AllGamesHarness(input: input);
    await _pumpHarness(tester, harness);

    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-card-chrono')),
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.keyF);
    await tester.pump();
    expect(
      find.byKey(const ValueKey<String>('all-games-filter-menu')),
      findsOneWidget,
    );

    input.value = _input(games: _games(), active: false);
    await tester.pump();
    expect(
      find.byKey(const ValueKey<String>('all-games-filter-menu')),
      findsNothing,
    );
  });

  testWidgets('restores remembered scroll and returns header to system rail', (
    tester,
  ) async {
    final games = <ConsoleGame>[
      for (var index = 0; index < 24; index++)
        _game(
          'game-$index',
          'Game $index',
          'snes',
          'SNES',
          1990 + index,
          8,
          'Action',
        ),
    ];
    final input = ValueNotifier<DiscoverAllGamesInput>(
      _input(
        games: games,
        focus: const DiscoverFocusSnapshot(
          itemId: 'game-12',
          scrollOffset: 520,
        ),
      ),
    );
    addTearDown(input.dispose);
    final harness = _AllGamesHarness(input: input);
    await _pumpHarness(tester, harness);
    await tester.pump();

    final grid = tester.widget<CustomScrollView>(
      find.byKey(const ValueKey<String>('all-games-grid')),
    );
    final restoredOffset = grid.controller!.offset;
    expect(restoredOffset, greaterThanOrEqualTo(0));
    _expectCardInsideGrid(tester, 'game-12');

    harness.focusHandoff.restoreContent(DiscoverSection.allGames);
    await tester.pump();
    await tester.pump();
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'browse-system-all');
    expect(grid.controller!.offset, closeTo(restoredOffset, 1));
    _expectCardInsideGrid(tester, 'game-12');
    expect(harness.rememberedFocus.last.itemId, 'game-12');
    expect(
      harness.rememberedFocus.last.scrollOffset,
      closeTo(restoredOffset, 1),
    );
    await tester.pump();
    expect(grid.controller!.offset, closeTo(restoredOffset, 1));
  });

  testWidgets('stale scroll cannot hide a remembered first-row game', (
    tester,
  ) async {
    final games = <ConsoleGame>[
      for (var index = 0; index < 24; index++)
        _game(
          'game-$index',
          'Game $index',
          'snes',
          'SNES',
          1990 + index,
          8,
          'Action',
        ),
    ];
    final harness = _AllGamesHarness(
      input: ValueNotifier<DiscoverAllGamesInput>(
        _input(
          games: games,
          focus: const DiscoverFocusSnapshot(
            itemId: 'game-0',
            scrollOffset: 520,
          ),
        ),
      ),
    );
    addTearDown(harness.input.dispose);
    await _pumpHarness(tester, harness);
    await tester.pump();

    final grid = tester.widget<CustomScrollView>(
      find.byKey(const ValueKey<String>('all-games-grid')),
    );
    expect(grid.controller!.offset, closeTo(0, 1));
    _expectCardInsideGrid(tester, 'game-0');

    harness.focusHandoff.restoreContent(DiscoverSection.allGames);
    await tester.pump();
    await tester.pump();
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'browse-system-all');
    expect(grid.controller!.offset, closeTo(0, 1));
    _expectCardInsideGrid(tester, 'game-0');
  });

  testWidgets('late detail response cannot replace the newly focused game', (
    tester,
  ) async {
    final chrono = Completer<ConsoleGameDetail?>();
    final zelda = Completer<ConsoleGameDetail?>();
    final harness = _AllGamesHarness(
      games: _games(),
      detailLoader: (id) => switch (id) {
        'chrono' => chrono.future,
        'zelda' => zelda.future,
        _ => Future<ConsoleGameDetail?>.value(),
      },
    );
    await _pumpHarness(tester, harness);

    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-card-chrono')),
    );
    await tester.pump(const Duration(milliseconds: 200));
    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-card-zelda')),
    );
    await tester.pump(const Duration(milliseconds: 200));

    chrono.complete(_detail(_games().first, 'STALE DESCRIPTION'));
    await tester.pump();
    expect(find.text('STALE DESCRIPTION'), findsNothing);
    expect(
      find.descendant(
        of: find.byKey(const ValueKey<String>('all-games-inspector')),
        matching: find.text('The Legend of Zelda'),
      ),
      findsOneWidget,
    );

    zelda.complete(_detail(_games()[2], 'CURRENT DESCRIPTION'));
    await tester.pump();
    await tester.pump();
    expect(find.text('CURRENT DESCRIPTION'), findsOneWidget);
  });

  testWidgets('inspector selects preferred detail media over fallback', (
    tester,
  ) async {
    final game = _games().first;
    final detail = _detail(
      game,
      'A journey through time.',
      media: <ConsoleMediaRef>[
        ConsoleMediaRef(
          id: 'secondary',
          type: 'screenshot',
          url: Uri.parse('https://media.invalid/secondary.png'),
          isPrimary: false,
        ),
        ConsoleMediaRef(
          id: 'preferred',
          type: 'background',
          url: Uri.parse('https://media.invalid/preferred.png'),
          isPrimary: true,
        ),
      ],
    );
    final harness = _AllGamesHarness(
      games: _games(),
      details: <String, ConsoleGameDetail>{game.id: detail},
    );
    await _pumpHarness(tester, harness);

    expect(
      find.byKey(
        const ValueKey<String>(
          'all-games-inspector-media-https://media.invalid/preferred.png',
        ),
      ),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('all-games-inspector-media-fallback')),
      findsOneWidget,
    );
  });

  testWidgets('reduced motion keeps every catalog card at unit scale', (
    tester,
  ) async {
    final harness = _AllGamesHarness(games: _games());
    await _pumpHarness(tester, harness);

    final scales = tester.widgetList<AnimatedScale>(
      find.descendant(
        of: find.byKey(const ValueKey<String>('all-games-card-battletoads')),
        matching: find.byType(AnimatedScale),
      ),
    );
    expect(scales, isNotEmpty);
    expect(scales.every((scale) => scale.scale == 1), isTrue);
  });

  testWidgets('2x text preserves critical controls and compact metadata', (
    tester,
  ) async {
    final harness = _AllGamesHarness(games: _games());
    await _pumpHarness(tester, harness, textScaler: const TextScaler.linear(2));

    expect(tester.takeException(), isNull);
    expect(
      find.byKey(const ValueKey<String>('all-games-filter-trigger')),
      findsOneWidget,
    );
    await tester.tap(
      find.byKey(const ValueKey<String>('all-games-filter-trigger')),
    );
    await tester.pump();
    expect(
      find.byKey(const ValueKey<String>('all-games-filter-sort')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('browse-system-rail')),
      findsOneWidget,
    );
    expect(
      tester
          .renderObject<RenderParagraph>(find.text('All Systems'))
          .didExceedMaxLines,
      isFalse,
    );
    expect(tester.takeException(), isNull);
    expect(find.text('Chrono Trigger'), findsWidgets);
    expect(find.text('SNES · 1995 · Role-playing'), findsOneWidget);
    expect(find.text('9.4'), findsOneWidget);
    expect(find.text('READY TO PLAY'), findsOneWidget);
    expect(
      tester
          .getSize(find.byKey(const ValueKey<String>('all-games-card-chrono')))
          .width,
      greaterThanOrEqualTo(144),
    );
    final inspectorBottom = tester
        .getBottomLeft(
          find.byKey(const ValueKey<String>('all-games-inspector')),
        )
        .dy;
    final gridTop = tester
        .getTopLeft(find.byKey(const ValueKey<String>('all-games-grid')))
        .dy;
    expect(gridTop, greaterThan(inspectorBottom));
    expect(
      find.byKey(const ValueKey<String>('all-games-inspector-media-fallback')),
      findsNothing,
    );
  });
}

final class _AllGamesHarness extends StatelessWidget {
  _AllGamesHarness({
    List<ConsoleGame>? games,
    this.nextCursor,
    this.pagingFailed = false,
    this.detailLoader,
    this.details = const <String, ConsoleGameDetail>{},
    ValueNotifier<DiscoverAllGamesInput>? input,
  }) : input =
           input ??
           ValueNotifier<DiscoverAllGamesInput>(
             _input(
               games: games ?? _games(),
               nextCursor: nextCursor,
               pagingFailed: pagingFailed,
             ),
           ),
       ownsInput = input == null;

  final ValueNotifier<DiscoverAllGamesInput> input;
  final bool ownsInput;
  final DiscoverShellController controller = DiscoverShellController(
    initialSection: DiscoverSection.allGames,
  );
  final DiscoverFocusHandoff focusHandoff = DiscoverFocusHandoff();
  final String? nextCursor;
  final bool pagingFailed;
  final Future<ConsoleGameDetail?> Function(String id)? detailLoader;
  final Map<String, ConsoleGameDetail> details;
  final List<DiscoverCatalogSort> sorts = <DiscoverCatalogSort>[];
  final List<String?> platforms = <String?>[];
  final List<Object?> loadMoreCalls = <Object?>[];
  final List<Object?> retrySystemCalls = <Object?>[];
  final List<Object?> clearFilterCalls = <Object?>[];
  final List<({String? itemId, double scrollOffset})> rememberedFocus =
      <({String? itemId, double scrollOffset})>[];

  @override
  Widget build(BuildContext context) => ValueListenableBuilder(
    valueListenable: input,
    builder: (context, value, _) => DiscoverShell(
      controller: controller,
      focusHandoff: focusHandoff,
      onSearch: () {},
      onBack: () {},
      slots: DiscoverFeatureSlots(
        featured: (_) => const SizedBox.shrink(),
        allGames: (_) => DiscoverAllGamesBoundary(
          input: value,
          focusHandoff: focusHandoff,
          callbacks: DiscoverAllGamesCallbacks(
            navigation: DiscoverFeatureNavigation(
              onSelectSection: (_) {},
              onSearch: () {},
              onConnect: () {},
            ),
            onOpenGame: (_, _) {},
            onSelectSort: sorts.add,
            onSelectPlatform: platforms.add,
            onClearFilters: () => clearFilterCalls.add(null),
            onLoadMore: () => loadMoreCalls.add(null),
            onRetry: () {},
            onRetrySystems: () => retrySystemCalls.add(null),
            onRememberFocus: ({required itemId, required scrollOffset}) {
              rememberedFocus.add((itemId: itemId, scrollOffset: scrollOffset));
            },
            detailFor: (id) => details[id],
            loadDetail: detailLoader ?? (_) async => null,
          ),
        ),
      ),
    ),
  );
}

Future<void> _pumpHarness(
  WidgetTester tester,
  _AllGamesHarness harness, {
  TextScaler textScaler = TextScaler.noScaling,
  bool disableAnimations = true,
  Size size = const Size(1280, 720),
}) async {
  tester.view
    ..physicalSize = size
    ..devicePixelRatio = 1;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);
  addTearDown(harness.controller.dispose);
  if (harness.ownsInput) addTearDown(harness.input.dispose);
  await tester.pumpWidget(
    MaterialApp(
      theme: RomdSkins.baselineDark(),
      home: MediaQuery(
        data: MediaQueryData(
          size: size,
          textScaler: textScaler,
          disableAnimations: disableAnimations,
        ),
        child: Scaffold(body: harness),
      ),
    ),
  );
  await tester.pump();
}

void _expectCardInsideGrid(WidgetTester tester, String id) {
  final gridRect = tester.getRect(
    find.byKey(const ValueKey<String>('all-games-grid')),
  );
  final cardRect = tester.getRect(
    find.byKey(ValueKey<String>('all-games-card-$id')),
  );
  expect(cardRect.top, greaterThanOrEqualTo(gridRect.top - 1));
  expect(cardRect.bottom, lessThanOrEqualTo(gridRect.bottom + 1));
}

DiscoverAllGamesInput _input({
  required List<ConsoleGame> games,
  DiscoverLoadState state = DiscoverLoadState.ready,
  bool active = true,
  String? nextCursor,
  bool pagingFailed = false,
  String? platformId,
  List<ConsolePlatform> platforms = _defaultPlatforms,
  DiscoverLoadState systemsState = DiscoverLoadState.ready,
  DiscoverFocusSnapshot focus = const DiscoverFocusSnapshot(itemId: 'chrono'),
  int operationEpoch = 1,
}) => DiscoverAllGamesInput(
  snapshot: DiscoverGamePageSnapshot(
    state: state,
    items: games,
    nextCursor: nextCursor,
    hasNextPage: nextCursor != null,
    pagingFailed: pagingFailed,
    requestKey: DiscoverRequestKey(
      operation: _operation(operationEpoch),
      surface: DiscoverSurface.allGames,
    ),
  ),
  focus: focus,
  sort: DiscoverCatalogSort.title,
  systems: DiscoverSystemsSnapshot(state: systemsState, items: platforms),
  installedReleaseIds: const <String>{'chrono-release'},
  operationAvailable: true,
  active: active,
  autofocusNavigation: false,
  platformId: platformId,
);

const List<ConsolePlatform> _defaultPlatforms = <ConsolePlatform>[
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

DiscoverOperationKey _operation(int epoch) => DiscoverOperationKey(
  localProfileId: 'profile-$epoch',
  serverInstanceId: 'server-$epoch',
  serverOrigin: 'https://server-$epoch.invalid',
  authorityGeneration: epoch,
  epoch: epoch,
  sessionIdentity: epoch,
);

List<ConsoleGame> _games() => <ConsoleGame>[
  _game('chrono', 'Chrono Trigger', 'snes', 'SNES', 1995, 9.4, 'Role-playing'),
  _game(
    'battletoads',
    'Battletoads',
    'genesis',
    'Genesis',
    1993,
    8.7,
    'Action',
  ),
  _game('zelda', 'The Legend of Zelda', 'snes', 'SNES', 1991, 9.2, 'Adventure'),
  _game('metroid', 'Super Metroid', 'snes', 'SNES', 1994, 9.6, 'Adventure'),
  _game('fzero', 'F-Zero', 'snes', 'SNES', 1990, 8.4, 'Racing'),
  _game(
    'castlevania',
    'Super Castlevania IV',
    'snes',
    'SNES',
    1991,
    9,
    'Action',
  ),
  _game('contra', 'Contra III', 'snes', 'SNES', 1992, 8.9, 'Action'),
  _game('earthbound', 'EarthBound', 'snes', 'SNES', 1994, 9.1, 'Role-playing'),
];

ConsoleGame _game(
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

ConsoleGameDetail _detail(
  ConsoleGame game,
  String description, {
  List<ConsoleMediaRef> media = const <ConsoleMediaRef>[],
}) => ConsoleGameDetail(
  id: game.id,
  platformId: game.platformId,
  platformName: game.platformName,
  title: game.title,
  description: description,
  publisher: null,
  developer: null,
  genre: game.genre,
  releaseDate: game.releaseDate,
  players: 1,
  rating: game.rating,
  media: media,
  releases: const <ConsoleRelease>[],
  defaultReleaseId: game.defaultReleaseId,
);
