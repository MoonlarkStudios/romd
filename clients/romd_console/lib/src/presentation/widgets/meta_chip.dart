import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';

/// A small mono pill for a single piece of metadata (year, genre, rating,
/// players…). When [color] is set it tints the chip; otherwise it renders in the
/// neutral steel/slate of secondary catalog text.
final class MetaChip extends StatelessWidget {
  const MetaChip({required this.label, this.icon, this.color, super.key});

  final String label;
  final IconData? icon;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    final tint = color ?? colors.textMuted;
    final hasColor = color != null;

    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(layout.chipRadius),
        color: hasColor
            ? tint.withValues(alpha: layout.tint.fillOpacity)
            : Colors.transparent,
        border: Border.all(
          color: hasColor
              ? tint.withValues(alpha: layout.tint.borderOpacity)
              : colors.borderStrong,
        ),
      ),
      child: Padding(
        padding: EdgeInsets.symmetric(
          horizontal: layout.sm,
          vertical: layout.xxs,
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: <Widget>[
            if (icon case final icon?) ...<Widget>[
              Icon(icon, size: layout.status.metadataIconSize, color: tint),
              SizedBox(width: layout.xxs),
            ],
            Text(label, style: context.text.chipLabel.copyWith(color: tint)),
          ],
        ),
      ),
    );
  }
}
