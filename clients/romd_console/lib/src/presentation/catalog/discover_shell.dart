import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:romd_console/src/input/console_intents.dart';
import 'package:romd_console/src/presentation/catalog/discover_models.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';
import 'package:romd_console/src/presentation/widgets/console_focusable.dart';
import 'package:romd_console/src/presentation/widgets/console_hint_bar.dart';

typedef DiscoverFeatureBuilder = Widget Function(BuildContext context);

@immutable
final class DiscoverLayoutSpec {
  const DiscoverLayoutSpec({
    required this.frameMargin,
    required this.headerHeight,
    required this.footerHeight,
    required this.headerSectionGap,
    required this.contentTopInset,
    required this.featuredSpotlightMaxFraction,
    required this.featuredCardWidth,
    required this.featuredCardGap,
    required this.allGamesGridColumns,
    required this.allGamesInspectorWidth,
    required this.allGamesPaneGap,
    required this.systemsPlateWidth,
    required this.systemsPlateGap,
    required this.searchKeyboardFraction,
    required this.searchZoneGap,
    required this.compactBreakpoint,
    required this.largeTextScaleBreakpoint,
  });

  factory DiscoverLayoutSpec.from(BuildContext context) {
    final metrics = context.layout.discover;
    return DiscoverLayoutSpec(
      frameMargin: metrics.frameMargin,
      headerHeight: metrics.headerHeight,
      footerHeight: metrics.footerHeight,
      headerSectionGap: metrics.headerSectionGap,
      contentTopInset: metrics.contentTopInset,
      featuredSpotlightMaxFraction: metrics.featuredSpotlightMaxFraction,
      featuredCardWidth: metrics.featuredCardWidth,
      featuredCardGap: metrics.featuredCardGap,
      allGamesGridColumns: metrics.allGamesGridColumns,
      allGamesInspectorWidth: metrics.allGamesInspectorWidth,
      allGamesPaneGap: metrics.allGamesPaneGap,
      systemsPlateWidth: metrics.systemsPlateWidth,
      systemsPlateGap: metrics.systemsPlateGap,
      searchKeyboardFraction: metrics.searchKeyboardFraction,
      searchZoneGap: metrics.searchZoneGap,
      compactBreakpoint: metrics.compactBreakpoint,
      largeTextScaleBreakpoint: metrics.largeTextScaleBreakpoint,
    );
  }

  final double frameMargin;
  final double headerHeight;
  final double footerHeight;
  final double headerSectionGap;
  final double contentTopInset;
  final double featuredSpotlightMaxFraction;
  final double featuredCardWidth;
  final double featuredCardGap;
  final int allGamesGridColumns;
  final double allGamesInspectorWidth;
  final double allGamesPaneGap;
  final double systemsPlateWidth;
  final double systemsPlateGap;
  final double searchKeyboardFraction;
  final double searchZoneGap;
  final double compactBreakpoint;
  final double largeTextScaleBreakpoint;
}

final class DiscoverLayoutScope extends InheritedWidget {
  const DiscoverLayoutScope({
    required this.spec,
    required super.child,
    super.key,
  });

  final DiscoverLayoutSpec spec;

  static DiscoverLayoutSpec of(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<DiscoverLayoutScope>()!.spec;

  @override
  bool updateShouldNotify(DiscoverLayoutScope oldWidget) =>
      oldWidget.spec != spec;
}

@immutable
final class DiscoverFeatureSlots {
  const DiscoverFeatureSlots({required this.featured, required this.allGames});

  final DiscoverFeatureBuilder featured;
  final DiscoverFeatureBuilder allGames;

  Widget build(BuildContext context, DiscoverSection section) => IndexedStack(
    index: section.index,
    children: <Widget>[featured(context), allGames(context)],
  );
}

/// Explicit bridge between the retained feature focus owners and the shared
/// Discover header. It avoids inferring cross-zone movement from geometry.
final class DiscoverFocusHandoff {
  final Map<DiscoverSection, FocusNode> _headerNodes =
      <DiscoverSection, FocusNode>{};
  final Set<FocusNode> _headerActionNodes = <FocusNode>{};
  final Map<DiscoverSection, VoidCallback> _contentRestorers =
      <DiscoverSection, VoidCallback>{};

  bool get headerHasFocus =>
      _headerNodes.values.any((node) => node.hasFocus) ||
      _headerActionNodes.any((node) => node.hasFocus);

  void attachHeader(DiscoverSection section, FocusNode node) {
    _headerNodes[section] = node;
  }

  void detachHeader(DiscoverSection section, FocusNode node) {
    if (identical(_headerNodes[section], node)) _headerNodes.remove(section);
  }

  void attachHeaderAction(FocusNode node) => _headerActionNodes.add(node);

  void detachHeaderAction(FocusNode node) => _headerActionNodes.remove(node);

  void attachContent(DiscoverSection section, VoidCallback restore) {
    _contentRestorers[section] = restore;
  }

  void detachContent(DiscoverSection section) =>
      _contentRestorers.remove(section);

  void focusHeader(DiscoverSection section) =>
      _headerNodes[section]?.requestFocus();

  void restoreContent(DiscoverSection section) =>
      _contentRestorers[section]?.call();
}

/// Route-local state for the Discover shell.
final class DiscoverShellController extends ChangeNotifier {
  DiscoverShellController({
    DiscoverSection initialSection = DiscoverSection.featured,
  }) : _section = initialSection;

  DiscoverSection _section;
  final ValueNotifier<double> featuredChromeProgress = ValueNotifier<double>(0);

  DiscoverSection get section => _section;

  void updateFeaturedChromeProgress(double progress) {
    final next = progress.clamp(0, 1).toDouble();
    if ((featuredChromeProgress.value - next).abs() < 0.01) return;
    featuredChromeProgress.value = next;
  }

  void select(DiscoverSection section) {
    if (_section == section) return;
    _section = section;
    notifyListeners();
  }

  void previous() {
    const values = DiscoverSection.values;
    select(values[(_section.index - 1 + values.length) % values.length]);
  }

  void next() {
    const values = DiscoverSection.values;
    select(values[(_section.index + 1) % values.length]);
  }

  @override
  void dispose() {
    featuredChromeProgress.dispose();
    super.dispose();
  }
}

/// Shared Discover composition and input boundary.
///
/// This widget makes no Consumer calls. It owns route-local section actions
/// and chooses one of the fixed feature slots supplied by the route.
final class DiscoverShell extends StatelessWidget {
  const DiscoverShell({
    required this.controller,
    required this.slots,
    required this.focusHandoff,
    required this.onSearch,
    required this.onBack,
    this.onFilters,
    this.footerHints,
    super.key,
  });

  final DiscoverShellController controller;
  final DiscoverFeatureSlots slots;
  final DiscoverFocusHandoff focusHandoff;
  final VoidCallback onSearch;
  final VoidCallback onBack;
  final VoidCallback? onFilters;

  final List<ConsoleHint>? footerHints;

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: controller,
    builder: (context, _) {
      return DiscoverLayoutScope(
        spec: DiscoverLayoutSpec.from(context),
        child: Builder(
          builder: (context) {
            final content = slots.build(context, controller.section);
            return Shortcuts(
              shortcuts: <ShortcutActivator, Intent>{
                const SingleActivator(LogicalKeyboardKey.pageUp):
                    const PreviousCatalogSectionIntent(),
                const SingleActivator(LogicalKeyboardKey.pageDown):
                    const NextCatalogSectionIntent(),
                const SingleActivator(LogicalKeyboardKey.slash):
                    const ShowControlsIntent(),
                const SingleActivator(LogicalKeyboardKey.keyF):
                    const ShowDetailsIntent(),
                const SingleActivator(LogicalKeyboardKey.keyX):
                    const ShowControlsIntent(),
                const SingleActivator(LogicalKeyboardKey.keyY):
                    const ShowDetailsIntent(),
              },
              child: Actions(
                actions: <Type, Action<Intent>>{
                  PreviousCatalogSectionIntent:
                      CallbackAction<PreviousCatalogSectionIntent>(
                        onInvoke: (_) {
                          final headerHadFocus = focusHandoff.headerHasFocus;
                          controller.previous();
                          if (!headerHadFocus) {
                            _restoreAcceleratedSectionFocus();
                          }
                          return null;
                        },
                      ),
                  NextCatalogSectionIntent:
                      CallbackAction<NextCatalogSectionIntent>(
                        onInvoke: (_) {
                          final headerHadFocus = focusHandoff.headerHasFocus;
                          controller.next();
                          if (!headerHadFocus) {
                            _restoreAcceleratedSectionFocus();
                          }
                          return null;
                        },
                      ),
                  ShowControlsIntent: CallbackAction<ShowControlsIntent>(
                    onInvoke: (_) {
                      onSearch();
                      return null;
                    },
                  ),
                  ShowDetailsIntent: CallbackAction<ShowDetailsIntent>(
                    onInvoke: (_) {
                      onFilters?.call();
                      return null;
                    },
                  ),
                  DismissIntent: CallbackAction<DismissIntent>(
                    onInvoke: (_) {
                      onBack();
                      return null;
                    },
                  ),
                },
                child: _DiscoverChrome(
                  section: controller.section,
                  featuredChromeProgress: controller.featuredChromeProgress,
                  onSelectSection: controller.select,
                  focusHandoff: focusHandoff,
                  onSearch: onSearch,
                  content: content,
                  hints: footerHints ?? _defaultHints(onFilters != null),
                ),
              ),
            );
          },
        ),
      );
    },
  );

  void _restoreAcceleratedSectionFocus() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      focusHandoff.restoreContent(controller.section);
    });
  }

  static List<ConsoleHint> _defaultHints(bool supportsFilters) => <ConsoleHint>[
    const ConsoleHint(
      glyph: '↑ ↓ ← →',
      gamepadGlyph: 'D-PAD',
      label: 'Navigate',
    ),
    const ConsoleHint(glyph: '/', gamepadGlyph: 'X', label: 'Search'),
    if (supportsFilters)
      const ConsoleHint(glyph: 'F', gamepadGlyph: 'Y', label: 'Filters'),
    const ConsoleHint(
      glyph: ConsoleHintGlyphs.confirm,
      gamepadGlyph: 'A',
      label: 'Open',
    ),
    const ConsoleHint(glyph: 'Esc', gamepadGlyph: 'B', label: 'Back'),
  ];
}

final class _DiscoverChrome extends StatefulWidget {
  const _DiscoverChrome({
    required this.section,
    required this.featuredChromeProgress,
    required this.onSelectSection,
    required this.focusHandoff,
    required this.onSearch,
    required this.content,
    required this.hints,
  });

  final DiscoverSection section;
  final ValueListenable<double> featuredChromeProgress;
  final ValueChanged<DiscoverSection> onSelectSection;
  final DiscoverFocusHandoff focusHandoff;
  final VoidCallback onSearch;
  final Widget content;
  final List<ConsoleHint> hints;

  @override
  State<_DiscoverChrome> createState() => _DiscoverChromeState();
}

final class _DiscoverChromeState extends State<_DiscoverChrome> {
  final List<FocusNode> _sectionNodes = <FocusNode>[
    FocusNode(debugLabel: 'discover-tab-featured'),
    FocusNode(debugLabel: 'discover-tab-browse'),
  ];
  final FocusNode _searchNode = FocusNode(debugLabel: 'discover-search-action');

  @override
  void initState() {
    super.initState();
    _attachHeaderNodes();
  }

  @override
  void didUpdateWidget(covariant _DiscoverChrome oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!identical(oldWidget.focusHandoff, widget.focusHandoff)) {
      _detachHeaderNodes(oldWidget.focusHandoff);
      _attachHeaderNodes();
    }
  }

  @override
  void dispose() {
    _detachHeaderNodes(widget.focusHandoff);
    for (final node in _sectionNodes) {
      node.dispose();
    }
    _searchNode.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final metrics = DiscoverLayoutScope.of(context);
    final hairline = layout.hairlineStroke;
    final double contentTop = widget.section == DiscoverSection.featured
        ? 0
        : metrics.headerHeight + hairline + metrics.contentTopInset;
    return Stack(
      clipBehavior: Clip.none,
      children: <Widget>[
        Positioned(
          top: contentTop,
          left: 0,
          right: 0,
          bottom: metrics.footerHeight,
          child: widget.content,
        ),
        Positioned(
          top: 0,
          left: 0,
          right: 0,
          height: metrics.headerHeight + hairline,
          child: ValueListenableBuilder<double>(
            valueListenable: widget.featuredChromeProgress,
            builder: (context, progress, _) => _buildHeader(
              context,
              progress: widget.section == DiscoverSection.featured
                  ? progress
                  : 1,
            ),
          ),
        ),
        Positioned(
          left: 0,
          right: 0,
          bottom: 0,
          height: metrics.footerHeight,
          child: ConsoleFooterBar(
            horizontalPadding: metrics.frameMargin,
            hints: widget.hints,
          ),
        ),
      ],
    );
  }

  /// The immersive variant of the console page band. Same anatomy — title,
  /// inline section switcher, trailing action along the shared gutter — but
  /// its anchoring is progress-driven: transparent over the Featured hero,
  /// chrome-filled once content scrolls beneath. Foregrounds lerp from the
  /// on-media roles to the ordinary text ramp with the same progress.
  Widget _buildHeader(BuildContext context, {required double progress}) {
    final layout = context.layout;
    final metrics = DiscoverLayoutScope.of(context);
    final colors = context.consoleColors;
    final band = layout.pageBand;
    final immersive = widget.section == DiscoverSection.featured;
    final foreground = immersive
        ? Color.lerp(colors.onMediaOverlay, colors.textStrong, progress)!
        : colors.textStrong;
    final mutedForeground = immersive
        ? Color.lerp(colors.onMediaOverlay, colors.textMuted, progress)!
        : colors.textMuted;
    // Content scrolls beneath this overlay, so the anchored fill blends to a
    // solid surface instead of the translucent chrome used by in-column bands.
    final anchoredSurface = Color.alphaBlend(
      colors.footerSurface,
      Theme.of(context).colorScheme.surface,
    );
    final surfaceColor = immersive
        ? Color.lerp(Colors.transparent, anchoredSurface, progress)!
        : anchoredSurface;
    // Over key art the switcher pill carries the documented on-media chip
    // backing so its labels stay legible; it fades out as the band anchors.
    final switcherBacking = immersive
        ? Color.lerp(colors.mediaChipSurface, Colors.transparent, progress)!
        : Colors.transparent;
    return DecoratedBox(
      key: const ValueKey<String>('discover-header-chrome'),
      decoration: BoxDecoration(color: surfaceColor),
      child: Stack(
        children: <Widget>[
          Positioned.fill(
            child: Padding(
              padding: EdgeInsets.symmetric(horizontal: metrics.frameMargin),
              child: LayoutBuilder(
                builder: (context, constraints) {
                  final textScale = MediaQuery.textScalerOf(context).scale(1);
                  final compact =
                      constraints.maxWidth < metrics.compactBreakpoint ||
                      textScale >= metrics.largeTextScaleBreakpoint;
                  return FocusTraversalGroup(
                    policy: WidgetOrderTraversalPolicy(),
                    child: Row(
                      children: <Widget>[
                        if (!compact) ...<Widget>[
                          Text(
                            'Catalog',
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: context.text.sectionHeading.copyWith(
                              color: foreground,
                            ),
                          ),
                          SizedBox(width: band.switcherGap),
                        ],
                        Container(
                          key: const ValueKey<String>(
                            'discover-section-navigation',
                          ),
                          padding: EdgeInsets.all(band.segmentInset),
                          decoration: BoxDecoration(
                            color: switcherBacking,
                            borderRadius: BorderRadius.circular(
                              layout.pillRadius,
                            ),
                            border: Border.all(
                              color: colors.hairline,
                              width: layout.hairlineStroke,
                            ),
                          ),
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: <Widget>[
                              for (final value in DiscoverSection.values)
                                _DiscoverHeaderSection(
                                  key: ValueKey<String>(
                                    'discover-section-${value.name}',
                                  ),
                                  section: value,
                                  focusNode: _sectionNodes[value.index],
                                  active: value == widget.section,
                                  foreground: foreground,
                                  mutedForeground: mutedForeground,
                                  onPressed: () =>
                                      widget.onSelectSection(value),
                                  onDirection: (direction) =>
                                      _moveSectionFocus(value, direction),
                                ),
                            ],
                          ),
                        ),
                        const Spacer(),
                        _DiscoverHeaderAction(
                          key: const ValueKey<String>('discover-search'),
                          focusNode: _searchNode,
                          semanticLabel: 'Search Catalog',
                          icon: Icons.search_rounded,
                          foreground: foreground,
                          onPressed: widget.onSearch,
                          onDirection: _moveSearchFocus,
                        ),
                      ],
                    ),
                  );
                },
              ),
            ),
          ),
          Positioned(
            left: metrics.frameMargin,
            right: metrics.frameMargin,
            bottom: 0,
            child: Opacity(
              opacity: immersive ? progress : 1,
              child: Divider(
                height: layout.hairlineStroke,
                thickness: layout.hairlineStroke,
                color: colors.hairline,
              ),
            ),
          ),
        ],
      ),
    );
  }

  void _attachHeaderNodes() {
    for (final section in DiscoverSection.values) {
      widget.focusHandoff.attachHeader(section, _sectionNodes[section.index]);
    }
    widget.focusHandoff.attachHeaderAction(_searchNode);
  }

  void _detachHeaderNodes(DiscoverFocusHandoff handoff) {
    for (final section in DiscoverSection.values) {
      handoff.detachHeader(section, _sectionNodes[section.index]);
    }
    handoff.detachHeaderAction(_searchNode);
  }

  void _moveSectionFocus(
    DiscoverSection focused,
    TraversalDirection direction,
  ) {
    switch (direction) {
      case TraversalDirection.left:
        if (focused.index > 0) {
          _sectionNodes[focused.index - 1].requestFocus();
        }
      case TraversalDirection.right:
        if (focused.index < _sectionNodes.length - 1) {
          _sectionNodes[focused.index + 1].requestFocus();
        } else {
          _searchNode.requestFocus();
        }
      case TraversalDirection.down:
        widget.focusHandoff.restoreContent(widget.section);
      case TraversalDirection.up:
        break;
    }
  }

  void _moveSearchFocus(TraversalDirection direction) {
    switch (direction) {
      case TraversalDirection.left:
        _sectionNodes.last.requestFocus();
      case TraversalDirection.down:
        widget.focusHandoff.restoreContent(widget.section);
      case TraversalDirection.right:
      case TraversalDirection.up:
        break;
    }
  }
}

final class _DiscoverHeaderSection extends StatelessWidget {
  const _DiscoverHeaderSection({
    required this.section,
    required this.focusNode,
    required this.active,
    required this.foreground,
    required this.mutedForeground,
    required this.onPressed,
    required this.onDirection,
    super.key,
  });

  final DiscoverSection section;
  final FocusNode focusNode;
  final bool active;
  final Color foreground;
  final Color mutedForeground;
  final VoidCallback onPressed;
  final ValueChanged<TraversalDirection> onDirection;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final pill = BorderRadius.circular(layout.pillRadius);
    return Semantics(
      button: true,
      selected: active,
      child: Actions(
        actions: <Type, Action<Intent>>{
          DirectionalFocusIntent: CallbackAction<DirectionalFocusIntent>(
            onInvoke: (intent) {
              onDirection(intent.direction);
              return null;
            },
          ),
        },
        child: ConsoleFocusable(
          focusNode: focusNode,
          onPressed: onPressed,
          semanticLabel: section.label,
          selected: active,
          builder: (context, focused) => ConsoleFocusRing(
            focused: focused,
            borderRadius: layout.pillRadius,
            restBorderColor: Colors.transparent,
            gap: 0,
            child: AnimatedContainer(
              duration: context.motion.resolve(
                context,
                context.motion.selection,
              ),
              curve: context.motion.standardCurve,
              padding: layout.pageBand.segmentPadding,
              decoration: BoxDecoration(
                borderRadius: pill,
                color: focused
                    ? colors.focusFill
                    : active
                    ? colors.selectionFill
                    : Colors.transparent,
                border: Border.all(
                  color: active ? colors.selectionBorder : Colors.transparent,
                  width: layout.hairlineStroke,
                ),
              ),
              child: Text(
                section.label,
                style: context.text.action.copyWith(
                  color: focused || active ? foreground : mutedForeground,
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

final class _DiscoverHeaderAction extends StatelessWidget {
  const _DiscoverHeaderAction({
    required this.focusNode,
    required this.semanticLabel,
    required this.icon,
    required this.foreground,
    required this.onPressed,
    required this.onDirection,
    super.key,
  });

  final FocusNode focusNode;
  final String semanticLabel;
  final IconData icon;
  final Color foreground;
  final VoidCallback onPressed;
  final ValueChanged<TraversalDirection> onDirection;

  @override
  Widget build(BuildContext context) => Tooltip(
    message: semanticLabel,
    excludeFromSemantics: true,
    child: Actions(
      actions: <Type, Action<Intent>>{
        DirectionalFocusIntent: CallbackAction<DirectionalFocusIntent>(
          onInvoke: (intent) {
            onDirection(intent.direction);
            return null;
          },
        ),
      },
      child: ConsoleFocusable(
        focusNode: focusNode,
        onPressed: onPressed,
        semanticLabel: semanticLabel,
        builder: (context, focused) => ConsoleFocusRing(
          focused: focused,
          borderRadius: context.layout.chipRadius,
          restBorderColor: Colors.transparent,
          gap: 0,
          child: AnimatedContainer(
            duration: context.motion.resolve(context, context.motion.selection),
            curve: context.motion.standardCurve,
            width: context.layout.surface.iconActionSize,
            height: context.layout.surface.iconActionSize,
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(context.layout.chipRadius),
              color: focused
                  ? context.consoleColors.focusFill
                  : Colors.transparent,
            ),
            child: Icon(
              icon,
              size: context.layout.surface.headerIconSize,
              color: foreground,
            ),
          ),
        ),
      ),
    ),
  );
}
