import 'dart:async';

import 'package:flutter/material.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/catalog/discover_shell.dart';
import 'package:romd_console/src/presentation/launcher/launcher_primitives.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';
import 'package:romd_console/src/presentation/widgets/console_focusable.dart';
import 'package:romd_console/src/presentation/widgets/cover_art.dart';
import 'package:romd_console/src/presentation/widgets/platform_presentation.dart';

@immutable
final class DiscoverFeaturedInput {
  const DiscoverFeaturedInput({
    required this.snapshot,
    required this.focus,
    required this.installedReleaseIds,
    required this.operationAvailable,
    required this.active,
    required this.autofocusNavigation,
    this.imageProvider,
  });

  final DiscoverFeaturedSnapshot snapshot;
  final DiscoverFocusSnapshot focus;
  final Set<String> installedReleaseIds;
  final bool operationAvailable;
  final bool active;
  final bool autofocusNavigation;

  /// Optional decoded-image seam for deterministic previews and goldens.
  ///
  /// Production resolves artwork through [NetworkImage].
  final ImageProvider<Object> Function(Uri uri)? imageProvider;
}

@immutable
final class DiscoverFeaturedCallbacks {
  const DiscoverFeaturedCallbacks({
    required this.navigation,
    required this.onOpenGame,
    required this.onRetry,
    required this.onRememberFocus,
    required this.detailFor,
    required this.loadDetail,
    this.onSelectedTone,
    this.onHeroChromeProgress,
  });

  final DiscoverFeatureNavigation navigation;
  final void Function(ConsoleGame game, String heroTag) onOpenGame;
  final VoidCallback onRetry;
  final void Function({required String? itemId, required double scrollOffset})
  onRememberFocus;
  final ConsoleGameDetail? Function(String titleId) detailFor;
  final Future<ConsoleGameDetail?> Function(String titleId) loadDetail;
  final ValueChanged<Color>? onSelectedTone;
  final ValueChanged<double>? onHeroChromeProgress;
}

/// Focus owner and cinematic living-catalog renderer for Discover / Featured.
///
/// The hero is contextual rather than focusable: only cards participate in
/// traversal. Basic card data is published immediately while detail
/// enrichment settles independently, so a slow Catalog response can never
/// stall the D-pad.
final class DiscoverFeaturedBoundary extends StatefulWidget {
  const DiscoverFeaturedBoundary({
    required this.input,
    required this.callbacks,
    required this.focusHandoff,
    super.key,
  });

  final DiscoverFeaturedInput input;
  final DiscoverFeaturedCallbacks callbacks;
  final DiscoverFocusHandoff focusHandoff;

  @override
  State<DiscoverFeaturedBoundary> createState() =>
      _DiscoverFeaturedBoundaryState();
}

final class _DiscoverFeaturedBoundaryState
    extends State<DiscoverFeaturedBoundary> {
  static const Duration _detailSettleDelay = Duration(milliseconds: 180);

  final FocusNode _statusActionNode = FocusNode(
    debugLabel: 'catalog-featured-status-action',
  );
  final ScrollController _contentScroll = ScrollController(
    debugLabel: 'catalog-featured-content',
  );
  final List<List<FocusNode>> _cardNodes = <List<FocusNode>>[];
  final List<ScrollController> _shelfScroll = <ScrollController>[];
  final List<int> _rememberedGameIndices = <int>[];
  final List<GlobalKey> _shelfKeys = <GlobalKey>[];

  List<DiscoverFeaturedShelf> _shelves = const <DiscoverFeaturedShelf>[];
  ConsoleGame? _focusedGame;
  ConsoleGameDetail? _focusedDetail;
  Timer? _detailTimer;
  int _detailRequestGeneration = 0;
  int _focusedShelf = 0;
  double _heroHeight = 0;
  bool _heroMeaningfullyVisible = true;

  @override
  void initState() {
    super.initState();
    widget.focusHandoff.attachContent(
      DiscoverSection.featured,
      _restoreContentFocus,
    );
    _contentScroll.addListener(_onContentScrolled);
    _syncSnapshot(restoreScroll: true);
  }

  @override
  void didUpdateWidget(covariant DiscoverFeaturedBoundary oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!identical(
      oldWidget.input.snapshot.shelves,
      widget.input.snapshot.shelves,
    )) {
      _syncSnapshot(
        restoreScroll: widget.input.snapshot.state == DiscoverLoadState.ready,
      );
    } else if (_focusedGame != null) {
      final cached = widget.callbacks.detailFor(_focusedGame!.id);
      if (cached != null && !identical(cached, _focusedDetail)) {
        _focusedDetail = cached;
      }
    }
    if (oldWidget.input.active != widget.input.active) {
      if (widget.input.active && _focusedGame != null) {
        _scheduleDetail(_focusedGame!.id);
        if (widget.input.autofocusNavigation) {
          WidgetsBinding.instance.addPostFrameCallback((_) {
            if (mounted) _restoreContentFocus();
          });
        }
      } else {
        _detailTimer?.cancel();
        _detailRequestGeneration++;
      }
    }
    if (!identical(oldWidget.focusHandoff, widget.focusHandoff)) {
      oldWidget.focusHandoff.detachContent(DiscoverSection.featured);
      widget.focusHandoff.attachContent(
        DiscoverSection.featured,
        _restoreContentFocus,
      );
    }
  }

  @override
  void dispose() {
    _detailTimer?.cancel();
    _statusActionNode.dispose();
    widget.focusHandoff.detachContent(DiscoverSection.featured);
    _contentScroll
      ..removeListener(_onContentScrolled)
      ..dispose();
    _disposeCards();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Actions(
    actions: launcherDirectionalActions(_move),
    child: switch (_surfaceState) {
      _FeaturedSurfaceState.offline => _framed(
        _status(
          icon: Icons.cloud_off_outlined,
          title: 'Catalog is offline',
          message: 'Choose and sign in to a ROMD server to browse its Catalog.',
          actionLabel: 'Choose server',
          actionIcon: Icons.link,
          onPressed: widget.callbacks.navigation.onConnect,
        ),
      ),
      _FeaturedSurfaceState.loading => _framed(
        const LauncherStatusPanel(
          icon: Icons.cloud_sync_outlined,
          title: 'Opening Featured',
          message: 'Reading the selected ROMD Catalog.',
        ),
      ),
      _FeaturedSurfaceState.failed => _framed(
        _status(
          icon: Icons.cloud_off_outlined,
          title: 'Catalog unavailable',
          message:
              'The selected ROMD server could not provide this view. '
              'Your local games are unaffected.',
          actionLabel: 'Retry',
          actionIcon: Icons.refresh,
          onPressed: widget.callbacks.onRetry,
        ),
      ),
      _FeaturedSurfaceState.empty => _framed(
        _status(
          icon: Icons.auto_awesome_outlined,
          title: 'Nothing featured yet',
          message: 'This ROMD Catalog has no featured games right now.',
          actionLabel: 'Browse All Games',
          actionIcon: Icons.grid_view_rounded,
          onPressed: () => widget.callbacks.navigation.onSelectSection(
            DiscoverSection.allGames,
          ),
        ),
      ),
      _FeaturedSurfaceState.ready => _buildReady(context),
    },
  );

  Widget _framed(Widget child) => Padding(
    padding: EdgeInsets.symmetric(
      horizontal: DiscoverLayoutScope.of(context).frameMargin,
    ),
    child: child,
  );

  _FeaturedSurfaceState get _surfaceState {
    if (!widget.input.operationAvailable) return _FeaturedSurfaceState.offline;
    return switch (widget.input.snapshot.state) {
      DiscoverLoadState.idle ||
      DiscoverLoadState.loading => _FeaturedSurfaceState.loading,
      DiscoverLoadState.failed => _FeaturedSurfaceState.failed,
      DiscoverLoadState.ready when _shelves.isEmpty =>
        _FeaturedSurfaceState.empty,
      DiscoverLoadState.ready => _FeaturedSurfaceState.ready,
    };
  }

  Widget _status({
    required IconData icon,
    required String title,
    required String message,
    required String actionLabel,
    required IconData actionIcon,
    required VoidCallback onPressed,
  }) => LauncherStatusPanel(
    icon: icon,
    title: title,
    message: message,
    action: FilledButton.icon(
      focusNode: _statusActionNode,
      onPressed: onPressed,
      icon: Icon(actionIcon),
      label: Text(actionLabel),
    ),
  );

  Widget _buildReady(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final spec = DiscoverLayoutScope.of(context);
      final contentHeight = (constraints.maxHeight - spec.headerHeight).clamp(
        0,
        constraints.maxHeight,
      );
      _heroHeight =
          spec.headerHeight +
          (contentHeight * spec.featuredSpotlightMaxFraction);
      return Stack(
        children: <Widget>[
          Positioned(
            top: 0,
            left: 0,
            right: 0,
            height: _heroHeight + context.layout.xxl,
            child: _FeaturedCinematicBackdrop(
              key: const ValueKey<String>('featured-hero-backdrop'),
              game: _focusedGame!,
              detail: _focusedDetail,
              scrollController: _contentScroll,
              heroHeight: _heroHeight,
              fadeDistanceFraction: _heroChromeCollapseFraction,
              imageProvider: widget.input.imageProvider,
            ),
          ),
          CustomScrollView(
            key: const ValueKey<String>('featured-content-scroll-view'),
            controller: _contentScroll,
            slivers: <Widget>[
              SliverToBoxAdapter(
                child: SizedBox(
                  height: _heroHeight,
                  child: AnimatedBuilder(
                    animation: _contentScroll,
                    builder: (context, child) {
                      final offset = _contentScroll.hasClients
                          ? _contentScroll.offset
                          : 0;
                      final progress =
                          (offset / (_heroHeight * _heroChromeCollapseFraction))
                              .clamp(0.0, 1.0);
                      return Opacity(
                        key: const ValueKey<String>('featured-hero-foreground'),
                        opacity: 1 - progress,
                        child: child,
                      );
                    },
                    child: _FeaturedHeroContent(
                      key: ValueKey<String>(
                        'featured-spotlight-${_focusedGame?.id ?? 'empty'}',
                      ),
                      game: _focusedGame!,
                      detail: _focusedDetail,
                      installed: _isInstalled(_focusedGame!),
                      headerSafeInset: spec.headerHeight,
                      imageProvider: widget.input.imageProvider,
                    ),
                  ),
                ),
              ),
              for (var shelf = 0; shelf < _shelves.length; shelf++)
                SliverPadding(
                  padding: EdgeInsets.symmetric(horizontal: spec.frameMargin),
                  sliver: SliverToBoxAdapter(
                    child: _FeaturedShelfView(
                      key: _shelfKeys[shelf],
                      shelf: _shelves[shelf],
                      shelfIndex: shelf,
                      cardNodes: _cardNodes[shelf],
                      scrollController: _shelfScroll[shelf],
                      active: shelf == _focusedShelf,
                      cardWidth: spec.featuredCardWidth,
                      cardGap: spec.featuredCardGap,
                      installedReleaseIds: widget.input.installedReleaseIds,
                      imageProvider: widget.input.imageProvider,
                      onFocused: _onCardFocused,
                      onOpenGame: widget.callbacks.onOpenGame,
                    ),
                  ),
                ),
              SliverToBoxAdapter(child: SizedBox(height: context.layout.lg)),
            ],
          ),
        ],
      );
    },
  );

  void _onContentScrolled() => _syncHeroVisibility();

  void _syncHeroVisibility({bool scheduleIfVisible = false}) {
    _publishHeroChromeProgress();
    if (!_contentScroll.hasClients || _heroHeight <= 0) return;
    final visible =
        _contentScroll.offset < _heroHeight * _heroDetailVisibilityFraction;
    final changed = visible != _heroMeaningfullyVisible;
    if (!changed && !scheduleIfVisible) return;
    _heroMeaningfullyVisible = visible;
    if (visible && _focusedGame != null) {
      _scheduleDetail(_focusedGame!.id);
    } else {
      _detailTimer?.cancel();
      _detailRequestGeneration++;
    }
  }

  static const double _heroDetailVisibilityFraction = 0.7;
  static const double _heroChromeCollapseFraction = 0.46;

  void _publishHeroChromeProgress() {
    final callback = widget.callbacks.onHeroChromeProgress;
    if (callback == null) return;
    if (!_contentScroll.hasClients || _heroHeight <= 0) {
      callback(_shelves.isEmpty ? 1 : 0);
      return;
    }
    callback(
      (_contentScroll.offset / (_heroHeight * _heroChromeCollapseFraction))
          .clamp(0, 1)
          .toDouble(),
    );
  }

  void _emitFocusedTone(ConsoleGame game) {
    final callback = widget.callbacks.onSelectedTone;
    if (callback == null) return;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || _focusedGame?.id != game.id) return;
      callback(
        platformPresentationFor(
          context,
          platformId: game.platformId,
          platformName: game.platformName,
        ).tone.base,
      );
    });
  }

  Future<void> _revealShelf(int shelf) async {
    if (!_contentScroll.hasClients) return;
    final duration = context.motion.resolve(
      context,
      context.motion.contentTransition,
    );
    if (shelf == 0) {
      if (duration == Duration.zero) {
        _contentScroll.jumpTo(0);
      } else {
        await _contentScroll.animateTo(
          0,
          duration: duration,
          curve: context.motion.emphasizedCurve,
        );
      }
    } else {
      final shelfContext = _shelfKeys[shelf].currentContext;
      if (shelfContext == null) return;
      final shelfBox = shelfContext.findRenderObject();
      final viewportBox = _contentScroll.position.context.storageContext
          .findRenderObject();
      if (shelfBox is! RenderBox || viewportBox is! RenderBox) return;

      final spec = DiscoverLayoutScope.of(context);
      final shelfRect = shelfBox.localToGlobal(Offset.zero) & shelfBox.size;
      final viewportRect =
          viewportBox.localToGlobal(Offset.zero) & viewportBox.size;
      final headerSafeTop =
          viewportRect.top +
          spec.headerHeight +
          context.layout.hairlineStroke +
          spec.contentTopInset;
      final footerSafeBottom = viewportRect.bottom - context.layout.md;
      var target = _contentScroll.offset;
      if (shelfRect.top < headerSafeTop) {
        target += shelfRect.top - headerSafeTop;
      } else if (shelfRect.bottom > footerSafeBottom) {
        target += shelfRect.bottom - footerSafeBottom;
      }
      target = target.clamp(
        _contentScroll.position.minScrollExtent,
        _contentScroll.position.maxScrollExtent,
      );
      await _animateOrJumpTo(_contentScroll, target, duration);
    }
    if (!mounted) return;
    widget.callbacks.onRememberFocus(
      itemId: _focusedGame?.id,
      scrollOffset: _contentScroll.offset,
    );
  }

  void _revealFocusedCard(int shelf, int game) {
    final controller = _shelfScroll[shelf];
    if (!controller.hasClients) return;
    final spec = DiscoverLayoutScope.of(context);
    final stride = spec.featuredCardWidth + spec.featuredCardGap;
    final cardLeft = game * stride;
    final cardRight = cardLeft + spec.featuredCardWidth;
    final viewport = controller.position.viewportDimension;
    var target = controller.offset;
    if (cardLeft < target) {
      target = cardLeft;
    } else if (cardRight > target + viewport) {
      target = cardRight - viewport;
    }
    target = target.clamp(0.0, controller.position.maxScrollExtent);
    if ((target - controller.offset).abs() <= context.layout.hairlineStroke) {
      return;
    }
    unawaited(
      _animateOrJumpTo(
        controller,
        target,
        context.motion.resolve(context, context.motion.contentTransition),
      ),
    );
  }

  Future<void> _animateOrJumpTo(
    ScrollController controller,
    double offset,
    Duration duration,
  ) async {
    if (duration == Duration.zero) {
      controller.jumpTo(offset);
      return;
    }
    await controller.animateTo(
      offset,
      duration: duration,
      curve: context.motion.emphasizedCurve,
    );
  }

  bool _isInstalled(ConsoleGame game) =>
      game.defaultReleaseId != null &&
      widget.input.installedReleaseIds.contains(game.defaultReleaseId);

  void _syncSnapshot({required bool restoreScroll}) {
    _detailTimer?.cancel();
    _detailRequestGeneration++;
    _disposeCards();
    _shelves = widget.input.snapshot.shelves
        .where((shelf) => shelf.games.isNotEmpty)
        .toList(growable: false);
    _cardNodes.addAll(<List<FocusNode>>[
      for (var shelf = 0; shelf < _shelves.length; shelf++)
        List<FocusNode>.generate(
          _shelves[shelf].games.length,
          (game) => FocusNode(debugLabel: 'catalog-featured-$shelf-$game'),
        ),
    ]);
    _shelfScroll.addAll(<ScrollController>[
      for (var shelf = 0; shelf < _shelves.length; shelf++)
        ScrollController(debugLabel: 'catalog-featured-shelf-$shelf'),
    ]);
    _shelfKeys.addAll(<GlobalKey>[
      for (var shelf = 0; shelf < _shelves.length; shelf++)
        GlobalKey(debugLabel: 'catalog-featured-shelf-$shelf'),
    ]);
    _rememberedGameIndices.addAll(List<int>.filled(_shelves.length, 0));

    var focusedShelf = 0;
    var focusedGame = 0;
    var focusMatched = false;
    final focusedId = widget.input.focus.itemId;
    if (focusedId != null) {
      for (var shelf = 0; shelf < _shelves.length; shelf++) {
        final game = _shelves[shelf].games.indexWhere(
          (candidate) => candidate.id == focusedId,
        );
        if (game >= 0) {
          focusedShelf = shelf;
          focusedGame = game;
          focusMatched = true;
          break;
        }
      }
    }
    _focusedShelf = focusedShelf;
    if (_shelves.isNotEmpty) {
      _rememberedGameIndices[focusedShelf] = focusedGame;
      _publishFocus(_shelves[focusedShelf].games[focusedGame]);
    } else {
      _focusedGame = null;
      _focusedDetail = null;
    }

    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      if (restoreScroll && _contentScroll.hasClients) {
        final double restoredOffset = focusMatched
            ? widget.input.focus.scrollOffset
            : 0;
        _contentScroll.jumpTo(
          restoredOffset.clamp(0, _contentScroll.position.maxScrollExtent),
        );
      }
      _syncHeroVisibility(scheduleIfVisible: true);
      if (widget.input.active && widget.input.autofocusNavigation) {
        unawaited(
          _restoreFocusAfterViewport(shelf: focusedShelf, game: focusedGame),
        );
      }
    });
  }

  Future<void> _restoreFocusAfterViewport({
    required int shelf,
    required int game,
  }) async {
    await WidgetsBinding.instance.endOfFrame;
    if (!mounted || !widget.input.active || !widget.input.autofocusNavigation) {
      return;
    }
    if (_cardNodes.isEmpty) {
      _restoreContentFocus();
      return;
    }
    if (shelf >= _cardNodes.length || game >= _cardNodes[shelf].length) return;

    final controller = _shelfScroll[shelf];
    if (controller.hasClients) {
      final spec = DiscoverLayoutScope.of(context);
      final stride = spec.featuredCardWidth + spec.featuredCardGap;
      final cardLeft = game * stride;
      final cardRight = cardLeft + spec.featuredCardWidth;
      final viewport = controller.position.viewportDimension;
      final double target = (cardRight > viewport ? cardRight - viewport : 0)
          .clamp(0, controller.position.maxScrollExtent)
          .toDouble();
      if ((target - controller.offset).abs() > context.layout.hairlineStroke) {
        controller.jumpTo(target);
        await WidgetsBinding.instance.endOfFrame;
      }
    }

    if (mounted && widget.input.active && widget.input.autofocusNavigation) {
      _restoreContentFocus();
    }
  }

  void _onCardFocused(int shelf, int game) {
    if (shelf >= _shelves.length || game >= _shelves[shelf].games.length) {
      return;
    }
    _rememberedGameIndices[shelf] = game;
    final focused = _shelves[shelf].games[game];
    final shelfChanged = shelf != _focusedShelf;
    setState(() {
      _focusedShelf = shelf;
      _publishFocus(focused);
    });
    widget.callbacks.onRememberFocus(
      itemId: focused.id,
      scrollOffset: _contentScroll.hasClients ? _contentScroll.offset : 0,
    );
    _revealFocusedCard(shelf, game);
    if (shelfChanged) {
      unawaited(_revealShelf(shelf));
    }
  }

  void _publishFocus(ConsoleGame game) {
    _focusedGame = game;
    _focusedDetail = widget.callbacks.detailFor(game.id);
    _emitFocusedTone(game);
    _scheduleDetail(game.id);
  }

  void _scheduleDetail(String titleId) {
    _detailTimer?.cancel();
    final generation = ++_detailRequestGeneration;
    if (_focusedDetail != null ||
        !widget.input.active ||
        !_heroMeaningfullyVisible) {
      return;
    }
    _detailTimer = Timer(_detailSettleDelay, () async {
      final detail = await widget.callbacks.loadDetail(titleId);
      if (!mounted ||
          generation != _detailRequestGeneration ||
          _focusedGame?.id != titleId ||
          detail == null) {
        return;
      }
      setState(() => _focusedDetail = detail);
    });
  }

  void _move(TraversalDirection direction) {
    if (_cardNodes.isEmpty) {
      if (direction == TraversalDirection.up) {
        widget.focusHandoff.focusHeader(DiscoverSection.featured);
      }
      return;
    }
    final location = _focusedCardLocation();
    if (location == null) {
      _restoreContentFocus();
      return;
    }
    final (shelf, game) = location;
    switch (direction) {
      case TraversalDirection.left:
        if (game > 0) _cardNodes[shelf][game - 1].requestFocus();
      case TraversalDirection.right:
        if (game < _cardNodes[shelf].length - 1) {
          _cardNodes[shelf][game + 1].requestFocus();
        }
      case TraversalDirection.up:
        if (shelf == 0) {
          widget.focusHandoff.focusHeader(DiscoverSection.featured);
        } else {
          _focusShelf(shelf - 1);
        }
      case TraversalDirection.down:
        if (shelf < _cardNodes.length - 1) _focusShelf(shelf + 1);
    }
  }

  (int, int)? _focusedCardLocation() {
    for (var shelf = 0; shelf < _cardNodes.length; shelf++) {
      final game = _cardNodes[shelf].indexWhere((node) => node.hasFocus);
      if (game >= 0) return (shelf, game);
    }
    return null;
  }

  void _focusShelf(int shelf) {
    final safeShelf = shelf.clamp(0, _cardNodes.length - 1);
    final game = _rememberedGameIndices[safeShelf].clamp(
      0,
      _cardNodes[safeShelf].length - 1,
    );
    _cardNodes[safeShelf][game].requestFocus();
  }

  void _restoreContentFocus() {
    if (_cardNodes.isEmpty) {
      _statusActionNode.requestFocus();
      return;
    }
    _focusShelf(_focusedShelf);
  }

  void _disposeCards() {
    for (final shelf in _cardNodes) {
      for (final node in shelf) {
        node.dispose();
      }
    }
    for (final controller in _shelfScroll) {
      controller.dispose();
    }
    _cardNodes.clear();
    _shelfScroll.clear();
    _shelfKeys.clear();
    _rememberedGameIndices.clear();
  }
}

enum _FeaturedSurfaceState { offline, loading, failed, empty, ready }

final class _FeaturedCinematicBackdrop extends StatelessWidget {
  const _FeaturedCinematicBackdrop({
    required this.game,
    required this.detail,
    required this.scrollController,
    required this.heroHeight,
    required this.fadeDistanceFraction,
    required this.imageProvider,
    super.key,
  });

  final ConsoleGame game;
  final ConsoleGameDetail? detail;
  final ScrollController scrollController;
  final double heroHeight;
  final double fadeDistanceFraction;
  final ImageProvider<Object> Function(Uri uri)? imageProvider;

  static const double _parallaxFraction = 0.14;

  @override
  Widget build(BuildContext context) {
    final platform = platformPresentationFor(
      context,
      platformId: game.platformId,
      platformName: game.platformName,
    );
    final mediaUrl = _preferredCinematicMedia(detail);
    return IgnorePointer(
      child: AnimatedBuilder(
        animation: scrollController,
        builder: (context, child) {
          final offset = scrollController.hasClients
              ? scrollController.offset
              : 0;
          final progress = (offset / (heroHeight * fadeDistanceFraction)).clamp(
            0.0,
            1.0,
          );
          return Opacity(
            opacity: 1 - progress,
            child: Transform.translate(
              offset: Offset(0, -offset * _parallaxFraction),
              child: child,
            ),
          );
        },
        child: ShaderMask(
          key: ValueKey<String>(
            'featured-cinematic-media-${mediaUrl ?? game.id}',
          ),
          shaderCallback: (rect) => LinearGradient(
            begin: Alignment.topCenter,
            end: Alignment.bottomCenter,
            colors: <Color>[
              context.artwork.highlight,
              context.artwork.highlight,
              Colors.transparent,
            ],
            stops: const <double>[0, 0.62, 1],
          ).createShader(rect),
          blendMode: BlendMode.dstIn,
          child: Stack(
            fit: StackFit.expand,
            children: <Widget>[
              AnimatedSwitcher(
                duration: context.motion.resolve(
                  context,
                  context.motion.contentTransition,
                ),
                switchInCurve: context.motion.emphasizedCurve,
                switchOutCurve: context.motion.standardCurve,
                child: _CinematicMedia(
                  key: ValueKey<String>(
                    'featured-cinematic-image-${mediaUrl ?? game.id}',
                  ),
                  game: game,
                  mediaUrl: mediaUrl,
                  tone: platform.tone,
                  imageProvider: imageProvider,
                ),
              ),
              DecoratedBox(
                decoration: BoxDecoration(
                  gradient: LinearGradient(
                    colors: <Color>[
                      context.consoleColors.mediaBackdrop,
                      context.consoleColors.mediaBackdrop,
                      context.consoleColors.scrim,
                      Colors.transparent,
                    ],
                    stops: const <double>[0, 0.18, 0.44, 0.72],
                  ),
                ),
              ),
              DecoratedBox(
                decoration: BoxDecoration(
                  gradient: LinearGradient(
                    begin: Alignment.topCenter,
                    end: Alignment.bottomCenter,
                    colors: <Color>[
                      context.consoleColors.mediaBackdrop,
                      context.consoleColors.scrim,
                      Colors.transparent,
                    ],
                    stops: const <double>[0, 0.16, 0.34],
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

final class _FeaturedHeroContent extends StatelessWidget {
  const _FeaturedHeroContent({
    required this.game,
    required this.detail,
    required this.installed,
    required this.headerSafeInset,
    required this.imageProvider,
    super.key,
  });

  final ConsoleGame game;
  final ConsoleGameDetail? detail;
  final bool installed;
  final double headerSafeInset;
  final ImageProvider<Object> Function(Uri uri)? imageProvider;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    final platform = platformPresentationFor(
      context,
      platformId: game.platformId,
      platformName: game.platformName,
    );
    final largeText =
        MediaQuery.textScalerOf(context).scale(1) >=
        DiscoverLayoutScope.of(context).largeTextScaleBreakpoint;
    final deck = _deck;
    final verticalInset = largeText ? layout.xs : layout.md;
    final bottomInset = largeText ? layout.lg : layout.xl;

    return Stack(
      fit: StackFit.expand,
      children: <Widget>[
        Padding(
          padding: EdgeInsets.fromLTRB(
            DiscoverLayoutScope.of(context).frameMargin,
            headerSafeInset + verticalInset,
            DiscoverLayoutScope.of(context).frameMargin,
            bottomInset,
          ),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: <Widget>[
              Expanded(
                flex: 5,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: <Widget>[
                    Text(
                      game.title.toUpperCase(),
                      key: const ValueKey<String>('featured-spotlight-title'),
                      maxLines: largeText ? 2 : 1,
                      overflow: TextOverflow.ellipsis,
                      style:
                          (largeText
                                  ? context.text.pageHeading
                                  : context.text.spotlightTitle)
                              .copyWith(color: colors.onMediaOverlay),
                    ),
                    if (!largeText && deck != null) ...[
                      SizedBox(height: layout.sm),
                      Text(
                        deck,
                        key: const ValueKey<String>(
                          'featured-spotlight-description',
                        ),
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: context.text.body.copyWith(
                          color: colors.onMediaOverlay,
                        ),
                      ),
                    ],
                    SizedBox(height: largeText ? layout.xs : layout.lg),
                    if (largeText) ...[
                      Text(
                        _compactMetadata,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: context.text.metadata.copyWith(
                          color: colors.onMediaOverlay,
                        ),
                      ),
                      if (installed) ...[
                        SizedBox(height: layout.xs),
                        _readyBadge(context),
                      ],
                    ] else
                      Wrap(
                        spacing: layout.sm,
                        runSpacing: layout.xs,
                        crossAxisAlignment: WrapCrossAlignment.center,
                        children: <Widget>[
                          _SpotlightChip(label: platform.shortCode),
                          if (game.releaseYear case final year?)
                            _SpotlightChip(label: '$year'),
                          if (detail?.players case final players?)
                            _SpotlightChip(
                              label:
                                  '$players ${players == 1 ? 'PLAYER' : 'PLAYERS'}',
                            ),
                          if (installed) _readyBadge(context),
                        ],
                      ),
                  ],
                ),
              ),
              if (!largeText && game.coverUrl != null) ...[
                const Spacer(flex: 2),
                Flexible(
                  flex: 2,
                  child: Center(
                    child: DecoratedBox(
                      key: const ValueKey<String>(
                        'featured-spotlight-cover-surround',
                      ),
                      decoration: BoxDecoration(
                        borderRadius: BorderRadius.circular(
                          layout.controlRadius + layout.xxs,
                        ),
                        color: colors.mediaChipSurface,
                        border: Border.all(
                          color: colors.borderStrong,
                          width: layout.hairlineStroke,
                        ),
                        boxShadow: context.elevation.hero,
                      ),
                      child: Padding(
                        padding: EdgeInsets.all(
                          layout.xxs + (layout.hairlineStroke * 2),
                        ),
                        child: DecoratedBox(
                          key: const ValueKey<String>(
                            'featured-spotlight-cover-frame',
                          ),
                          position: DecorationPosition.foreground,
                          decoration: BoxDecoration(
                            borderRadius: BorderRadius.circular(
                              layout.controlRadius,
                            ),
                            border: Border.all(
                              color: colors.onMediaAccent,
                              width: layout.hairlineStroke,
                            ),
                          ),
                          child: AspectRatio(
                            aspectRatio: kCoverAspectRatio,
                            child: CoverArt(
                              coverUrl: game.poster?.url ?? game.coverUrl,
                              contain: game.poster?.contain ?? true,
                              platformId: game.platformId,
                              platformName: game.platformName,
                              title: game.title,
                              borderRadius:
                                  layout.controlRadius -
                                  (layout.hairlineStroke * 2),
                              imageProvider: imageProvider?.call(
                                game.coverUrl!,
                              ),
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }

  String? get _deck {
    final description = detail?.description;
    if (description == null) return null;
    final normalized = description.replaceAll(RegExp(r'\s+'), ' ').trim();
    if (normalized.isEmpty) return null;
    final sentenceEnd = normalized.indexOf(RegExp(r'[.!?]'));
    return sentenceEnd >= 0
        ? normalized.substring(0, sentenceEnd + 1)
        : normalized;
  }

  String get _compactMetadata => <String>[
    game.platformName.toUpperCase(),
    if (game.releaseYear case final year?) '$year',
    if (detail?.players case final players?)
      '$players ${players == 1 ? 'PLAYER' : 'PLAYERS'}',
  ].join('  ·  ');

  Widget _readyBadge(BuildContext context) => Text(
    'READY',
    key: const ValueKey<String>('featured-spotlight-readiness'),
    maxLines: 1,
    overflow: TextOverflow.ellipsis,
    style: context.text.metadataStrong.copyWith(
      color: context.consoleColors.onMediaAccent,
    ),
  );
}

Uri? _preferredCinematicMedia(ConsoleGameDetail? detail) {
  final media = detail?.media ?? const <ConsoleMediaRef>[];
  if (media.isEmpty) return null;
  const preferredTypes = <String>['background', 'banner', 'screenshot'];
  for (final type in preferredTypes) {
    final matching = media
        .where((item) => item.type.toLowerCase().contains(type))
        .toList(growable: false);
    if (matching.isNotEmpty) {
      return matching
          .firstWhere((item) => item.isPrimary, orElse: () => matching.first)
          .url;
    }
  }
  return null;
}

final class _CinematicMedia extends StatelessWidget {
  const _CinematicMedia({
    required this.game,
    required this.mediaUrl,
    required this.tone,
    required this.imageProvider,
    super.key,
  });

  final ConsoleGame game;
  final Uri? mediaUrl;
  final CoverTone tone;
  final ImageProvider<Object> Function(Uri uri)? imageProvider;

  @override
  Widget build(BuildContext context) {
    final fallback = CoverPlate(tone: tone, title: game.title);
    if (mediaUrl == null) return fallback;
    return LayoutBuilder(
      builder: (context, constraints) => Stack(
        fit: StackFit.expand,
        children: <Widget>[
          fallback,
          Positioned(
            top: 0,
            bottom: 0,
            left: constraints.maxWidth * 0.14,
            right: constraints.maxWidth * -0.04,
            child: Image(
              image: ResizeImage.resizeIfNeeded(
                decodeWidthFor(context, constraints),
                null,
                imageProvider?.call(mediaUrl!) ??
                    NetworkImage(mediaUrl!.toString()),
              ),
              fit: BoxFit.cover,
              alignment: Alignment.centerRight,
              frameBuilder: coverFadeIn,
              errorBuilder: (_, _, _) => const SizedBox.shrink(),
            ),
          ),
        ],
      ),
    );
  }
}

final class _SpotlightChip extends StatelessWidget {
  const _SpotlightChip({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      color: context.consoleColors.mediaChipSurface,
      border: Border.all(color: context.consoleColors.onMediaAccent),
      borderRadius: BorderRadius.circular(context.layout.chipRadius),
    ),
    child: Padding(
      padding: EdgeInsets.symmetric(
        horizontal: context.layout.sm,
        vertical: context.layout.xs / 2,
      ),
      child: Text(
        label,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: context.text.chipLabel.copyWith(
          color: context.consoleColors.onMediaOverlay,
        ),
      ),
    ),
  );
}

final class _FeaturedShelfView extends StatelessWidget {
  const _FeaturedShelfView({
    required this.shelf,
    required this.shelfIndex,
    required this.cardNodes,
    required this.scrollController,
    required this.active,
    required this.cardWidth,
    required this.cardGap,
    required this.installedReleaseIds,
    required this.imageProvider,
    required this.onFocused,
    required this.onOpenGame,
    super.key,
  });

  final DiscoverFeaturedShelf shelf;
  final int shelfIndex;
  final List<FocusNode> cardNodes;
  final ScrollController scrollController;
  final bool active;
  final double cardWidth;
  final double cardGap;
  final Set<String> installedReleaseIds;
  final ImageProvider<Object> Function(Uri uri)? imageProvider;
  final void Function(int shelf, int game) onFocused;
  final void Function(ConsoleGame game, String heroTag) onOpenGame;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    final cardHeight = cardWidth / kCoverAspectRatio;
    final topRated =
        shelf.eyebrow.trim().toUpperCase() == 'FEATURED' &&
        shelf.title.trim().toLowerCase() == 'top rated';
    return Padding(
      padding: EdgeInsets.only(top: layout.lg, bottom: layout.md),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: <Widget>[
          Row(
            children: <Widget>[
              Text(
                topRated ? 'TOP RATED' : shelf.eyebrow.toUpperCase(),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: context.text.eyebrow.copyWith(
                  color: colors.catalogAccent,
                ),
              ),
              if (!topRated) ...[
                SizedBox(width: layout.sm),
                Flexible(
                  child: Text(
                    shelf.title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: context.text.supportingTitle.copyWith(
                      color: colors.textStrong,
                    ),
                  ),
                ),
              ],
              SizedBox(width: layout.sm),
              Text(
                '· ${shelf.games.length}',
                style: context.text.metadata.copyWith(color: colors.textFaint),
              ),
            ],
          ),
          SizedBox(height: layout.xs),
          SizedBox(
            height: cardHeight,
            child: ListView.separated(
              key: ValueKey<String>('featured-shelf-list-${shelf.id}'),
              controller: scrollController,
              scrollDirection: Axis.horizontal,
              clipBehavior: Clip.none,
              itemCount: shelf.games.length,
              separatorBuilder: (_, _) => SizedBox(width: cardGap),
              itemBuilder: (context, index) {
                final game = shelf.games[index];
                final heroTag = 'featured-$shelfIndex-$index';
                final installed = _installed(game);
                return SizedBox(
                  width: cardWidth,
                  child: ConsoleFocusable(
                    key: ValueKey<String>('featured-card-${game.id}'),
                    focusNode: cardNodes[index],
                    onFocusChange: (focused) {
                      if (focused) onFocused(shelfIndex, index);
                    },
                    onPressed: () => onOpenGame(game, heroTag),
                    semanticLabel: <String>[
                      game.title,
                      game.platformName,
                      if (installed) 'ready to play',
                    ].join(', '),
                    builder: (context, focused) => AnimatedOpacity(
                      duration: context.motion.resolve(
                        context,
                        context.motion.selection,
                      ),
                      opacity: focused
                          ? 1
                          : active
                          ? context.motion.interaction.rowActiveOpacity
                          : context.motion.interaction.rowInactiveOpacity,
                      child: AnimatedScale(
                        duration: context.motion.resolve(
                          context,
                          context.motion.focus,
                        ),
                        curve: context.motion.spatialCurve,
                        scale: focused
                            ? context.motion.interaction.focusedScale
                            : context.motion.interaction.restScale,
                        child: ConsoleFocusRing(
                          focused: focused,
                          borderRadius: layout.controlRadius,
                          ringWidth: layout.focusStroke,
                          gap: layout.hairlineStroke,
                          child: _FeaturedCardBody(
                            game: game,
                            heroTag: heroTag,
                            installed: installed,
                            focused: focused,
                            imageProvider: imageProvider,
                          ),
                        ),
                      ),
                    ),
                  ),
                );
              },
            ),
          ),
        ],
      ),
    );
  }

  bool _installed(ConsoleGame game) =>
      game.defaultReleaseId != null &&
      installedReleaseIds.contains(game.defaultReleaseId);
}

final class _FeaturedCardBody extends StatelessWidget {
  const _FeaturedCardBody({
    required this.game,
    required this.heroTag,
    required this.installed,
    required this.focused,
    required this.imageProvider,
  });

  final ConsoleGame game;
  final String heroTag;
  final bool installed;
  final bool focused;
  final ImageProvider<Object> Function(Uri uri)? imageProvider;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return DecoratedBox(
      decoration: BoxDecoration(
        color: colors.mediaChipSurface,
        borderRadius: BorderRadius.circular(layout.controlRadius),
        border: Border.all(
          color: colors.hairline,
          width: layout.hairlineStroke,
        ),
      ),
      child: ClipRRect(
        borderRadius: BorderRadius.circular(layout.controlRadius),
        child: Stack(
          fit: StackFit.expand,
          children: <Widget>[
            Hero(
              tag: heroTag,
              child: CoverArt(
                coverUrl: game.poster?.url ?? game.coverUrl,
                contain: game.poster?.contain ?? true,
                platformId: game.platformId,
                platformName: game.platformName,
                title: game.title,
                borderRadius: 0,
                imageProvider: game.coverUrl == null
                    ? null
                    : imageProvider?.call(game.coverUrl!),
              ),
            ),
            if (focused)
              Align(
                alignment: Alignment.bottomCenter,
                child: DecoratedBox(
                  decoration: BoxDecoration(
                    gradient: LinearGradient(
                      begin: Alignment.topCenter,
                      end: Alignment.bottomCenter,
                      colors: <Color>[
                        Colors.transparent,
                        colors.mediaChipSurface,
                      ],
                    ),
                  ),
                  child: Padding(
                    padding: EdgeInsets.fromLTRB(
                      layout.sm,
                      layout.lg,
                      layout.sm,
                      layout.sm,
                    ),
                    child: Row(
                      crossAxisAlignment: CrossAxisAlignment.end,
                      children: <Widget>[
                        Expanded(
                          child: Text(
                            game.title,
                            key: ValueKey<String>(
                              'featured-card-title-${game.id}',
                            ),
                            maxLines: 2,
                            overflow: TextOverflow.ellipsis,
                            style: context.text.metadataStrong.copyWith(
                              color: colors.onMediaOverlay,
                            ),
                          ),
                        ),
                        if (installed) ...[
                          SizedBox(width: layout.xs),
                          Icon(
                            Icons.download_done_rounded,
                            key: ValueKey<String>(
                              'featured-card-readiness-${game.id}',
                            ),
                            size: layout.iconSm,
                            color: colors.onMediaAccent,
                          ),
                        ],
                      ],
                    ),
                  ),
                ),
              ),
            if (installed && !focused)
              Positioned(
                right: layout.xs,
                bottom: layout.xs,
                child: DecoratedBox(
                  decoration: BoxDecoration(
                    color: colors.mediaChipSurface,
                    borderRadius: BorderRadius.circular(layout.chipRadius),
                    border: Border.all(
                      color: colors.onMediaAccent,
                      width: layout.hairlineStroke,
                    ),
                  ),
                  child: Padding(
                    padding: EdgeInsets.all(layout.xs),
                    child: Icon(
                      Icons.download_done_rounded,
                      key: ValueKey<String>(
                        'featured-card-readiness-${game.id}',
                      ),
                      size: layout.iconSm,
                      color: colors.onMediaAccent,
                    ),
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}
