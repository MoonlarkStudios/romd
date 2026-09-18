import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/input/console_intents.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/console_blinking_caret.dart';
import 'package:romd_console/src/presentation/widgets/console_circle_button.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';
import 'package:romd_console/src/presentation/widgets/console_focusable.dart';
import 'package:romd_console/src/presentation/widgets/console_hint_bar.dart';
import 'package:romd_console/src/presentation/widgets/cover_art.dart';
import 'package:romd_console/src/presentation/widgets/platform_presentation.dart';

@immutable
final class DiscoverSearchInput {
  const DiscoverSearchInput({
    required this.results,
    required this.systems,
    required this.focus,
  });

  final DiscoverGamePageSnapshot results;
  final DiscoverSystemsSnapshot systems;
  final DiscoverSearchFocusSnapshot focus;
}

@immutable
final class DiscoverSearchRequest {
  const DiscoverSearchRequest({
    required this.query,
    required this.completeness,
    this.platformId,
  });

  final String query;
  final String? platformId;
  final DiscoverReleaseCompleteness completeness;
}

@immutable
final class DiscoverSearchCallbacks {
  const DiscoverSearchCallbacks({
    required this.onSearch,
    required this.onRetry,
    required this.onLoadMore,
    required this.onOpenGame,
    required this.onBack,
    required this.onRememberFocus,
  });

  final ValueChanged<DiscoverSearchRequest> onSearch;
  final ValueChanged<DiscoverSearchRequest> onRetry;
  final VoidCallback onLoadMore;
  final void Function(ConsoleGame game, String heroTag) onOpenGame;
  final VoidCallback onBack;
  final ValueChanged<DiscoverSearchFocusSnapshot> onRememberFocus;
}

/// Full-screen Search content. It owns input, filters, focus, scroll, and
/// debounce state while all remote state remains in the operation-scoped
/// Discover session.
final class DiscoverSearchBoundary extends StatefulWidget {
  const DiscoverSearchBoundary({
    required this.input,
    required this.callbacks,
    super.key,
  });

  final DiscoverSearchInput input;
  final DiscoverSearchCallbacks callbacks;

  @override
  State<DiscoverSearchBoundary> createState() => _DiscoverSearchBoundaryState();
}

final class _DiscoverSearchBoundaryState extends State<DiscoverSearchBoundary> {
  static const List<_KeyboardRow> _keyboardRows = <_KeyboardRow>[
    _KeyboardRow(<_KeyboardKey>[
      _KeyboardKey.character('A'),
      _KeyboardKey.character('B'),
      _KeyboardKey.character('C'),
      _KeyboardKey.character('D'),
      _KeyboardKey.character('E'),
      _KeyboardKey.character('F'),
      _KeyboardKey.character('G'),
    ]),
    _KeyboardRow(<_KeyboardKey>[
      _KeyboardKey.character('H'),
      _KeyboardKey.character('I'),
      _KeyboardKey.character('J'),
      _KeyboardKey.character('K'),
      _KeyboardKey.character('L'),
      _KeyboardKey.character('M'),
      _KeyboardKey.character('N'),
    ]),
    _KeyboardRow(<_KeyboardKey>[
      _KeyboardKey.character('O'),
      _KeyboardKey.character('P'),
      _KeyboardKey.character('Q'),
      _KeyboardKey.character('R'),
      _KeyboardKey.character('S'),
      _KeyboardKey.character('T'),
      _KeyboardKey.character('U'),
    ]),
    _KeyboardRow(<_KeyboardKey>[
      _KeyboardKey.character('V'),
      _KeyboardKey.character('W'),
      _KeyboardKey.character('X'),
      _KeyboardKey.character('Y'),
      _KeyboardKey.character('Z'),
      _KeyboardKey.character('1'),
      _KeyboardKey.character('2'),
    ]),
    _KeyboardRow(<_KeyboardKey>[
      _KeyboardKey.character('3'),
      _KeyboardKey.character('4'),
      _KeyboardKey.character('5'),
      _KeyboardKey.character('6'),
      _KeyboardKey.character('7'),
      _KeyboardKey.character('8'),
      _KeyboardKey.character('9'),
    ]),
    _KeyboardRow(<_KeyboardKey>[
      _KeyboardKey.character('0'),
      _KeyboardKey.space(),
      _KeyboardKey.backspace(),
      _KeyboardKey.clear(),
    ]),
  ];

  static final List<_KeyboardKey> _keyboardKeys = <_KeyboardKey>[
    for (final row in _keyboardRows) ...row.keys,
  ];

  late String _query;
  late String? _platformId;
  late DiscoverReleaseCompleteness _completeness;
  Timer? _debounce;
  final ScrollController _resultsScroll = ScrollController(
    debugLabel: 'discover-search-results',
  );
  late final List<FocusNode> _keyboardNodes = List<FocusNode>.generate(
    _keyboardKeys.length,
    (index) => FocusNode(debugLabel: 'search-key-${_keyboardKeys[index].name}'),
  );
  final List<FocusNode> _filterNodes = <FocusNode>[
    FocusNode(debugLabel: 'search-filter-system'),
    FocusNode(debugLabel: 'search-filter-release'),
  ];
  final FocusNode _pagingNode = FocusNode(debugLabel: 'search-load-more');
  List<FocusNode> _resultNodes = <FocusNode>[];
  List<String> _resultIds = <String>[];
  int _keyboardIndex = 0;
  int _resultIndex = 0;
  int _resultColumns = 3;
  late DiscoverSearchZone _zone;
  String? _resultId;
  int _focusRecoveryGeneration = 0;
  bool _restoredInitialFocus = false;

  @override
  void initState() {
    super.initState();
    _query = widget.input.focus.query;
    _zone = widget.input.focus.zone;
    _resultId = widget.input.focus.resultId;
    _platformId = widget.input.results.requestKey?.platformId;
    _completeness =
        widget.input.results.requestKey?.completeness ??
        DiscoverReleaseCompleteness.any;
    _keyboardIndex = widget.input.focus.keyboardKeyIndex.clamp(
      0,
      _keyboardNodes.length - 1,
    );
    for (final node in _keyboardNodes) {
      node.addListener(_rememberKeyboardFocus);
    }
    for (final node in _filterNodes) {
      node.addListener(_rememberResultsFocus);
    }
    _resultsScroll.addListener(_onResultsScroll);
    _syncResults();
    WidgetsBinding.instance.addPostFrameCallback((_) => _restoreInitialFocus());
  }

  @override
  void didUpdateWidget(covariant DiscoverSearchBoundary oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!identical(oldWidget.input.results.items, widget.input.results.items)) {
      _syncResults(recoverCompletedReplacement: true);
    }
    if (_platformId != null &&
        widget.input.systems.state == DiscoverLoadState.ready &&
        !widget.input.systems.items.any((system) => system.id == _platformId)) {
      _platformId = null;
      _debounce?.cancel();
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted && _query.trim().isNotEmpty) _search();
      });
    }
  }

  @override
  void dispose() {
    _focusRecoveryGeneration++;
    _debounce?.cancel();
    _remember();
    _resultsScroll
      ..removeListener(_onResultsScroll)
      ..dispose();
    for (final node in _keyboardNodes) {
      node
        ..removeListener(_rememberKeyboardFocus)
        ..dispose();
    }
    for (final node in _filterNodes) {
      node
        ..removeListener(_rememberResultsFocus)
        ..dispose();
    }
    for (final node in _resultNodes) {
      node.dispose();
    }
    _pagingNode.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Shortcuts(
    shortcuts: const <ShortcutActivator, Intent>{
      SingleActivator(LogicalKeyboardKey.pageDown): NextCatalogSectionIntent(),
    },
    child: Actions(
      actions: <Type, Action<Intent>>{
        DirectionalFocusIntent: CallbackAction<DirectionalFocusIntent>(
          onInvoke: (intent) {
            _move(intent.direction);
            return null;
          },
        ),
        NextCatalogSectionIntent: CallbackAction<NextCatalogSectionIntent>(
          onInvoke: (_) {
            if (_zone != DiscoverSearchZone.results) {
              _focusResultsFromKeyboard();
            }
            return null;
          },
        ),
        ShowDetailsIntent: CallbackAction<ShowDetailsIntent>(
          onInvoke: (_) {
            _remember(zone: DiscoverSearchZone.results);
            _filterNodes.first.requestFocus();
            return null;
          },
        ),
        DismissIntent: CallbackAction<DismissIntent>(
          onInvoke: (_) {
            widget.callbacks.onBack();
            return null;
          },
        ),
      },
      child: Focus(
        canRequestFocus: false,
        onKeyEvent: _onKey,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            _DiscoverQueryBar(
              query: _query,
              results: widget.input.results,
              onBack: widget.callbacks.onBack,
            ),
            Expanded(
              child: Padding(
                padding: EdgeInsets.fromLTRB(
                  context.layout.surface.frameMargin,
                  context.layout.md,
                  context.layout.surface.frameMargin,
                  context.layout.sm,
                ),
                child: LayoutBuilder(
                  builder: (context, constraints) {
                    final discover = context.layout.discover;
                    final keyboardWidth =
                        (constraints.maxWidth * discover.searchKeyboardFraction)
                            .clamp(360.0, 520.0);
                    return Row(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: <Widget>[
                        SizedBox(
                          width: keyboardWidth,
                          child: _buildKeyboardPane(),
                        ),
                        SizedBox(width: discover.searchZoneGap / 2),
                        VerticalDivider(
                          color: context.consoleColors.hairline,
                          width: context.layout.hairlineStroke,
                          thickness: context.layout.hairlineStroke,
                        ),
                        SizedBox(width: discover.searchZoneGap / 2),
                        Expanded(child: _buildResultsPane()),
                      ],
                    );
                  },
                ),
              ),
            ),
            const ConsoleFooterBar(
              hints: <ConsoleHint>[
                ConsoleHint(
                  glyph: 'Type',
                  gamepadGlyph: '⌨',
                  label: 'Keyboard',
                ),
                ConsoleHint(
                  glyph: ConsoleHintGlyphs.navigate,
                  gamepadGlyph: 'D-PAD',
                  label: 'Navigate',
                ),
                ConsoleHint(
                  glyph: 'PgDn',
                  gamepadGlyph: 'RB',
                  label: 'Results',
                ),
                ConsoleHint(glyph: 'Y', gamepadGlyph: 'Y', label: 'Filters'),
                ConsoleHint(
                  glyph: ConsoleHintGlyphs.confirm,
                  gamepadGlyph: 'A',
                  label: 'Select',
                ),
                ConsoleHint(glyph: 'Esc', gamepadGlyph: 'B', label: 'Close'),
              ],
            ),
          ],
        ),
      ),
    ),
  );

  Widget _buildKeyboardPane() => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: <Widget>[
      _CompactSearchKeyboard(
        rows: _keyboardRows,
        nodes: _keyboardNodes,
        onPressed: _activateKeyboardKey,
      ),
      const Spacer(),
      Align(
        alignment: Alignment.centerRight,
        child: Semantics(
          label: 'Press right to move to filters and results',
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              const _SearchKeycap(label: '→'),
              SizedBox(width: context.layout.xs),
              Text(
                'FILTERS + RESULTS',
                style: context.text.sectionLabel.copyWith(
                  color: context.consoleColors.textMuted,
                ),
              ),
              SizedBox(width: context.layout.xs),
              Icon(
                Icons.keyboard_double_arrow_right_rounded,
                color: context.consoleColors.focusBorder,
              ),
            ],
          ),
        ),
      ),
      SizedBox(height: context.layout.md),
    ],
  );

  Widget _buildResultsPane() => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: <Widget>[
      Row(
        children: <Widget>[
          Expanded(
            child: _SearchFilterButton(
              key: const ValueKey<String>('search-system-filter'),
              focusNode: _filterNodes[0],
              label: _selectedSystemLabel,
              semanticsLabel: 'System filter',
              onPressed: _cycleSystem,
            ),
          ),
          SizedBox(width: context.layout.sm),
          Expanded(
            child: _SearchFilterButton(
              key: const ValueKey<String>('search-release-filter'),
              focusNode: _filterNodes[1],
              label: _completenessLabel,
              semanticsLabel: 'Release filter',
              onPressed: _cycleCompleteness,
            ),
          ),
        ],
      ),
      SizedBox(height: context.layout.sm),
      Expanded(child: _buildResults()),
    ],
  );

  Widget _buildResults() {
    final trimmed = _query.trim();
    final results = widget.input.results;
    if (trimmed.isEmpty) {
      return const _DiscoverSearchMessage(
        icon: Icons.search_rounded,
        title: 'Search the Catalog',
        text: 'Use the keyboard or type a title.',
      );
    }
    if (results.state == DiscoverLoadState.loading && results.items.isEmpty) {
      return const _DiscoverSearchMessage(
        icon: Icons.manage_search_rounded,
        title: 'Searching',
        text: 'Reading the selected ROMD Catalog.',
        busy: true,
      );
    }
    if (results.state == DiscoverLoadState.failed && results.items.isEmpty) {
      return _DiscoverSearchMessage(
        icon: Icons.cloud_off_outlined,
        title: 'Search unavailable',
        text: 'Check the selected ROMD server and try again.',
        action: _SearchActionButton(
          key: const ValueKey<String>('search-retry'),
          label: 'Retry',
          icon: Icons.refresh_rounded,
          onPressed: _retry,
        ),
      );
    }
    if (results.items.isEmpty) {
      return _DiscoverSearchMessage(
        icon: Icons.search_off_rounded,
        title: 'No results',
        text: 'No titles match "$trimmed" and these filters.',
      );
    }

    return LayoutBuilder(
      builder: (context, constraints) {
        _resultColumns = constraints.maxWidth >= 620 ? 3 : 2;
        final grid = GridView.builder(
          key: const ValueKey<String>('discover-search-results'),
          controller: _resultsScroll,
          padding: EdgeInsets.fromLTRB(
            context.layout.xxs,
            context.layout.xxs,
            context.layout.xxs,
            context.layout.sm,
          ),
          gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
            crossAxisCount: _resultColumns,
            crossAxisSpacing: context.layout.xs,
            mainAxisSpacing: context.layout.xs,
            mainAxisExtent: math.min(
              190,
              math.max(158, (constraints.maxHeight - context.layout.xs) / 2),
            ),
          ),
          itemCount: results.items.length,
          itemBuilder: (context, index) {
            final game = results.items[index];
            return _SearchResultCard(
              key: ValueKey<String>('search-result-${game.id}'),
              game: game,
              focusNode: _resultNodes[index],
              heroTag: 'search#card$index',
              dimmed: results.state == DiscoverLoadState.loading,
              onFocused: () {
                _resultIndex = index;
                _remember(resultId: game.id, zone: DiscoverSearchZone.results);
              },
              onPressed: () =>
                  widget.callbacks.onOpenGame(game, 'search#card$index'),
            );
          },
        );

        final showPaging =
            results.hasNextPage || results.paging || results.pagingFailed;
        return Stack(
          children: <Widget>[
            Column(
              children: <Widget>[
                Expanded(child: grid),
                if (showPaging)
                  Padding(
                    padding: EdgeInsets.only(top: context.layout.xxs),
                    child: _SearchActionButton(
                      key: const ValueKey<String>('search-load-more'),
                      focusNode: _pagingNode,
                      label: results.paging
                          ? 'Loading more'
                          : results.pagingFailed
                          ? 'Retry loading'
                          : 'Load more',
                      icon: results.paging
                          ? null
                          : results.pagingFailed
                          ? Icons.refresh_rounded
                          : Icons.expand_more_rounded,
                      busy: results.paging,
                      onPressed: widget.callbacks.onLoadMore,
                    ),
                  ),
              ],
            ),
            if (results.state == DiscoverLoadState.loading)
              Positioned(
                top: 0,
                left: 0,
                right: 0,
                child: LinearProgressIndicator(
                  minHeight: context.layout.hairlineStroke * 2,
                ),
              ),
            if (results.state == DiscoverLoadState.failed)
              Positioned(
                top: context.layout.xs,
                right: context.layout.xs,
                child: _SearchActionButton(
                  key: const ValueKey<String>('search-inline-retry'),
                  label: 'Retry search',
                  icon: Icons.refresh_rounded,
                  onPressed: _retry,
                ),
              ),
          ],
        );
      },
    );
  }

  String get _selectedSystemLabel {
    final platformId = _platformId;
    if (platformId == null) return 'ALL SYSTEMS';
    final match = widget.input.systems.items.where(
      (system) => system.id == platformId,
    );
    return match.isEmpty ? 'ALL SYSTEMS' : match.first.shortName.toUpperCase();
  }

  String get _completenessLabel => switch (_completeness) {
    DiscoverReleaseCompleteness.any => 'ANY RELEASE',
    DiscoverReleaseCompleteness.complete => 'COMPLETE',
    DiscoverReleaseCompleteness.partial => 'PARTIAL',
  };

  void _activateKeyboardKey(int index) {
    final key = _keyboardKeys[index];
    switch (key.action) {
      case _KeyboardAction.character:
        _appendChar(key.value!);
      case _KeyboardAction.space:
        _appendChar(' ');
      case _KeyboardAction.backspace:
        _backspace();
      case _KeyboardAction.clear:
        _clear();
    }
  }

  void _appendChar(String char) => _setQuery(_query + char.toLowerCase());

  void _backspace() {
    if (_query.isNotEmpty) {
      _setQuery(_query.substring(0, _query.length - 1));
    }
  }

  void _clear() => _setQuery('');

  void _setQuery(String query) {
    setState(() => _query = query);
    _debounce?.cancel();
    _remember();
    final trimmed = query.trim();
    if (trimmed.isEmpty) return;
    _debounce = Timer(const Duration(milliseconds: 300), _search);
  }

  void _search() => widget.callbacks.onSearch(_request);

  void _retry() => widget.callbacks.onRetry(_request);

  DiscoverSearchRequest get _request => DiscoverSearchRequest(
    query: _query.trim(),
    platformId: _platformId,
    completeness: _completeness,
  );

  void _cycleSystem() {
    final systems = widget.input.systems.items;
    final values = <String?>[null, for (final system in systems) system.id];
    final current = values.indexOf(_platformId);
    setState(() => _platformId = values[(current + 1) % values.length]);
    _searchAfterFilterChange();
  }

  void _cycleCompleteness() {
    const values = DiscoverReleaseCompleteness.values;
    setState(() {
      _completeness = values[(_completeness.index + 1) % values.length];
    });
    _searchAfterFilterChange();
  }

  void _searchAfterFilterChange() {
    _debounce?.cancel();
    if (_query.trim().isNotEmpty) _search();
  }

  KeyEventResult _onKey(FocusNode node, KeyEvent event) {
    if (event is! KeyDownEvent && event is! KeyRepeatEvent) {
      return KeyEventResult.ignored;
    }
    if (event.logicalKey == LogicalKeyboardKey.backspace) {
      _backspace();
      return KeyEventResult.handled;
    }
    if (event.logicalKey == LogicalKeyboardKey.escape) {
      widget.callbacks.onBack();
      return KeyEventResult.handled;
    }
    final char = event.character;
    if (char != null && char.length == 1) {
      final code = char.codeUnitAt(0);
      if (code >= 0x20 && code != 0x7f) {
        _appendChar(char);
        return KeyEventResult.handled;
      }
    }
    return KeyEventResult.ignored;
  }

  void _move(TraversalDirection direction) {
    final keyboardIndex = _keyboardNodes.indexWhere((node) => node.hasFocus);
    if (keyboardIndex >= 0) {
      _moveFromKeyboard(keyboardIndex, direction);
      return;
    }
    final filterIndex = _filterNodes.indexWhere((node) => node.hasFocus);
    if (filterIndex >= 0) {
      switch (direction) {
        case TraversalDirection.left:
          if (filterIndex == 0) {
            _focusNearestKeyboard(_filterNodes[filterIndex]);
          } else {
            _filterNodes[filterIndex - 1].requestFocus();
          }
        case TraversalDirection.right:
          if (filterIndex < _filterNodes.length - 1) {
            _filterNodes[filterIndex + 1].requestFocus();
          }
        case TraversalDirection.up:
          break;
        case TraversalDirection.down:
          _focusNearestResult(_filterNodes[filterIndex]);
      }
      return;
    }
    final resultIndex = _resultNodes.indexWhere((node) => node.hasFocus);
    if (resultIndex >= 0) {
      _moveFromResult(resultIndex, direction);
      return;
    }
    if (_pagingNode.hasFocus && direction == TraversalDirection.up) {
      _focusNearestResult(_pagingNode);
    }
  }

  void _moveFromKeyboard(int index, TraversalDirection direction) {
    final position = _keyboardPosition(index);
    final row = _keyboardRows[position.row];
    switch (direction) {
      case TraversalDirection.left:
        if (position.column > 0) {
          _keyboardNodes[index - 1].requestFocus();
        }
      case TraversalDirection.right:
        if (position.column < row.keys.length - 1) {
          _keyboardNodes[index + 1].requestFocus();
        } else {
          _focusRightPaneFromKeyboard(_keyboardNodes[index]);
        }
      case TraversalDirection.up:
        _focusKeyboardUnit(position.row - 1, position.unitCenter);
      case TraversalDirection.down:
        _focusKeyboardUnit(position.row + 1, position.unitCenter);
    }
  }

  void _moveFromResult(int index, TraversalDirection direction) {
    final column = index % _resultColumns;
    switch (direction) {
      case TraversalDirection.left:
        if (column == 0) {
          _focusNearestKeyboard(_resultNodes[index]);
        } else {
          _focusResult(index - 1);
        }
      case TraversalDirection.right:
        if (column < _resultColumns - 1 && index + 1 < _resultNodes.length) {
          _focusResult(index + 1);
        }
      case TraversalDirection.up:
        if (index < _resultColumns) {
          _focusNearestFilter(_resultNodes[index]);
        } else {
          _focusResult(index - _resultColumns);
        }
      case TraversalDirection.down:
        final below = index + _resultColumns;
        if (below < _resultNodes.length) {
          _focusResult(below);
        } else if (_hasFocusablePagingAction) {
          _pagingNode.requestFocus();
        }
    }
  }

  void _focusKeyboardUnit(int rowIndex, double unitCenter) {
    if (rowIndex < 0 || rowIndex >= _keyboardRows.length) return;
    var flatIndex = 0;
    for (var row = 0; row < rowIndex; row++) {
      flatIndex += _keyboardRows[row].keys.length;
    }
    final row = _keyboardRows[rowIndex];
    var start = 0.0;
    var best = 0;
    var bestDistance = double.infinity;
    for (var index = 0; index < row.keys.length; index++) {
      final center = start + row.keys[index].span / 2;
      final distance = (center - unitCenter).abs();
      if (distance < bestDistance) {
        best = index;
        bestDistance = distance;
      }
      start += row.keys[index].span;
    }
    _keyboardNodes[flatIndex + best].requestFocus();
  }

  ({int row, int column, double unitCenter}) _keyboardPosition(int flatIndex) {
    var cursor = 0;
    for (var rowIndex = 0; rowIndex < _keyboardRows.length; rowIndex++) {
      final row = _keyboardRows[rowIndex];
      var unitStart = 0.0;
      for (var column = 0; column < row.keys.length; column++) {
        if (cursor == flatIndex) {
          return (
            row: rowIndex,
            column: column,
            unitCenter: unitStart + row.keys[column].span / 2,
          );
        }
        unitStart += row.keys[column].span;
        cursor++;
      }
    }
    return (row: 0, column: 0, unitCenter: 0.5);
  }

  void _focusResultsFromKeyboard() {
    if (_resultNodes.isEmpty) {
      _filterNodes.first.requestFocus();
      return;
    }
    _focusNearestResult(_keyboardNodes[_keyboardIndex]);
  }

  void _focusRightPaneFromKeyboard(FocusNode source) {
    final entryNodes = <FocusNode>[
      ..._filterNodes,
      for (var index = 0; index < _resultNodes.length; index += _resultColumns)
        _resultNodes[index],
      if (_hasFocusablePagingAction) _pagingNode,
    ];
    (_nearestNode(source, entryNodes) ?? _filterNodes.first).requestFocus();
  }

  bool get _hasFocusablePagingAction {
    final results = widget.input.results;
    return !results.paging && (results.hasNextPage || results.pagingFailed);
  }

  void _focusNearestKeyboard(FocusNode source) {
    final nearest = _nearestNode(source, _keyboardNodes);
    (nearest ?? _keyboardNodes[_keyboardIndex]).requestFocus();
  }

  void _focusNearestResult(FocusNode source) {
    if (_resultNodes.isEmpty) return;
    final nearest = _nearestNode(source, _resultNodes);
    (nearest ?? _resultNodes[_resultIndex]).requestFocus();
  }

  void _focusNearestFilter(FocusNode source) {
    final nearest = _nearestNode(source, _filterNodes);
    (nearest ?? _filterNodes.first).requestFocus();
  }

  FocusNode? _nearestNode(FocusNode source, List<FocusNode> candidates) {
    if (source.context == null) return null;
    final sourceRect = source.rect;
    if (sourceRect == Rect.zero) return null;
    FocusNode? best;
    var bestVerticalDistance = double.infinity;
    var bestHorizontalDistance = double.infinity;
    for (final candidate in candidates) {
      // GridView only attaches focus nodes for lazily built, visible cards.
      // Reading rect on an off-screen node asserts in Flutter's focus manager.
      if (candidate.context == null) continue;
      final rect = candidate.rect;
      if (rect == Rect.zero) continue;
      final verticalDistance = (rect.center.dy - sourceRect.center.dy).abs();
      final horizontalDistance = (rect.center.dx - sourceRect.center.dx).abs();
      if (verticalDistance < bestVerticalDistance ||
          (verticalDistance == bestVerticalDistance &&
              horizontalDistance < bestHorizontalDistance)) {
        best = candidate;
        bestVerticalDistance = verticalDistance;
        bestHorizontalDistance = horizontalDistance;
      }
    }
    return best;
  }

  void _focusResult(int index) {
    if (_resultNodes.isEmpty) return;
    _resultNodes[index.clamp(0, _resultNodes.length - 1)].requestFocus();
  }

  void _syncResults({bool recoverCompletedReplacement = false}) {
    final previousIds = _resultIds;
    final previousNodes = _resultNodes;
    final focusedIndex = previousNodes.indexWhere((node) => node.hasFocus);
    final rememberedIndex = _resultId == null
        ? -1
        : previousIds.indexOf(_resultId!);
    final previousIndex = focusedIndex >= 0
        ? focusedIndex
        : rememberedIndex >= 0
        ? rememberedIndex
        : _resultIndex;
    final previousNode =
        previousIndex >= 0 && previousIndex < previousNodes.length
        ? previousNodes[previousIndex]
        : null;
    final nearestKeyboard = previousNode == null
        ? null
        : _nearestNode(previousNode, _keyboardNodes);
    final nearestKeyboardIndex = nearestKeyboard == null
        ? _keyboardIndex
        : _keyboardNodes.indexOf(nearestKeyboard);

    final existing = <String, FocusNode>{
      for (var index = 0; index < previousIds.length; index++)
        previousIds[index]: previousNodes[index],
    };
    final nextIds = widget.input.results.items
        .map((game) => game.id)
        .toList(growable: false);
    final nodes = <FocusNode>[
      for (final game in widget.input.results.items)
        existing.remove(game.id) ??
            FocusNode(debugLabel: 'search-result-${game.id}'),
    ];
    final focusedResultRemoved =
        recoverCompletedReplacement &&
        widget.input.results.state == DiscoverLoadState.ready &&
        _zone == DiscoverSearchZone.results &&
        _resultId != null &&
        !nextIds.contains(_resultId);

    _resultIds = nextIds;
    _resultNodes = nodes;

    if (focusedResultRemoved) {
      final generation = ++_focusRecoveryGeneration;
      if (nodes.isNotEmpty) {
        _resultIndex = previousIndex.clamp(0, nodes.length - 1);
        _resultId = nextIds[_resultIndex];
        _zone = DiscoverSearchZone.results;
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (!mounted || generation != _focusRecoveryGeneration) return;
          _focusResult(_resultIndex);
        });
      } else {
        _keyboardIndex = nearestKeyboardIndex.clamp(
          0,
          _keyboardNodes.length - 1,
        );
        _resultId = null;
        _zone = DiscoverSearchZone.keyboard;
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (!mounted || generation != _focusRecoveryGeneration) return;
          _keyboardNodes[_keyboardIndex].requestFocus();
        });
      }
    }

    for (final node in existing.values) {
      node.dispose();
    }
    final rememberedId = _resultId;
    if (rememberedId != null) {
      final index = _resultIds.indexOf(rememberedId);
      if (index >= 0) _resultIndex = index;
    }
  }

  void _restoreInitialFocus() {
    if (!mounted || _restoredInitialFocus) return;
    _restoredInitialFocus = true;
    if (_resultsScroll.hasClients) {
      _resultsScroll.jumpTo(
        widget.input.focus.resultScrollOffset.clamp(
          0,
          _resultsScroll.position.maxScrollExtent,
        ),
      );
    }
    if (widget.input.focus.zone == DiscoverSearchZone.results &&
        _resultNodes.isNotEmpty) {
      _focusResult(_resultIndex);
    } else {
      _keyboardNodes[_keyboardIndex].requestFocus();
    }
  }

  void _rememberKeyboardFocus() {
    final index = _keyboardNodes.indexWhere((node) => node.hasFocus);
    if (index < 0) return;
    _keyboardIndex = index;
    _remember(zone: DiscoverSearchZone.keyboard);
  }

  void _rememberResultsFocus() {
    if (_filterNodes.any((node) => node.hasFocus)) {
      _remember(zone: DiscoverSearchZone.results);
    }
  }

  void _onResultsScroll() {
    if (!_resultsScroll.hasClients) return;
    _remember();
    final results = widget.input.results;
    if (results.hasNextPage &&
        !results.paging &&
        _resultsScroll.position.extentAfter < 180) {
      widget.callbacks.onLoadMore();
    }
  }

  void _remember({String? resultId, DiscoverSearchZone? zone}) {
    if (zone != null) _zone = zone;
    if (resultId != null) _resultId = resultId;
    widget.callbacks.onRememberFocus(
      DiscoverSearchFocusSnapshot(
        query: _query,
        zone: _zone,
        keyboardKeyIndex: _keyboardIndex,
        resultId: _resultId,
        resultScrollOffset: _resultsScroll.hasClients
            ? _resultsScroll.offset
            : widget.input.focus.resultScrollOffset,
      ),
    );
  }
}

final class _DiscoverQueryBar extends StatelessWidget {
  const _DiscoverQueryBar({
    required this.query,
    required this.results,
    required this.onBack,
  });

  final String query;
  final DiscoverGamePageSnapshot results;
  final VoidCallback onBack;

  @override
  Widget build(BuildContext context) => Padding(
    padding: EdgeInsets.fromLTRB(
      context.layout.surface.frameMargin,
      context.layout.md,
      context.layout.surface.frameMargin,
      0,
    ),
    child: Column(
      children: <Widget>[
        Row(
          children: <Widget>[
            ConsoleCircleButton(
              key: const ValueKey<String>('discover-search-back'),
              icon: Icons.close_rounded,
              onTap: onBack,
            ),
            SizedBox(width: context.layout.md),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: <Widget>[
                  Row(
                    children: <Widget>[
                      Text(
                        'SEARCH',
                        style: context.text.eyebrow.copyWith(
                          color: context.consoleColors.textFaint,
                        ),
                      ),
                      Text(
                        ' CATALOG',
                        style: context.text.eyebrow.copyWith(
                          color: context.consoleColors.textFaint,
                        ),
                      ),
                    ],
                  ),
                  SizedBox(height: context.layout.xxs),
                  Row(
                    children: <Widget>[
                      Flexible(
                        child: Text(
                          query.isEmpty ? 'Type a title' : query,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: context.text.pageHeading.copyWith(
                            color: query.isEmpty
                                ? context.consoleColors.textFaint
                                : context.consoleColors.textStrong,
                          ),
                        ),
                      ),
                      if (query.isNotEmpty)
                        ConsoleBlinkingCaret(
                          height: context.layout.surface.headerIconSize,
                          margin: EdgeInsets.only(left: context.layout.xxs),
                        ),
                    ],
                  ),
                ],
              ),
            ),
            if (query.trim().isNotEmpty)
              _SearchCountBadge(
                count: results.items.length,
                hasMore: results.hasNextPage || results.paging,
                state: results.state,
              ),
          ],
        ),
        SizedBox(height: context.layout.sm),
        Divider(
          height: context.layout.hairlineStroke,
          thickness: context.layout.hairlineStroke,
          color: context.consoleColors.catalogAccent,
        ),
      ],
    ),
  );
}

final class _SearchCountBadge extends StatelessWidget {
  const _SearchCountBadge({
    required this.count,
    required this.hasMore,
    required this.state,
  });

  final int count;
  final bool hasMore;
  final DiscoverLoadState state;

  @override
  Widget build(BuildContext context) {
    final exact =
        state == DiscoverLoadState.ready || state == DiscoverLoadState.idle;
    final style = context.text.sectionLabel.copyWith(
      color: context.consoleColors.textStrong,
    );
    return DecoratedBox(
      key: const ValueKey<String>('search-result-count'),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(context.layout.controlRadius),
        color: context.consoleColors.panelSurface,
        border: Border.all(
          color: context.consoleColors.panelBorder,
          width: context.layout.hairlineStroke,
        ),
      ),
      child: Padding(
        padding: EdgeInsets.symmetric(
          horizontal: context.layout.md,
          vertical: context.layout.sm,
        ),
        child: exact
            ? Text.rich(
                TextSpan(
                  children: <InlineSpan>[
                    TextSpan(
                      text: '$count${hasMore ? '+' : ''}',
                      style: TextStyle(
                        color: context.consoleColors.focusBorder,
                      ),
                    ),
                    TextSpan(
                      text: count == 1 && !hasMore ? ' RESULT' : ' RESULTS',
                    ),
                  ],
                ),
                style: style,
              )
            : Text(
                state == DiscoverLoadState.loading && count == 0
                    ? 'SEARCHING'
                    : '$count SHOWN',
                style: style.copyWith(color: context.consoleColors.textMuted),
              ),
      ),
    );
  }
}

enum _KeyboardAction { character, space, backspace, clear }

@immutable
final class _KeyboardKey {
  const _KeyboardKey._({
    required this.name,
    required this.action,
    required this.span,
    this.value,
    this.icon,
  });

  const _KeyboardKey.character(String value)
    : this._(
        name: value,
        action: _KeyboardAction.character,
        span: 1,
        value: value,
      );

  const _KeyboardKey.space()
    : this._(name: 'SPACE', action: _KeyboardAction.space, span: 3);

  const _KeyboardKey.backspace()
    : this._(
        name: 'BACKSPACE',
        action: _KeyboardAction.backspace,
        span: 1,
        icon: Icons.backspace_outlined,
      );

  const _KeyboardKey.clear()
    : this._(name: 'CLEAR', action: _KeyboardAction.clear, span: 2);

  final String name;
  final String? value;
  final _KeyboardAction action;
  final int span;
  final IconData? icon;
}

@immutable
final class _KeyboardRow {
  const _KeyboardRow(this.keys);

  final List<_KeyboardKey> keys;
}

final class _CompactSearchKeyboard extends StatelessWidget {
  const _CompactSearchKeyboard({
    required this.rows,
    required this.nodes,
    required this.onPressed,
  });

  final List<_KeyboardRow> rows;
  final List<FocusNode> nodes;
  final ValueChanged<int> onPressed;

  @override
  Widget build(BuildContext context) {
    final children = <Widget>[];
    var rowStart = 0;
    for (var rowIndex = 0; rowIndex < rows.length; rowIndex++) {
      if (rowIndex > 0) {
        children.add(SizedBox(height: context.layout.keyboard.gap));
      }
      final row = rows[rowIndex];
      final currentRowStart = rowStart;
      children.add(
        Row(
          children: <Widget>[
            for (
              var column = 0;
              column < row.keys.length;
              column++
            ) ...<Widget>[
              if (column > 0) SizedBox(width: context.layout.keyboard.gap),
              Expanded(
                flex: row.keys[column].span,
                child: _SearchKeyboardButton(
                  key: ValueKey<String>('search-key-${row.keys[column].name}'),
                  keyboardKey: row.keys[column],
                  focusNode: nodes[currentRowStart + column],
                  onPressed: () => onPressed(currentRowStart + column),
                ),
              ),
            ],
          ],
        ),
      );
      rowStart += row.keys.length;
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: children,
    );
  }
}

final class _SearchKeyboardButton extends StatelessWidget {
  const _SearchKeyboardButton({
    required this.keyboardKey,
    required this.focusNode,
    required this.onPressed,
    super.key,
  });

  final _KeyboardKey keyboardKey;
  final FocusNode focusNode;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) => ConsoleFocusable(
    focusNode: focusNode,
    semanticLabel: keyboardKey.name,
    onPressed: onPressed,
    builder: (context, focused) => ConsoleFocusRing(
      focused: focused,
      borderRadius: context.layout.controlRadius,
      gap: 0,
      child: AnimatedContainer(
        duration: context.motion.resolve(context, context.motion.focus),
        height: context.layout.keyboard.keyHeight,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(context.layout.controlRadius),
          color: focused
              ? context.consoleColors.focusFill
              : context.consoleColors.panelSurface,
        ),
        child: keyboardKey.icon != null
            ? Icon(
                keyboardKey.icon,
                size: context.layout.keyboard.iconSize,
                color: focused
                    ? context.consoleColors.textStrong
                    : context.consoleColors.textMuted,
              )
            : Text(
                keyboardKey.name,
                style:
                    (keyboardKey.name.length > 1
                            ? context.text.sectionLabel
                            : context.text.supportingTitle)
                        .copyWith(
                          color: focused
                              ? context.consoleColors.textStrong
                              : context.consoleColors.textMuted,
                        ),
              ),
      ),
    ),
  );
}

final class _SearchFilterButton extends StatelessWidget {
  const _SearchFilterButton({
    required this.focusNode,
    required this.label,
    required this.semanticsLabel,
    required this.onPressed,
    super.key,
  });

  final FocusNode focusNode;
  final String label;
  final String semanticsLabel;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) => ConsoleFocusable(
    focusNode: focusNode,
    semanticLabel: '$semanticsLabel: $label',
    onPressed: onPressed,
    builder: (context, focused) => ConsoleFocusRing(
      focused: focused,
      borderRadius: context.layout.controlRadius,
      gap: 0,
      child: AnimatedContainer(
        duration: context.motion.resolve(context, context.motion.focus),
        height: context.layout.keyboard.keyHeight,
        padding: EdgeInsets.symmetric(horizontal: context.layout.sm),
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(context.layout.controlRadius),
          color: focused
              ? context.consoleColors.focusFill
              : context.consoleColors.panelSurface,
        ),
        child: Row(
          children: <Widget>[
            Expanded(
              child: Text(
                label,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: context.text.sectionLabel.copyWith(
                  color: focused
                      ? context.consoleColors.textStrong
                      : context.consoleColors.textMuted,
                ),
              ),
            ),
            Icon(
              Icons.keyboard_arrow_down_rounded,
              color: focused
                  ? context.consoleColors.textStrong
                  : context.consoleColors.textMuted,
            ),
          ],
        ),
      ),
    ),
  );
}

final class _SearchResultCard extends StatelessWidget {
  const _SearchResultCard({
    required this.game,
    required this.focusNode,
    required this.heroTag,
    required this.dimmed,
    required this.onFocused,
    required this.onPressed,
    super.key,
  });

  final ConsoleGame game;
  final FocusNode focusNode;
  final String heroTag;
  final bool dimmed;
  final VoidCallback onFocused;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    final platform = platformPresentationFor(
      context,
      platformId: game.platformId,
      platformName: game.platformName,
    );
    return ConsoleFocusable(
      focusNode: focusNode,
      semanticLabel: '${game.title}. ${platform.label}.',
      scrollIntoViewOnFocus: true,
      onFocusChange: (focused) {
        if (focused) onFocused();
      },
      onPressed: onPressed,
      builder: (context, focused) => ConsoleFocusRing(
        focused: focused,
        borderRadius: context.layout.panelRadius,
        gap: 0,
        child: AnimatedOpacity(
          duration: context.motion.resolve(
            context,
            context.motion.contentTransition,
          ),
          opacity: dimmed ? 0.58 : 1,
          child: ClipRRect(
            borderRadius: BorderRadius.circular(context.layout.panelRadius),
            child: ColoredBox(
              color: context.consoleColors.panelSurface,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  Expanded(
                    child: Hero(
                      tag: heroTag,
                      child: CoverArt(
                        coverUrl: game.poster?.url ?? game.coverUrl,
                        contain: game.poster?.contain ?? true,
                        platformId: game.platformId,
                        platformName: game.platformName,
                        title: game.title,
                        borderRadius: 0,
                      ),
                    ),
                  ),
                  Padding(
                    padding: EdgeInsets.fromLTRB(
                      context.layout.xs,
                      context.layout.xxs,
                      context.layout.xs,
                      context.layout.xxs,
                    ),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: <Widget>[
                        Text(
                          game.title,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: context.text.supportingTitle.copyWith(
                            color: focused
                                ? context.consoleColors.textStrong
                                : context.consoleColors.textBody,
                          ),
                        ),
                        Text(
                          <String>[
                            platform.shortCode,
                            if (game.releaseYear case final year?)
                              year.toString(),
                          ].join(' · '),
                          maxLines: 1,
                          style: context.text.metadata.copyWith(
                            color: context.consoleColors.textMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

final class _SearchActionButton extends StatelessWidget {
  const _SearchActionButton({
    required this.label,
    required this.onPressed,
    this.icon,
    this.focusNode,
    this.busy = false,
    super.key,
  });

  final String label;
  final IconData? icon;
  final FocusNode? focusNode;
  final bool busy;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) => ConsoleFocusable(
    focusNode: focusNode,
    enabled: !busy,
    semanticLabel: label,
    onPressed: onPressed,
    builder: (context, focused) => ConsoleFocusRing(
      focused: focused,
      borderRadius: context.layout.controlRadius,
      gap: 0,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: focused
              ? context.consoleColors.focusFill
              : context.consoleColors.controlRestFill,
          borderRadius: BorderRadius.circular(context.layout.controlRadius),
        ),
        child: Padding(
          padding: context.layout.surface.compactControlPadding,
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              if (busy)
                SizedBox.square(
                  dimension: context.layout.progressIndicatorSm,
                  child: CircularProgressIndicator(
                    strokeWidth: context.layout.focusStroke,
                  ),
                )
              else if (icon != null)
                Icon(
                  icon,
                  size: context.layout.iconSm,
                  color: context.consoleColors.textMuted,
                ),
              if (busy || icon != null) SizedBox(width: context.layout.xs),
              Text(
                label,
                style: context.text.sectionLabel.copyWith(
                  color: focused
                      ? context.consoleColors.textStrong
                      : context.consoleColors.textMuted,
                ),
              ),
            ],
          ),
        ),
      ),
    ),
  );
}

final class _DiscoverSearchMessage extends StatelessWidget {
  const _DiscoverSearchMessage({
    required this.icon,
    required this.title,
    required this.text,
    this.busy = false,
    this.action,
  });

  final IconData icon;
  final String title;
  final String text;
  final bool busy;
  final Widget? action;

  @override
  Widget build(BuildContext context) => Center(
    child: Column(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        Icon(
          icon,
          size: context.layout.iconLg,
          color: context.consoleColors.textFaint,
        ),
        SizedBox(height: context.layout.sm),
        Text(
          title,
          style: context.text.sectionHeading.copyWith(
            color: context.consoleColors.textStrong,
          ),
        ),
        SizedBox(height: context.layout.xxs),
        Text(
          text,
          textAlign: TextAlign.center,
          style: context.text.body.copyWith(
            color: context.consoleColors.textMuted,
          ),
        ),
        if (busy) ...<Widget>[
          SizedBox(height: context.layout.md),
          const CircularProgressIndicator(),
        ],
        if (action case final action?) ...<Widget>[
          SizedBox(height: context.layout.md),
          action,
        ],
      ],
    ),
  );
}

final class _SearchKeycap extends StatelessWidget {
  const _SearchKeycap({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      borderRadius: BorderRadius.circular(context.layout.hints.keycapRadius),
      border: Border.all(color: context.consoleColors.borderStrong),
    ),
    child: Padding(
      padding: EdgeInsets.symmetric(
        horizontal: context.layout.xs,
        vertical: context.layout.xxs,
      ),
      child: Text(
        label,
        style: context.text.metadataStrong.copyWith(
          color: context.consoleColors.textStrong,
        ),
      ),
    ),
  );
}
