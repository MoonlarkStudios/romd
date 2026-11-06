import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/input/console_intents.dart';
import 'package:romd_console/src/play/content/domain/profile_local_library.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/presentation/catalog/catalog_operation_context.dart';
import 'package:romd_console/src/presentation/launcher/launcher_destination.dart';
import 'package:romd_console/src/presentation/launcher/launcher_primitives.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';
import 'package:romd_console/src/presentation/widgets/console_focusable.dart';
import 'package:romd_console/src/presentation/widgets/game_browser_inspector.dart';
import 'package:romd_console/src/presentation/widgets/game_tile.dart';

final class LibraryLauncherDestination extends StatefulWidget {
  const LibraryLauncherDestination({
    required this.consumerApiClient,
    required this.operation,
    required this.playServices,
    required this.active,
    required this.headerFocusNode,
    required this.onConnect,
    required this.onOpenGame,
    required this.onMoveToUtilities,
    this.autofocusContent = false,
    super.key,
  });

  final ConsumerApiClient consumerApiClient;
  final CatalogOperationContext? operation;
  final PlayServices playServices;
  final bool active;
  final FocusNode headerFocusNode;
  final VoidCallback onConnect;
  final void Function(ConsoleGame game, String heroTag) onOpenGame;
  final VoidCallback onMoveToUtilities;
  final bool autofocusContent;

  @override
  State<LibraryLauncherDestination> createState() =>
      LibraryLauncherDestinationState();
}

final class LibraryLauncherDestinationState
    extends State<LibraryLauncherDestination>
    implements LauncherDestinationFocus {
  static const Duration _detailSettleDelay = Duration(milliseconds: 180);

  final FocusNode _filterTriggerNode = FocusNode(
    debugLabel: 'library-filter-trigger',
  );
  final List<FocusNode> _filterMenuNodes = <FocusNode>[
    FocusNode(debugLabel: 'library-filter-sort'),
    FocusNode(debugLabel: 'library-filter-system'),
    FocusNode(debugLabel: 'library-filter-reset'),
  ];
  final Map<String, FocusNode> _filterOptionNodes = <String, FocusNode>{};
  final FocusNode _statusActionNode = FocusNode(
    debugLabel: 'library-status-action',
  );
  final ScrollController _gridScroll = ScrollController(
    debugLabel: 'local-library-grid',
  );
  final Map<String, ConsoleGameDetail> _detailCache =
      <String, ConsoleGameDetail>{};

  StreamSubscription<ProfileLocalLibraryResult>? _subscription;
  Timer? _detailTimer;
  ConsoleGameDetail? _focusedDetail;
  int _detailRequestGeneration = 0;
  int _subscriptionGeneration = 0;
  ProfileLocalLibrarySort _sort = ProfileLocalLibrarySort.recentPlay;
  String? _platformId;
  ProfileLocalLibraryResult? _result;
  List<ProfileLocalLibraryTitle> _allTitles =
      const <ProfileLocalLibraryTitle>[];
  List<ProfileLocalLibraryTitle> _titles = const <ProfileLocalLibraryTitle>[];
  List<String> _gameIds = const <String>[];
  List<FocusNode> _gameNodes = <FocusNode>[];
  int _focusedGame = 0;
  int _columns = 4;
  bool _initialFocusHandled = false;
  bool _filterMenuOpen = false;
  _LibraryFilterMenuPage _filterMenuPage = _LibraryFilterMenuPage.root;
  ProfileLocalLibrarySort _menuSort = ProfileLocalLibrarySort.recentPlay;
  String? _menuPlatformId;
  FocusNode? _filterMenuReturnNode;
  String? _filterMenuReturnGameId;
  String? _announcement;

  @override
  void initState() {
    super.initState();
    widget.operation?.changes?.addListener(_onOperationChanged);
    _subscribe();
  }

  @override
  void didUpdateWidget(covariant LibraryLauncherDestination oldWidget) {
    super.didUpdateWidget(oldWidget);
    final operationChanged = !_sameOperation(
      oldWidget.operation,
      widget.operation,
    );
    if (operationChanged) {
      oldWidget.operation?.changes?.removeListener(_onOperationChanged);
      widget.operation?.changes?.addListener(_onOperationChanged);
    }
    if (operationChanged ||
        !identical(oldWidget.consumerApiClient, widget.consumerApiClient)) {
      _clearRemoteMetadata();
      _settleFocusedDetail();
    }
    if (oldWidget.playServices.localLibrary !=
        widget.playServices.localLibrary) {
      unawaited(_subscription?.cancel());
      _subscribe();
    }
    if (oldWidget.active && !widget.active && _filterMenuOpen) {
      _filterMenuOpen = false;
      _filterMenuPage = _LibraryFilterMenuPage.root;
      _filterMenuReturnNode = null;
      _filterMenuReturnGameId = null;
    }
  }

  @override
  void dispose() {
    widget.operation?.changes?.removeListener(_onOperationChanged);
    _detailTimer?.cancel();
    _detailRequestGeneration++;
    _subscriptionGeneration++;
    unawaited(_subscription?.cancel());
    _filterTriggerNode.dispose();
    for (final node in _filterMenuNodes) {
      node.dispose();
    }
    for (final node in _filterOptionNodes.values) {
      node.dispose();
    }
    for (final node in _gameNodes) {
      node.dispose();
    }
    _statusActionNode.dispose();
    _gridScroll.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => SliverFillRemaining(
    child: Shortcuts(
      shortcuts: const <ShortcutActivator, Intent>{
        SingleActivator(LogicalKeyboardKey.keyF): ShowDetailsIntent(),
        SingleActivator(LogicalKeyboardKey.keyY): ShowDetailsIntent(),
      },
      child: Actions(
        actions: <Type, Action<Intent>>{
          ...launcherDirectionalActions(_move),
          ShowDetailsIntent: CallbackAction<ShowDetailsIntent>(
            onInvoke: (_) {
              _openFilterMenu();
              return null;
            },
          ),
        },
        child: _body(context),
      ),
    ),
  );

  @override
  void enterFromHeader() {
    if (_result is ProfileLocalLibraryNoServer) {
      _statusActionNode.requestFocus();
    } else if (_gameNodes.isNotEmpty) {
      _focusGame();
    } else if (_result is ProfileLocalLibraryReady) {
      if (_platformId != null) {
        _statusActionNode.requestFocus();
      } else {
        _filterTriggerNode.requestFocus();
      }
    }
  }

  @override
  void restoreContentFocus() => enterFromHeader();

  Widget _body(BuildContext context) {
    final result = _result;
    if (result == null) {
      return const LauncherStatusPanel(
        icon: Icons.inventory_2_outlined,
        title: 'Opening Local Library',
        message: 'Reading games acquired by this profile.',
      );
    }
    if (result is ProfileLocalLibraryNoServer) {
      return LauncherStatusPanel(
        icon: Icons.dns_outlined,
        title: 'Choose a ROMD server',
        message:
            'Local Library follows the ROMD server selected by this profile.',
        action: FilledButton.icon(
          focusNode: _statusActionNode,
          onPressed: widget.onConnect,
          icon: const Icon(Icons.link),
          label: const Text('Choose server'),
        ),
      );
    }
    if (result is ProfileLocalLibraryUnavailable) {
      return const LauncherStatusPanel(
        icon: Icons.shield_outlined,
        title: 'Local Library unavailable',
        message:
            'Ottercade couldn’t safely read this profile’s local records. '
            'Nothing has been removed.',
      );
    }
    return Padding(
      padding: EdgeInsets.symmetric(
        horizontal: context.layout.discover.frameMargin,
      ),
      child: Stack(
        fit: StackFit.expand,
        children: <Widget>[
          ExcludeFocus(
            excluding: _filterMenuOpen,
            child: IgnorePointer(
              ignoring: _filterMenuOpen,
              child: Semantics(
                liveRegion: true,
                label: _announcement,
                child: _readyBody(context),
              ),
            ),
          ),
          if (_filterMenuOpen) _filterMenu(context),
        ],
      ),
    );
  }

  Widget _readyBody(BuildContext context) {
    final metrics = context.layout.discover;
    final textScaler = MediaQuery.textScalerOf(context);
    final bodyScale = context.text.body.fontSize ?? 16;
    final largeText =
        textScaler.scale(bodyScale) >=
        bodyScale * metrics.largeTextScaleBreakpoint;
    return LayoutBuilder(
      builder: (context, constraints) {
        final compact =
            constraints.maxWidth < metrics.compactBreakpoint || largeText;
        final controlBar = _controlBar(context);
        if (compact) {
          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: <Widget>[
              controlBar,
              SizedBox(height: context.layout.sm),
              if (_titles.isEmpty)
                Expanded(child: _emptyResults(context))
              else ...<Widget>[
                GameBrowserInspector(
                  key: const ValueKey<String>('library-inspector'),
                  keyPrefix: 'library',
                  game: _games[_focusedGame],
                  detail: _focusedDetail,
                  readiness: _readinessFor(_titles[_focusedGame]),
                  compact: true,
                ),
                SizedBox(height: context.layout.sm),
                Expanded(child: _grid(context, constraints.maxWidth)),
              ],
            ],
          );
        }
        final gridWidth =
            constraints.maxWidth -
            metrics.allGamesInspectorWidth -
            metrics.allGamesPaneGap;
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: <Widget>[
            controlBar,
            SizedBox(height: context.layout.sm),
            Expanded(
              child: _titles.isEmpty
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
                            key: const ValueKey<String>('library-inspector'),
                            keyPrefix: 'library',
                            game: _games[_focusedGame],
                            detail: _focusedDetail,
                            readiness: _readinessFor(_titles[_focusedGame]),
                            compact: false,
                          ),
                        ),
                      ],
                    ),
            ),
          ],
        );
      },
    );
  }

  Widget _emptyResults(BuildContext context) {
    final filtered = _platformId != null;
    return ConstrainedBox(
      constraints: BoxConstraints(
        minHeight: context.layout.surface.emptyStateMinHeight,
      ),
      child: LauncherStatusPanel(
        icon: filtered
            ? Icons.filter_alt_off_outlined
            : Icons.download_done_outlined,
        title: filtered
            ? 'No games match these filters'
            : 'No local games for this profile',
        message: filtered
            ? 'Clear the active System filter to see every local title.'
            : 'Games acquired from the selected ROMD server will appear here.',
        action: filtered
            ? FilledButton.icon(
                key: const ValueKey<String>('library-clear-filters'),
                focusNode: _statusActionNode,
                onPressed: _clearFilters,
                icon: const Icon(Icons.filter_alt_off_rounded),
                label: const Text('Clear filters'),
              )
            : null,
      ),
    );
  }

  Widget _controlBar(BuildContext context) => Row(
    children: <Widget>[
      _filterTrigger(context),
      SizedBox(width: context.layout.sm),
      Flexible(
        child: Text(
          '${_sortLabel(_effectiveSort).toUpperCase()} · '
          '${_platformLabel(_effectivePlatformId).toUpperCase()}',
          key: const ValueKey<String>('library-filter-summary'),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: context.text.metadataStrong.copyWith(
            color: context.consoleColors.textMuted,
          ),
        ),
      ),
      const Spacer(),
      Text(
        '${_titles.length} ${_titles.length == 1 ? 'LOCAL GAME' : 'LOCAL GAMES'}',
        key: const ValueKey<String>('library-count'),
        style: context.text.metadataStrong.copyWith(
          color: context.consoleColors.textMuted,
        ),
      ),
    ],
  );

  Widget _filterTrigger(BuildContext context) => ConsoleFocusable(
    key: const ValueKey<String>('library-filter-trigger'),
    focusNode: _filterTriggerNode,
    onPressed: _openFilterMenu,
    semanticLabel: 'Sort and filter local games',
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
                _effectiveSort != ProfileLocalLibrarySort.recentPlay ||
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

  Widget _grid(BuildContext context, double width) {
    final layout = context.layout;
    final gap = layout.gameRail.strip.tileGap;
    _columns = GameTile.gridColumnCount(
      context,
      width: width,
      designColumns: layout.discover.allGamesGridColumns,
    );
    final tileWidth = (width - gap * (_columns - 1)) / _columns;
    final tileHeight = GameTile.minimumHeight(context, tileWidth);
    return CustomScrollView(
      key: const ValueKey<String>('library-grid'),
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
            childCount: _titles.length,
            (context, index) => GameBrowserMotionSafeCard(
              child: GameTile(
                key: ValueKey<String>(
                  'library-card-${_titles[index].install.titleId}',
                ),
                game: _games[index],
                heroTag: 'local-library#${_titles[index].install.titleId}',
                focusNode: _gameNodes[index],
                installed:
                    _titles[index].state == ProfileLocalLibraryState.ready,
                supportingText: _supportingTextFor(_titles[index]),
                rowActive: index ~/ _columns == _focusedGame ~/ _columns,
                onFocused: () => _onGameFocused(index),
                onPressed: () => widget.onOpenGame(
                  _games[index],
                  'local-library#${_titles[index].install.titleId}',
                ),
              ),
            ),
          ),
        ),
      ],
    );
  }

  Widget _filterMenu(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final metrics = layout.discover;
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
              if (_filterMenuPage == _LibraryFilterMenuPage.root) {
                _closeFilterMenu();
              } else {
                _returnToFilterRoot(
                  row: _filterMenuPage == _LibraryFilterMenuPage.sort ? 0 : 1,
                );
              }
              return null;
            },
          ),
        },
        child: FocusScope(
          child: LayoutBuilder(
            builder: (context, constraints) => Stack(
              key: const ValueKey<String>('library-filter-menu'),
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
      _LibraryFilterMenuPage.root => 'SORT & FILTER',
      _LibraryFilterMenuPage.sort => 'SORT BY',
      _LibraryFilterMenuPage.system => 'SYSTEM',
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
          _LibraryFilterMenuPage.root => Flexible(
            child: SingleChildScrollView(child: _filterRootMenu(context)),
          ),
          _LibraryFilterMenuPage.sort => Flexible(
            child: SingleChildScrollView(child: _sortOptions(context)),
          ),
          _LibraryFilterMenuPage.system => Flexible(
            child: _systemOptions(context),
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
        key: 'library-filter-sort',
        node: _filterMenuNodes[0],
        label: 'Sort by',
        value: _sortLabel(_menuSort),
        onActivate: () => _openFilterSubpage(_LibraryFilterMenuPage.sort),
      ),
      SizedBox(height: context.layout.xs),
      _filterMenuRow(
        context,
        key: 'library-filter-system',
        node: _filterMenuNodes[1],
        label: 'System',
        value: _platformLabel(_menuPlatformId),
        onActivate: () => _openFilterSubpage(_LibraryFilterMenuPage.system),
      ),
      SizedBox(height: context.layout.md),
      _filterMenuRow(
        context,
        key: 'library-filter-reset',
        node: _filterMenuNodes[2],
        label: 'Reset filters',
        enabled:
            _menuSort != ProfileLocalLibrarySort.recentPlay ||
            _menuPlatformId != null,
        onActivate: _resetMenuFilters,
      ),
    ],
  );

  Widget _sortOptions(BuildContext context) => Column(
    mainAxisSize: MainAxisSize.min,
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: <Widget>[
      for (final sort in ProfileLocalLibrarySort.values) ...<Widget>[
        _filterOptionRow(
          context,
          key: 'library-filter-sort-${sort.name}',
          node: _filterOptionNode('sort-${sort.name}'),
          label: _sortLabel(sort),
          selected: sort == _menuSort,
          onActivate: () => _applyMenuSort(sort),
        ),
        if (sort != ProfileLocalLibrarySort.values.last)
          SizedBox(height: context.layout.xs),
      ],
    ],
  );

  Widget _systemOptions(BuildContext context) {
    final platforms = _platforms;
    return ListView.separated(
      key: const ValueKey<String>('library-filter-system-options'),
      shrinkWrap: true,
      itemCount: platforms.length + 1,
      separatorBuilder: (_, _) => SizedBox(height: context.layout.xs),
      itemBuilder: (context, index) {
        final platform = index == 0 ? null : platforms[index - 1];
        final id = platform?.id;
        return _filterOptionRow(
          context,
          key: id == null
              ? 'library-filter-system-all'
              : 'library-filter-system-$id',
          node: _filterOptionNode('system-${id ?? 'all'}'),
          label: platform?.name ?? 'All Systems',
          selected: id == _menuPlatformId,
          onActivate: () => _applyMenuPlatform(id),
        );
      },
    );
  }

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

  void _subscribe() {
    final generation = ++_subscriptionGeneration;
    _subscription = widget.playServices.localLibrary
        .watchLibrary(sort: _sort)
        .listen((result) {
          if (generation == _subscriptionGeneration) _replaceResult(result);
        });
  }

  void _replaceResult(ProfileLocalLibraryResult result) {
    if (!mounted) return;
    final allTitles = switch (result) {
      ProfileLocalLibraryReady(:final titles) => titles,
      ProfileLocalLibraryNoServer() ||
      ProfileLocalLibraryUnavailable() => const <ProfileLocalLibraryTitle>[],
    };
    final knownPlatforms = <String>{
      for (final title in allTitles) _platformKey(title),
    };
    final platformId =
        _platformId != null && !knownPlatforms.contains(_platformId)
        ? null
        : _platformId;
    _syncTitles(result: result, allTitles: allTitles, platformId: platformId);
  }

  void _syncTitles({
    required ProfileLocalLibraryResult result,
    required List<ProfileLocalLibraryTitle> allTitles,
    required String? platformId,
  }) {
    final next = platformId == null
        ? allTitles
        : allTitles
              .where((title) => _platformKey(title) == platformId)
              .toList(growable: false);
    final focusedId =
        _gameNodes.isNotEmpty &&
            _focusedGame < _gameNodes.length &&
            _gameNodes[_focusedGame].hasFocus
        ? _gameIds[_focusedGame]
        : null;
    final existing = <String, FocusNode>{
      for (var index = 0; index < _gameIds.length; index++)
        _gameIds[index]: _gameNodes[index],
    };
    final nextIds = next
        .map((title) => title.install.titleId)
        .toList(growable: false);
    final nextNodes = <FocusNode>[
      for (final id in nextIds)
        existing.remove(id) ?? FocusNode(debugLabel: 'local-library-$id'),
    ];
    final removed = existing.values.toList(growable: false);
    var nextFocus = next.isEmpty ? 0 : _focusedGame.clamp(0, next.length - 1);
    if (focusedId != null) {
      final carried = nextIds.indexOf(focusedId);
      if (carried >= 0) {
        nextFocus = carried;
      } else {
        _announcement = 'A local game is no longer available.';
      }
    }
    setState(() {
      _result = result;
      _allTitles = List<ProfileLocalLibraryTitle>.unmodifiable(allTitles);
      _platformId = platformId;
      _titles = List<ProfileLocalLibraryTitle>.unmodifiable(next);
      _gameIds = nextIds;
      _gameNodes = nextNodes;
      _focusedGame = nextFocus;
      _focusedDetail = next.isEmpty ? null : _detailCache[nextIds[nextFocus]];
    });
    WidgetsBinding.instance.addPostFrameCallback((_) {
      for (final node in removed) {
        node.dispose();
      }
      if (!mounted) return;
      if (!_initialFocusHandled && widget.autofocusContent) {
        _initialFocusHandled = true;
        _focusInitialResult();
        _settleFocusedDetail();
        return;
      }
      if (widget.active && focusedId != null && next.isNotEmpty) {
        _focusGame(nextFocus);
      }
      _settleFocusedDetail();
    });
  }

  void _focusInitialResult() {
    if (!widget.active) return;
    if (_gameNodes.isNotEmpty) {
      _focusGame();
    } else if (_result is ProfileLocalLibraryNoServer) {
      _statusActionNode.requestFocus();
    } else if (_result is ProfileLocalLibraryReady) {
      _filterTriggerNode.requestFocus();
    } else {
      widget.headerFocusNode.requestFocus();
    }
  }

  void _onGameFocused(int index) {
    if (index < 0 || index >= _titles.length || _focusedGame == index) return;
    setState(() {
      _focusedGame = index;
      _focusedDetail = _detailCache[_titles[index].install.titleId];
    });
    _settleFocusedDetail();
  }

  void _settleFocusedDetail() {
    _detailTimer?.cancel();
    final generation = ++_detailRequestGeneration;
    final operation = widget.operation;
    if (_titles.isEmpty || operation == null || !operation.isRequestCurrent) {
      return;
    }
    final titleId = _titles[_focusedGame].install.titleId;
    final cached = _detailCache[titleId];
    if (cached != null) {
      if (!identical(cached, _focusedDetail)) {
        setState(() => _focusedDetail = cached);
      }
      return;
    }
    _detailTimer = Timer(_detailSettleDelay, () async {
      final ConsoleGameDetail detail;
      try {
        detail = await operation.executeConsumer(
          (accessToken) => widget.consumerApiClient.getTitle(
            accessToken: accessToken,
            titleId: titleId,
          ),
        );
      } on Object {
        return;
      }
      if (!mounted ||
          generation != _detailRequestGeneration ||
          !operation.isRequestCurrent ||
          !_sameOperation(operation, widget.operation) ||
          detail.id != titleId ||
          _titles.isEmpty ||
          _titles[_focusedGame].install.titleId != titleId) {
        return;
      }
      _detailCache[titleId] = detail;
      setState(() => _focusedDetail = detail);
    });
  }

  void _onOperationChanged() {
    final operation = widget.operation;
    if (operation != null && operation.isRequestCurrent) return;
    if (!mounted) return;
    setState(_clearRemoteMetadata);
  }

  void _clearRemoteMetadata() {
    _detailTimer?.cancel();
    _detailRequestGeneration++;
    _detailCache.clear();
    _focusedDetail = null;
  }

  static bool _sameOperation(
    CatalogOperationContext? left,
    CatalogOperationContext? right,
  ) => switch ((left, right)) {
    (null, null) => true,
    (final CatalogOperationContext a, final CatalogOperationContext b) =>
      a.hasSameOperation(b),
    _ => false,
  };

  void _move(TraversalDirection direction) {
    if (_filterTriggerNode.hasFocus) {
      switch (direction) {
        case TraversalDirection.up:
          widget.headerFocusNode.requestFocus();
        case TraversalDirection.down:
          if (_gameNodes.isNotEmpty) {
            _focusGame();
          } else if (_platformId != null) {
            _statusActionNode.requestFocus();
          }
        case TraversalDirection.left || TraversalDirection.right:
          break;
      }
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
        if (target < _titles.length) {
          _focusGame(target);
        } else {
          widget.onMoveToUtilities();
        }
      case TraversalDirection.left:
        if (column > 0) _focusGame(gameIndex - 1);
      case TraversalDirection.right:
        if (column < _columns - 1 && gameIndex + 1 < _titles.length) {
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

  void _openFilterMenu() {
    if (_filterMenuOpen || _result is! ProfileLocalLibraryReady) return;
    final currentFocus = FocusManager.instance.primaryFocus;
    final gameIndex = currentFocus == null
        ? -1
        : _gameNodes.indexOf(currentFocus);
    _filterMenuReturnGameId = gameIndex < 0 ? null : _gameIds[gameIndex];
    _filterMenuReturnNode =
        currentFocus == _filterTriggerNode ||
            currentFocus == _statusActionNode ||
            gameIndex >= 0
        ? currentFocus
        : _filterTriggerNode;
    setState(() {
      _filterMenuOpen = true;
      _filterMenuPage = _LibraryFilterMenuPage.root;
      _menuSort = _sort;
      _menuPlatformId = _platformId;
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
      _filterMenuPage = _LibraryFilterMenuPage.root;
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
      } else if (returnGameId != null && _gameNodes.isNotEmpty) {
        _focusGame();
      } else if (returnNode?.canRequestFocus ?? false) {
        returnNode!.requestFocus();
      } else {
        _filterTriggerNode.requestFocus();
      }
    });
  }

  void _openFilterSubpage(_LibraryFilterMenuPage page) {
    if (page == _LibraryFilterMenuPage.root) return;
    setState(() => _filterMenuPage = page);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_filterMenuOpen || _filterMenuPage != page) return;
      switch (page) {
        case _LibraryFilterMenuPage.root:
          break;
        case _LibraryFilterMenuPage.sort:
          _filterOptionNode('sort-${_menuSort.name}').requestFocus();
        case _LibraryFilterMenuPage.system:
          _filterOptionNode(
            'system-${_menuPlatformId ?? 'all'}',
          ).requestFocus();
      }
    });
  }

  void _returnToFilterRoot({required int row}) {
    setState(() => _filterMenuPage = _LibraryFilterMenuPage.root);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && _filterMenuOpen) _filterMenuNodes[row].requestFocus();
    });
  }

  void _applyMenuSort(ProfileLocalLibrarySort sort) {
    if (_menuSort != sort) {
      setState(() => _menuSort = sort);
      _selectSort(sort);
    }
    _returnToFilterRoot(row: 0);
  }

  void _applyMenuPlatform(String? platformId) {
    if (_menuPlatformId != platformId) {
      setState(() => _menuPlatformId = platformId);
      _setPlatform(platformId);
    }
    _returnToFilterRoot(row: 1);
  }

  void _resetMenuFilters() {
    if (_menuSort == ProfileLocalLibrarySort.recentPlay &&
        _menuPlatformId == null) {
      return;
    }
    final sortChanged = _sort != ProfileLocalLibrarySort.recentPlay;
    setState(() {
      _menuSort = ProfileLocalLibrarySort.recentPlay;
      _menuPlatformId = null;
    });
    _setPlatform(null);
    if (sortChanged) _selectSort(ProfileLocalLibrarySort.recentPlay);
    _filterMenuNodes[2].requestFocus();
  }

  void _clearFilters() {
    final sortChanged = _sort != ProfileLocalLibrarySort.recentPlay;
    _setPlatform(null);
    if (sortChanged) _selectSort(ProfileLocalLibrarySort.recentPlay);
  }

  void _selectSort(ProfileLocalLibrarySort sort) {
    if (_sort == sort) return;
    setState(() => _sort = sort);
    unawaited(_subscription?.cancel());
    _subscribe();
  }

  void _setPlatform(String? platformId) {
    if (_platformId == platformId) return;
    _syncTitles(
      result: _result!,
      allTitles: _allTitles,
      platformId: platformId,
    );
  }

  void _moveFilterMenu(TraversalDirection direction) {
    switch (_filterMenuPage) {
      case _LibraryFilterMenuPage.root:
        _moveFilterRoot(direction);
      case _LibraryFilterMenuPage.sort:
        _moveFilterOptions(direction, <FocusNode>[
          for (final sort in ProfileLocalLibrarySort.values)
            _filterOptionNode('sort-${sort.name}'),
        ]);
      case _LibraryFilterMenuPage.system:
        _moveFilterOptions(direction, <FocusNode>[
          _filterOptionNode('system-all'),
          for (final platform in _platforms)
            _filterOptionNode('system-${platform.id}'),
        ]);
    }
  }

  void _moveFilterRoot(TraversalDirection direction) {
    final available = <int>[
      0,
      1,
      if (_menuSort != ProfileLocalLibrarySort.recentPlay ||
          _menuPlatformId != null)
        2,
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
          _openFilterSubpage(_LibraryFilterMenuPage.sort);
        } else if (available[position] == 1) {
          _openFilterSubpage(_LibraryFilterMenuPage.system);
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
        _returnToFilterRoot(
          row: _filterMenuPage == _LibraryFilterMenuPage.sort ? 0 : 1,
        );
      case TraversalDirection.right:
        break;
    }
  }

  FocusNode _filterOptionNode(String key) => _filterOptionNodes.putIfAbsent(
    key,
    () => FocusNode(debugLabel: 'library-filter-option-$key'),
  );

  List<_LibraryPlatformOption> get _platforms {
    final byId = <String, _LibraryPlatformOption>{};
    for (final title in _allTitles) {
      final id = _platformKey(title);
      byId[id] = _LibraryPlatformOption(
        id: id,
        name:
            title.install.platformName ??
            title.install.platformShortName.toUpperCase(),
      );
    }
    return byId.values.toList(growable: false)
      ..sort((a, b) => a.name.compareTo(b.name));
  }

  String _platformLabel(String? id) {
    if (id == null) return 'All Systems';
    for (final platform in _platforms) {
      if (platform.id == id) return platform.name;
    }
    return 'All Systems';
  }

  static String _platformKey(ProfileLocalLibraryTitle title) =>
      title.install.platformId ?? title.install.platformShortName;

  static String _sortLabel(ProfileLocalLibrarySort sort) => switch (sort) {
    ProfileLocalLibrarySort.recentPlay => 'Recently played',
    ProfileLocalLibrarySort.recentInstall => 'Recently installed',
    ProfileLocalLibrarySort.title => 'Title',
    ProfileLocalLibrarySort.system => 'System',
  };

  ProfileLocalLibrarySort get _effectiveSort =>
      _filterMenuOpen ? _menuSort : _sort;

  String? get _effectivePlatformId =>
      _filterMenuOpen ? _menuPlatformId : _platformId;

  List<ConsoleGame> get _games => _titles.map(_gameFor).toList(growable: false);

  static String _supportingTextFor(ProfileLocalLibraryTitle title) =>
      switch (title.state) {
        ProfileLocalLibraryState.ready =>
          title.releaseCount == 1
              ? 'Ready to play'
              : '${title.releaseCount} releases',
        ProfileLocalLibraryState.accessRequired => 'Access required',
        ProfileLocalLibraryState.notReady => 'Download incomplete',
        ProfileLocalLibraryState.repairRequired => 'Repair required',
      };

  static GameBrowserReadiness _readinessFor(ProfileLocalLibraryTitle title) =>
      switch (title.state) {
        ProfileLocalLibraryState.ready => GameBrowserReadiness.ready,
        ProfileLocalLibraryState.accessRequired =>
          GameBrowserReadiness.accessRequired,
        ProfileLocalLibraryState.notReady => GameBrowserReadiness.incomplete,
        ProfileLocalLibraryState.repairRequired =>
          GameBrowserReadiness.repairRequired,
      };

  static ConsoleGame _gameFor(ProfileLocalLibraryTitle title) => ConsoleGame(
    id: title.install.titleId,
    platformId: title.install.platformId ?? title.install.platformShortName,
    platformName:
        title.install.platformName ??
        title.install.platformShortName.toUpperCase(),
    title: title.install.titleName,
    releaseDate: null,
    coverUrl:
        title.install.artwork.forRole('Poster')?.url ?? title.install.coverUrl,
    artwork: title.install.artwork,
    genre: null,
    rating: null,
    releaseCount: title.releaseCount,
    defaultReleaseId: title.install.releaseId,
  );
}

enum _LibraryFilterMenuPage { root, sort, system }

@immutable
final class _LibraryPlatformOption {
  const _LibraryPlatformOption({required this.id, required this.name});

  final String id;
  final String name;
}
