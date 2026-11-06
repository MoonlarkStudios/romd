import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/catalog/discover_shell.dart';
import 'package:romd_console/src/presentation/catalog/featured/discover_featured_boundary.dart';
import 'package:romd_console/src/presentation/theme/console_theme_extensions.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_hint_bar.dart';
import 'package:romd_console/src/presentation/widgets/cover_art.dart';

void main() {
  testWidgets(
    'focus updates spotlight immediately and detail enrichment settles',
    (tester) async {
      final detailRequests = <String>[];
      final secondDetail = Completer<ConsoleGameDetail?>();
      String? rememberedId;

      await _pumpFeatured(
        tester,
        callbacks: _callbacks(
          onRememberFocus: ({required itemId, required scrollOffset}) =>
              rememberedId = itemId,
          loadDetail: (id) {
            detailRequests.add(id);
            return id == 'second'
                ? secondDetail.future
                : Future<ConsoleGameDetail?>.value();
          },
        ),
      );
      await tester.pump();

      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'catalog-featured-0-0',
      );
      expect(find.text('First Quest'), findsWidgets);

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.pump();

      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'catalog-featured-0-1',
      );
      expect(
        tester
            .widget<Text>(
              find.byKey(const ValueKey<String>('featured-spotlight-title')),
            )
            .data,
        'SECOND MISSION',
      );
      expect(rememberedId, 'second');
      expect(detailRequests, isEmpty);

      await tester.pump(const Duration(milliseconds: 179));
      expect(detailRequests, isEmpty);
      await tester.pump(const Duration(milliseconds: 1));
      expect(detailRequests, <String>['second']);

      secondDetail.complete(_detail('second', 'Enriched after focus settles.'));
      await tester.pump();
      await tester.pump();
      expect(find.text('Enriched after focus settles.'), findsOneWidget);
    },
  );

  testWidgets('stale detail response cannot replace the current spotlight', (
    tester,
  ) async {
    final secondDetail = Completer<ConsoleGameDetail?>();

    await _pumpFeatured(
      tester,
      callbacks: _callbacks(
        loadDetail: (id) => id == 'second'
            ? secondDetail.future
            : Future<ConsoleGameDetail?>.value(),
      ),
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump(const Duration(milliseconds: 180));
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    expect(
      tester
          .widget<Text>(
            find.byKey(const ValueKey<String>('featured-spotlight-title')),
          )
          .data,
      'THIRD STORY',
    );

    secondDetail.complete(_detail('second', 'Wrong title detail.'));
    await tester.pump();

    expect(find.text('Wrong title detail.'), findsNothing);
    expect(
      tester
          .widget<Text>(
            find.byKey(const ValueKey<String>('featured-spotlight-title')),
          )
          .data,
      'THIRD STORY',
    );
  });

  testWidgets(
    'readiness is visible and announced only for an installed default release',
    (tester) async {
      await _pumpFeatured(tester, callbacks: _callbacks());

      expect(find.text('READY'), findsOneWidget);
      expect(
        tester
            .getSemantics(
              find.byKey(const ValueKey<String>('featured-card-first')),
            )
            .label,
        contains('ready to play'),
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.pump();

      expect(
        find.byKey(const ValueKey<String>('featured-spotlight-readiness')),
        findsNothing,
      );
      expect(find.text('NOT ON DEVICE'), findsNothing);
      final uninstalledSemantics = tester.getSemantics(
        find.byKey(const ValueKey<String>('featured-card-second')),
      );
      expect(uninstalledSemantics.label, startsWith('Second Mission, GEN'));
      expect(uninstalledSemantics.label, isNot(contains('ready to play')));
      expect(uninstalledSemantics.label, isNot(contains('not on device')));
    },
  );

  testWidgets('D-pad traversal hands off to the header and restores shelf', (
    tester,
  ) async {
    await _pumpFeatured(tester, callbacks: _callbacks());

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
    await tester.pump();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'discover-tab-featured',
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'catalog-featured-0-1',
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'catalog-featured-1-0',
    );
  });

  testWidgets('focused cards remain fully opaque across shelves', (
    tester,
  ) async {
    await _pumpFeatured(tester, callbacks: _callbacks());
    await tester.pump();

    expect(_cardOpacity(tester, 'first'), 1);

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    expect(_cardOpacity(tester, 'second'), 1);

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    expect(_cardOpacity(tester, 'archive-first'), 1);
  });

  testWidgets(
    'only the focused shelf card reveals its title while semantics persist',
    (tester) async {
      await _pumpFeatured(tester, callbacks: _callbacks());
      await tester.pump();

      expect(
        find.byKey(const ValueKey<String>('featured-card-title-first')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey<String>('featured-card-title-second')),
        findsNothing,
      );
      expect(
        tester
            .getSemantics(
              find.byKey(const ValueKey<String>('featured-card-second')),
            )
            .label,
        startsWith('Second Mission, GEN'),
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.pump();

      expect(
        find.byKey(const ValueKey<String>('featured-card-title-first')),
        findsNothing,
      );
      expect(
        find.byKey(const ValueKey<String>('featured-card-title-second')),
        findsOneWidget,
      );
      expect(
        tester
            .getSemantics(
              find.byKey(const ValueKey<String>('featured-card-first')),
            )
            .label,
        contains('First Quest, SNES, ready to play'),
      );
    },
  );

  testWidgets('Enter opens only the focused card', (tester) async {
    ConsoleGame? opened;
    String? heroTag;
    await _pumpFeatured(
      tester,
      callbacks: _callbacks(
        onOpenGame: (game, tag) {
          opened = game;
          heroTag = tag;
        },
      ),
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    final sourceHero = tester.widget<Hero>(
      find.descendant(
        of: find.byKey(const ValueKey<String>('featured-card-second')),
        matching: find.byType(Hero),
      ),
    );
    expect(sourceHero.tag, 'featured-0-1');
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(opened?.id, 'second');
    expect(heroTag, 'featured-0-1');
  });

  testWidgets('focus publishes the selected platform tone', (tester) async {
    final tones = <Color>[];
    await _pumpFeatured(
      tester,
      callbacks: _callbacks(onSelectedTone: tones.add),
    );
    await tester.pump();

    expect(tones, isNotEmpty);
    final initialTone = tones.last;
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    await tester.pump();

    expect(tones.last, isNot(initialTone));
  });

  testWidgets('portrait cover media is never used as the cinematic backdrop', (
    tester,
  ) async {
    final cover = Uri.parse('https://media.invalid/portrait-cover.png');
    final requestedImages = <Uri>[];
    final provider = _pixelImageProvider();
    final detail = _detail(
      'first',
      'A cover-only detail.',
      media: <ConsoleMediaRef>[
        ConsoleMediaRef(
          id: 'portrait',
          type: 'cover',
          url: cover,
          isPrimary: true,
        ),
      ],
    );

    await _pumpFeatured(
      tester,
      imageProvider: (uri) {
        requestedImages.add(uri);
        return provider;
      },
      callbacks: _callbacks(detailFor: (id) => id == 'first' ? detail : null),
    );

    expect(requestedImages, isEmpty);
    expect(
      find.descendant(
        of: find.byKey(const ValueKey<String>('featured-hero-backdrop')),
        matching: find.byType(Image),
      ),
      findsNothing,
    );
    expect(tester.takeException(), isNull);
  });

  testWidgets('cinematic media uses the deterministic image-provider seam', (
    tester,
  ) async {
    final screenshot = Uri.parse(
      'https://media.invalid/cinematic-screenshot.png',
    );
    final requestedImages = <Uri>[];
    final provider = _pixelImageProvider();
    final detail = _detail(
      'first',
      'A cinematic detail.',
      media: <ConsoleMediaRef>[
        ConsoleMediaRef(
          id: 'wide',
          type: 'screenshot',
          url: screenshot,
          isPrimary: true,
        ),
      ],
    );

    await _pumpFeatured(
      tester,
      imageProvider: (uri) {
        requestedImages.add(uri);
        return provider;
      },
      callbacks: _callbacks(detailFor: (id) => id == 'first' ? detail : null),
    );

    expect(requestedImages, isNotEmpty);
    expect(requestedImages.toSet(), <Uri>{screenshot});
    final image = tester.widget<Image>(
      find.descendant(
        of: find.byKey(const ValueKey<String>('featured-hero-backdrop')),
        matching: find.byType(Image),
      ),
    );
    final resized = image.image as ResizeImage;
    expect(resized.imageProvider, same(provider));
    expect(image.alignment, Alignment.centerRight);
    final backdropRect = tester.getRect(
      find.byKey(const ValueKey<String>('featured-hero-backdrop')),
    );
    expect(
      tester.getRect(find.byWidget(image)).left,
      greaterThan(backdropRect.left),
    );
    expect(tester.takeException(), isNull);
  });

  testWidgets('spotlight uses scoped type and layered portrait cover framing', (
    tester,
  ) async {
    final cover = Uri.parse('https://media.invalid/hero-cover.png');
    await _pumpFeatured(
      tester,
      callbacks: _callbacks(),
      shelves: <DiscoverFeaturedShelf>[
        DiscoverFeaturedShelf(
          id: 'framed',
          eyebrow: 'Featured',
          title: 'Framed',
          games: <ConsoleGame>[
            ConsoleGame(
              id: 'framed',
              platformId: 'snes',
              platformName: 'SNES',
              title: 'Framed Quest',
              releaseDate: DateTime.utc(1994),
              coverUrl: cover,
              genre: 'Adventure',
              rating: 9.6,
              releaseCount: 1,
              defaultReleaseId: 'release-framed',
            ),
          ],
        ),
      ],
      imageProvider: (_) => _pixelImageProvider(),
    );

    final title = tester.widget<Text>(
      find.byKey(const ValueKey<String>('featured-spotlight-title')),
    );
    final titleContext = tester.element(
      find.byKey(const ValueKey<String>('featured-spotlight-title')),
    );
    expect(
      title.style!.fontSize,
      Theme.of(titleContext).textTheme.displayMedium!.fontSize,
    );

    final surroundFinder = find.byKey(
      const ValueKey<String>('featured-spotlight-cover-surround'),
    );
    final frameFinder = find.byKey(
      const ValueKey<String>('featured-spotlight-cover-frame'),
    );
    final surroundRect = tester.getRect(surroundFinder);
    final frameRect = tester.getRect(frameFinder);
    expect(surroundRect.width - frameRect.width, closeTo(12, 0.01));
    expect(surroundRect.height - frameRect.height, closeTo(12, 0.01));
    expect(
      frameRect.width / frameRect.height,
      closeTo(kCoverAspectRatio, 0.01),
    );

    final surround = tester.widget<DecoratedBox>(surroundFinder);
    final decoration = surround.decoration as BoxDecoration;
    final colors = Theme.of(
      tester.element(surroundFinder),
    ).extension<ConsoleColors>()!;
    expect(decoration.color, colors.mediaChipSurface);
    expect(decoration.border!.top.color, colors.borderStrong);
    expect(tester.takeException(), isNull);
  });

  testWidgets('shelf cards restore portrait density at 1280', (tester) async {
    await _pumpFeatured(tester, callbacks: _callbacks());

    final firstCard = tester.getRect(
      find.byKey(const ValueKey<String>('featured-card-first')),
    );
    expect(
      firstCard.width / firstCard.height,
      closeTo(kCoverAspectRatio, 0.01),
    );

    final viewport = tester.getRect(
      find.byKey(const ValueKey<String>('featured-shelf-list-top-rated')),
    );
    final fullyVisibleCards = <ConsoleGame>[
      for (final game in _games.take(7))
        if (find
            .byKey(ValueKey<String>('featured-card-${game.id}'))
            .evaluate()
            .isNotEmpty)
          if (_isFullyInside(
            tester.getRect(
              find.byKey(ValueKey<String>('featured-card-${game.id}')),
            ),
            viewport,
          ))
            game,
    ];
    expect(fullyVisibleCards, _games.take(6));
    expect(tester.takeException(), isNull);
  });

  testWidgets(
    'first-shelf horizontal traversal preserves the expanded hero and offset',
    (tester) async {
      await _pumpFeatured(tester, callbacks: _callbacks());

      final initialSpotlight = tester.getRect(
        find.byKey(const ValueKey<String>('featured-spotlight-first')),
      );
      expect(initialSpotlight.height, greaterThan(250));
      expect(_contentScrollOffset(tester), 0);
      final firstShelfViewport = tester.getRect(
        find.byKey(const ValueKey<String>('featured-shelf-list-top-rated')),
      );
      final fullyVisibleCards = <ConsoleGame>[
        for (final game in _games.take(6))
          if (find
              .byKey(ValueKey<String>('featured-card-${game.id}'))
              .evaluate()
              .isNotEmpty)
            if (_isFullyInside(
              tester.getRect(
                find.byKey(ValueKey<String>('featured-card-${game.id}')),
              ),
              firstShelfViewport,
            ))
              game,
      ];
      expect(fullyVisibleCards, _games.take(6));

      for (var move = 0; move < 4; move++) {
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
        await tester.pump();
      }

      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'catalog-featured-0-4',
      );
      expect(_contentScrollOffset(tester), 0);
      expect(
        tester
            .getRect(
              find.byKey(const ValueKey<String>('featured-spotlight-fifth')),
            )
            .height,
        initialSpotlight.height,
      );
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('first poster and focused caption fit above the 720p footer', (
    tester,
  ) async {
    await _pumpFeatured(tester, callbacks: _callbacks());
    await tester.pump();

    final poster = tester.getRect(
      find.byKey(const ValueKey<String>('featured-card-first')),
    );
    final caption = tester.getRect(
      find.byKey(const ValueKey<String>('featured-card-title-first')),
    );
    expect(poster.width / poster.height, closeTo(2 / 3, 0.001));
    expect(poster.bottom, lessThanOrEqualTo(_footerTop(tester)));
    expect(caption.bottom, lessThanOrEqualTo(_footerTop(tester)));
    expect(tester.takeException(), isNull);
  });

  testWidgets(
    'vertical traversal reveals stable later shelves above the footer',
    (tester) async {
      await _pumpFeatured(tester, callbacks: _callbacks());

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pump();
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pump();

      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'catalog-featured-2-0',
      );
      final settledOffset = _contentScrollOffset(tester);
      expect(settledOffset, greaterThan(0));
      expect(
        tester
            .getRect(
              find.byKey(const ValueKey<String>('featured-card-late-first')),
            )
            .bottom,
        lessThanOrEqualTo(_footerTop(tester) - 16),
      );

      await tester.pump(const Duration(seconds: 1));
      expect(_contentScrollOffset(tester), settledOffset);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'upward traversal positions the focused row below collapsed header chrome',
    (tester) async {
      await _pumpFeatured(tester, callbacks: _callbacks());

      for (var move = 0; move < 3; move++) {
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
        await tester.pump();
      }
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump();

      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'catalog-featured-2-0',
      );
      final headerBottom = tester
          .getRect(find.byKey(const ValueKey<String>('discover-header-chrome')))
          .bottom;
      final focusedCardTop = tester
          .getRect(
            find.byKey(const ValueKey<String>('featured-card-late-first')),
          )
          .top;
      expect(focusedCardTop, greaterThan(headerBottom));
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('deep horizontal traversal never moves the vertical viewport', (
    tester,
  ) async {
    await _pumpFeatured(tester, callbacks: _callbacks());

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    final verticalOffset = _contentScrollOffset(tester);

    for (var move = 0; move < 5; move++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.pump();
    }

    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'catalog-featured-2-5',
    );
    expect(_contentScrollOffset(tester), verticalOffset);
    expect(tester.takeException(), isNull);
  });

  testWidgets(
    'hero chrome fades in for deep shelves and clears on return to the first',
    (tester) async {
      final progress = <double>[];
      await _pumpFeatured(
        tester,
        callbacks: _callbacks(onHeroChromeProgress: progress.add),
      );
      await tester.pump();

      expect(progress, isNotEmpty);
      expect(progress.last, closeTo(0, 0.001));

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pump();
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pump();

      expect(_contentScrollOffset(tester), greaterThan(0));
      expect(progress.last, closeTo(1, 0.001));
      expect(_heroForegroundOpacity(tester), 0);

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump();
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump();

      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'catalog-featured-0-0',
      );
      expect(_contentScrollOffset(tester), 0);
      expect(progress.last, closeTo(0, 0.001));
      expect(_heroForegroundOpacity(tester), 1);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('returning to the first shelf restores offset zero and hero', (
    tester,
  ) async {
    await _pumpFeatured(tester, callbacks: _callbacks());

    for (var move = 0; move < 3; move++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pump();
    }
    expect(_contentScrollOffset(tester), greaterThan(0));
    expect(
      find.byKey(const ValueKey<String>('featured-spotlight-vault-first')),
      findsNothing,
    );

    for (var move = 0; move < 3; move++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump();
    }

    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'catalog-featured-0-0',
    );
    expect(_contentScrollOffset(tester), 0);
    expect(
      tester
          .getRect(
            find.byKey(const ValueKey<String>('featured-spotlight-first')),
          )
          .height,
      greaterThan(250),
    );
    expect(tester.takeException(), isNull);
  });

  testWidgets(
    'reload restores deep focus and offset without eager detail loading',
    (tester) async {
      final detailRequests = <String>[];
      String? rememberedId;
      var rememberedOffset = 0.0;
      final harness = await _pumpReloadHarness(
        tester,
        callbacks: _callbacks(
          onRememberFocus:
              ({required String? itemId, required double scrollOffset}) {
                rememberedId = itemId;
                rememberedOffset = scrollOffset;
              },
          loadDetail: (id) {
            detailRequests.add(id);
            return Future<ConsoleGameDetail?>.value();
          },
        ),
      );

      for (var move = 0; move < 3; move++) {
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
        await tester.pump();
      }
      expect(rememberedId, 'vault-first');
      expect(rememberedOffset, greaterThan(0));
      expect(rememberedOffset, closeTo(_contentScrollOffset(tester), 0.01));
      await tester.pump(const Duration(milliseconds: 180));
      expect(detailRequests, isEmpty);

      harness.update(
        snapshot: const DiscoverFeaturedSnapshot(
          state: DiscoverLoadState.loading,
        ),
        focus: DiscoverFocusSnapshot(
          itemId: rememberedId,
          scrollOffset: rememberedOffset,
        ),
      );
      await tester.pump();
      expect(find.text('Opening Featured'), findsOneWidget);

      harness.update(
        snapshot: DiscoverFeaturedSnapshot(
          state: DiscoverLoadState.ready,
          shelves: List<DiscoverFeaturedShelf>.of(_shelves),
        ),
        focus: DiscoverFocusSnapshot(
          itemId: rememberedId,
          scrollOffset: rememberedOffset,
        ),
      );
      await tester.pump();
      await tester.pump();

      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'catalog-featured-3-0',
      );
      expect(_contentScrollOffset(tester), closeTo(rememberedOffset, 0.01));
      await tester.pump(const Duration(milliseconds: 180));
      expect(detailRequests, isEmpty);

      for (var move = 0; move < 3; move++) {
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
        await tester.pump();
      }
      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'catalog-featured-0-0',
      );
      expect(_contentScrollOffset(tester), 0);
      await tester.pump(const Duration(milliseconds: 179));
      expect(detailRequests, isEmpty);
      await tester.pump(const Duration(milliseconds: 1));
      expect(detailRequests, <String>['first']);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('persisted far-horizontal focus is revealed before restoration', (
    tester,
  ) async {
    const shelfId = 'complete-archive';
    await _pumpReloadHarness(
      tester,
      initialSnapshot: DiscoverFeaturedSnapshot(
        state: DiscoverLoadState.ready,
        shelves: <DiscoverFeaturedShelf>[
          DiscoverFeaturedShelf(
            id: shelfId,
            eyebrow: 'Complete archive',
            title: 'Twenty-four essential games',
            games: _games,
          ),
        ],
      ),
      initialFocus: const DiscoverFocusSnapshot(itemId: 'late-first'),
      callbacks: _callbacks(),
    );
    await tester.pump();
    await tester.pump();

    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'catalog-featured-0-12',
    );
    expect(_shelfScrollOffset(tester, shelfId), greaterThan(0));
    expect(tester.takeException(), isNull);
  });

  testWidgets(
    'reload with a removed deep item restores the first visible hero',
    (tester) async {
      final detailRequests = <String>[];
      String? rememberedId;
      var rememberedOffset = 0.0;
      final harness = await _pumpReloadHarness(
        tester,
        callbacks: _callbacks(
          onRememberFocus:
              ({required String? itemId, required double scrollOffset}) {
                rememberedId = itemId;
                rememberedOffset = scrollOffset;
              },
          loadDetail: (id) {
            detailRequests.add(id);
            return Future<ConsoleGameDetail?>.value();
          },
        ),
      );

      for (var move = 0; move < 3; move++) {
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
        await tester.pump();
      }
      expect(rememberedId, 'vault-first');
      expect(rememberedOffset, greaterThan(0));
      await tester.pump(const Duration(milliseconds: 180));
      expect(detailRequests, isEmpty);

      final persistedFocus = DiscoverFocusSnapshot(
        itemId: rememberedId,
        scrollOffset: rememberedOffset,
      );
      harness.update(
        snapshot: const DiscoverFeaturedSnapshot(
          state: DiscoverLoadState.loading,
        ),
        focus: persistedFocus,
      );
      await tester.pump();

      harness.update(
        snapshot: DiscoverFeaturedSnapshot(
          state: DiscoverLoadState.ready,
          shelves: _shelves
              .map(
                (shelf) => shelf.id == 'vault'
                    ? DiscoverFeaturedShelf(
                        id: shelf.id,
                        eyebrow: shelf.eyebrow,
                        title: shelf.title,
                        games: shelf.games
                            .where((game) => game.id != rememberedId)
                            .toList(growable: false),
                      )
                    : shelf,
              )
              .toList(growable: false),
        ),
        focus: persistedFocus,
      );
      await tester.pump();
      await tester.pump();

      expect(
        tester.binding.focusManager.primaryFocus?.debugLabel,
        'catalog-featured-0-0',
      );
      expect(_contentScrollOffset(tester), 0);
      expect(
        find.byKey(const ValueKey<String>('featured-spotlight-first')),
        findsOneWidget,
      );
      await tester.pump(const Duration(milliseconds: 179));
      expect(detailRequests, isEmpty);
      await tester.pump(const Duration(milliseconds: 1));
      expect(detailRequests, <String>['first']);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('reduced motion makes spotlight swaps immediate', (tester) async {
    await _pumpFeatured(tester, callbacks: _callbacks());

    final switcher = tester.widget<AnimatedSwitcher>(
      find.descendant(
        of: find.byKey(const ValueKey<String>('featured-hero-backdrop')),
        matching: find.byType(AnimatedSwitcher),
      ),
    );
    expect(switcher.duration, Duration.zero);
  });

  testWidgets('2x text keeps focused title and readiness contained', (
    tester,
  ) async {
    await _pumpFeatured(
      tester,
      callbacks: _callbacks(),
      textScaler: const TextScaler.linear(2),
    );

    expect(
      find.byKey(const ValueKey<String>('featured-spotlight-title')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey<String>('featured-spotlight-readiness')),
      findsOneWidget,
    );
    expect(tester.takeException(), isNull);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    expect(tester.takeException(), isNull);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    expect(tester.takeException(), isNull);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'catalog-featured-2-0',
    );
    expect(
      tester
          .getRect(
            find.byKey(const ValueKey<String>('featured-card-late-first')),
          )
          .bottom,
      lessThanOrEqualTo(_footerTop(tester) - 16),
    );
    expect(tester.takeException(), isNull);
  });
}

final class _FeaturedReloadHarness extends StatefulWidget {
  const _FeaturedReloadHarness({
    required this.focusHandoff,
    required this.callbacks,
    required this.initialSnapshot,
    required this.initialFocus,
    super.key,
  });

  final DiscoverFocusHandoff focusHandoff;
  final DiscoverFeaturedCallbacks callbacks;
  final DiscoverFeaturedSnapshot initialSnapshot;
  final DiscoverFocusSnapshot initialFocus;

  @override
  State<_FeaturedReloadHarness> createState() => _FeaturedReloadHarnessState();
}

final class _FeaturedReloadHarnessState extends State<_FeaturedReloadHarness> {
  late DiscoverFeaturedSnapshot _snapshot;
  late DiscoverFocusSnapshot _focus;

  @override
  void initState() {
    super.initState();
    _snapshot = widget.initialSnapshot;
    _focus = widget.initialFocus;
  }

  void update({
    required DiscoverFeaturedSnapshot snapshot,
    required DiscoverFocusSnapshot focus,
  }) {
    setState(() {
      _snapshot = snapshot;
      _focus = focus;
    });
  }

  @override
  Widget build(BuildContext context) => DiscoverFeaturedBoundary(
    focusHandoff: widget.focusHandoff,
    input: DiscoverFeaturedInput(
      snapshot: _snapshot,
      focus: _focus,
      installedReleaseIds: const <String>{'release-first'},
      operationAvailable: true,
      active: true,
      autofocusNavigation: true,
    ),
    callbacks: widget.callbacks,
  );
}

Future<_FeaturedReloadHarnessState> _pumpReloadHarness(
  WidgetTester tester, {
  required DiscoverFeaturedCallbacks callbacks,
  DiscoverFeaturedSnapshot? initialSnapshot,
  DiscoverFocusSnapshot initialFocus = const DiscoverFocusSnapshot(
    itemId: 'first',
  ),
}) async {
  tester.view
    ..devicePixelRatio = 1
    ..physicalSize = const Size(1280, 720);
  addTearDown(tester.view.resetDevicePixelRatio);
  addTearDown(tester.view.resetPhysicalSize);

  final controller = DiscoverShellController();
  final handoff = DiscoverFocusHandoff();
  final harnessKey = GlobalKey<_FeaturedReloadHarnessState>();
  addTearDown(controller.dispose);
  await tester.pumpWidget(
    MaterialApp(
      theme: RomdSkins.baselineDark(),
      builder: (context, child) => MediaQuery(
        data: MediaQuery.of(context).copyWith(disableAnimations: true),
        child: child!,
      ),
      home: Scaffold(
        body: DiscoverShell(
          controller: controller,
          focusHandoff: handoff,
          onSearch: () {},
          onBack: () {},
          slots: DiscoverFeatureSlots(
            featured: (_) => _FeaturedReloadHarness(
              key: harnessKey,
              focusHandoff: handoff,
              callbacks: callbacks,
              initialSnapshot:
                  initialSnapshot ??
                  DiscoverFeaturedSnapshot(
                    state: DiscoverLoadState.ready,
                    shelves: List<DiscoverFeaturedShelf>.of(_shelves),
                  ),
              initialFocus: initialFocus,
            ),
            allGames: (_) => const SizedBox.shrink(),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  return harnessKey.currentState!;
}

Future<void> _pumpFeatured(
  WidgetTester tester, {
  required DiscoverFeaturedCallbacks callbacks,
  bool disableAnimations = true,
  TextScaler textScaler = TextScaler.noScaling,
  ImageProvider<Object> Function(Uri uri)? imageProvider,
  List<DiscoverFeaturedShelf>? shelves,
}) async {
  tester.view
    ..devicePixelRatio = 1
    ..physicalSize = const Size(1280, 720);
  addTearDown(tester.view.resetDevicePixelRatio);
  addTearDown(tester.view.resetPhysicalSize);

  final controller = DiscoverShellController();
  final handoff = DiscoverFocusHandoff();
  addTearDown(controller.dispose);
  await tester.pumpWidget(
    MaterialApp(
      theme: RomdSkins.baselineDark(),
      builder: (context, child) => MediaQuery(
        data: MediaQuery.of(context).copyWith(
          disableAnimations: disableAnimations,
          textScaler: textScaler,
        ),
        child: child!,
      ),
      home: Scaffold(
        body: DiscoverShell(
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
                  shelves: shelves ?? _shelves,
                ),
                focus: const DiscoverFocusSnapshot(itemId: 'first'),
                installedReleaseIds: const <String>{'release-first'},
                operationAvailable: true,
                active: true,
                autofocusNavigation: true,
                imageProvider: imageProvider,
              ),
              callbacks: callbacks,
            ),
            allGames: (_) => const SizedBox.shrink(),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
}

double _contentScrollOffset(WidgetTester tester) => tester
    .widget<CustomScrollView>(
      find.byKey(const ValueKey<String>('featured-content-scroll-view')),
    )
    .controller!
    .offset;

double _footerTop(WidgetTester tester) =>
    tester.getRect(find.byType(ConsoleFooterBar)).top;

double _shelfScrollOffset(WidgetTester tester, String shelfId) => tester
    .widget<ListView>(
      find.byKey(ValueKey<String>('featured-shelf-list-$shelfId')),
    )
    .controller!
    .offset;

bool _isFullyInside(Rect child, Rect viewport) =>
    child.left >= viewport.left && child.right <= viewport.right;

double _cardOpacity(WidgetTester tester, String gameId) => tester
    .widget<AnimatedOpacity>(
      find.descendant(
        of: find.byKey(ValueKey<String>('featured-card-$gameId')),
        matching: find.byType(AnimatedOpacity),
      ),
    )
    .opacity;

double _heroForegroundOpacity(WidgetTester tester) {
  final finder = find.byKey(const ValueKey<String>('featured-hero-foreground'));
  if (finder.evaluate().isEmpty) return 0;
  return tester.widget<Opacity>(finder).opacity;
}

DiscoverFeaturedCallbacks _callbacks({
  void Function(ConsoleGame game, String heroTag)? onOpenGame,
  void Function({required String? itemId, required double scrollOffset})?
  onRememberFocus,
  ConsoleGameDetail? Function(String titleId)? detailFor,
  Future<ConsoleGameDetail?> Function(String titleId)? loadDetail,
  ValueChanged<Color>? onSelectedTone,
  ValueChanged<double>? onHeroChromeProgress,
}) => DiscoverFeaturedCallbacks(
  navigation: DiscoverFeatureNavigation(
    onSelectSection: (_) {},
    onSearch: () {},
    onConnect: () {},
  ),
  onOpenGame: onOpenGame ?? (_, _) {},
  onRetry: () {},
  onRememberFocus:
      onRememberFocus ?? ({required itemId, required scrollOffset}) {},
  detailFor: detailFor ?? (_) => null,
  loadDetail: loadDetail ?? (_) async => null,
  onSelectedTone: onSelectedTone,
  onHeroChromeProgress: onHeroChromeProgress,
);

final List<ConsoleGame> _games = <ConsoleGame>[
  _game('first', 'First Quest', 'SNES', 1994, 9.6),
  _game('second', 'Second Mission', 'GEN', 1993, 9.2),
  _game('third', 'Third Story', 'PS1', 1997, 8.9),
  _game('fourth', 'Fourth Signal', 'N64', 1998, 8.7),
  _game('fifth', 'Fifth Element', 'GBA', 2002, 8.4),
  _game('sixth', 'Sixth Archive', 'SNES', 1992, 8.1),
  _game('archive-first', 'Archive First', 'GEN', 1991, 8.0),
  _game('archive-second', 'Archive Second', 'PS1', 1996, 7.9),
  _game('archive-third', 'Archive Third', 'N64', 1999, 7.8),
  _game('archive-fourth', 'Archive Fourth', 'GBA', 2003, 7.7),
  _game('archive-fifth', 'Archive Fifth', 'SNES', 1995, 7.6),
  _game('archive-sixth', 'Archive Sixth', 'GEN', 1994, 7.5),
  _game('late-first', 'Late First', 'PS1', 1998, 7.4),
  _game('late-second', 'Late Second', 'N64', 2000, 7.3),
  _game('late-third', 'Late Third', 'GBA', 2004, 7.2),
  _game('late-fourth', 'Late Fourth', 'SNES', 1996, 7.1),
  _game('late-fifth', 'Late Fifth', 'GEN', 1995, 7.0),
  _game('late-sixth', 'Late Sixth', 'PS1', 1999, 6.9),
  _game('vault-first', 'Vault First', 'N64', 2001, 6.8),
  _game('vault-second', 'Vault Second', 'GBA', 2005, 6.7),
  _game('vault-third', 'Vault Third', 'SNES', 1997, 6.6),
  _game('vault-fourth', 'Vault Fourth', 'GEN', 1996, 6.5),
  _game('vault-fifth', 'Vault Fifth', 'PS1', 2000, 6.4),
  _game('vault-sixth', 'Vault Sixth', 'N64', 2002, 6.3),
];

final List<DiscoverFeaturedShelf> _shelves = <DiscoverFeaturedShelf>[
  DiscoverFeaturedShelf(
    id: 'top-rated',
    eyebrow: 'Top rated',
    title: 'Essential play',
    games: _games.take(6).toList(growable: false),
  ),
  DiscoverFeaturedShelf(
    id: 'archive-picks',
    eyebrow: 'Featured collection',
    title: 'Archive picks',
    games: _games.skip(6).take(6).toList(growable: false),
  ),
  DiscoverFeaturedShelf(
    id: 'late-night',
    eyebrow: 'After dark',
    title: 'Late-night legends',
    games: _games.skip(12).take(6).toList(growable: false),
  ),
  DiscoverFeaturedShelf(
    id: 'vault',
    eyebrow: 'From the vault',
    title: 'Rare discoveries',
    games: _games.skip(18).take(6).toList(growable: false),
  ),
];

ConsoleGame _game(
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
  coverUrl: null,
  genre: id == 'first' ? 'Adventure' : 'Action',
  rating: rating,
  releaseCount: 1,
  defaultReleaseId: 'release-$id',
);

ConsoleGameDetail _detail(
  String id,
  String description, {
  List<ConsoleMediaRef> media = const <ConsoleMediaRef>[],
}) => ConsoleGameDetail(
  id: id,
  platformId: 'gen',
  platformName: 'GEN',
  title: 'Second Mission',
  description: description,
  publisher: 'ROMD Archive',
  developer: 'ROMD Archive',
  genre: 'Action',
  releaseDate: DateTime.utc(1993),
  players: 2,
  rating: 9.2,
  media: media,
  releases: const <ConsoleRelease>[],
  defaultReleaseId: 'release-$id',
);

MemoryImage _pixelImageProvider() => MemoryImage(
  base64Decode(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlE'
    'QVQIHWP4z8DwHwAFgAI/ScLkWQAAAABJRU5ErkJggg==',
  ),
);
