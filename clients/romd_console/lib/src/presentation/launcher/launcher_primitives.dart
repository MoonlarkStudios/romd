import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/widgets/console_focus_ring.dart';
import 'package:romd_console/src/presentation/widgets/console_focusable.dart';
import 'package:romd_console/src/presentation/widgets/game_tile.dart';
import 'package:romd_console/src/presentation/widgets/platform_presentation.dart';

const double launcherNextPageExtentThreshold = 640;
const int launcherMaxContinuePlaying = 10;

String launcherCount(int count, String noun) =>
    count == 1 ? '1 $noun' : '$count ${noun}S';

const List<String> _monthAbbreviations = <String>[
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

/// Shared "Last played Mon D · CODE" caption for recently-played surfaces.
String launcherLastPlayedLabel(DateTime playedAt, String platformShortCode) =>
    'Last played ${_monthAbbreviations[playedAt.month - 1]} ${playedAt.day} · '
    '$platformShortCode';

Map<Type, Action<Intent>> launcherDirectionalActions(
  void Function(TraversalDirection direction) onDirection,
) => <Type, Action<Intent>>{
  DirectionalFocusIntent: CallbackAction<DirectionalFocusIntent>(
    onInvoke: (intent) {
      onDirection(intent.direction);
      return null;
    },
  ),
};

final class LauncherNavChip extends StatelessWidget {
  const LauncherNavChip({
    required this.label,
    required this.node,
    required this.selected,
    required this.onActivate,
    this.fontSize,
    super.key,
  });

  final String label;
  final FocusNode node;
  final bool selected;
  final double? fontSize;
  final VoidCallback onActivate;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    return ConsoleFocusable(
      focusNode: node,
      onPressed: onActivate,
      selected: selected,
      semanticLabel: label,
      scrollIntoViewOnFocus: true,
      builder: (context, focused) => ConsoleFocusRing(
        focused: focused,
        borderRadius: layout.controlRadius,
        restBorderColor: Colors.transparent,
        gap: 0,
        child: AnimatedContainer(
          duration: motion.resolve(context, motion.selection),
          curve: motion.standardCurve,
          padding: EdgeInsets.symmetric(
            horizontal: layout.sm,
            vertical: layout.xs,
          ),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(layout.controlRadius),
            color: selected
                ? colors.selectionFill
                : focused
                ? colors.focusFill
                : Colors.transparent,
            border: Border.all(
              color: selected ? colors.selectionBorder : Colors.transparent,
            ),
          ),
          child: Text(
            label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style:
                (selected
                        ? context.text.sectionLabel
                        : context.text.supportingTitle)
                    .copyWith(
                      color: selected || focused
                          ? colors.textStrong
                          : colors.textMuted,
                      fontSize:
                          fontSize ?? context.text.supportingTitle.fontSize,
                    ),
          ),
        ),
      ),
    );
  }
}

final class LauncherStatusPanel extends StatelessWidget {
  const LauncherStatusPanel({
    required this.icon,
    required this.title,
    required this.message,
    this.action,
    super.key,
  });

  final IconData icon;
  final String title;
  final String message;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Center(
      child: ConstrainedBox(
        constraints: BoxConstraints(maxWidth: layout.status.panelMaxWidth),
        child: Container(
          padding: EdgeInsets.all(layout.lg),
          decoration: BoxDecoration(
            color: colors.panelSurface,
            border: Border.all(color: colors.panelBorder),
            borderRadius: BorderRadius.circular(layout.panelRadius),
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              Icon(icon, size: layout.iconLg, color: colors.textFaint),
              SizedBox(height: layout.md),
              Text(
                title,
                textAlign: TextAlign.center,
                style: context.text.sectionHeading,
              ),
              SizedBox(height: layout.xs),
              Text(
                message,
                textAlign: TextAlign.center,
                style: context.text.body.copyWith(color: colors.textMuted),
              ),
              if (action != null) ...<Widget>[
                SizedBox(height: layout.md),
                action!,
              ],
            ],
          ),
        ),
      ),
    );
  }
}

final class LauncherLibraryGrid extends StatelessWidget {
  const LauncherLibraryGrid({
    required this.games,
    required this.heroPrefix,
    required this.installedReleaseIds,
    required this.cardNodes,
    required this.onOpenGame,
    required this.onFocused,
    this.supportingTextByGameId = const <String, String>{},
    this.forceInstalled = false,
    this.edgeInset,
    super.key,
  });

  final List<ConsoleGame> games;
  final String heroPrefix;
  final Set<String> installedReleaseIds;
  final Map<String, String> supportingTextByGameId;
  final List<FocusNode> cardNodes;
  final void Function(ConsoleGame game, String heroTag) onOpenGame;
  final void Function(int index, int row, bool isLastRow) onFocused;
  final bool forceInstalled;

  /// Horizontal inset around the grid. Library keeps the full-bleed
  /// Defaults to the active skin's screen gutter. Catalog frames itself and
  /// passes zero.
  final double? edgeInset;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final layout = context.layout;
      final edgeInset = this.edgeInset ?? layout.screenGutter;
      final available = constraints.maxWidth - edgeInset * 2;
      final columns = math.max(
        1,
        ((available + layout.gameRail.strip.tileGap) /
                (layout.gameRail.strip.tileWidth +
                    layout.gameRail.strip.tileGap))
            .floor(),
      );
      return Padding(
        padding: EdgeInsets.fromLTRB(
          edgeInset,
          layout.gameRail.strip.tileGap,
          edgeInset,
          layout.lg,
        ),
        child: Wrap(
          spacing: layout.gameRail.strip.tileGap,
          runSpacing: layout.gameRail.gridRunGap,
          children: <Widget>[
            for (var index = 0; index < games.length; index++)
              SizedBox(
                width: layout.gameRail.strip.tileWidth,
                child: GameTile(
                  game: games[index],
                  heroTag: '$heroPrefix#card$index',
                  focusNode: cardNodes[index],
                  installed: forceInstalled || _isInstalled(games[index]),
                  supportingText: supportingTextByGameId[games[index].id],
                  onFocused: () => onFocused(
                    index,
                    index ~/ columns,
                    index ~/ columns == (games.length - 1) ~/ columns,
                  ),
                  onPressed: () =>
                      onOpenGame(games[index], '$heroPrefix#card$index'),
                ),
              ),
          ],
        ),
      );
    },
  );

  bool _isInstalled(ConsoleGame game) =>
      game.defaultReleaseId != null &&
      installedReleaseIds.contains(game.defaultReleaseId);
}

final class LauncherLoadMoreButton extends StatefulWidget {
  const LauncherLoadMoreButton({
    required this.loading,
    required this.hasError,
    required this.onPressed,
    super.key,
  });

  final bool loading;
  final bool hasError;
  final VoidCallback onPressed;

  @override
  State<LauncherLoadMoreButton> createState() => _LauncherLoadMoreButtonState();
}

final class _LauncherLoadMoreButtonState extends State<LauncherLoadMoreButton> {
  bool _focused = false;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final motion = context.motion;
    final enabled = !widget.loading;
    final label = widget.loading
        ? 'Loading more'
        : widget.hasError
        ? 'Retry loading'
        : 'Load more';
    return Shortcuts(
      shortcuts: const <ShortcutActivator, Intent>{
        SingleActivator(LogicalKeyboardKey.enter): ActivateIntent(),
        SingleActivator(LogicalKeyboardKey.select): ActivateIntent(),
        SingleActivator(LogicalKeyboardKey.gameButtonA): ActivateIntent(),
      },
      child: Actions(
        actions: <Type, Action<Intent>>{
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) {
              if (enabled) widget.onPressed();
              return null;
            },
          ),
        },
        child: FocusableActionDetector(
          mouseCursor: enabled
              ? SystemMouseCursors.click
              : SystemMouseCursors.basic,
          onFocusChange: (focused) => setState(() => _focused = focused),
          child: GestureDetector(
            onTap: enabled ? widget.onPressed : null,
            child: AnimatedContainer(
              duration: motion.resolve(context, motion.selection),
              curve: motion.standardCurve,
              padding: layout.surface.compactControlPadding,
              decoration: BoxDecoration(
                borderRadius: BorderRadius.circular(layout.controlRadius),
                color: _focused ? colors.focusFill : colors.controlRestFill,
                border: Border.all(
                  color: _focused ? colors.focusBorder : colors.borderStrong,
                  width: _focused ? layout.focusStroke : layout.hairlineStroke,
                ),
              ),
              child: widget.loading
                  ? SizedBox.square(
                      dimension: layout.progressIndicatorSm,
                      child: CircularProgressIndicator(
                        strokeWidth: layout.focusStroke,
                      ),
                    )
                  : Text(
                      label,
                      style: context.text.sectionLabel.copyWith(
                        color: _focused
                            ? colors.onMediaOverlay
                            : colors.textMuted,
                      ),
                    ),
            ),
          ),
        ),
      ),
    );
  }
}

final class LauncherRail {
  const LauncherRail({
    required this.title,
    required this.eyebrow,
    required this.games,
    this.continuePlaying = false,
    this.lastPlayedAtByGameId = const <String, DateTime>{},
  });

  final String title;

  /// Amber mono eyebrow rendered above the heading — the Catalog's archival
  /// signature (§7 of the Catalog design contract). Uppercased on render.
  final String eyebrow;
  final List<ConsoleGame> games;
  final bool continuePlaying;
  final Map<String, DateTime> lastPlayedAtByGameId;
}

final class LauncherGameRail extends StatelessWidget {
  const LauncherGameRail({
    required this.rail,
    required this.railIndex,
    required this.cardNodes,
    required this.scrollController,
    required this.rowActive,
    required this.autofocusFirstCard,
    required this.installedReleaseIds,
    required this.onOpenGame,
    required this.onCardFocused,
    super.key,
  });

  final LauncherRail rail;
  final int railIndex;
  final List<FocusNode> cardNodes;
  final ScrollController scrollController;
  final bool rowActive;
  final bool autofocusFirstCard;
  final Set<String> installedReleaseIds;
  final void Function(ConsoleGame game, String heroTag) onOpenGame;
  final void Function(int railIndex, int gameIndex) onCardFocused;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final topPadding = rail.continuePlaying ? layout.md : layout.lg;
    final railHeight = math.max(
      layout.gameRail.minimumHeight,
      GameTile.minimumHeight(context, layout.gameRail.strip.tileWidth) +
          topPadding +
          layout.md,
    );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        rail.continuePlaying
            ? _ContinuePlayingHeading(count: rail.games.length)
            : _StandardRailHeading(
                eyebrow: rail.eyebrow,
                title: rail.title,
                caption: _caption,
              ),
        SizedBox(height: rail.continuePlaying ? layout.xs : layout.sm),
        SizedBox(
          height: railHeight,
          child: ShaderMask(
            shaderCallback: (rect) => LinearGradient(
              // dstIn consumes alpha only; the skin highlight supplies an
              // opaque mask without embedding a nominal black visual color.
              colors: <Color>[
                context.artwork.highlight,
                context.artwork.highlight,
                Colors.transparent,
              ],
              stops: <double>[0, layout.contentRail.fadeStart, 1],
            ).createShader(rect),
            blendMode: BlendMode.dstIn,
            child: ListView.separated(
              controller: scrollController,
              scrollDirection: Axis.horizontal,
              padding: EdgeInsets.fromLTRB(0, topPadding, 0, layout.md),
              itemCount: rail.games.length,
              separatorBuilder: (_, _) =>
                  SizedBox(width: layout.gameRail.strip.tileGap),
              itemBuilder: (context, index) {
                final game = rail.games[index];
                final heroTag = 'rail$railIndex#card$index';
                return SizedBox(
                  width: layout.gameRail.strip.tileWidth,
                  child: GameTile(
                    game: game,
                    heroTag: heroTag,
                    focusNode: cardNodes[index],
                    installed:
                        game.defaultReleaseId != null &&
                        installedReleaseIds.contains(game.defaultReleaseId),
                    rowActive: rowActive,
                    supportingText: rail.continuePlaying
                        ? _lastPlayedLabel(
                            context,
                            rail.lastPlayedAtByGameId[game.id],
                            game,
                          )
                        : null,
                    autofocus: autofocusFirstCard && index == 0,
                    revealOnFocus: false,
                    onFocused: () => onCardFocused(railIndex, index),
                    onPressed: () => onOpenGame(game, heroTag),
                  ),
                );
              },
            ),
          ),
        ),
      ],
    );
  }

  // Captions are counts only (§7); descriptions belong to the collection's
  // own screen.
  String get _caption => '· ${rail.games.length}';

  static String? _lastPlayedLabel(
    BuildContext context,
    DateTime? playedAt,
    ConsoleGame game,
  ) {
    if (playedAt == null) return null;
    final platform = platformPresentationFor(
      context,
      platformId: game.platformId,
      platformName: game.platformName,
    );
    return launcherLastPlayedLabel(playedAt, platform.shortCode);
  }
}

final class _ContinuePlayingHeading extends StatelessWidget {
  const _ContinuePlayingHeading({required this.count});
  final int count;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        Text(
          'YOUR GAMES',
          style: context.text.eyebrow.copyWith(color: colors.focusBorder),
        ),
        SizedBox(height: layout.xs),
        Row(
          children: <Widget>[
            Text(
              'Continue Playing',
              style: context.text.sectionHeading.copyWith(
                color: colors.textStrong,
              ),
            ),
            SizedBox(width: layout.md),
            Text(
              '$count IN ROTATION',
              style: context.text.utilityLabel.copyWith(
                color: colors.textFaint,
              ),
            ),
          ],
        ),
      ],
    );
  }
}

final class _StandardRailHeading extends StatelessWidget {
  const _StandardRailHeading({
    required this.eyebrow,
    required this.title,
    required this.caption,
  });
  final String eyebrow;
  final String title;
  final String caption;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        Text(
          eyebrow.toUpperCase(),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: context.text.eyebrow.copyWith(color: colors.catalogAccent),
        ),
        SizedBox(height: layout.xxs),
        Row(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: <Widget>[
            Flexible(
              child: Text(
                title,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: context.text.sectionHeading.copyWith(
                  color: colors.textStrong,
                ),
              ),
            ),
            SizedBox(width: layout.sm),
            Text(
              caption,
              maxLines: 1,
              style: context.text.metadata.copyWith(color: colors.textMuted),
            ),
          ],
        ),
      ],
    );
  }
}
