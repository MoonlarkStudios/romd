import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';
import 'cover_art.dart';
import 'platform_logo_resolver.dart';
import 'platform_presentation.dart';

/// A hardware identity plate for the Systems shelf.
///
/// Seeded consoles use a bundled monochrome mark keyed by their stable short
/// name. Custom systems retain the generated wordmark fallback. The full
/// platform name and metadata remain outside the plate on the owning card.
final class PlatformIdentityPlate extends StatelessWidget {
  const PlatformIdentityPlate({
    required this.platformName,
    required this.shortName,
    required this.tone,
    required this.focused,
    super.key,
  });

  final String platformName;
  final String shortName;
  final CoverTone tone;
  final bool focused;

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final rendering = context.artwork.coverRendering;
    final wordmark = platformPresentationFor(
      context,
      platformId: shortName.trim(),
      platformName: platformName,
    ).shortCode;
    final logo = PlatformLogoResolverScope.of(context).resolve(
      platformId: shortName,
      platformName: platformName,
      shortCode: wordmark,
    );
    final wordmarkFallback = LayoutBuilder(
      builder: (context, constraints) => Center(
        child: FittedBox(
          fit: BoxFit.scaleDown,
          child: Text(
            wordmark,
            key: ValueKey<String>(
              'platform-wordmark-${wordmark.toLowerCase()}',
            ),
            maxLines: 1,
            style: context.artwork.coverMonogramStyle.copyWith(
              fontSize: constraints.maxHeight * rendering.plateRingSizeFactor,
              color: tone.mono.withValues(
                alpha: focused ? 1 : rendering.plateMonogramAlpha,
              ),
            ),
          ),
        ),
      ),
    );

    return ExcludeSemantics(
      child: Stack(
        fit: StackFit.expand,
        children: <Widget>[
          CoverPlate(tone: tone, title: platformName, glyph: ''),
          Padding(
            padding: EdgeInsets.all(layout.lg),
            child: logo == null
                ? wordmarkFallback
                : Image(
                    image: logo.image,
                    key: ValueKey<String>(
                      'platform-logo-${shortName.trim().toLowerCase()}',
                    ),
                    fit: BoxFit.contain,
                    filterQuality: FilterQuality.high,
                    color: logo.tintable
                        ? tone.mono.withValues(
                            alpha: focused ? 1 : rendering.plateMonogramAlpha,
                          )
                        : null,
                    colorBlendMode: logo.tintable ? BlendMode.srcIn : null,
                    errorBuilder: (context, error, stackTrace) =>
                        wordmarkFallback,
                  ),
          ),
        ],
      ),
    );
  }
}
