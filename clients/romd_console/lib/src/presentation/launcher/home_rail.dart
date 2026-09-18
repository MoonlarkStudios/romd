import 'dart:async';

import 'package:flutter/material.dart';

import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/play/content/domain/profile_local_library.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/presentation/launcher/launcher_primitives.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';
import 'package:romd_console/src/presentation/widgets/console_focusable.dart';
import 'package:romd_console/src/presentation/widgets/cover_art.dart';
import 'package:romd_console/src/presentation/widgets/platform_corner_mark.dart';
import 'package:romd_console/src/presentation/widgets/platform_presentation.dart';

/// Home's single rail of recently played games (max
/// [launcherMaxContinuePlaying]).
///
/// The rail is the hero of the home screen: large portrait covers with the
/// animated focus ring, and the focused game announcing itself in a label
/// lane *above* the rail rather than captions under every tile. When the
/// profile has nothing to show, quiet skeleton slots keep the composition and
/// a single message row explains what will appear here.
final class HomeRail extends StatefulWidget {
  const HomeRail({
    required this.playServices,
    required this.onOpenGame,
    required this.onConnect,
    required this.onMoveUp,
    required this.onMoveDown,
    required this.onFirstResult,
    this.tileWidth,
    this.onAnyFocus,
    super.key,
  });

  final PlayServices playServices;
  final void Function(ConsoleGame game, String heroTag) onOpenGame;
  final VoidCallback onConnect;
  final VoidCallback onMoveUp;
  final VoidCallback onMoveDown;

  /// Fired once, after the first library result lands, so the home screen can
  /// place initial focus (rail when it has games, dock otherwise).
  final VoidCallback onFirstResult;
  final VoidCallback? onAnyFocus;

  /// Optional structural override for responsive canvases. When omitted, the
  /// active skin supplies the cover width; height follows the shared poster aspect ratio.
  final double? tileWidth;

  static const int _minSlots = 5;

  /// Height of the focused-title lane above the rail: one title line,
  /// tracking the ambient text scale so large type never overflows its
  /// reserved slot.
  static double labelLaneHeight(BuildContext context) =>
      MediaQuery.textScalerOf(context).scale(
        context.text.cardTitle.fontSize! * context.text.cardTitle.height!,
      ) +
      context.layout.xxs +
      context.layout.hairlineStroke * 2;

  @override
  State<HomeRail> createState() => HomeRailState();
}

final class HomeRailState extends State<HomeRail> {
  final ScrollController _scroll = ScrollController(debugLabel: 'home-rail');
  final FocusNode _connectNode = FocusNode(debugLabel: 'home-rail-connect');
  StreamSubscription<ProfileLocalLibraryResult>? _subscription;
  ProfileLocalLibraryResult? _result;
  List<ProfileLocalLibraryTitle> _recent = const <ProfileLocalLibraryTitle>[];
  List<String> _gameIds = const <String>[];
  List<FocusNode> _gameNodes = const <FocusNode>[];
  int _focusedIndex = 0;
  bool _railFocused = false;
  bool _reportedFirstResult = false;
  String? _announcement;

  /// The rail's settled scroll destination. The floating label positions
  /// against this (not the live offset) so it lands instantly where the
  /// focused tile will rest.
  double _railScrollTarget = 0;

  /// Whether the rail currently owns any focusable control with focus.
  bool get hasFocusWithin =>
      _connectNode.hasFocus || _gameNodes.any((node) => node.hasFocus);

  /// Whether the rail offers anything to focus.
  bool get hasFocusables => _gameNodes.isNotEmpty || _showsConnectAction;

  bool get _showsConnectAction => _result is ProfileLocalLibraryNoServer;

  @override
  void initState() {
    super.initState();
    _subscribe();
  }

  @override
  void didUpdateWidget(covariant HomeRail oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.playServices.localLibrary !=
        widget.playServices.localLibrary) {
      unawaited(_subscription?.cancel());
      _subscribe();
    }
  }

  @override
  void dispose() {
    unawaited(_subscription?.cancel());
    for (final node in _gameNodes) {
      node.dispose();
    }
    _connectNode.dispose();
    _scroll.dispose();
    super.dispose();
  }

  /// Focuses the remembered rail position (or the connect action). Returns
  /// false when the rail has nothing focusable.
  bool focusContent() {
    if (_gameNodes.isNotEmpty) {
      _focusIndex(_focusedIndex);
      return true;
    }
    if (_showsConnectAction) {
      _connectNode.requestFocus();
      return true;
    }
    return false;
  }

  void _subscribe() {
    _subscription = widget.playServices.localLibrary
        .watchLibrary(sort: ProfileLocalLibrarySort.recentPlay)
        .listen(_replaceResult);
  }

  void _replaceResult(ProfileLocalLibraryResult result) {
    if (!mounted) return;
    final next = switch (result) {
      ProfileLocalLibraryReady(:final titles) =>
        titles
            .where((title) => title.launchable && title.lastPlayedAt != null)
            .take(launcherMaxContinuePlaying)
            .toList(growable: false),
      ProfileLocalLibraryNoServer() ||
      ProfileLocalLibraryUnavailable() => const <ProfileLocalLibraryTitle>[],
    };
    final focusedId =
        _focusedIndex < _gameIds.length &&
            _gameNodes.isNotEmpty &&
            _gameNodes[_focusedIndex].hasFocus
        ? _gameIds[_focusedIndex]
        : null;
    final existing = <String, FocusNode>{
      for (var index = 0; index < _gameIds.length; index++)
        _gameIds[index]: _gameNodes[index],
    };
    final nextIds = next.map((title) => title.install.titleId).toList();
    final nextNodes = <FocusNode>[
      for (final id in nextIds)
        existing.remove(id) ?? FocusNode(debugLabel: 'home-rail-$id'),
    ];
    final removed = existing.values.toList(growable: false);
    var nextFocus = _focusedIndex.clamp(0, next.isEmpty ? 0 : next.length - 1);
    if (focusedId != null) {
      final carried = nextIds.indexOf(focusedId);
      if (carried >= 0) {
        nextFocus = carried;
      } else {
        _announcement = 'A game is no longer available on Home.';
      }
    }
    setState(() {
      _result = result;
      _recent = next;
      _gameIds = nextIds;
      _gameNodes = nextNodes;
      _focusedIndex = nextFocus;
    });
    WidgetsBinding.instance.addPostFrameCallback((_) {
      for (final node in removed) {
        node.dispose();
      }
      if (!mounted) return;
      if (focusedId != null && next.isNotEmpty) {
        _focusIndex(nextFocus);
      }
      if (!_reportedFirstResult) {
        _reportedFirstResult = true;
        widget.onFirstResult();
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final focusedGame =
        _railFocused && _gameNodes.isNotEmpty && _focusedIndex < _recent.length
        ? _recent[_focusedIndex]
        : null;
    return Semantics(
      liveRegion: true,
      label: _announcement,
      child: Actions(
        actions: launcherDirectionalActions(_handleDirection),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            SizedBox(
              height: HomeRail.labelLaneHeight(context),
              // The label floats centered above the focused tile
              // (Switch-style). It swaps and repositions instantly against
              // the rail's scroll destination, so the title reads snappy and
              // accurate before any reveal animation settles.
              child: CustomSingleChildLayout(
                delegate: _FocusedLabelLayout(
                  tileCenterX: _focusedTileCenterX(),
                  edgeInset: layout.contentRail.labelEdgeInset,
                ),
                child: focusedGame == null
                    ? const SizedBox.shrink()
                    : _FocusedTitleLabel(
                        key: ValueKey<String>(focusedGame.install.titleId),
                        title: focusedGame,
                      ),
              ),
            ),
            SizedBox(height: layout.sm),
            SizedBox(
              height: _coverHeight + layout.contentRail.ringInset * 2,
              child: _buildRail(),
            ),
            SizedBox(height: layout.sm),
            Padding(
              padding: EdgeInsets.symmetric(horizontal: layout.screenGutter),
              child: _buildMessageRow(),
            ),
          ],
        ),
      ),
    );
  }

  double get _tileWidth =>
      widget.tileWidth ?? context.layout.contentRail.tileWidth;

  double get _coverHeight => _tileWidth / kCoverAspectRatio;

  double get _slotExtent =>
      _tileWidth + context.layout.contentRail.ringInset * 2;

  double get _slotStride => _slotExtent + context.layout.contentRail.tileGap;

  double get _railLeadingPad =>
      context.layout.screenGutter - context.layout.contentRail.ringInset;

  /// Viewport x of the focused tile's center at its scroll destination —
  /// where the tile will rest once the reveal animation finishes, so the
  /// label can take that position immediately.
  double _focusedTileCenterX() =>
      _railLeadingPad +
      _focusedIndex * _slotStride +
      _slotExtent / 2 -
      _railScrollTarget;

  /// Minimal-reveal scroll target: scroll only as far as needed to rest the
  /// focused tile's edge on the content gutter; tiles already comfortably in
  /// view leave the rail untouched.
  double _scrollTargetFor(int index) {
    if (!_scroll.hasClients) return 0;
    final position = _scroll.position;
    final tileLeft = _railLeadingPad + index * _slotStride;
    final tileRight = tileLeft + _slotExtent;
    final viewport = position.viewportDimension;
    // Base on the settled destination, not the live offset, so rapid focus
    // moves accumulate correctly while a reveal is still animating.
    var target = _railScrollTarget.clamp(0.0, position.maxScrollExtent);
    if (tileLeft - _railLeadingPad < target) {
      target = tileLeft - _railLeadingPad;
    } else if (tileRight + _railLeadingPad > target + viewport) {
      target = tileRight + _railLeadingPad - viewport;
    }
    return target.clamp(0.0, position.maxScrollExtent);
  }

  Widget _buildRail() {
    final layout = context.layout;
    final skeletonCount = _recent.length >= HomeRail._minSlots
        ? 0
        : HomeRail._minSlots - _recent.length;
    return ListView.separated(
      controller: _scroll,
      scrollDirection: Axis.horizontal,
      padding: EdgeInsets.symmetric(
        horizontal: layout.screenGutter - layout.contentRail.ringInset,
      ),
      itemCount: _recent.length + skeletonCount,
      separatorBuilder: (_, _) => SizedBox(width: layout.contentRail.tileGap),
      itemBuilder: (context, index) {
        if (index >= _recent.length) {
          // Before the first library result the vessels breathe (loading);
          // once the shelf is known-empty they hold still.
          return _SkeletonTile(
            width: _tileWidth,
            height: _coverHeight,
            breathing: _result == null,
          );
        }
        final title = _recent[index];
        final game = _gameFor(title);
        final heroTag = 'home-local#card$index';
        return _HomeRailTile(
          game: game,
          heroTag: heroTag,
          width: _tileWidth,
          height: _coverHeight,
          focusNode: _gameNodes[index],
          autofocus: index == 0,
          onFocusChange: (focused) => _handleTileFocus(index, focused),
          onPressed: () => widget.onOpenGame(game, heroTag),
        );
      },
    );
  }

  Widget _buildMessageRow() {
    final layout = context.layout;
    final colors = context.consoleColors;
    final result = _result;
    final (String? message, bool connect) = switch (result) {
      null => (null, false),
      // An empty shelf stays silent: the still vessels carry the state and
      // the dock's Catalog action carries the invitation. No copy.
      ProfileLocalLibraryReady() => (null, false),
      ProfileLocalLibraryNoServer() => (
        'Connect this profile to a ROMD server to install and play games.',
        true,
      ),
      ProfileLocalLibraryUnavailable() => (
        'Ottercade couldn’t safely read this profile’s local games. '
            'Nothing has been removed.',
        false,
      ),
    };
    return ConstrainedBox(
      constraints: BoxConstraints(
        minHeight: layout.contentRail.messageMinHeight,
      ),
      child: message == null
          ? const SizedBox.shrink()
          : Row(
              children: <Widget>[
                Flexible(
                  child: Text(
                    message,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: context.text.metadata.copyWith(
                      color: colors.textMuted,
                    ),
                  ),
                ),
                if (connect) ...<Widget>[
                  SizedBox(width: layout.md),
                  _ConnectAction(
                    focusNode: _connectNode,
                    onPressed: widget.onConnect,
                    onFocusChange: (focused) {
                      if (focused) widget.onAnyFocus?.call();
                      _setRailFocused(focused || hasFocusWithin);
                    },
                  ),
                ],
              ],
            ),
    );
  }

  void _handleTileFocus(int index, bool focused) {
    if (focused) {
      widget.onAnyFocus?.call();
      _setFocusedIndex(index);
      _revealIndex(index);
    }
    _setRailFocused(hasFocusWithin);
  }

  void _setFocusedIndex(int index) {
    if (_focusedIndex == index || !mounted) return;
    setState(() => _focusedIndex = index);
  }

  void _setRailFocused(bool focused) {
    if (_railFocused != focused && mounted) {
      setState(() => _railFocused = focused);
    }
  }

  void _handleDirection(TraversalDirection direction) {
    switch (direction) {
      case TraversalDirection.up:
        widget.onMoveUp();
      case TraversalDirection.down:
        widget.onMoveDown();
      case TraversalDirection.left:
        if (_gameNodes.isNotEmpty) {
          _focusIndex((_focusedIndex - 1).clamp(0, _gameNodes.length - 1));
        }
      case TraversalDirection.right:
        if (_gameNodes.isNotEmpty) {
          _focusIndex((_focusedIndex + 1).clamp(0, _gameNodes.length - 1));
        }
    }
  }

  void _focusIndex(int index) {
    if (_gameNodes.isEmpty) return;
    final safe = index.clamp(0, _gameNodes.length - 1);
    _setFocusedIndex(safe);
    final node = _gameNodes[safe];
    if (node.context != null) {
      node.requestFocus();
    } else {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) node.requestFocus();
      });
    }
  }

  void _revealIndex(int index) {
    if (!_scroll.hasClients) return;
    final target = _scrollTargetFor(index);
    _railScrollTarget = target;
    if (target == _scroll.offset) return;
    unawaited(
      _scroll.animateTo(
        target,
        duration: context.motion.resolve(
          context,
          context.motion.contentTransition,
        ),
        curve: context.motion.emphasizedCurve,
      ),
    );
  }

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

/// Positions the floating title label so its horizontal center sits over
/// [tileCenterX], clamped inside the TV-safe gutters, hugging the lane's
/// bottom edge.
final class _FocusedLabelLayout extends SingleChildLayoutDelegate {
  const _FocusedLabelLayout({
    required this.tileCenterX,
    required this.edgeInset,
  });

  final double tileCenterX;
  final double edgeInset;

  @override
  BoxConstraints getConstraintsForChild(BoxConstraints constraints) =>
      constraints.loosen().copyWith(
        maxWidth: (constraints.maxWidth - edgeInset * 2).clamp(
          0.0,
          double.infinity,
        ),
      );

  @override
  Offset getPositionForChild(Size size, Size childSize) {
    final left = (tileCenterX - childSize.width / 2).clamp(
      edgeInset,
      (size.width - edgeInset - childSize.width).clamp(
        edgeInset,
        double.infinity,
      ),
    );
    return Offset(left, size.height - childSize.height);
  }

  @override
  bool shouldRelayout(covariant _FocusedLabelLayout oldDelegate) =>
      oldDelegate.tileCenterX != tileCenterX ||
      oldDelegate.edgeInset != edgeInset;
}

/// The focused game's title, floating centered above its tile
/// (Switch-style).
final class _FocusedTitleLabel extends StatelessWidget {
  const _FocusedTitleLabel({required this.title, super.key});

  final ProfileLocalLibraryTitle title;

  @override
  Widget build(BuildContext context) => Text(
    title.install.titleName,
    maxLines: 1,
    overflow: TextOverflow.ellipsis,
    textAlign: TextAlign.center,
    // Accent, matching the dock's label lane: both announce what currently
    // holds focus.
    style: context.text.sectionHeading.copyWith(
      color: context.consoleColors.focusBorder,
    ),
  );
}

final class _HomeRailTile extends StatelessWidget {
  const _HomeRailTile({
    required this.game,
    required this.heroTag,
    required this.width,
    required this.height,
    required this.focusNode,
    required this.autofocus,
    required this.onFocusChange,
    required this.onPressed,
  });

  final ConsoleGame game;
  final String heroTag;
  final double width;
  final double height;
  final FocusNode focusNode;
  final bool autofocus;
  final ValueChanged<bool> onFocusChange;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final motion = context.motion;
    final platform = platformPresentationFor(
      context,
      platformId: game.platformId,
      platformName: game.platformName,
    );
    return ConsoleFocusable(
      focusNode: focusNode,
      autofocus: autofocus,
      onPressed: onPressed,
      onFocusChange: onFocusChange,
      semanticLabel: '${game.title}. ${platform.label}.',
      builder: (context, focused) => ConsoleFocusRing(
        focused: focused,
        child: AnimatedScale(
          duration: motion.resolve(context, motion.focus),
          curve: motion.standardCurve,
          scale: focused ? 1 : motion.interaction.restScale,
          child: SizedBox(
            width: width,
            height: height,
            child: Stack(
              children: <Widget>[
                Positioned.fill(
                  child: Hero(
                    tag: heroTag,
                    child: CoverArt(
                      coverUrl: game.poster?.url ?? game.coverUrl,
                      contain: game.poster?.contain ?? true,
                      platformId: game.platformId,
                      platformName: game.platformName,
                      title: game.title,
                    ),
                  ),
                ),
                // Outside the Hero so it never flies to the detail screen.
                Positioned(
                  left: layout.xs,
                  bottom: layout.xs,
                  child: PlatformCornerMark(
                    platformId: game.platformId,
                    platformName: game.platformName,
                    shortCode: platform.shortCode,
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// A quiet placeholder slot keeping the rail's composition when there are
/// fewer games than visible slots — the Switch-style vessel register.
/// Vessels are silent and never focusable. While the library result is still
/// unknown they [breathing]ly pulse; a known-empty shelf holds still.
final class _SkeletonTile extends StatefulWidget {
  const _SkeletonTile({
    required this.width,
    required this.height,
    this.breathing = false,
  });

  final double width;
  final double height;
  final bool breathing;

  @override
  State<_SkeletonTile> createState() => _SkeletonTileState();
}

final class _SkeletonTileState extends State<_SkeletonTile>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController(vsync: this);

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _syncAnimation();
  }

  @override
  void didUpdateWidget(covariant _SkeletonTile oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.breathing != widget.breathing) _syncAnimation();
  }

  void _syncAnimation() {
    final motion = context.motion;
    final duration = motion.resolve(context, motion.pulse);
    if (!widget.breathing || duration == Duration.zero) {
      _controller
        ..stop()
        ..value = 0;
      return;
    }
    _controller.duration = duration;
    if (!_controller.isAnimating) {
      _controller.repeat(reverse: true);
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Padding(
      padding: EdgeInsets.all(layout.contentRail.ringInset),
      child: FadeTransition(
        opacity: _controller.drive(
          Tween<double>(begin: 1, end: layout.contentRail.skeletonOpacity),
        ),
        child: Container(
          key: const ValueKey<String>('home-rail-skeleton'),
          width: widget.width,
          height: widget.height,
          decoration: BoxDecoration(
            color: colors.panelSurface.withValues(
              alpha: layout.contentRail.skeletonOpacity,
            ),
            borderRadius: BorderRadius.circular(layout.panelRadius),
            border: Border.all(
              color: colors.hairline,
              width: layout.hairlineStroke,
            ),
          ),
        ),
      ),
    );
  }
}

final class _ConnectAction extends StatelessWidget {
  const _ConnectAction({
    required this.focusNode,
    required this.onPressed,
    required this.onFocusChange,
  });

  final FocusNode focusNode;
  final VoidCallback onPressed;
  final ValueChanged<bool> onFocusChange;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    return ConsoleFocusable(
      focusNode: focusNode,
      onPressed: onPressed,
      onFocusChange: onFocusChange,
      semanticLabel: 'Choose server',
      builder: (context, focused) => AnimatedContainer(
        duration: motion.resolve(context, motion.focus),
        curve: motion.standardCurve,
        padding: EdgeInsets.symmetric(
          horizontal: layout.md,
          vertical: layout.xs,
        ),
        decoration: BoxDecoration(
          color: focused ? colors.focusFill : Colors.transparent,
          borderRadius: BorderRadius.circular(layout.controlRadius),
          border: Border.all(
            color: focused ? colors.focusBorder : colors.borderStrong,
            width: focused ? layout.focusStroke : layout.hairlineStroke,
          ),
        ),
        child: Text(
          'Choose server',
          style: context.text.supportingTitle.copyWith(
            color: focused ? colors.textStrong : colors.textMuted,
          ),
        ),
      ),
    );
  }
}
