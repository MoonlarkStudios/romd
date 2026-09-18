import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../domain/console_game.dart';
import '../theme/console_theme_context.dart';
import 'console_focus_ring.dart';
import 'console_focusable.dart';
import 'cover_art.dart';
import 'platform_corner_mark.dart';
import 'platform_presentation.dart';

/// A cover-forward rail card: a portrait plate (the shared-element source for the
/// detail transition) with a 2-line title and a mono `CODE · YEAR` caption.
///
/// Three visual states, per the focus model: **focus** (this card is selected —
/// scaled, teal ring + halo, title white), **dim** ([rowActive] but not this
/// card — neighbours recede), and **rest** (a row that isn't being navigated —
/// near-neutral, no scale).
final class GameTile extends StatelessWidget {
  const GameTile({
    required this.game,
    required this.heroTag,
    required this.onPressed,
    this.installed = false,
    this.focusNode,
    this.rowActive = false,
    this.supportingText,
    this.autofocus = false,
    this.revealOnFocus = true,
    this.onFocused,
    super.key,
  });

  final ConsoleGame game;

  /// Whether this title's default release is installed on this device. Drives
  /// the corner "on device" marker — the sparse "play instantly" signal.
  final bool installed;
  final FocusNode? focusNode;

  /// Unique within the route — a game can appear in several rails, so the tag is
  /// derived from the rail + index, not the game id. Handed to the detail screen
  /// so the cover lands on the matching Hero.
  final String heroTag;

  /// Whether this card's row currently holds focus. Drives the dim-vs-rest
  /// distinction for cards that are not themselves focused.
  final bool rowActive;
  final String? supportingText;
  final bool autofocus;

  /// Whether the tile pulls itself into view when focused. Grids leave this
  /// on; the home rails turn it off because the shell owns their scrolling
  /// (horizontal carousel + whole-page reveal) and self-centering would drag
  /// the shared page viewport around.
  final bool revealOnFocus;
  final VoidCallback onPressed;
  final VoidCallback? onFocused;

  static double minimumHeight(BuildContext context, double coverWidth) {
    final layout = context.layout;
    final text = context.text;
    return coverWidth / kCoverAspectRatio +
        layout.xs +
        _textBlockHeight(context, text.supportingTitle, lines: 2) +
        layout.xxs +
        _textBlockHeight(context, text.metadata);
  }

  /// Column count for a game-tile grid of [width]. Tiles live between the
  /// strip's minimum width and [ConsoleGameRailMetrics.gridTileMaxWidth]:
  /// narrow viewports drop below [designColumns] to respect the minimum,
  /// wide viewports gain columns so covers never stretch past the cap.
  static int gridColumnCount(
    BuildContext context, {
    required double width,
    required int designColumns,
  }) {
    final rail = context.layout.gameRail;
    final gap = rail.strip.tileGap;
    final columnsForCap = ((width + gap) / (rail.gridTileMaxWidth + gap))
        .ceil();
    final columnsForMinimum = ((width + gap) / (rail.strip.tileWidth + gap))
        .floor();
    return math.max(
      1,
      math.min(columnsForMinimum, math.max(designColumns, columnsForCap)),
    );
  }

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final text = context.text;
    final platform = platformPresentationFor(
      context,
      platformId: game.platformId,
      platformName: game.platformName,
    );
    return ConsoleFocusable(
      focusNode: focusNode,
      autofocus: autofocus,
      onPressed: onPressed,
      onFocusChange: (focused) {
        if (focused) onFocused?.call();
      },
      scrollIntoViewOnFocus: revealOnFocus,
      semanticLabel:
          '${game.title}. ${platform.label}. '
          '${supportingText ?? _metaLine(platform.shortCode)}.',
      builder: (context, focused) => AnimatedOpacity(
        duration: motion.resolve(context, motion.focus),
        curve: motion.standardCurve,
        opacity: focused
            ? 1
            : rowActive
            ? motion.interaction.rowActiveOpacity
            : motion.interaction.rowInactiveOpacity,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            AnimatedScale(
              duration: motion.resolve(context, motion.focus),
              curve: motion.standardCurve,
              scale: focused ? 1 : motion.interaction.restScale,
              child: ConsoleFocusRing(
                focused: focused,
                child: AspectRatio(
                  aspectRatio: kCoverAspectRatio,
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(layout.panelRadius),
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
                              borderRadius: layout.panelRadius,
                            ),
                          ),
                        ),
                        Positioned(
                          left: layout.xs,
                          bottom: layout.xs,
                          child: PlatformCornerMark(
                            platformId: game.platformId,
                            platformName: game.platformName,
                            shortCode: platform.shortCode,
                          ),
                        ),
                        if (installed)
                          Positioned(
                            top: layout.xs,
                            right: layout.xs,
                            child: const _InstalledBadge(),
                          ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
            SizedBox(height: layout.xs),
            SizedBox(
              height: _textBlockHeight(context, text.supportingTitle, lines: 2),
              child: Text(
                game.title,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: text.supportingTitle.copyWith(
                  color: focused ? colors.textStrong : colors.textBody,
                ),
              ),
            ),
            SizedBox(height: layout.xxs),
            Text(
              supportingText ?? _metaLine(platform.shortCode),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: text.metadata.copyWith(color: colors.textMuted),
            ),
          ],
        ),
      ),
    );
  }

  String _metaLine(String shortCode) => <String>[
    shortCode,
    if (game.releaseYear case final year?) year.toString(),
  ].join(' · ');

  static double _textBlockHeight(
    BuildContext context,
    TextStyle style, {
    int lines = 1,
  }) {
    final painter = TextPainter(
      text: TextSpan(
        text: List<String>.filled(lines, 'M').join('\n'),
        style: style,
      ),
      maxLines: lines,
      textDirection: TextDirection.ltr,
      textScaler: MediaQuery.textScalerOf(context),
    )..layout();
    return painter.height;
  }
}

/// Marks a tile whose default release is installed on this device — the sparse
/// "play instantly, no download" signal. Its absence is the calm default: the
/// title is in the catalog and will download on launch.
final class _InstalledBadge extends StatelessWidget {
  const _InstalledBadge();

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Semantics(
      label: 'Installed on this device',
      child: Container(
        key: const ValueKey<String>('tile-installed'),
        width: layout.lg,
        height: layout.lg,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          color: colors.controlRestFill,
          border: Border.all(color: colors.selectionBorder),
        ),
        child: Icon(
          Icons.download_done_rounded,
          size: layout.md,
          color: colors.focusBorder,
        ),
      ),
    );
  }
}
