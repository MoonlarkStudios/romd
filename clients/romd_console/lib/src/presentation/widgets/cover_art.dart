import 'dart:io';

import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';
import 'platform_presentation.dart';

/// Standardized poster aspect. Original box art is contained. The rail card and the detail
/// Hero landing both lay a [CoverArt] into this aspect so the shared-element
/// flight interpolates size only — it never stretches.
const double kCoverAspectRatio = 2 / 3;

/// A title's cover. When real art is present it fills the box; otherwise we draw
/// an archival "plate" — a platform-toned gradient, a faint ring, a diagonal
/// hatch, and a large low-opacity monogram. The plate is intentional placeholder
/// imagery (we never ship copyrighted box art); a dropped-in cover replaces it.
final class CoverArt extends StatelessWidget {
  const CoverArt({
    required this.coverUrl,
    required this.platformId,
    required this.platformName,
    required this.title,
    this.borderRadius = 10,
    this.imageProvider,
    this.contain = true,
    super.key,
  });

  final Uri? coverUrl;
  final bool contain;
  final String platformId;
  final String platformName;
  final String title;
  final double borderRadius;

  /// Optional decoded-image seam for deterministic tests and previews.
  /// Production resolves [coverUrl] through [NetworkImage].
  final ImageProvider<Object>? imageProvider;

  @override
  Widget build(BuildContext context) {
    final platform = platformPresentationFor(
      context,
      platformId: platformId,
      platformName: platformName,
    );
    final fallback = CoverPlate(tone: platform.tone, title: title);

    return ClipRRect(
      borderRadius: BorderRadius.circular(borderRadius),
      child: SizedBox.expand(
        child: coverUrl == null
            ? fallback
            // The plate sits underneath while the cover loads (and if it
            // fails), so a slow network shows the archival placeholder rather
            // than a hole; the real art fades in over it once decoded.
            : Stack(
                fit: StackFit.expand,
                children: <Widget>[
                  fallback,
                  Image(
                    image: ResizeImage.resizeIfNeeded(
                      coverDecodeCacheWidth(context),
                      null,
                      imageProvider ?? artworkImageProvider(coverUrl!),
                    ),
                    fit: contain ? BoxFit.contain : BoxFit.cover,
                    // One decode size for every cover, everywhere. cacheWidth
                    // is part of the image-cache key, so if this varied by
                    // painted size the detail Hero would re-decode the cover
                    // the rail already holds and flash the plate while it
                    // loads. A single bucket keeps memory bounded AND lets the
                    // tile, grid card, and detail landing share one bitmap —
                    // an already-seen cover appears instantly.
                    frameBuilder: coverFadeIn,
                    errorBuilder: (context, error, stackTrace) =>
                        const SizedBox.shrink(),
                  ),
                ],
              ),
      ),
    );
  }
}

/// Largest logical width a [CoverArt] renders at (the detail Hero, 208) with
/// headroom; every cover decodes at this one width so all contexts share a
/// single image-cache entry.
const double _kCoverDecodeLogicalWidth = 256;

/// Physical decode width for cover art — constant per device so the same URL
/// resolves to the same cached bitmap in every [CoverArt].
int coverDecodeCacheWidth(BuildContext context) =>
    (_kCoverDecodeLogicalWidth * MediaQuery.devicePixelRatioOf(context)).ceil();

/// Physical decode width for a network image laid out by [constraints]; null
/// (intrinsic size) when the width is unbounded. Only for images whose
/// constraints are stable (e.g. a full-bleed backdrop) — measured widths that
/// shift with focus chrome change the image-cache key and force a re-decode,
/// which flickers. For tiles, use [decodeWidthForLogical] with the tile's
/// constant design width instead.
int? decodeWidthFor(BuildContext context, BoxConstraints constraints) {
  final width = constraints.maxWidth;
  if (!width.isFinite || width <= 0) {
    return null;
  }
  return (width * MediaQuery.devicePixelRatioOf(context)).ceil();
}

/// Physical decode width for an image whose layout width is a known design
/// constant. Immune to focus-driven layout jitter (border width changes,
/// selection animations), so the provider — and its cache entry — never
/// changes once loaded.
int decodeWidthForLogical(BuildContext context, double logicalWidth) =>
    (logicalWidth * MediaQuery.devicePixelRatioOf(context)).ceil();

/// Standard cover fade: images decoded from the network ease in instead of
/// popping; synchronously available (cached) frames render immediately.
Widget coverFadeIn(
  BuildContext context,
  Widget child,
  int? frame,
  bool wasSynchronouslyLoaded,
) {
  if (wasSynchronouslyLoaded) {
    return child;
  }
  return AnimatedOpacity(
    opacity: frame == null ? 0 : 1,
    duration: context.motion.resolve(context, context.motion.contentTransition),
    curve: context.motion.standardCurve,
    child: child,
  );
}

/// The archival placeholder plate: gradient [tone].glow → [tone].base → near
/// black, a faint concentric ring, a fine diagonal hatch, and the title's
/// monogram in [tone].mono. Fills whatever box it is given, so it scales from a
/// rail card up to the detail Hero without changing proportion.
final class CoverPlate extends StatelessWidget {
  const CoverPlate({
    required this.tone,
    required this.title,
    this.glyph,
    super.key,
  });

  final CoverTone tone;
  final String title;

  /// Shown verbatim instead of the title monogram — used for platform plates
  /// that read better as their short code (e.g. "SNES") than as initials.
  final String? glyph;

  @override
  Widget build(BuildContext context) {
    final monogram = glyph ?? monogramFor(title);
    final rendering = context.artwork.coverRendering;

    return DecoratedBox(
      decoration: BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: <Color>[
            tone.glow,
            tone.base,
            context.artwork.coverPlateBackdrop,
          ],
          stops: <double>[0, rendering.plateGradientMiddleStop, 1],
        ),
      ),
      child: LayoutBuilder(
        builder: (context, constraints) {
          final shortest = constraints.biggest.shortestSide;
          return Stack(
            fit: StackFit.expand,
            children: <Widget>[
              Positioned.fill(
                child: CustomPaint(
                  painter: _HatchPainter(
                    color: context.consoleColors.onMediaOverlay,
                    alpha: rendering.hatchAlpha,
                    strokeWidth: rendering.hatchStrokeWidth,
                    spacing: rendering.hatchSpacing,
                  ),
                ),
              ),
              Center(
                child: SizedBox.square(
                  dimension: shortest * rendering.plateRingSizeFactor,
                  child: DecoratedBox(
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      border: Border.all(
                        color: tone.mono.withValues(
                          alpha: rendering.plateRingAlpha,
                        ),
                      ),
                    ),
                  ),
                ),
              ),
              Center(
                child: Padding(
                  padding: EdgeInsets.symmetric(
                    horizontal:
                        constraints.maxWidth *
                        rendering.plateMonogramInsetFactor,
                  ),
                  child: FittedBox(
                    child: Text(
                      monogram,
                      style: context.artwork.coverMonogramStyle.copyWith(
                        fontSize: shortest * rendering.plateMonogramSizeFactor,
                        color: tone.mono.withValues(
                          alpha: rendering.plateMonogramAlpha,
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ],
          );
        },
      ),
    );
  }
}

/// Fine diagonal hatch laid over the plate gradient — the faint "archival film"
/// texture from the spec, kept very low contrast.
final class _HatchPainter extends CustomPainter {
  const _HatchPainter({
    required this.color,
    required this.alpha,
    required this.strokeWidth,
    required this.spacing,
  });

  final Color color;
  final double alpha;
  final double strokeWidth;
  final double spacing;

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = color.withValues(alpha: alpha)
      ..strokeWidth = strokeWidth;

    for (var x = -size.height; x < size.width; x += spacing) {
      canvas.drawLine(
        Offset(x, size.height),
        Offset(x + size.height, 0),
        paint,
      );
    }
  }

  @override
  bool shouldRepaint(covariant _HatchPainter oldDelegate) =>
      color != oldDelegate.color ||
      alpha != oldDelegate.alpha ||
      strokeWidth != oldDelegate.strokeWidth ||
      spacing != oldDelegate.spacing;
}

ImageProvider<Object> artworkImageProvider(Uri uri) => uri.scheme == 'file'
    ? FileImage(File.fromUri(uri))
    : NetworkImage(uri.toString());
