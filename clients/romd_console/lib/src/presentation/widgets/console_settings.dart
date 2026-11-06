import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../theme/console_theme_context.dart';
import 'console_clock.dart';
import 'console_hint_bar.dart';
import 'console_page_band.dart';

/// Shared layout and interaction contract for every Settings surface.
///
/// The shell owns the compact page band, scrolling, dismissal, and the footer
/// hint bar. Detail screens supply their title, content, an optional lead-in
/// [description], and an optional right-hand [contextPanel] beside the
/// left-anchored utility column.
final class ConsoleSettingsShell extends StatefulWidget {
  const ConsoleSettingsShell({
    required this.title,
    required this.onBack,
    required this.child,
    this.eyebrow,
    this.description,
    this.contentMaxWidth,
    this.contextPanel,
    this.hints = _defaultHints,
    this.showClock = true,
    this.clockNow,
    super.key,
  });

  static const List<ConsoleHint> _defaultHints = <ConsoleHint>[
    ConsoleHint(glyph: '↕', gamepadGlyph: 'D-PAD', label: 'Navigate'),
    ConsoleHint(
      glyph: ConsoleHintGlyphs.confirm,
      gamepadGlyph: 'A',
      label: 'Select',
    ),
    ConsoleHint(glyph: 'Esc', gamepadGlyph: 'B', label: 'Back'),
  ];

  final String title;
  final String? eyebrow;
  final String? description;
  final VoidCallback onBack;
  final Widget child;
  final double? contentMaxWidth;

  /// Optional contextual panel (identity, totals, guidance) laid out to the
  /// right of the utility column on wide surfaces; omitted below the settings
  /// context-panel breakpoint. Never focusable content.
  final Widget? contextPanel;
  final List<ConsoleHint> hints;
  final bool showClock;
  final DateTime Function()? clockNow;

  @override
  State<ConsoleSettingsShell> createState() => _ConsoleSettingsShellState();
}

final class _ConsoleSettingsShellState extends State<ConsoleSettingsShell> {
  final ScrollController _scroll = ScrollController(
    debugLabel: 'settings-shell',
  );
  final FocusScopeNode _shellScope = FocusScopeNode(
    debugLabel: 'settings-shell',
  );
  bool _scrolled = false;

  @override
  void initState() {
    super.initState();
    _scroll.addListener(_handleScroll);
    // Controller-first guarantee: when a state renders nothing focusable
    // (plain empty/loading panels), the shell's scope parks focus so Esc/B
    // dismissal keeps working. A scope with no focused child still honors a
    // later child's autofocus, so real content claims always win.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      Future<void>.microtask(() {
        if (!mounted) return;
        final primary = FocusManager.instance.primaryFocus;
        final insideShell =
            primary != null &&
            (primary == _shellScope || primary.ancestors.contains(_shellScope));
        if (!insideShell) _shellScope.requestFocus();
      });
    });
  }

  @override
  void dispose() {
    _scroll.removeListener(_handleScroll);
    _scroll.dispose();
    _shellScope.dispose();
    super.dispose();
  }

  void _handleScroll() {
    final scrolled = _scroll.hasClients && _scroll.offset > 0;
    if (scrolled != _scrolled) setState(() => _scrolled = scrolled);
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    final onBack = widget.onBack;

    final column = ConstrainedBox(
      constraints: BoxConstraints(
        maxWidth: widget.contentMaxWidth ?? layout.settings.contentMaxWidth,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          if (widget.description case final description?) ...<Widget>[
            Text(
              description,
              style: context.text.bodyCompact.copyWith(color: colors.textMuted),
            ),
            SizedBox(height: layout.lg),
          ],
          widget.child,
        ],
      ),
    );

    return _ConsoleSettingsDismissScope(
      onBack: onBack,
      child: CallbackShortcuts(
        bindings: <ShortcutActivator, VoidCallback>{
          const SingleActivator(LogicalKeyboardKey.escape): onBack,
        },
        child: Actions(
          actions: <Type, Action<Intent>>{
            DismissIntent: CallbackAction<DismissIntent>(
              onInvoke: (_) {
                onBack();
                return null;
              },
            ),
          },
          child: FocusTraversalGroup(
            policy: ReadingOrderTraversalPolicy(),
            child: FocusScope(
              node: _shellScope,
              child: Scaffold(
                backgroundColor: Colors.transparent,
                body: SafeArea(
                  child: Column(
                    children: <Widget>[
                      ConsolePageBand(
                        title: widget.title,
                        eyebrow: widget.eyebrow?.toUpperCase(),
                        anchored: _scrolled,
                        trailing: <Widget>[
                          if (widget.showClock)
                            ConsoleClock(now: widget.clockNow),
                        ],
                      ),
                      Expanded(
                        child: LayoutBuilder(
                          builder: (context, constraints) {
                            final contextPanel = widget.contextPanel;
                            final showPanel =
                                contextPanel != null &&
                                constraints.maxWidth >=
                                    layout.settings.contextPanelBreakpoint;
                            final scrollable = SingleChildScrollView(
                              controller: _scroll,
                              padding: EdgeInsets.fromLTRB(
                                layout.screenGutter,
                                layout.lg,
                                showPanel ? 0 : layout.screenGutter,
                                layout.lg,
                              ),
                              child: Align(
                                alignment: Alignment.topLeft,
                                child: column,
                              ),
                            );
                            if (!showPanel) return scrollable;
                            return Row(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: <Widget>[
                                Expanded(child: scrollable),
                                SizedBox(width: layout.lg),
                                Padding(
                                  padding: EdgeInsets.only(
                                    top: layout.lg,
                                    right: layout.screenGutter,
                                  ),
                                  child: SizedBox(
                                    width: layout.settings.contextPanelWidth,
                                    child: ExcludeFocus(child: contextPanel),
                                  ),
                                ),
                              ],
                            );
                          },
                        ),
                      ),
                      ConsoleFooterBar(hints: widget.hints),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

final class _ConsoleSettingsDismissScope extends InheritedWidget {
  const _ConsoleSettingsDismissScope({
    required this.onBack,
    required super.child,
    super.key,
  });

  final VoidCallback onBack;

  static VoidCallback? maybeOf(BuildContext context) => context
      .dependOnInheritedWidgetOfExactType<_ConsoleSettingsDismissScope>()
      ?.onBack;

  @override
  bool updateShouldNotify(_ConsoleSettingsDismissScope oldWidget) =>
      onBack != oldWidget.onBack;
}

/// A stable category in a hierarchical Settings page.
@immutable
final class ConsoleSettingsCategory {
  const ConsoleSettingsCategory({
    required this.id,
    required this.icon,
    required this.title,
    required this.child,
    this.subtitle,
  });

  final Object id;
  final IconData icon;
  final String title;
  final String? subtitle;
  final Widget child;
}

/// A Settings page with a category rail and a controller-first detail pane.
///
/// Wide layouts keep both panes visible. Compact layouts and large text use a
/// stacked category/detail flow so neither column is compressed beyond its
/// useful reading width.
final class ConsoleSettingsHierarchicalPage extends StatelessWidget {
  const ConsoleSettingsHierarchicalPage({
    required this.title,
    required this.onBack,
    required this.categories,
    this.initialCategoryIndex = 0,
    this.onCategoryChanged,
    this.eyebrow,
    this.description,
    this.showClock = true,
    this.clockNow,
    super.key,
  });

  final String title;
  final VoidCallback onBack;
  final List<ConsoleSettingsCategory> categories;
  final int initialCategoryIndex;
  final ValueChanged<int>? onCategoryChanged;
  final String? eyebrow;
  final String? description;
  final bool showClock;
  final DateTime Function()? clockNow;

  @override
  Widget build(BuildContext context) => ConsoleSettingsShell(
    title: title,
    eyebrow: eyebrow,
    description: description,
    onBack: onBack,
    contentMaxWidth: context.layout.settings.hierarchyMaxWidth,
    showClock: showClock,
    clockNow: clockNow,
    child: ConsoleSettingsHierarchy(
      categories: categories,
      initialCategoryIndex: initialCategoryIndex,
      onCategoryChanged: onCategoryChanged,
    ),
  );
}

/// The reusable rail/detail portion of a hierarchical Settings page.
final class ConsoleSettingsHierarchy extends StatefulWidget {
  const ConsoleSettingsHierarchy({
    required this.categories,
    this.initialCategoryIndex = 0,
    this.onCategoryChanged,
    super.key,
  });

  final List<ConsoleSettingsCategory> categories;
  final int initialCategoryIndex;
  final ValueChanged<int>? onCategoryChanged;

  @override
  State<ConsoleSettingsHierarchy> createState() =>
      _ConsoleSettingsHierarchyState();
}

final class _ConsoleSettingsHierarchyState
    extends State<ConsoleSettingsHierarchy> {
  late List<FocusNode> _categoryNodes;
  late List<FocusScopeNode> _detailScopes;
  late List<FocusNode> _detailEntryNodes;
  late List<FocusNode?> _lastDetailFocus;
  late int _selectedIndex;
  bool _compactDetailVisible = false;
  bool _railHasFocus = true;
  bool _compact = false;

  @override
  void initState() {
    super.initState();
    _selectedIndex = _normalizedIndex(widget.initialCategoryIndex);
    _createFocusNodes();
  }

  int _normalizedIndex(int requested) {
    if (widget.categories.isEmpty) return 0;
    return requested.clamp(0, widget.categories.length - 1);
  }

  void _createFocusNodes() {
    _categoryNodes = List<FocusNode>.generate(
      widget.categories.length,
      (index) => FocusNode(
        debugLabel: 'Settings category ${widget.categories[index].id}',
      ),
    );
    _detailScopes = List<FocusScopeNode>.generate(
      widget.categories.length,
      (index) => FocusScopeNode(
        debugLabel: 'Settings detail ${widget.categories[index].id}',
      ),
    );
    _detailEntryNodes = List<FocusNode>.generate(
      widget.categories.length,
      (index) => FocusNode(
        debugLabel: 'Settings detail entry ${widget.categories[index].id}',
      ),
    );
    _lastDetailFocus = List<FocusNode?>.filled(widget.categories.length, null);
  }

  void _disposeFocusNodes() {
    for (final node in _categoryNodes) {
      node.dispose();
    }
    for (final node in _detailScopes) {
      node.dispose();
    }
    for (final node in _detailEntryNodes) {
      node.dispose();
    }
  }

  @override
  void didUpdateWidget(ConsoleSettingsHierarchy oldWidget) {
    super.didUpdateWidget(oldWidget);
    final oldSelectedId = oldWidget.categories.isEmpty
        ? null
        : oldWidget.categories[_selectedIndex].id;
    final structureChanged =
        oldWidget.categories.length != widget.categories.length ||
        List<bool>.generate(
          widget.categories.length,
          (index) =>
              oldWidget.categories[index].id != widget.categories[index].id,
        ).any((changed) => changed);
    if (!structureChanged) return;
    _disposeFocusNodes();
    _createFocusNodes();
    final retainedIndex = oldSelectedId == null
        ? -1
        : widget.categories.indexWhere(
            (category) => category.id == oldSelectedId,
          );
    _selectedIndex = retainedIndex >= 0
        ? retainedIndex
        : _normalizedIndex(widget.initialCategoryIndex);
    _compactDetailVisible = false;
    _railHasFocus = true;
  }

  @override
  void dispose() {
    _disposeFocusNodes();
    super.dispose();
  }

  void _selectCategory(int index) {
    if (_selectedIndex == index) return;
    setState(() => _selectedIndex = index);
    widget.onCategoryChanged?.call(index);
  }

  void _handleCategoryFocus(int index, bool focused) {
    if (!focused) return;
    if (!_railHasFocus || _selectedIndex != index) {
      setState(() {
        _railHasFocus = true;
        _selectedIndex = index;
      });
      widget.onCategoryChanged?.call(index);
    }
  }

  void _rememberDetailFocus() {
    if (widget.categories.isEmpty) return;
    final current = FocusManager.instance.primaryFocus;
    if (current != null && _detailScopes[_selectedIndex].hasFocus) {
      _lastDetailFocus[_selectedIndex] = current;
    }
  }

  void _focusDetail() {
    if (widget.categories.isEmpty) return;
    setState(() => _railHasFocus = false);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final remembered = _lastDetailFocus[_selectedIndex];
      if (remembered != null &&
          remembered.context != null &&
          remembered.canRequestFocus) {
        remembered.requestFocus();
        return;
      }
      final entry = _detailEntryNodes[_selectedIndex];
      entry.requestFocus();
      entry.nextFocus();
    });
  }

  void _enterDetail() {
    if (widget.categories.isEmpty) return;
    if (_compact && !_compactDetailVisible) {
      setState(() => _compactDetailVisible = true);
    }
    _focusDetail();
  }

  void _focusRail() {
    if (widget.categories.isEmpty) return;
    _rememberDetailFocus();
    if (_compact && _compactDetailVisible) {
      setState(() => _compactDetailVisible = false);
    } else if (!_railHasFocus) {
      setState(() => _railHasFocus = true);
    }
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _categoryNodes[_selectedIndex].requestFocus();
    });
  }

  void _moveRail(TraversalDirection direction) {
    final delta = switch (direction) {
      TraversalDirection.up => -1,
      TraversalDirection.down => 1,
      _ => 0,
    };
    if (delta == 0 || widget.categories.isEmpty) return;
    final next = (_selectedIndex + delta).clamp(
      0,
      widget.categories.length - 1,
    );
    if (next == _selectedIndex) return;
    _selectCategory(next);
    _categoryNodes[next].requestFocus();
  }

  void _handleDirection(TraversalDirection direction) {
    if (_railHasFocus) {
      if (direction == TraversalDirection.right) {
        _enterDetail();
      } else {
        _moveRail(direction);
      }
      return;
    }
    if (direction == TraversalDirection.left) {
      _focusRail();
      return;
    }
    FocusManager.instance.primaryFocus?.focusInDirection(direction);
  }

  void _dismiss(VoidCallback onBack) {
    if (_compact && _compactDetailVisible) {
      _focusRail();
    } else {
      onBack();
    }
  }

  Widget _buildRail() {
    return FocusTraversalGroup(
      policy: OrderedTraversalPolicy(),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: <Widget>[
          for (var index = 0; index < widget.categories.length; index++)
            FocusTraversalOrder(
              order: NumericFocusOrder(index.toDouble()),
              child: _ConsoleSettingsCategoryRow(
                key: ValueKey<Object>(widget.categories[index].id),
                category: widget.categories[index],
                selected: index == _selectedIndex,
                focusNode: _categoryNodes[index],
                autofocus: index == _selectedIndex,
                onFocusChange: (focused) =>
                    _handleCategoryFocus(index, focused),
                onPressed: () {
                  _selectCategory(index);
                  _enterDetail();
                },
              ),
            ),
        ],
      ),
    );
  }

  Widget _buildDetail(bool compact, VoidCallback onBack) {
    if (widget.categories.isEmpty) return const SizedBox.shrink();
    final selected = widget.categories[_selectedIndex];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: <Widget>[
        if (compact) ...<Widget>[
          Semantics(
            header: true,
            child: Text(
              selected.title,
              style: context.text.sectionHeading.copyWith(
                color: context.consoleColors.textStrong,
              ),
            ),
          ),
          SizedBox(height: context.layout.lg),
        ],
        IndexedStack(
          index: _selectedIndex,
          children: <Widget>[
            for (var index = 0; index < widget.categories.length; index++)
              _ConsoleSettingsDismissScope(
                key: ValueKey<String>(
                  'settings-detail-${widget.categories[index].id}',
                ),
                onBack: () => _dismiss(onBack),
                child: FocusScope(
                  node: _detailScopes[index],
                  child: Focus(
                    focusNode: _detailEntryNodes[index],
                    skipTraversal: true,
                    child: FocusTraversalGroup(
                      policy: ReadingOrderTraversalPolicy(),
                      child: widget.categories[index].child,
                    ),
                  ),
                ),
              ),
          ],
        ),
      ],
    );
  }

  @override
  Widget build(BuildContext context) {
    final onBack =
        _ConsoleSettingsDismissScope.maybeOf(context) ??
        () => Navigator.of(context).maybePop();
    final settings = context.layout.settings;
    return LayoutBuilder(
      builder: (context, constraints) {
        final textScale = MediaQuery.textScalerOf(context).scale(1);
        _compact =
            constraints.maxWidth < settings.hierarchyBreakpoint ||
            textScale >= settings.hierarchyTextScaleBreakpoint;
        final content = _compact
            ? (_compactDetailVisible
                  ? _buildDetail(true, onBack)
                  : _buildRail())
            : Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: <Widget>[
                  SizedBox(
                    width: settings.categoryRailWidth,
                    child: _buildRail(),
                  ),
                  SizedBox(width: settings.hierarchyPaneGap),
                  Expanded(child: _buildDetail(false, onBack)),
                ],
              );
        return Shortcuts(
          shortcuts: const <ShortcutActivator, Intent>{
            SingleActivator(LogicalKeyboardKey.escape): DismissIntent(),
            SingleActivator(LogicalKeyboardKey.gameButtonB): DismissIntent(),
          },
          child: Actions(
            actions: <Type, Action<Intent>>{
              DirectionalFocusIntent: CallbackAction<DirectionalFocusIntent>(
                onInvoke: (intent) {
                  _handleDirection(intent.direction);
                  return null;
                },
              ),
              DismissIntent: CallbackAction<DismissIntent>(
                onInvoke: (_) {
                  _dismiss(onBack);
                  return null;
                },
              ),
            },
            child: content,
          ),
        );
      },
    );
  }
}

final class _ConsoleSettingsCategoryRow extends StatefulWidget {
  const _ConsoleSettingsCategoryRow({
    required this.category,
    required this.selected,
    required this.focusNode,
    required this.autofocus,
    required this.onFocusChange,
    required this.onPressed,
    super.key,
  });

  final ConsoleSettingsCategory category;
  final bool selected;
  final FocusNode focusNode;
  final bool autofocus;
  final ValueChanged<bool> onFocusChange;
  final VoidCallback onPressed;

  @override
  State<_ConsoleSettingsCategoryRow> createState() =>
      _ConsoleSettingsCategoryRowState();
}

final class _ConsoleSettingsCategoryRowState
    extends State<_ConsoleSettingsCategoryRow> {
  bool _focused = false;

  void _handleFocusChange(bool focused) {
    if (_focused != focused) setState(() => _focused = focused);
    widget.onFocusChange(focused);
  }

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final rowPadding = layout.surface.compactControlPadding;
    final foreground = _focused || widget.selected
        ? colors.textStrong
        : colors.textFaint;
    final subtitleForeground = _focused || widget.selected
        ? colors.textMuted
        : colors.textFaint;
    final separatorInset =
        layout.focusStroke +
        rowPadding.left +
        layout.iconLg +
        layout.controlGap;
    return Semantics(
      button: true,
      selected: widget.selected,
      label: widget.category.title,
      child: FocusableActionDetector(
        focusNode: widget.focusNode,
        autofocus: widget.autofocus,
        mouseCursor: SystemMouseCursors.click,
        onFocusChange: _handleFocusChange,
        shortcuts: const <ShortcutActivator, Intent>{
          SingleActivator(LogicalKeyboardKey.enter): ActivateIntent(),
          SingleActivator(LogicalKeyboardKey.numpadEnter): ActivateIntent(),
          SingleActivator(LogicalKeyboardKey.select): ActivateIntent(),
          SingleActivator(LogicalKeyboardKey.gameButtonA): ActivateIntent(),
        },
        actions: <Type, Action<Intent>>{
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) {
              widget.onPressed();
              return null;
            },
          ),
        },
        child: GestureDetector(
          onTap: widget.onPressed,
          child: Stack(
            children: <Widget>[
              Positioned(
                left: separatorInset,
                right: layout.focusStroke + rowPadding.right,
                bottom: 0,
                child: AnimatedOpacity(
                  opacity: _focused ? 0 : 1,
                  duration: motion.resolve(context, motion.focus),
                  curve: motion.standardCurve,
                  child: ColoredBox(
                    color: colors.hairline,
                    child: SizedBox(height: layout.hairlineStroke),
                  ),
                ),
              ),
              AnimatedContainer(
                duration: motion.resolve(context, motion.focus),
                curve: motion.standardCurve,
                constraints: BoxConstraints(
                  minHeight: layout.settings.categoryRowHeight,
                ),
                padding: rowPadding,
                decoration: BoxDecoration(
                  color: _focused ? colors.focusFill : Colors.transparent,
                  borderRadius: BorderRadius.circular(layout.controlRadius),
                  border: Border.all(
                    color: _focused ? colors.focusBorder : Colors.transparent,
                    width: layout.focusStroke,
                  ),
                ),
                child: Row(
                  children: <Widget>[
                    SizedBox(
                      width: layout.iconLg,
                      child: Icon(
                        widget.category.icon,
                        size: layout.iconMd,
                        color: foreground,
                      ),
                    ),
                    SizedBox(width: layout.controlGap),
                    Expanded(
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: <Widget>[
                          Text(
                            widget.category.title,
                            maxLines: 2,
                            overflow: TextOverflow.ellipsis,
                            style: context.text.sectionLabel.copyWith(
                              color: foreground,
                            ),
                          ),
                          if (widget.category.subtitle
                              case final subtitle?) ...<Widget>[
                            SizedBox(height: layout.xxs),
                            Text(
                              subtitle,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                              style: context.text.metadata.copyWith(
                                color: subtitleForeground,
                              ),
                            ),
                          ],
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// A labelled group of Settings rows or custom settings content.
final class ConsoleSettingsSection extends StatelessWidget {
  const ConsoleSettingsSection({
    required this.children,
    this.title,
    this.description,
    super.key,
  });

  final String? title;
  final String? description;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final colors = context.consoleColors;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: <Widget>[
        if (title case final title?)
          Text(
            title.toUpperCase(),
            style: context.text.metadataStrong.copyWith(
              color: colors.textFaint,
            ),
          ),
        if (description case final description?) ...<Widget>[
          if (title != null) SizedBox(height: layout.xxs),
          Text(
            description,
            style: context.text.metadata.copyWith(color: colors.textMuted),
          ),
        ],
        if (title != null || description != null) SizedBox(height: layout.xs),
        ...children,
      ],
    );
  }
}

/// The common couch-distance Settings row.
///
/// Rows distinguish navigation from actions with a trailing chevron, expose an
/// optional current value, and use the same focus, activation, semantics, and
/// scroll-visibility behavior everywhere.
enum ConsoleSettingsRowKind { legacy, navigation, value, action, choice }

final class ConsoleSettingsRow extends StatefulWidget {
  const ConsoleSettingsRow({
    required this.icon,
    required this.title,
    this.subtitle,
    this.value,
    this.onPressed,
    this.focusNode,
    this.autofocus = false,
    this.enabled = true,
    this.showChevron = false,
    this.destructive = false,
    this.selected = false,
    this.kind = ConsoleSettingsRowKind.legacy,
    this.semanticLabel,
    this.onFocusChange,
    super.key,
  });

  final IconData icon;
  final String title;
  final String? subtitle;
  final String? value;
  final VoidCallback? onPressed;
  final FocusNode? focusNode;
  final bool autofocus;
  final bool enabled;
  final bool showChevron;
  final bool destructive;
  final bool selected;
  final ConsoleSettingsRowKind kind;
  final String? semanticLabel;
  final ValueChanged<bool>? onFocusChange;

  @override
  State<ConsoleSettingsRow> createState() => _ConsoleSettingsRowState();
}

final class _ConsoleSettingsRowState extends State<ConsoleSettingsRow> {
  bool _focused = false;
  FocusNode? _ownedFocusNode;

  FocusNode get _effectiveFocusNode =>
      widget.focusNode ??
      (_ownedFocusNode ??= FocusNode(
        debugLabel: 'settings-row-${widget.title}',
      ));

  @override
  void didUpdateWidget(ConsoleSettingsRow oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.focusNode == null && oldWidget.title != widget.title) {
      _ownedFocusNode?.debugLabel = 'settings-row-${widget.title}';
    }
  }

  @override
  void dispose() {
    _ownedFocusNode?.dispose();
    super.dispose();
  }

  bool get _interactive => widget.enabled && widget.onPressed != null;

  void _handleFocusChange(bool focused) {
    setState(() => _focused = focused);
    widget.onFocusChange?.call(focused);
    if (!focused) return;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_focused) return;
      final motion = context.motion;
      Scrollable.ensureVisible(
        context,
        alignment: 0.5,
        duration: motion.resolve(context, motion.focus),
        curve: motion.standardCurve,
      );
    });
  }

  void _activate() {
    if (_interactive) widget.onPressed!.call();
  }

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final onBack = _ConsoleSettingsDismissScope.maybeOf(context);
    final focusedForeground = widget.destructive
        ? colors.warning
        : colors.textStrong;
    final restingForeground = widget.destructive
        ? colors.warning
        : colors.textBody;
    final foreground = _focused ? focusedForeground : restingForeground;
    final paintsSelectionChrome =
        widget.selected && widget.kind != ConsoleSettingsRowKind.choice;
    final emphasized = _focused || paintsSelectionChrome;
    final decoration = BoxDecoration(
      color: _focused
          ? colors.focusFill
          : paintsSelectionChrome
          ? colors.selectionFill
          : Colors.transparent,
      borderRadius: BorderRadius.circular(layout.controlRadius),
      border: Border.all(
        color: _focused
            ? colors.focusBorder
            : paintsSelectionChrome
            ? colors.selectionBorder
            : Colors.transparent,
        width: layout.focusStroke,
      ),
    );
    final rowPadding = layout.surface.compactControlPadding;
    final separatorInset =
        layout.focusStroke +
        rowPadding.left +
        layout.iconLg +
        layout.controlGap;

    return Semantics(
      button: widget.onPressed != null,
      enabled: widget.enabled,
      selected: widget.kind == ConsoleSettingsRowKind.choice
          ? null
          : widget.selected,
      checked: widget.kind == ConsoleSettingsRowKind.choice
          ? widget.selected
          : null,
      inMutuallyExclusiveGroup: widget.kind == ConsoleSettingsRowKind.choice,
      label: widget.semanticLabel,
      child: Shortcuts(
        shortcuts: const <ShortcutActivator, Intent>{
          SingleActivator(LogicalKeyboardKey.enter): ActivateIntent(),
          SingleActivator(LogicalKeyboardKey.numpadEnter): ActivateIntent(),
          SingleActivator(LogicalKeyboardKey.select): ActivateIntent(),
          SingleActivator(LogicalKeyboardKey.gameButtonA): ActivateIntent(),
        },
        child: Actions(
          actions: <Type, Action<Intent>>{
            ActivateIntent: CallbackAction<ActivateIntent>(
              onInvoke: (_) {
                _activate();
                return null;
              },
            ),
            if (onBack != null)
              DismissIntent: CallbackAction<DismissIntent>(
                onInvoke: (_) {
                  onBack();
                  return null;
                },
              ),
          },
          child: FocusableActionDetector(
            focusNode: _effectiveFocusNode,
            autofocus: widget.autofocus,
            enabled: _interactive,
            mouseCursor: _interactive
                ? SystemMouseCursors.click
                : SystemMouseCursors.basic,
            onFocusChange: _handleFocusChange,
            child: GestureDetector(
              onTap: _interactive ? _activate : null,
              child: Stack(
                children: <Widget>[
                  Positioned(
                    left: separatorInset,
                    right: layout.focusStroke + rowPadding.right,
                    bottom: 0,
                    child: AnimatedOpacity(
                      opacity: emphasized ? 0 : 1,
                      duration: motion.resolve(context, motion.focus),
                      curve: motion.standardCurve,
                      child: ColoredBox(
                        color: colors.hairline,
                        child: SizedBox(height: layout.hairlineStroke),
                      ),
                    ),
                  ),
                  AnimatedContainer(
                    duration: motion.resolve(context, motion.focus),
                    curve: motion.standardCurve,
                    constraints: BoxConstraints(
                      minHeight: layout.settings.compactActionHeight,
                    ),
                    padding: rowPadding,
                    decoration: decoration,
                    child: Opacity(
                      opacity: widget.enabled ? 1 : layout.disabledOpacity,
                      child: Row(
                        children: <Widget>[
                          SizedBox(
                            width: layout.iconLg,
                            child: Icon(
                              widget.icon,
                              size: layout.iconMd,
                              color: foreground,
                            ),
                          ),
                          SizedBox(width: layout.controlGap),
                          Expanded(
                            flex: 3,
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              mainAxisSize: MainAxisSize.min,
                              children: <Widget>[
                                Text(
                                  widget.title,
                                  maxLines: 2,
                                  overflow: TextOverflow.ellipsis,
                                  style: context.text.sectionLabel.copyWith(
                                    color: foreground,
                                  ),
                                ),
                                if (widget.subtitle
                                    case final subtitle?) ...<Widget>[
                                  SizedBox(height: layout.xxs),
                                  Text(
                                    subtitle,
                                    maxLines: 3,
                                    overflow: TextOverflow.ellipsis,
                                    style: context.text.metadata.copyWith(
                                      color: colors.textFaint,
                                    ),
                                  ),
                                ],
                              ],
                            ),
                          ),
                          if (widget.value case final value?) ...<Widget>[
                            SizedBox(width: layout.md),
                            Flexible(
                              flex: 2,
                              child: Text(
                                value,
                                maxLines: 2,
                                overflow: TextOverflow.ellipsis,
                                textAlign: TextAlign.end,
                                style: context.text.metadata.copyWith(
                                  color: _focused
                                      ? colors.textStrong
                                      : widget.selected
                                      ? colors.connected
                                      : colors.textMuted,
                                ),
                              ),
                            ),
                          ],
                          if (widget.showChevron) ...<Widget>[
                            SizedBox(width: layout.sm),
                            Icon(
                              Icons.chevron_right,
                              size: layout.iconMd,
                              color: _focused
                                  ? colors.textStrong
                                  : colors.textFaint,
                            ),
                          ],
                          if (widget.kind ==
                              ConsoleSettingsRowKind.choice) ...<Widget>[
                            SizedBox(width: layout.sm),
                            Icon(
                              widget.selected
                                  ? Icons.radio_button_checked
                                  : Icons.radio_button_unchecked,
                              size: layout.iconMd,
                              color: widget.selected
                                  ? colors.connected
                                  : colors.textFaint,
                            ),
                          ],
                        ],
                      ),
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

/// A Settings row that opens another page or pane.
final class ConsoleSettingsNavigationRow extends StatelessWidget {
  const ConsoleSettingsNavigationRow({
    required this.icon,
    required this.title,
    required this.onPressed,
    this.subtitle,
    this.value,
    this.focusNode,
    this.autofocus = false,
    this.enabled = true,
    this.semanticLabel,
    this.onFocusChange,
    super.key,
  });

  final IconData icon;
  final String title;
  final String? subtitle;
  final String? value;
  final VoidCallback onPressed;
  final FocusNode? focusNode;
  final bool autofocus;
  final bool enabled;
  final String? semanticLabel;
  final ValueChanged<bool>? onFocusChange;

  @override
  Widget build(BuildContext context) => ConsoleSettingsRow(
    icon: icon,
    title: title,
    subtitle: subtitle,
    value: value,
    onPressed: onPressed,
    focusNode: focusNode,
    autofocus: autofocus,
    enabled: enabled,
    showChevron: true,
    semanticLabel: semanticLabel,
    onFocusChange: onFocusChange,
    kind: ConsoleSettingsRowKind.navigation,
  );
}

/// A non-interactive Settings row that reports current state.
final class ConsoleSettingsValueRow extends StatelessWidget {
  const ConsoleSettingsValueRow({
    required this.icon,
    required this.title,
    required this.value,
    this.subtitle,
    this.enabled = true,
    this.semanticLabel,
    super.key,
  });

  final IconData icon;
  final String title;
  final String value;
  final String? subtitle;
  final bool enabled;
  final String? semanticLabel;

  @override
  Widget build(BuildContext context) => ConsoleSettingsRow(
    icon: icon,
    title: title,
    subtitle: subtitle,
    value: value,
    enabled: enabled,
    semanticLabel: semanticLabel,
    kind: ConsoleSettingsRowKind.value,
  );
}

/// A Settings row that performs an immediate action.
final class ConsoleSettingsActionRow extends StatelessWidget {
  const ConsoleSettingsActionRow({
    required this.icon,
    required this.title,
    required this.onPressed,
    this.subtitle,
    this.value,
    this.focusNode,
    this.autofocus = false,
    this.enabled = true,
    this.destructive = false,
    this.semanticLabel,
    this.onFocusChange,
    super.key,
  });

  final IconData icon;
  final String title;
  final String? subtitle;
  final String? value;
  final VoidCallback onPressed;
  final FocusNode? focusNode;
  final bool autofocus;
  final bool enabled;
  final bool destructive;
  final String? semanticLabel;
  final ValueChanged<bool>? onFocusChange;

  @override
  Widget build(BuildContext context) => ConsoleSettingsRow(
    icon: icon,
    title: title,
    subtitle: subtitle,
    value: value,
    onPressed: onPressed,
    focusNode: focusNode,
    autofocus: autofocus,
    enabled: enabled,
    destructive: destructive,
    semanticLabel: semanticLabel,
    onFocusChange: onFocusChange,
    kind: ConsoleSettingsRowKind.action,
  );
}

/// A mutually exclusive Settings choice.
///
/// Choice state is represented by the trailing radio indicator. The large row
/// outline is reserved exclusively for keyboard/controller focus.
final class ConsoleSettingsChoiceRow extends StatelessWidget {
  const ConsoleSettingsChoiceRow({
    required this.icon,
    required this.title,
    required this.selected,
    required this.onPressed,
    this.subtitle,
    this.value,
    this.focusNode,
    this.autofocus = false,
    this.enabled = true,
    this.semanticLabel,
    this.onFocusChange,
    super.key,
  });

  final IconData icon;
  final String title;
  final bool selected;
  final VoidCallback onPressed;
  final String? subtitle;
  final String? value;
  final FocusNode? focusNode;
  final bool autofocus;
  final bool enabled;
  final String? semanticLabel;
  final ValueChanged<bool>? onFocusChange;

  @override
  Widget build(BuildContext context) => ConsoleSettingsRow(
    icon: icon,
    title: title,
    subtitle: subtitle,
    value: value,
    onPressed: onPressed,
    focusNode: focusNode,
    autofocus: autofocus,
    enabled: enabled,
    selected: selected,
    semanticLabel: semanticLabel,
    onFocusChange: onFocusChange,
    kind: ConsoleSettingsRowKind.choice,
  );
}
