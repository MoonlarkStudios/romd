import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/input/console_intents.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/catalog/discover_shell.dart';
import 'package:romd_console/src/presentation/launcher/launcher_destination.dart';
import 'package:romd_console/src/presentation/launcher/launcher_primitives.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';
import 'package:romd_console/src/presentation/widgets/console_focusable.dart';
import 'package:romd_console/src/presentation/widgets/game_browser_inspector.dart';
import 'package:romd_console/src/presentation/widgets/game_tile.dart';
import 'package:romd_console/src/presentation/widgets/platform_logo_resolver.dart';
import 'package:romd_console/src/presentation/widgets/platform_presentation.dart';

@immutable
final class DiscoverAllGamesInput {
  const DiscoverAllGamesInput({
    required this.snapshot,
    required this.focus,
    required this.sort,
    required this.systems,
    required this.installedReleaseIds,
    required this.operationAvailable,
    required this.active,
    required this.autofocusNavigation,
    this.platformId,
  });

  final DiscoverGamePageSnapshot snapshot;
  final DiscoverFocusSnapshot focus;
  final DiscoverCatalogSort sort;
  final DiscoverSystemsSnapshot systems;
  final Set<String> installedReleaseIds;
  final bool operationAvailable;
  final bool active;
  final bool autofocusNavigation;
  final String? platformId;
}

@immutable
final class DiscoverAllGamesCallbacks {
  const DiscoverAllGamesCallbacks({
    required this.navigation,
    required this.onOpenGame,
    required this.onSelectSort,
    required this.onSelectPlatform,
    required this.onClearFilters,
    required this.onLoadMore,
    required this.onRetry,
    required this.onRetrySystems,
    required this.onRememberFocus,
    required this.detailFor,
    required this.loadDetail,
  });

  final DiscoverFeatureNavigation navigation;
  final void Function(ConsoleGame game, String heroTag) onOpenGame;
  final ValueChanged<DiscoverCatalogSort> onSelectSort;

  /// A null platform clears the System filter.
  final ValueChanged<String?> onSelectPlatform;
  final VoidCallback onClearFilters;
  final VoidCallback onLoadMore;
  final VoidCallback onRetry;
  final VoidCallback onRetrySystems;
  final void Function({required String? itemId, required double scrollOffset})
  onRememberFocus;
  final ConsoleGameDetail? Function(String titleId) detailFor;
  final Future<ConsoleGameDetail?> Function(String titleId) loadDetail;
}

/// Four-column catalog grid, toolbar, contextual inspector, and paging owner.
final class DiscoverAllGamesBoundary extends StatefulWidget {
  const DiscoverAllGamesBoundary({
    required this.input,
    required this.callbacks,
    required this.focusHandoff,
    super.key,
  });

  final DiscoverAllGamesInput input;
  final DiscoverAllGamesCallbacks callbacks;
  final DiscoverFocusHandoff focusHandoff;

  @override
  State<DiscoverAllGamesBoundary> createState() =>
      DiscoverAllGamesBoundaryState();
}

final class DiscoverAllGamesBoundaryState
    extends State<DiscoverAllGamesBoundary> {
  static const int _primaryColumns = 4;
  static const Duration _detailSettleDelay = Duration(milliseconds: 180);

  final FocusNode _filterTriggerNode = FocusNode(
    debugLabel: 'all-games-filter-trigger',
  );
  final List<FocusNode> _filterMenuNodes = <FocusNode>[
    FocusNode(debugLabel: 'all-games-filter-sort'),
    FocusNode(debugLabel: 'all-games-filter-reset'),
  ];
  final Map<String, FocusNode> _filterOptionNodes = <String, FocusNode>{};
  final Map<String, FocusNode> _systemNodes = <String, FocusNode>{};
  final FocusNode _statusActionNode = FocusNode(
    debugLabel: 'all-games-status-action',
  );
  final FocusNode _systemRetryNode = FocusNode(
    debugLabel: 'browse-system-retry',
  );
  final FocusNode _pagingActionNode = FocusNode(
    debugLabel: 'all-games-paging-action',
  );
  final ScrollController _gridScroll = ScrollController(
    debugLabel: 'all-games-grid',
  );
  final ScrollController _systemScroll = ScrollController(
    debugLabel: 'browse-system-filter',
  );
  final Map<String, ConsolePlatform> _knownPlatforms =
      <String, ConsolePlatform>{};

  DiscoverOperationKey? _knownPlatformOperation;
  List<ConsoleGame> _games = const <ConsoleGame>[];
  List<String> _gameIds = const <String>[];
  List<FocusNode> _gameNodes = <FocusNode>[];
  int _focusedGame = 0;
  int _columns = _primaryColumns;
  double _gridTileHeight = 0;
  double _gridRunGap = 0;
  String? _requestedCursor;
  Timer? _detailTimer;
  ConsoleGameDetail? _focusedDetail;
  int _detailRequestGeneration = 0;
  bool _pendingScrollRestore = true;
  bool _filterMenuOpen = false;
  _AllGamesFilterMenuPage _filterMenuPage = _AllGamesFilterMenuPage.root;
  DiscoverCatalogSort _menuSort = DiscoverCatalogSort.title;
  String? _menuPlatformId;
  FocusNode? _filterMenuReturnNode;
  String? _filterMenuReturnGameId;

  @override
  void initState() {
    super.initState();
    widget.focusHandoff.attachContent(
      DiscoverSection.allGames,
      _restoreContentFocus,
    );
    _syncGames();
    _rememberPlatforms();
    if (widget.input.snapshot.pagingFailed) {
      _requestedCursor = widget.input.snapshot.nextCursor;
    }
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      _restoreRememberedScroll();
      if (_games.isNotEmpty) _settleFocusedDetail();
    });
  }

  @override
  void didUpdateWidget(covariant DiscoverAllGamesBoundary oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!identical(
      oldWidget.input.snapshot.items,
      widget.input.snapshot.items,
    )) {
      _syncGames();
      _rememberPlatforms();
    }
    if (!identical(oldWidget.input.systems.items, widget.input.systems.items)) {
      _rememberPlatforms();
    }
    if (oldWidget.input.snapshot.requestKey?.operation !=
        widget.input.snapshot.requestKey?.operation) {
      _rememberPlatforms();
    }
    if (oldWidget.input.focus.scrollOffset != widget.input.focus.scrollOffset) {
      _pendingScrollRestore = true;
      _scheduleScrollRestore();
    }
    if (oldWidget.input.snapshot.nextCursor !=
        widget.input.snapshot.nextCursor) {
      _requestedCursor = null;
    }
    if (!identical(oldWidget.focusHandoff, widget.focusHandoff)) {
      oldWidget.focusHandoff.detachContent(DiscoverSection.allGames);
      widget.focusHandoff.attachContent(
        DiscoverSection.allGames,
        _restoreContentFocus,
      );
    }
    if (oldWidget.input.active && !widget.input.active && _filterMenuOpen) {
      _filterMenuOpen = false;
      _filterMenuPage = _AllGamesFilterMenuPage.root;
      _filterMenuReturnNode = null;
      _filterMenuReturnGameId = null;
    }
    if (_games.isNotEmpty) {
      final cached = widget.callbacks.detailFor(_games[_focusedGame].id);
      if (!identical(cached, _focusedDetail)) _focusedDetail = cached;
    }
  }

  @override
  void dispose() {
    _detailTimer?.cancel();
    widget.focusHandoff.detachContent(DiscoverSection.allGames);
    _filterTriggerNode.dispose();
    for (final node in _filterMenuNodes) {
      node.dispose();
    }
    for (final node in _filterOptionNodes.values) {
      node.dispose();
    }
    for (final node in _systemNodes.values) {
      node.dispose();
    }
    for (final node in _gameNodes) {
      node.dispose();
    }
    _statusActionNode.dispose();
    _systemRetryNode.dispose();
    _pagingActionNode.dispose();
    _gridScroll.dispose();
    _systemScroll.dispose();
    super.dispose();
  }

  void focusFilters() => _focusFilters();

  @override
  Widget build(BuildContext context) => Shortcuts(
    shortcuts: const <ShortcutActivator, Intent>{
      SingleActivator(LogicalKeyboardKey.keyF): ShowDetailsIntent(),
      SingleActivator(LogicalKeyboardKey.keyY): ShowDetailsIntent(),
    },
    child: Actions(
      actions: <Type, Action<Intent>>{
        ...launcherDirectionalActions(_move),
        ShowDetailsIntent: CallbackAction<ShowDetailsIntent>(
          onInvoke: (_) {
            _focusFilters();
            return null;
          },
        ),
      },
      child: Padding(
        padding: EdgeInsets.symmetric(
          horizontal: DiscoverLayoutScope.of(context).frameMargin,
        ),
        child: Stack(
          fit: StackFit.expand,
          children: <Widget>[
            ExcludeFocus(
              key: const ValueKey<String>('all-games-content-focus-boundary'),
              excluding: _filterMenuOpen,
              child: IgnorePointer(
                ignoring: _filterMenuOpen,
                child: _body(context),
              ),
            ),
            if (_filterMenuOpen) _filterMenu(context),
          ],
        ),
      ),
    ),
  );

  Widget _body(BuildContext context) {
    if (!widget.input.operationAvailable) {
      return LauncherStatusPanel(
        icon: Icons.cloud_off_outlined,
        title: 'Catalog is offline',
        message: 'Choose and sign in to a ROMD server to browse its Catalog.',
        action: FilledButton.icon(
          focusNode: _statusActionNode,
          onPressed: widget.callbacks.navigation.onConnect,
          icon: const Icon(Icons.link),
          label: const Text('Choose server'),
        ),
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: <Widget>[
        _systemRail(context),
        SizedBox(height: context.layout.sm),
        Expanded(
          child: switch (widget.input.snapshot.state) {
            DiscoverLoadState.idle ||
            DiscoverLoadState.loading => const LauncherStatusPanel(
              icon: Icons.cloud_sync_outlined,
              title: 'Opening Browse',
              message: 'Reading the selected ROMD Catalog.',
            ),
            DiscoverLoadState.failed => LauncherStatusPanel(
              icon: Icons.cloud_off_outlined,
              title: 'Catalog unavailable',
              message:
                  'The selected ROMD server could not provide this view. '
                  'Your local games are unaffected.',
              action: FilledButton.icon(
                focusNode: _statusActionNode,
                onPressed: widget.callbacks.onRetry,
                icon: const Icon(Icons.refresh),
                label: const Text('Retry'),
              ),
            ),
            DiscoverLoadState.ready => _readyBody(context),
          },
        ),
      ],
    );
  }

  Widget _systemRail(BuildContext context) {
    final systems = _sortedPlatforms;
    final options = <ConsolePlatform?>[null, ...systems];
    final state = widget.input.systems.state;
    final textScale = MediaQuery.textScalerOf(context).scale(1);
    final largeText =
        textScale >= DiscoverLayoutScope.of(context).largeTextScaleBreakpoint;
    return SizedBox(
      key: const ValueKey<String>('browse-system-rail'),
      height: largeText ? 78 : 60,
      child: Row(
        children: <Widget>[
          Expanded(
            child: ListView.separated(
              controller: _systemScroll,
              scrollDirection: Axis.horizontal,
              itemCount: options.length,
              separatorBuilder: (_, _) => SizedBox(width: context.layout.sm),
              itemBuilder: (context, index) =>
                  _systemControl(context, options[index], largeText: largeText),
            ),
          ),
          if (state == DiscoverLoadState.loading ||
              state == DiscoverLoadState.idle) ...<Widget>[
            SizedBox(width: context.layout.sm),
            const SizedBox.square(
              dimension: 18,
              child: CircularProgressIndicator(strokeWidth: 2),
            ),
          ] else if (state == DiscoverLoadState.failed) ...<Widget>[
            SizedBox(width: context.layout.sm),
            ConsoleFocusable(
              key: const ValueKey<String>('browse-system-retry'),
              focusNode: _systemRetryNode,
              onPressed: widget.callbacks.onRetrySystems,
              semanticLabel: 'Retry loading systems',
              builder: (context, focused) => ConsoleFocusRing(
                focused: focused,
                borderRadius: context.layout.controlRadius,
                restBorderColor: context.consoleColors.panelBorder,
                gap: 0,
                child: SizedBox.square(
                  dimension: context.layout.surface.iconActionSize,
                  child: Icon(
                    Icons.refresh_rounded,
                    color: focused
                        ? context.consoleColors.textStrong
                        : context.consoleColors.textMuted,
                  ),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }

  Widget _systemControl(
    BuildContext context,
    ConsolePlatform? platform, {
    required bool largeText,
  }) {
    final id = platform?.id;
    final key = id ?? 'all';
    final selected = id == widget.input.platformId;
    final label = platform?.name ?? 'All Systems';
    final count = platform == null
        ? 'ALL TITLES'
        : '${platform.titleCount} ${platform.titleCount == 1 ? 'TITLE' : 'TITLES'}';
    final width = platform == null
        ? (largeText ? 252.0 : 196.0)
        : (largeText ? 164.0 : 132.0);
    return Tooltip(
      message: label,
      child: SizedBox(
        width: width,
        child: ConsoleFocusable(
          key: ValueKey<String>('browse-system-$key'),
          focusNode: _systemNode(key),
          autofocus:
              widget.input.active &&
              widget.input.autofocusNavigation &&
              selected,
          selected: selected,
          scrollIntoViewOnFocus: true,
          onPressed: () {
            if (!selected) widget.callbacks.onSelectPlatform(id);
          },
          semanticLabel: '$label, $count',
          builder: (context, focused) => ConsoleFocusRing(
            focused: focused,
            borderRadius: context.layout.controlRadius,
            restBorderColor: selected
                ? context.consoleColors.selectionBorder
                : Colors.transparent,
            gap: 0,
            child: AnimatedContainer(
              duration: context.motion.resolve(context, context.motion.focus),
              curve: context.motion.standardCurve,
              padding: EdgeInsets.symmetric(
                horizontal: context.layout.sm,
                vertical: context.layout.xs,
              ),
              decoration: BoxDecoration(
                color: focused
                    ? context.consoleColors.focusFill
                    : selected
                    ? context.consoleColors.selectionFill
                    : Colors.transparent,
                borderRadius: BorderRadius.circular(
                  context.layout.controlRadius,
                ),
              ),
              child: platform == null
                  ? _allSystemsControl(context, focused, selected)
                  : _platformLogoControl(
                      context,
                      platform,
                      focused: focused,
                      selected: selected,
                    ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _allSystemsControl(
    BuildContext context,
    bool focused,
    bool selected,
  ) => Row(
    mainAxisAlignment: MainAxisAlignment.center,
    children: <Widget>[
      Icon(
        Icons.apps_rounded,
        color: focused || selected
            ? context.consoleColors.textStrong
            : context.consoleColors.catalogAccent,
      ),
      SizedBox(width: context.layout.xs),
      Flexible(
        child: FittedBox(
          fit: BoxFit.scaleDown,
          alignment: Alignment.centerLeft,
          child: Text(
            'All Systems',
            maxLines: 1,
            style: context.text.action.copyWith(
              color: focused || selected
                  ? context.consoleColors.textStrong
                  : context.consoleColors.textMuted,
            ),
          ),
        ),
      ),
    ],
  );

  Widget _platformLogoControl(
    BuildContext context,
    ConsolePlatform platform, {
    required bool focused,
    required bool selected,
  }) {
    final presentation = platformPresentationFor(
      context,
      platformId: platform.shortName,
      platformName: platform.name,
    );
    final logo = PlatformLogoResolverScope.of(context).resolve(
      platformId: platform.shortName,
      platformName: platform.name,
      shortCode: presentation.shortCode,
    );
    if (logo != null) {
      return Center(
        child: Image(
          image: logo.image,
          key: ValueKey<String>(
            'browse-system-logo-${platform.shortName.trim().toLowerCase()}',
          ),
          fit: BoxFit.contain,
          filterQuality: FilterQuality.high,
          color: logo.tintable
              ? focused || selected
                    ? presentation.tone.mono
                    : context.consoleColors.textMuted
              : null,
          colorBlendMode: logo.tintable ? BlendMode.srcIn : null,
        ),
      );
    }
    return Center(
      child: FittedBox(
        fit: BoxFit.scaleDown,
        child: Text(
          presentation.shortCode,
          style: context.text.sectionHeading.copyWith(
            color: focused || selected
                ? presentation.tone.mono
                : context.consoleColors.textMuted,
          ),
        ),
      ),
    );
  }

  Widget _readyBody(BuildContext context) {
    final metrics = DiscoverLayoutScope.of(context);
    final textScaler = MediaQuery.textScalerOf(context);
    final bodyScale = context.text.body.fontSize ?? 16;
    final largeText =
        textScaler.scale(bodyScale) >=
        bodyScale * metrics.largeTextScaleBreakpoint;
    if (_pendingScrollRestore) _scheduleScrollRestore();
    return LayoutBuilder(
      builder: (context, constraints) {
        final compact =
            constraints.maxWidth < metrics.compactBreakpoint || largeText;
        final controlBar = _controlBar(context);
        final body = compact
            ? Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: <Widget>[
                  controlBar,
                  SizedBox(height: context.layout.sm),
                  if (_games.isEmpty)
                    Expanded(child: _emptyResults(context))
                  else ...<Widget>[
                    GameBrowserInspector(
                      key: const ValueKey<String>('all-games-inspector'),
                      game: _games[_focusedGame],
                      detail: _focusedDetail,
                      readiness: _readinessFor(_games[_focusedGame]),
                      compact: true,
                    ),
                    SizedBox(height: context.layout.sm),
                    Expanded(child: _grid(context, constraints.maxWidth)),
                  ],
                ],
              )
            : _wideReadyBody(context, constraints.maxWidth, controlBar);
        return body;
      },
    );
  }

  Widget _wideReadyBody(
    BuildContext context,
    double availableWidth,
    Widget controlBar,
  ) {
    final metrics = DiscoverLayoutScope.of(context);
    final gridWidth =
        availableWidth -
        metrics.allGamesInspectorWidth -
        metrics.allGamesPaneGap;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: <Widget>[
        controlBar,
        SizedBox(height: context.layout.sm),
        Expanded(
          child: _games.isEmpty
              ? _emptyResults(context)
              : Row(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: <Widget>[
                    SizedBox(
                      width: gridWidth,
                      child: _grid(context, gridWidth),
                    ),
                    SizedBox(width: metrics.allGamesPaneGap),
                    SizedBox(
                      width: metrics.allGamesInspectorWidth,
                      child: GameBrowserInspector(
                        key: const ValueKey<String>('all-games-inspector'),
                        game: _games[_focusedGame],
                        detail: _focusedDetail,
                        readiness: _readinessFor(_games[_focusedGame]),
                        compact: false,
                      ),
                    ),
                  ],
                ),
        ),
      ],
    );
  }

  Widget _emptyResults(BuildContext context) {
    final filtered = widget.input.platformId != null;
    return LauncherStatusPanel(
      icon: Icons.filter_alt_off_outlined,
      title: filtered ? 'No games match these filters' : 'No games available',
      message: filtered
          ? 'Clear the active System filter to see every title.'
          : 'This ROMD Catalog has no visible games.',
      action: filtered
          ? FilledButton.icon(
              key: const ValueKey<String>('all-games-clear-filters'),
              focusNode: _statusActionNode,
              onPressed: widget.callbacks.onClearFilters,
              icon: const Icon(Icons.filter_alt_off_rounded),
              label: const Text('Clear filters'),
            )
          : null,
    );
  }

  Widget _controlBar(BuildContext context) => Row(
    children: <Widget>[
      _filterTrigger(context),
      SizedBox(width: context.layout.sm),
      Flexible(
        child: Text(
          _sortLabel(_effectiveSort).toUpperCase(),
          key: const ValueKey<String>('all-games-filter-summary'),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: context.text.metadataStrong.copyWith(
            color: context.consoleColors.textMuted,
          ),
        ),
      ),
      const Spacer(),
      Text(
        '${_games.length} ${_games.length == 1 ? 'TITLE' : 'TITLES'}',
        key: const ValueKey<String>('all-games-count'),
        style: context.text.metadataStrong.copyWith(
          color: context.consoleColors.textMuted,
        ),
      ),
    ],
  );

  Widget _filterTrigger(BuildContext context) => ConsoleFocusable(
    key: const ValueKey<String>('all-games-filter-trigger'),
    focusNode: _filterTriggerNode,
    onPressed: _openFilterMenu,
    semanticLabel: 'Sort and filter games',
    builder: (context, focused) => ConsoleFocusRing(
      focused: focused,
      borderRadius: context.layout.controlRadius,
      restBorderColor: Colors.transparent,
      gap: 0,
      child: AnimatedContainer(
        duration: context.motion.resolve(context, context.motion.selection),
        curve: context.motion.standardCurve,
        width: context.layout.surface.iconActionSize,
        height: context.layout.surface.iconActionSize,
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(context.layout.controlRadius),
          color: focused
              ? context.consoleColors.focusFill
              : context.consoleColors.controlRestFill,
          border: Border.all(
            color:
                _effectiveSort != DiscoverCatalogSort.title ||
                    _effectivePlatformId != null
                ? context.consoleColors.selectionBorder
                : context.consoleColors.borderStrong,
          ),
        ),
        child: Icon(
          Icons.sort_rounded,
          size: context.layout.lg,
          color: focused
              ? context.consoleColors.textStrong
              : context.consoleColors.textMuted,
        ),
      ),
    ),
  );

  Widget _filterMenu(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final metrics = DiscoverLayoutScope.of(context);
    return Shortcuts(
      shortcuts: const <ShortcutActivator, Intent>{
        SingleActivator(LogicalKeyboardKey.escape): DismissIntent(),
        SingleActivator(LogicalKeyboardKey.gameButtonB): DismissIntent(),
        SingleActivator(LogicalKeyboardKey.tab): DirectionalFocusIntent(
          TraversalDirection.down,
        ),
        SingleActivator(LogicalKeyboardKey.tab, shift: true):
            DirectionalFocusIntent(TraversalDirection.up),
      },
      child: Actions(
        actions: <Type, Action<Intent>>{
          ...launcherDirectionalActions(_moveFilterMenu),
          DismissIntent: CallbackAction<DismissIntent>(
            onInvoke: (_) {
              if (_filterMenuPage == _AllGamesFilterMenuPage.root) {
                _closeFilterMenu();
              } else {
                _returnToFilterRoot(row: 0);
              }
              return null;
            },
          ),
        },
        child: FocusScope(
          child: LayoutBuilder(
            builder: (context, constraints) => Stack(
              key: const ValueKey<String>('all-games-filter-menu'),
              fit: StackFit.expand,
              children: <Widget>[
                GestureDetector(
                  onTap: _closeFilterMenu,
                  child: ColoredBox(color: colors.scrim),
                ),
                Align(
                  alignment: Alignment.topLeft,
                  child: Padding(
                    padding: EdgeInsets.only(top: layout.xxl),
                    child: ConstrainedBox(
                      constraints: BoxConstraints(
                        maxWidth: metrics.allGamesInspectorWidth + layout.lg,
                        maxHeight: math.max(
                          0,
                          constraints.maxHeight - layout.xxl,
                        ),
                      ),
                      child: DecoratedBox(
                        decoration: BoxDecoration(
                          color: colors.panelSurface,
                          border: Border.all(color: colors.panelBorder),
                          borderRadius: BorderRadius.circular(
                            layout.panelRadius,
                          ),
                          boxShadow: context.elevation.panel,
                        ),
                        child: Padding(
                          padding: EdgeInsets.all(layout.lg),
                          child: _filterMenuContent(context),
                        ),
                      ),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _filterMenuContent(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final title = switch (_filterMenuPage) {
      _AllGamesFilterMenuPage.root => 'SORT & FILTER',
      _AllGamesFilterMenuPage.sort => 'SORT BY',
    };
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: <Widget>[
        Text(
          title,
          style: context.text.sectionHeading.copyWith(color: colors.textStrong),
        ),
        SizedBox(height: layout.sm),
        Divider(color: colors.hairline),
        SizedBox(height: layout.sm),
        switch (_filterMenuPage) {
          _AllGamesFilterMenuPage.root => Flexible(
            child: SingleChildScrollView(child: _filterRootMenu(context)),
          ),
          _AllGamesFilterMenuPage.sort => Flexible(
            child: SingleChildScrollView(child: _sortOptions(context)),
          ),
        },
      ],
    );
  }

  Widget _filterRootMenu(BuildContext context) => Column(
    mainAxisSize: MainAxisSize.min,
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: <Widget>[
      _filterMenuRow(
        context,
        key: 'all-games-filter-sort',
        node: _filterMenuNodes[0],
        label: 'Sort by',
        value: _sortLabel(_menuSort),
        onActivate: () => _openFilterSubpage(_AllGamesFilterMenuPage.sort),
      ),
      SizedBox(height: context.layout.md),
      _filterMenuRow(
        context,
        key: 'all-games-filter-reset',
        node: _filterMenuNodes[1],
        label: 'Reset filters',
        enabled:
            _menuSort != DiscoverCatalogSort.title || _menuPlatformId != null,
        onActivate: _resetMenuFilters,
      ),
    ],
  );

  Widget _sortOptions(BuildContext context) => Column(
    mainAxisSize: MainAxisSize.min,
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: <Widget>[
      for (final sort in DiscoverCatalogSort.values) ...<Widget>[
        _filterOptionRow(
          context,
          key: 'all-games-filter-sort-${sort.name}',
          node: _filterOptionNode('sort-${sort.name}'),
          label: _sortLabel(sort),
          selected: sort == _menuSort,
          onActivate: () => _applyMenuSort(sort),
        ),
        if (sort != DiscoverCatalogSort.values.last)
          SizedBox(height: context.layout.xs),
      ],
    ],
  );

  Widget _filterMenuRow(
    BuildContext context, {
    required String key,
    required FocusNode node,
    required String label,
    required VoidCallback onActivate,
    String? value,
    bool enabled = true,
  }) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return ConsoleFocusable(
      key: ValueKey<String>(key),
      focusNode: node,
      enabled: enabled,
      onPressed: onActivate,
      semanticLabel: value == null ? label : '$label, $value',
      builder: (context, focused) => ConsoleFocusRing(
        focused: focused,
        borderRadius: layout.controlRadius,
        restBorderColor: Colors.transparent,
        gap: 0,
        child: AnimatedContainer(
          duration: context.motion.resolve(context, context.motion.selection),
          curve: context.motion.standardCurve,
          padding: layout.surface.compactControlPadding,
          decoration: BoxDecoration(
            color: focused ? colors.focusFill : Colors.transparent,
            borderRadius: BorderRadius.circular(layout.controlRadius),
          ),
          child: Row(
            children: <Widget>[
              Expanded(
                child: Text(
                  label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: context.text.action.copyWith(
                    color: enabled
                        ? focused
                              ? colors.textStrong
                              : colors.textBody
                        : colors.textMuted,
                  ),
                ),
              ),
              if (value case final currentValue?) ...<Widget>[
                SizedBox(width: layout.sm),
                Flexible(
                  child: Text(
                    currentValue,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    textAlign: TextAlign.end,
                    style: context.text.supportingTitle.copyWith(
                      color: focused ? colors.textStrong : colors.textMuted,
                    ),
                  ),
                ),
                Icon(
                  Icons.chevron_right_rounded,
                  color: focused ? colors.textStrong : colors.textMuted,
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }

  Widget _filterOptionRow(
    BuildContext context, {
    required String key,
    required FocusNode node,
    required String label,
    required bool selected,
    required VoidCallback onActivate,
  }) => ConsoleFocusable(
    key: ValueKey<String>(key),
    focusNode: node,
    selected: selected,
    onPressed: onActivate,
    semanticLabel: label,
    builder: (context, focused) => ConsoleFocusRing(
      focused: focused,
      borderRadius: context.layout.controlRadius,
      restBorderColor: Colors.transparent,
      gap: 0,
      child: AnimatedContainer(
        duration: context.motion.resolve(context, context.motion.selection),
        curve: context.motion.standardCurve,
        padding: context.layout.surface.compactControlPadding,
        decoration: BoxDecoration(
          color: focused ? context.consoleColors.focusFill : Colors.transparent,
          borderRadius: BorderRadius.circular(context.layout.controlRadius),
        ),
        child: Row(
          children: <Widget>[
            Expanded(
              child: Text(
                label,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: context.text.action.copyWith(
                  color: focused || selected
                      ? context.consoleColors.textStrong
                      : context.consoleColors.textBody,
                ),
              ),
            ),
            if (selected)
              Icon(
                Icons.check_rounded,
                color: context.consoleColors.catalogAccent,
              ),
          ],
        ),
      ),
    ),
  );

  Widget _grid(BuildContext context, double width) {
    final layout = context.layout;
    final gap = layout.gameRail.strip.tileGap;
    _columns = GameTile.gridColumnCount(
      context,
      width: width,
      designColumns: DiscoverLayoutScope.of(context).allGamesGridColumns,
    );
    final tileWidth = (width - gap * (_columns - 1)) / _columns;
    final tileHeight = GameTile.minimumHeight(context, tileWidth);
    _gridTileHeight = tileHeight;
    _gridRunGap = layout.gameRail.gridRunGap;
    return CustomScrollView(
      key: const ValueKey<String>('all-games-grid'),
      controller: _gridScroll,
      slivers: <Widget>[
        SliverGrid(
          gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
            crossAxisCount: _columns,
            mainAxisSpacing: layout.gameRail.gridRunGap,
            crossAxisSpacing: gap,
            childAspectRatio: tileWidth / tileHeight,
          ),
          delegate: SliverChildBuilderDelegate(
            childCount: _games.length,
            (context, index) => GameBrowserMotionSafeCard(
              child: GameTile(
                key: ValueKey<String>('all-games-card-${_games[index].id}'),
                game: _games[index],
                heroTag: 'catalog-all#${_games[index].id}',
                focusNode: _gameNodes[index],
                installed: _isInstalled(_games[index]),
                rowActive: index ~/ _columns == _focusedGame ~/ _columns,
                onFocused: () => _onGameFocused(index),
                onPressed: () => widget.callbacks.onOpenGame(
                  _games[index],
                  'catalog-all#${_games[index].id}',
                ),
              ),
            ),
          ),
        ),
        if (widget.input.snapshot.hasNextPage ||
            widget.input.snapshot.paging ||
            widget.input.snapshot.pagingFailed)
          SliverToBoxAdapter(
            child: Padding(
              padding: EdgeInsets.symmetric(vertical: layout.md),
              child: Center(child: _pagingAction(context)),
            ),
          ),
      ],
    );
  }

  Widget _pagingAction(BuildContext context) {
    final snapshot = widget.input.snapshot;
    final label = snapshot.paging
        ? 'Loading more'
        : snapshot.pagingFailed
        ? 'Retry loading'
        : 'Load more';
    return ConsoleFocusable(
      key: const ValueKey<String>('all-games-paging-action'),
      focusNode: _pagingActionNode,
      enabled: !snapshot.paging,
      onPressed: () => _requestNextPage(force: true),
      semanticLabel: label,
      builder: (context, focused) => ConsoleFocusRing(
        focused: focused,
        borderRadius: context.layout.controlRadius,
        restBorderColor: Colors.transparent,
        gap: 0,
        child: Container(
          padding: context.layout.surface.compactControlPadding,
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(context.layout.controlRadius),
            color: focused
                ? context.consoleColors.focusFill
                : context.consoleColors.controlRestFill,
            border: Border.all(color: context.consoleColors.borderStrong),
          ),
          child: snapshot.paging
              ? SizedBox.square(
                  dimension: context.layout.progressIndicatorSm,
                  child: CircularProgressIndicator(
                    strokeWidth: context.layout.focusStroke,
                  ),
                )
              : Text(
                  label,
                  style: context.text.sectionLabel.copyWith(
                    color: focused
                        ? context.consoleColors.textStrong
                        : context.consoleColors.textMuted,
                  ),
                ),
        ),
      ),
    );
  }

  void _syncGames() {
    final unique = <ConsoleGame>[];
    final ids = <String>{};
    for (final game in widget.input.snapshot.items) {
      if (ids.add(game.id)) unique.add(game);
    }
    final existing = <String, FocusNode>{
      for (var index = 0; index < _gameIds.length; index++)
        _gameIds[index]: _gameNodes[index],
    };
    final nodes = <FocusNode>[
      for (final game in unique)
        existing.remove(game.id) ??
            FocusNode(debugLabel: 'all-games-${game.id}'),
    ];
    for (final node in existing.values) {
      node.dispose();
    }
    _games = List<ConsoleGame>.unmodifiable(unique);
    _gameIds = _games.map((game) => game.id).toList(growable: false);
    _gameNodes = nodes;

    final remembered = widget.input.focus.itemId;
    final rememberedIndex = remembered == null
        ? -1
        : _games.indexWhere((game) => game.id == remembered);
    if (rememberedIndex >= 0) {
      _focusedGame = rememberedIndex;
    } else if (_games.isNotEmpty) {
      _focusedGame = _focusedGame.clamp(0, _games.length - 1);
    } else {
      _focusedGame = 0;
    }
  }

  void _rememberPlatforms() {
    final operation = widget.input.snapshot.requestKey?.operation;
    if (_knownPlatformOperation != operation) {
      _knownPlatformOperation = operation;
      _knownPlatforms.clear();
    }
    // The shared session supplies the operation's complete platform list.
    // Replace rather than append so removed platforms and prior authorities
    // can never remain in the cycling control.
    _knownPlatforms.clear();
    final liveIds = <String>{};
    for (final platform in widget.input.systems.items) {
      liveIds.add(platform.id);
      _knownPlatforms[platform.id] = platform;
    }
    final removed = _systemNodes.keys
        .where((key) => key != 'all' && !liveIds.contains(key))
        .toList(growable: false);
    for (final key in removed) {
      _systemNodes.remove(key)?.dispose();
    }
  }

  List<ConsolePlatform> get _sortedPlatforms =>
      _knownPlatforms.values.toList(growable: false)
        ..sort((a, b) => a.name.compareTo(b.name));

  String _sortLabel(DiscoverCatalogSort sort) => switch (sort) {
    DiscoverCatalogSort.title => 'Title',
    DiscoverCatalogSort.rating => 'Rating',
  };

  DiscoverCatalogSort get _effectiveSort =>
      _filterMenuOpen ? _menuSort : widget.input.sort;

  String? get _effectivePlatformId =>
      _filterMenuOpen ? _menuPlatformId : widget.input.platformId;

  FocusNode _filterOptionNode(String key) => _filterOptionNodes.putIfAbsent(
    key,
    () => FocusNode(debugLabel: 'all-games-filter-option-$key'),
  );

  FocusNode _systemNode(String key) => _systemNodes.putIfAbsent(
    key,
    () => FocusNode(debugLabel: 'browse-system-$key'),
  );

  void _openFilterMenu() {
    if (_filterMenuOpen) return;
    final currentFocus = FocusManager.instance.primaryFocus;
    final gameIndex = currentFocus == null
        ? -1
        : _gameNodes.indexOf(currentFocus);
    final systemFocused =
        currentFocus != null && _systemNodes.values.contains(currentFocus);
    _filterMenuReturnGameId = gameIndex < 0 ? null : _gameIds[gameIndex];
    _filterMenuReturnNode =
        currentFocus == _filterTriggerNode ||
            currentFocus == _statusActionNode ||
            currentFocus == _pagingActionNode ||
            systemFocused ||
            gameIndex >= 0
        ? currentFocus
        : _filterTriggerNode;
    setState(() {
      _filterMenuOpen = true;
      _filterMenuPage = _AllGamesFilterMenuPage.root;
      _menuSort = widget.input.sort;
      _menuPlatformId = widget.input.platformId;
    });
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && _filterMenuOpen) _filterMenuNodes.first.requestFocus();
    });
  }

  void _closeFilterMenu() {
    if (!_filterMenuOpen) return;
    final returnNode = _filterMenuReturnNode;
    final returnGameId = _filterMenuReturnGameId;
    setState(() {
      _filterMenuOpen = false;
      _filterMenuPage = _AllGamesFilterMenuPage.root;
      _filterMenuReturnNode = null;
      _filterMenuReturnGameId = null;
    });
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final returnGameIndex = returnGameId == null
          ? -1
          : _gameIds.indexOf(returnGameId);
      if (returnGameIndex >= 0) {
        _gameNodes[returnGameIndex].requestFocus();
      } else if (returnNode?.canRequestFocus ?? false) {
        returnNode!.requestFocus();
      } else {
        _filterTriggerNode.requestFocus();
      }
    });
  }

  void _openFilterSubpage(_AllGamesFilterMenuPage page) {
    if (page == _AllGamesFilterMenuPage.root) return;
    setState(() => _filterMenuPage = page);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_filterMenuOpen || _filterMenuPage != page) return;
      switch (page) {
        case _AllGamesFilterMenuPage.root:
          break;
        case _AllGamesFilterMenuPage.sort:
          _filterOptionNode('sort-${_menuSort.name}').requestFocus();
      }
    });
  }

  void _returnToFilterRoot({required int row}) {
    setState(() => _filterMenuPage = _AllGamesFilterMenuPage.root);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && _filterMenuOpen) _filterMenuNodes[row].requestFocus();
    });
  }

  void _applyMenuSort(DiscoverCatalogSort sort) {
    if (_menuSort != sort) {
      setState(() => _menuSort = sort);
      widget.callbacks.onSelectSort(sort);
    }
    _returnToFilterRoot(row: 0);
  }

  void _resetMenuFilters() {
    if (_menuSort == DiscoverCatalogSort.title && _menuPlatformId == null) {
      return;
    }
    setState(() {
      _menuSort = DiscoverCatalogSort.title;
      _menuPlatformId = null;
    });
    widget.callbacks.onClearFilters();
    _filterMenuNodes[1].requestFocus();
  }

  void _moveFilterMenu(TraversalDirection direction) {
    switch (_filterMenuPage) {
      case _AllGamesFilterMenuPage.root:
        _moveFilterRoot(direction);
      case _AllGamesFilterMenuPage.sort:
        _moveFilterOptions(direction, <FocusNode>[
          for (final sort in DiscoverCatalogSort.values)
            _filterOptionNode('sort-${sort.name}'),
        ]);
    }
  }

  void _moveFilterRoot(TraversalDirection direction) {
    final available = <int>[
      0,
      if (_menuSort != DiscoverCatalogSort.title || _menuPlatformId != null) 1,
    ];
    final focused = available.indexWhere(
      (index) => _filterMenuNodes[index].hasFocus,
    );
    final position = focused < 0 ? 0 : focused;
    switch (direction) {
      case TraversalDirection.up:
        _filterMenuNodes[available[math.max(0, position - 1)]].requestFocus();
      case TraversalDirection.down:
        _filterMenuNodes[available[math.min(
              available.length - 1,
              position + 1,
            )]]
            .requestFocus();
      case TraversalDirection.left:
        break;
      case TraversalDirection.right:
        if (available[position] == 0) {
          _openFilterSubpage(_AllGamesFilterMenuPage.sort);
        }
    }
  }

  void _moveFilterOptions(TraversalDirection direction, List<FocusNode> nodes) {
    final focused = nodes.indexWhere((node) => node.hasFocus);
    final index = focused < 0 ? 0 : focused;
    switch (direction) {
      case TraversalDirection.up:
        nodes[math.max(0, index - 1)].requestFocus();
      case TraversalDirection.down:
        nodes[math.min(nodes.length - 1, index + 1)].requestFocus();
      case TraversalDirection.left:
        _returnToFilterRoot(row: 0);
      case TraversalDirection.right:
        break;
    }
  }

  void _onGameFocused(int index) {
    if (index < 0 || index >= _games.length) return;
    final changed = _focusedGame != index;
    _focusedGame = index;
    if (changed) setState(() => _focusedDetail = null);
    widget.callbacks.onRememberFocus(
      itemId: _games[index].id,
      scrollOffset: _gridScroll.hasClients ? _gridScroll.offset : 0,
    );
    _settleFocusedDetail();

    final row = index ~/ _columns;
    final finalRow = (_games.length - 1) ~/ _columns;
    if (row >= finalRow) _requestNextPage();
  }

  void _settleFocusedDetail() {
    _detailTimer?.cancel();
    if (_games.isEmpty) return;
    final game = _games[_focusedGame];
    final cached = widget.callbacks.detailFor(game.id);
    if (cached != null) {
      if (!identical(cached, _focusedDetail)) {
        setState(() => _focusedDetail = cached);
      }
      return;
    }
    final generation = ++_detailRequestGeneration;
    _detailTimer = Timer(_detailSettleDelay, () async {
      final detail = await widget.callbacks.loadDetail(game.id);
      if (!mounted ||
          generation != _detailRequestGeneration ||
          _games.isEmpty ||
          _games[_focusedGame].id != game.id ||
          detail == null) {
        return;
      }
      setState(() => _focusedDetail = detail);
    });
  }

  void _requestNextPage({bool force = false}) {
    final snapshot = widget.input.snapshot;
    final cursor = snapshot.nextCursor;
    if (!snapshot.hasNextPage ||
        snapshot.paging ||
        cursor == null ||
        (!force && _requestedCursor == cursor)) {
      return;
    }
    _requestedCursor = cursor;
    widget.callbacks.onLoadMore();
  }

  bool _isInstalled(ConsoleGame game) =>
      game.defaultReleaseId != null &&
      widget.input.installedReleaseIds.contains(game.defaultReleaseId);

  GameBrowserReadiness _readinessFor(ConsoleGame game) => _isInstalled(game)
      ? GameBrowserReadiness.ready
      : game.defaultReleaseId == null
      ? GameBrowserReadiness.unavailable
      : GameBrowserReadiness.available;

  void _scheduleScrollRestore() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _restoreRememberedScroll();
    });
  }

  void _restoreRememberedScroll({bool force = false}) {
    if ((!_pendingScrollRestore && !force) ||
        widget.input.snapshot.state != DiscoverLoadState.ready ||
        _games.isEmpty ||
        !_gridScroll.hasClients) {
      return;
    }
    final position = _gridScroll.position;
    final target = widget.input.focus.scrollOffset.clamp(
      position.minScrollExtent,
      position.maxScrollExtent,
    );
    _gridScroll.jumpTo(target);
    final focusedContext = _gameNodes[_focusedGame].context;
    final focusedRenderObject = focusedContext?.findRenderObject();
    if (focusedRenderObject != null && focusedRenderObject.attached) {
      position
        ..ensureVisible(
          focusedRenderObject,
          alignmentPolicy: ScrollPositionAlignmentPolicy.keepVisibleAtStart,
        )
        ..ensureVisible(
          focusedRenderObject,
          alignmentPolicy: ScrollPositionAlignmentPolicy.keepVisibleAtEnd,
        );
    } else if (_gridTileHeight > 0) {
      final row = _focusedGame ~/ _columns;
      final leading = row * (_gridTileHeight + _gridRunGap);
      final trailing = leading + _gridTileHeight;
      var visibleTarget = position.pixels;
      if (leading < visibleTarget) {
        visibleTarget = leading;
      } else if (trailing > visibleTarget + position.viewportDimension) {
        visibleTarget = trailing - position.viewportDimension;
      }
      position.jumpTo(
        visibleTarget.clamp(position.minScrollExtent, position.maxScrollExtent),
      );
    }
    _pendingScrollRestore = false;
    final resolvedOffset = position.pixels;
    if ((resolvedOffset - widget.input.focus.scrollOffset).abs() > 0.5) {
      widget.callbacks.onRememberFocus(
        itemId: _games[_focusedGame].id,
        scrollOffset: resolvedOffset,
      );
    }
  }

  void _focusFilters() => _openFilterMenu();

  void _move(TraversalDirection direction) {
    final systemKeys = <String>[
      'all',
      for (final platform in _sortedPlatforms) platform.id,
    ];
    final systemIndex = systemKeys.indexWhere(
      (key) => _systemNodes[key]?.hasFocus ?? false,
    );
    if (systemIndex >= 0) {
      switch (direction) {
        case TraversalDirection.up:
          widget.focusHandoff.focusHeader(DiscoverSection.allGames);
        case TraversalDirection.down:
          _filterTriggerNode.requestFocus();
        case TraversalDirection.left:
          if (systemIndex > 0) {
            _systemNode(systemKeys[systemIndex - 1]).requestFocus();
          }
        case TraversalDirection.right:
          if (systemIndex < systemKeys.length - 1) {
            _systemNode(systemKeys[systemIndex + 1]).requestFocus();
          } else if (widget.input.systems.state == DiscoverLoadState.failed) {
            _systemRetryNode.requestFocus();
          }
      }
      return;
    }

    if (_systemRetryNode.hasFocus) {
      switch (direction) {
        case TraversalDirection.up:
          widget.focusHandoff.focusHeader(DiscoverSection.allGames);
        case TraversalDirection.down:
          _filterTriggerNode.requestFocus();
        case TraversalDirection.left:
          _systemNode(systemKeys.last).requestFocus();
        case TraversalDirection.right:
          break;
      }
      return;
    }

    if (_filterTriggerNode.hasFocus) {
      switch (direction) {
        case TraversalDirection.up:
          _focusSelectedSystem();
        case TraversalDirection.down:
          _focusGame();
        case TraversalDirection.left || TraversalDirection.right:
          break;
      }
      return;
    }

    if (_pagingActionNode.hasFocus) {
      if (direction == TraversalDirection.up) _focusGame(_games.length - 1);
      return;
    }
    if (_statusActionNode.hasFocus) {
      if (direction == TraversalDirection.up) {
        _filterTriggerNode.requestFocus();
      }
      return;
    }

    final gameIndex = _gameNodes.indexWhere((node) => node.hasFocus);
    if (gameIndex < 0) {
      moveLauncherFocus(direction);
      return;
    }
    final row = gameIndex ~/ _columns;
    final column = gameIndex % _columns;
    switch (direction) {
      case TraversalDirection.up:
        if (row == 0) {
          _filterTriggerNode.requestFocus();
        } else {
          _focusGame(gameIndex - _columns);
        }
      case TraversalDirection.down:
        final target = gameIndex + _columns;
        if (target < _games.length) {
          _focusGame(target);
        } else if (widget.input.snapshot.pagingFailed) {
          _pagingActionNode.requestFocus();
        } else {
          _requestNextPage();
        }
      case TraversalDirection.left:
        if (column > 0) _focusGame(gameIndex - 1);
      case TraversalDirection.right:
        if (column < _columns - 1 && gameIndex + 1 < _games.length) {
          _focusGame(gameIndex + 1);
        }
    }
  }

  void _focusGame([int? index]) {
    if (_gameNodes.isEmpty) {
      _filterTriggerNode.requestFocus();
      return;
    }
    final target = (index ?? _focusedGame).clamp(0, _gameNodes.length - 1);
    _gameNodes[target].requestFocus();
  }

  void _restoreContentFocus() {
    _focusSelectedSystem();
  }

  void _focusSelectedSystem() {
    final selected = widget.input.platformId;
    final key = selected != null && _knownPlatforms.containsKey(selected)
        ? selected
        : 'all';
    _systemNode(key).requestFocus();
  }
}

enum _AllGamesFilterMenuPage { root, sort }
