import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';

/// The platform tag — the spec's "catalog value" accent. Renders in mono so it
/// reads as cataloguing metadata, not a UI control.
///
/// [PlatformBadge.compact] floats directly on cover artwork, so it uses the
/// media-chrome contract (skin-stable scrim pill plus
/// [ConsoleColors.onMediaAccent]) instead of on-surface roles: artwork never
/// adapts to the active skin, so neither can chrome that sits on it.
final class PlatformBadge extends StatelessWidget {
  const PlatformBadge({required this.label, super.key}) : compact = false;

  const PlatformBadge.compact({required this.label, super.key})
    : compact = true;

  final String label;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final colors = context.consoleColors;
    final layout = context.layout;
    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(layout.controlRadius),
        color: compact ? colors.mediaChipSurface : colors.selectionFill,
        border: compact ? null : Border.all(color: colors.selectionBorder),
      ),
      child: Padding(
        padding: compact
            ? EdgeInsets.symmetric(horizontal: layout.xs, vertical: layout.xxs)
            : EdgeInsets.symmetric(horizontal: layout.sm, vertical: layout.xs),
        child: Text(
          label,
          style: context.text.metadata.copyWith(
            color: compact ? colors.onMediaAccent : colors.catalogAccent,
          ),
        ),
      ),
    );
  }
}
