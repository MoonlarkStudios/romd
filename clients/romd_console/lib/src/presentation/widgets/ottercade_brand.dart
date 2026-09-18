import 'package:flutter/material.dart';

const String ottercadeRompAsset = 'assets/brand/ottercade_romp.png';
const String ottercadeWordmarkCreamAsset =
    'assets/brand/ottercade_wordmark_cream.png';
const String ottercadeWordmarkInkAsset =
    'assets/brand/ottercade_wordmark_ink.png';

/// The approved full-color Romp mark.
final class OttercadeRompMark extends StatelessWidget {
  const OttercadeRompMark({required this.size, super.key});

  final double size;

  @override
  Widget build(BuildContext context) => Image.asset(
    ottercadeRompAsset,
    width: size,
    height: size,
    filterQuality: FilterQuality.high,
    semanticLabel: 'Romp the Otter holding a game controller',
  );
}

/// The canonical outlined wordmark rendered from a deterministic export.
final class OttercadeWordmark extends StatelessWidget {
  const OttercadeWordmark({required this.width, this.onDark = true, super.key});

  final double width;
  final bool onDark;

  @override
  Widget build(BuildContext context) => Image.asset(
    onDark ? ottercadeWordmarkCreamAsset : ottercadeWordmarkInkAsset,
    width: width,
    filterQuality: FilterQuality.high,
    semanticLabel: 'Ottercade',
  );
}

/// The approved stacked hero lockup used by the entry attract state.
final class OttercadeStackedLockup extends StatelessWidget {
  const OttercadeStackedLockup({
    required this.rompSize,
    required this.wordmarkWidth,
    required this.spacing,
    super.key,
  });

  final double rompSize;
  final double wordmarkWidth;
  final double spacing;

  @override
  Widget build(BuildContext context) => Column(
    mainAxisSize: MainAxisSize.min,
    children: <Widget>[
      OttercadeRompMark(
        key: const ValueKey<String>('entry-romp'),
        size: rompSize,
      ),
      SizedBox(height: spacing),
      OttercadeWordmark(
        key: const ValueKey<String>('entry-wordmark'),
        width: wordmarkWidth,
      ),
    ],
  );
}
