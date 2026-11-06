import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';
import 'platform_badge.dart';
import 'platform_logo_resolver.dart';

/// The compact platform identity overlaid on game artwork.
///
/// A resolved logo replaces the text badge. Platforms without artwork retain
/// the existing badge, so custom systems and incomplete metadata stay legible.
final class PlatformCornerMark extends StatelessWidget {
  const PlatformCornerMark({
    required this.platformId,
    required this.platformName,
    required this.shortCode,
    super.key,
  });

  final String platformId;
  final String platformName;
  final String shortCode;

  @override
  Widget build(BuildContext context) {
    final source = PlatformLogoResolverScope.of(context).resolve(
      platformId: platformId,
      platformName: platformName,
      shortCode: shortCode,
    );
    if (source == null) {
      return PlatformBadge.compact(label: shortCode);
    }

    final colors = context.consoleColors;
    final layout = context.layout;
    return Semantics(
      label: platformName,
      image: true,
      child: ExcludeSemantics(
        child: DecoratedBox(
          key: ValueKey<String>(
            'platform-corner-logo-${shortCode.trim().toLowerCase()}',
          ),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(layout.controlRadius),
            color: colors.mediaChipSurface,
          ),
          child: Padding(
            padding: EdgeInsets.symmetric(
              horizontal: layout.xs,
              vertical: layout.xxs,
            ),
            child: ConstrainedBox(
              constraints: BoxConstraints(maxWidth: layout.xxxl),
              child: SizedBox(
                height: layout.md,
                child: Image(
                  image: source.image,
                  fit: BoxFit.contain,
                  alignment: Alignment.centerLeft,
                  filterQuality: FilterQuality.high,
                  color: source.tintable ? colors.onMediaAccent : null,
                  colorBlendMode: source.tintable ? BlendMode.srcIn : null,
                  errorBuilder: (context, error, stackTrace) => FittedBox(
                    fit: BoxFit.scaleDown,
                    alignment: Alignment.centerLeft,
                    child: Text(
                      shortCode,
                      style: context.text.metadata.copyWith(
                        color: colors.onMediaAccent,
                      ),
                    ),
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
