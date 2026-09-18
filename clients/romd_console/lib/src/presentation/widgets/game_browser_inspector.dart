import 'package:flutter/material.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/theme/console_theme_extensions.dart';
import 'package:romd_console/src/presentation/widgets/platform_presentation.dart';

enum GameBrowserReadiness {
  ready,
  available,
  unavailable,
  accessRequired,
  incomplete,
  repairRequired,
}

/// Focused-title context shared by the Discover All Games and Library grids.
final class GameBrowserInspector extends StatelessWidget {
  const GameBrowserInspector({
    required this.game,
    required this.detail,
    required this.readiness,
    required this.compact,
    this.keyPrefix = 'all-games',
    super.key,
  });

  final ConsoleGame game;
  final ConsoleGameDetail? detail;
  final GameBrowserReadiness readiness;
  final bool compact;
  final String keyPrefix;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final platform = platformPresentationFor(
      context,
      platformId: game.platformId,
      platformName: game.platformName,
    );
    final rating = detail?.rating ?? game.rating;
    final genre = detail?.genre ?? game.genre;
    return AnimatedSwitcher(
      duration: context.motion.resolve(
        context,
        context.motion.contentTransition,
      ),
      child: Container(
        key: ValueKey<String>('$keyPrefix-inspector-${game.id}'),
        clipBehavior: Clip.antiAlias,
        decoration: BoxDecoration(
          color: colors.panelSurface,
          border: Border.all(color: colors.panelBorder),
          borderRadius: BorderRadius.circular(layout.panelRadius),
          boxShadow: context.elevation.panel,
        ),
        child: Stack(
          children: <Widget>[
            if (!compact)
              Positioned.fill(child: _backdrop(context, platform.tone)),
            Padding(
              padding: EdgeInsets.all(layout.md),
              child: compact
                  ? _compactContent(context, platform.shortCode, rating, genre)
                  : _fullContent(context, platform.shortCode, rating, genre),
            ),
          ],
        ),
      ),
    );
  }

  Widget _backdrop(BuildContext context, CoverTone tone) {
    final media = _preferredMedia;
    return IgnorePointer(
      child: Stack(
        fit: StackFit.expand,
        children: <Widget>[
          DecoratedBox(
            key: ValueKey<String>('$keyPrefix-inspector-media-fallback'),
            decoration: BoxDecoration(
              color: context.consoleColors.panelSurface,
            ),
          ),
          Opacity(
            opacity: 0.18,
            child: DecoratedBox(
              decoration: BoxDecoration(
                gradient: LinearGradient(
                  begin: Alignment.topRight,
                  end: Alignment.bottomLeft,
                  colors: <Color>[tone.glow, Colors.transparent],
                ),
              ),
            ),
          ),
          if (media != null)
            Opacity(
              opacity: 0.14,
              child: Image.network(
                media.toString(),
                key: ValueKey<String>(
                  '$keyPrefix-inspector-media-${media.toString()}',
                ),
                fit: BoxFit.cover,
                filterQuality: FilterQuality.low,
                errorBuilder: (_, _, _) => const SizedBox.shrink(),
              ),
            ),
        ],
      ),
    );
  }

  Uri? get _preferredMedia {
    final media = detail?.media ?? const <ConsoleMediaRef>[];
    if (media.isEmpty) return null;
    bool preferredType(ConsoleMediaRef item) {
      final type = item.type.toLowerCase();
      return type.contains('background') ||
          type.contains('hero') ||
          type.contains('screenshot');
    }

    for (final item in media) {
      if (item.isPrimary && preferredType(item)) return item.url;
    }
    for (final item in media) {
      if (item.isPrimary) return item.url;
    }
    for (final item in media) {
      if (preferredType(item)) return item.url;
    }
    return media.first.url;
  }

  Widget _compactContent(
    BuildContext context,
    String platform,
    double? rating,
    String? genre,
  ) => Wrap(
    spacing: context.layout.md,
    runSpacing: context.layout.sm,
    crossAxisAlignment: WrapCrossAlignment.center,
    children: <Widget>[
      ConstrainedBox(
        constraints: const BoxConstraints(minWidth: 220, maxWidth: 520),
        child: _identity(context, platform, genre),
      ),
      _rating(context, rating),
      _readiness(context),
    ],
  );

  Widget _fullContent(
    BuildContext context,
    String platform,
    double? rating,
    String? genre,
  ) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: <Widget>[
      _identity(context, platform, genre),
      SizedBox(height: context.layout.lg),
      Divider(color: context.consoleColors.hairline),
      SizedBox(height: context.layout.sm),
      _rating(context, rating),
      if (detail?.players case final players?) ...<Widget>[
        SizedBox(height: context.layout.sm),
        Text(
          '$players ${players == 1 ? 'PLAYER' : 'PLAYERS'}',
          key: ValueKey<String>('$keyPrefix-inspector-players'),
          style: context.text.metadataStrong.copyWith(
            color: context.consoleColors.textMuted,
          ),
        ),
      ],
      if (detail?.description case final description?
          when description.trim().isNotEmpty) ...<Widget>[
        SizedBox(height: context.layout.lg),
        Expanded(
          child: ClipRect(
            child: Align(
              alignment: Alignment.topLeft,
              child: Text(
                description,
                key: ValueKey<String>('$keyPrefix-inspector-description'),
                maxLines: 6,
                overflow: TextOverflow.ellipsis,
                style: context.text.body.copyWith(
                  color: context.consoleColors.textBody,
                ),
              ),
            ),
          ),
        ),
      ] else
        const Spacer(),
      _readiness(context),
      SizedBox(height: context.layout.md),
      Row(
        children: <Widget>[
          Icon(
            Icons.keyboard_return_rounded,
            color: context.consoleColors.textMuted,
          ),
          SizedBox(width: context.layout.xs),
          Text(
            'OPEN DETAILS',
            style: context.text.sectionLabel.copyWith(
              color: context.consoleColors.textStrong,
            ),
          ),
        ],
      ),
    ],
  );

  Widget _identity(BuildContext context, String platform, String? genre) =>
      Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          Text(
            game.title,
            key: ValueKey<String>('$keyPrefix-inspector-title'),
            maxLines: compact ? 1 : 3,
            overflow: TextOverflow.ellipsis,
            style:
                (compact ? context.text.cardTitle : context.text.sectionHeading)
                    .copyWith(color: context.consoleColors.textStrong),
          ),
          SizedBox(height: context.layout.xs),
          Text(
            <String>[
              platform,
              if (game.releaseYear case final year?) year.toString(),
              if (genre != null && genre.trim().isNotEmpty) genre,
            ].join(' · '),
            maxLines: compact ? 1 : 2,
            overflow: TextOverflow.ellipsis,
            style: context.text.metadataStrong.copyWith(
              color: context.consoleColors.textMuted,
            ),
          ),
        ],
      );

  Widget _rating(BuildContext context, double? rating) => Row(
    mainAxisSize: MainAxisSize.min,
    children: <Widget>[
      Icon(
        Icons.star_rounded,
        size: context.layout.lg,
        color: context.consoleColors.catalogAccent,
      ),
      SizedBox(width: context.layout.xs),
      Text(
        rating?.toStringAsFixed(1) ?? '—',
        key: ValueKey<String>('$keyPrefix-inspector-rating'),
        style: context.text.cardTitle.copyWith(
          color: context.consoleColors.catalogAccent,
        ),
      ),
    ],
  );

  Widget _readiness(BuildContext context) {
    final (label, icon, color) = switch (readiness) {
      GameBrowserReadiness.ready => (
        'READY TO PLAY',
        Icons.check_circle_outline_rounded,
        context.consoleColors.connected,
      ),
      GameBrowserReadiness.available => (
        'AVAILABLE IN CATALOG',
        Icons.cloud_outlined,
        context.consoleColors.textMuted,
      ),
      GameBrowserReadiness.unavailable => (
        'UNAVAILABLE',
        Icons.block_rounded,
        context.consoleColors.warning,
      ),
      GameBrowserReadiness.accessRequired => (
        'ACCESS REQUIRED',
        Icons.lock_outline_rounded,
        context.consoleColors.warning,
      ),
      GameBrowserReadiness.incomplete => (
        'DOWNLOAD INCOMPLETE',
        Icons.downloading_rounded,
        context.consoleColors.warning,
      ),
      GameBrowserReadiness.repairRequired => (
        'REPAIR REQUIRED',
        Icons.build_outlined,
        context.consoleColors.warning,
      ),
    };
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        Icon(icon, size: context.layout.lg, color: color),
        SizedBox(width: context.layout.xs),
        Flexible(
          child: Text(
            label,
            key: ValueKey<String>('$keyPrefix-inspector-readiness'),
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: context.text.sectionLabel.copyWith(color: color),
          ),
        ),
      ],
    );
  }
}

/// Prevents focused cards from scaling outside their grid cells when reduced
/// motion is requested.
final class GameBrowserMotionSafeCard extends StatelessWidget {
  const GameBrowserMotionSafeCard({required this.child, super.key});

  final Widget child;

  @override
  Widget build(BuildContext context) {
    if (!MediaQuery.disableAnimationsOf(context)) return child;
    final theme = Theme.of(context);
    final motion = context.motion;
    final interaction = motion.interaction;
    final reducedMotion = motion.copyWith(
      interaction: ConsoleInteractionSpec(
        focusedScale: 1,
        restScale: 1,
        subtleEnterScale: 1,
        rowActiveOpacity: interaction.rowActiveOpacity,
        rowInactiveOpacity: interaction.rowInactiveOpacity,
      ),
    );
    final extensions = theme.extensions.values.toList()
      ..removeWhere((extension) => extension is ConsoleMotionTheme)
      ..add(reducedMotion);
    return Theme(
      data: theme.copyWith(extensions: extensions),
      child: child,
    );
  }
}
