import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';

import '../theme/console_theme_context.dart';
import '../theme/console_theme_extensions.dart';

enum ConsoleAmbientBackgroundConcept { signalTide, emberHorizon, glassWake }

/// Low cinematic particle flow used behind every console screen.
///
/// Mount this once (see `ConsoleRootScreen`) so motion stays continuous through
/// screen transitions. The effect is deterministic and paints a dark backdrop,
/// faint geometric flow lines, and particles seeded along low sine-wave bands.
final class ConsoleAmbientBackground extends StatefulWidget {
  const ConsoleAmbientBackground({
    this.concept = ConsoleAmbientBackgroundConcept.signalTide,
    this.dimmed = false,
    this.effectIntensity = 1,
    this.tone,
    this.child,
    super.key,
  });

  final ConsoleAmbientBackgroundConcept concept;

  /// When true, the flow freezes on a static frame and the atmosphere fades
  /// back (the backdrop tone stays). The entry stage keeps the flow active;
  /// in-app surfaces stay cinematic without spending CPU and GPU on an obscured
  /// full-screen animation.
  final bool dimmed;

  /// Scales the particles, ribbons, and atmosphere without changing the
  /// underlying backdrop. Attract uses a restrained current; profile selection
  /// restores the full established treatment.
  final double effectIntensity;

  /// Optional theme-owned tone layered over the ambient artwork.
  ///
  /// Callers own the scope of this accent. Changes cross-fade using the
  /// console's content-transition motion token.
  final Color? tone;

  final Widget? child;

  @override
  State<ConsoleAmbientBackground> createState() =>
      _ConsoleAmbientBackgroundState();
}

final class _ConsoleAmbientBackgroundState
    extends State<ConsoleAmbientBackground>
    with SingleTickerProviderStateMixin {
  final ValueNotifier<double> _seconds = ValueNotifier<double>(0);
  late final Ticker _ticker;
  double _tickerStartSeconds = 0;
  late _FlowField _field;
  ConsoleAmbientPalette? _palette;
  Color? _highlight;
  Color? _shadow;
  ConsoleAmbientGeometrySpec? _geometry;
  ConsoleAmbientRenderSpec? _render;

  @override
  void initState() {
    super.initState();
    _ticker = createTicker((elapsed) {
      _seconds.value =
          _tickerStartSeconds +
          elapsed.inMicroseconds / Duration.microsecondsPerSecond;
    });
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _resolveArtwork();
    _syncTicker();
  }

  @override
  void didUpdateWidget(covariant ConsoleAmbientBackground oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.concept != widget.concept) {
      _resolveArtwork(force: true);
    }
    if (oldWidget.dimmed != widget.dimmed) {
      _syncTicker();
    }
  }

  void _syncTicker() {
    final reduceMotion =
        MediaQuery.maybeOf(context)?.disableAnimations ?? false;
    final shouldAnimate = !widget.dimmed && !reduceMotion;
    if (shouldAnimate && !_ticker.isActive) {
      _tickerStartSeconds = _seconds.value;
      _ticker.start();
    } else if (!shouldAnimate && _ticker.isActive) {
      _ticker.stop();
    }
  }

  void _resolveArtwork({bool force = false}) {
    final artwork = context.artwork;
    final palettes = artwork.ambientPalettes;
    final geometries = artwork.ambientGeometry;
    assert(
      palettes.length > widget.concept.index,
      'Every ambient concept requires a skin palette.',
    );
    assert(
      geometries.length > widget.concept.index,
      'Every ambient concept requires a skin geometry specification.',
    );
    final palette = palettes[widget.concept.index];
    final geometry = geometries[widget.concept.index];
    final render = artwork.ambientRender;
    if (force ||
        _palette != palette ||
        _highlight != artwork.highlight ||
        _shadow != artwork.shadow ||
        _geometry != geometry ||
        _render != render) {
      _palette = palette;
      _highlight = artwork.highlight;
      _shadow = artwork.shadow;
      _geometry = geometry;
      _render = render;
      _field = _FlowField.build(
        geometry,
        render,
        palette,
        artwork.highlight,
        artwork.shadow,
      );
    }
  }

  @override
  void dispose() {
    _ticker.dispose();
    _seconds.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final look = _field.look;
    final palette = _palette!;
    final intensity = context.artwork.ambientIntensity;

    return DecoratedBox(
      decoration: BoxDecoration(
        gradient: RadialGradient(
          center: look.backdropCenter,
          radius: _render!.backdropRadius,
          colors: <Color>[
            look.backdropTop,
            look.backdropMiddle,
            look.backdropBottom,
          ],
          stops: <double>[0, _render!.backdropMiddleStop, 1],
        ),
      ),
      child: Stack(
        fit: StackFit.expand,
        children: <Widget>[
          AnimatedOpacity(
            duration: context.motion.resolve(
              context,
              context.motion.contentTransition,
            ),
            curve: context.motion.standardCurve,
            opacity:
                ((widget.dimmed ? 0.5 : 1) * intensity * widget.effectIntensity)
                    .clamp(0, 1),
            child: Stack(
              fit: StackFit.expand,
              children: <Widget>[
                RepaintBoundary(
                  child: CustomPaint(
                    painter: _FlowPainter(field: _field, seconds: _seconds),
                  ),
                ),
                IgnorePointer(
                  child: _Atmosphere(
                    look: look,
                    render: _render!,
                    spotlight: palette.spotlight,
                  ),
                ),
              ],
            ),
          ),
          Opacity(
            opacity: (0.1 * intensity).clamp(0, 1),
            child: AnimatedSwitcher(
              duration: context.motion.resolve(
                context,
                context.motion.contentTransition,
              ),
              switchInCurve: context.motion.emphasizedCurve,
              switchOutCurve: context.motion.standardCurve,
              child: widget.tone == null
                  ? const SizedBox.expand(
                      key: ValueKey<String>('console-ambient-tone-none'),
                    )
                  : ColoredBox(
                      key: ValueKey<Color>(widget.tone!),
                      color: widget.tone!,
                    ),
            ),
          ),
          if (widget.child case final child?) child,
        ],
      ),
    );
  }
}

final class _FlowLook {
  const _FlowLook({
    required this.backdropCenter,
    required this.backdropTop,
    required this.backdropMiddle,
    required this.backdropBottom,
    required this.primaryParticle,
    required this.secondaryParticle,
    required this.warmParticle,
    required this.meshColor,
    required this.starColor,
    required this.highlight,
    required this.shadow,
    required this.particleAlpha,
    required this.meshAlpha,
    required this.starAlpha,
    required this.warmParticleChance,
    required this.warmAccentAlpha,
    required this.motionScale,
    required this.particleCount,
    required this.falloutCount,
    required this.starCount,
    required this.layers,
  });

  factory _FlowLook.fromTheme(
    ConsoleAmbientGeometrySpec geometry,
    ConsoleAmbientPalette palette,
    Color highlight,
    Color shadow,
  ) => _FlowLook(
    backdropCenter: geometry.backdropCenter,
    backdropTop: palette.backdropTop,
    backdropMiddle: palette.backdropMiddle,
    backdropBottom: palette.backdropBottom,
    primaryParticle: palette.particlePrimary,
    secondaryParticle: palette.particleSecondary,
    warmParticle: palette.particleWarm,
    meshColor: palette.mesh,
    starColor: palette.star,
    highlight: highlight,
    shadow: shadow,
    particleAlpha: geometry.particleAlpha,
    meshAlpha: geometry.meshAlpha,
    starAlpha: geometry.starAlpha,
    warmParticleChance: geometry.warmParticleChance,
    warmAccentAlpha: geometry.warmAccentAlpha,
    motionScale: geometry.motionScale,
    particleCount: geometry.particleCount,
    falloutCount: geometry.falloutCount,
    starCount: geometry.starCount,
    layers: <_WaveLayer>[
      for (final wave in geometry.waves)
        _WaveLayer.fromTheme(wave, switch (wave.tone) {
          ConsoleAmbientWaveTone.primary => palette.wavePrimary,
          ConsoleAmbientWaveTone.secondary => palette.waveSecondary,
          ConsoleAmbientWaveTone.tertiary => palette.waveTertiary,
        }),
    ],
  );

  final Alignment backdropCenter;
  final Color backdropTop;
  final Color backdropMiddle;
  final Color backdropBottom;
  final Color primaryParticle;
  final Color secondaryParticle;
  final Color warmParticle;
  final Color meshColor;
  final Color starColor;
  final Color highlight;
  final Color shadow;
  final double particleAlpha;
  final double meshAlpha;
  final double starAlpha;
  final double warmParticleChance;
  final double warmAccentAlpha;
  final double motionScale;
  final int particleCount;
  final int falloutCount;
  final int starCount;
  final List<_WaveLayer> layers;
}

final class _WaveLayer {
  const _WaveLayer({
    required this.baseY,
    required this.amplitude,
    required this.secondaryAmplitude,
    required this.frequency,
    required this.secondaryFrequency,
    required this.phase,
    required this.speed,
    required this.spread,
    required this.color,
    required this.alpha,
    required this.glowWidth,
    required this.lineWidth,
    required this.hazeBlur,
  });

  factory _WaveLayer.fromTheme(ConsoleAmbientWaveSpec wave, Color color) =>
      _WaveLayer(
        baseY: wave.baseY,
        amplitude: wave.amplitude,
        secondaryAmplitude: wave.secondaryAmplitude,
        frequency: wave.frequency,
        secondaryFrequency: wave.secondaryFrequency,
        phase: wave.phase,
        speed: wave.speed,
        spread: wave.spread,
        color: color,
        alpha: wave.alpha,
        glowWidth: wave.glowWidth,
        lineWidth: wave.lineWidth,
        hazeBlur: wave.hazeBlur,
      );

  final double baseY;
  final double amplitude;
  final double secondaryAmplitude;
  final double frequency;
  final double secondaryFrequency;
  final double phase;
  final double speed;
  final double spread;
  final Color color;
  final double alpha;
  final double glowWidth;
  final double lineWidth;
  final double hazeBlur;
}

final class _FlowField {
  const _FlowField({
    required this.look,
    required this.render,
    required this.particles,
    required this.fallout,
    required this.stars,
  });

  factory _FlowField.build(
    ConsoleAmbientGeometrySpec geometry,
    ConsoleAmbientRenderSpec render,
    ConsoleAmbientPalette palette,
    Color highlight,
    Color shadow,
  ) {
    final random = math.Random(geometry.seed);
    final look = _FlowLook.fromTheme(geometry, palette, highlight, shadow);

    return _FlowField(
      look: look,
      render: render,
      particles: <_FlowParticle>[
        for (var i = 0; i < look.particleCount; i++)
          _FlowParticle.random(random, look, render),
      ],
      fallout: <_FlowFallout>[
        for (var i = 0; i < look.falloutCount; i++)
          _FlowFallout.random(random, look, render),
      ],
      stars: <_SkySpark>[
        for (var i = 0; i < look.starCount; i++)
          _SkySpark.random(random, look, render),
      ],
    );
  }

  final _FlowLook look;
  final ConsoleAmbientRenderSpec render;
  final List<_FlowParticle> particles;
  final List<_FlowFallout> fallout;
  final List<_SkySpark> stars;
}

final class _FlowParticle {
  const _FlowParticle({
    required this.layerIndex,
    required this.progress,
    required this.bandOffset,
    required this.radius,
    required this.alpha,
    required this.speed,
    required this.phase,
    required this.shimmerFrequency,
    required this.color,
  });

  factory _FlowParticle.random(
    math.Random random,
    _FlowLook look,
    ConsoleAmbientRenderSpec render,
  ) {
    return _FlowParticle(
      layerIndex: _flowLayerIndex(random.nextDouble(), render),
      progress: random.nextDouble(),
      bandOffset:
          (random.nextDouble() + random.nextDouble() - 1) *
          render.particleBandOffsetScale,
      radius: _rangeValues(random, render.particleRadius),
      alpha: _rangeValues(random, render.particleAlpha),
      speed: _rangeValues(random, render.particleSpeed),
      phase: random.nextDouble() * math.pi * 2,
      shimmerFrequency: _rangeValues(random, render.particleShimmerFrequency),
      color: _flowParticleColor(random.nextDouble(), look, render),
    );
  }

  final int layerIndex;
  final double progress;
  final double bandOffset;
  final double radius;
  final double alpha;
  final double speed;
  final double phase;
  final double shimmerFrequency;
  final Color color;
}

final class _FlowFallout {
  const _FlowFallout({
    required this.progress,
    required this.drop,
    required this.radius,
    required this.alpha,
    required this.speed,
    required this.phase,
    required this.color,
  });

  factory _FlowFallout.random(
    math.Random random,
    _FlowLook look,
    ConsoleAmbientRenderSpec render,
  ) {
    final color =
        random.nextDouble() <
            look.warmParticleChance * render.falloutWarmChanceScale
        ? look.warmParticle
        : look.primaryParticle;

    return _FlowFallout(
      progress: random.nextDouble(),
      drop: _rangeValues(random, render.falloutDrop),
      radius: _rangeValues(random, render.falloutRadius),
      alpha: _rangeValues(random, render.falloutAlpha),
      speed: _rangeValues(random, render.falloutSpeed),
      phase: random.nextDouble() * math.pi * 2,
      color: color,
    );
  }

  final double progress;
  final double drop;
  final double radius;
  final double alpha;
  final double speed;
  final double phase;
  final Color color;
}

final class _SkySpark {
  const _SkySpark({
    required this.x,
    required this.y,
    required this.radius,
    required this.alpha,
    required this.phase,
    required this.twinkleFrequency,
    required this.color,
  });

  factory _SkySpark.random(
    math.Random random,
    _FlowLook look,
    ConsoleAmbientRenderSpec render,
  ) => _SkySpark(
    x: random.nextDouble(),
    y: _rangeValues(random, render.starY),
    radius: _rangeValues(random, render.starRadius),
    alpha: _rangeValues(random, render.starAlpha),
    phase: random.nextDouble() * math.pi * 2,
    twinkleFrequency: _rangeValues(random, render.starTwinkleFrequency),
    color: random.nextDouble() < render.starWarmChance
        ? look.warmParticle
        : look.starColor,
  );

  final double x;
  final double y;
  final double radius;
  final double alpha;
  final double phase;
  final double twinkleFrequency;
  final Color color;
}

final class _FlowPainter extends CustomPainter {
  _FlowPainter({required this.field, required this.seconds})
    : super(repaint: seconds);

  final _FlowField field;
  final ValueListenable<double> seconds;

  @override
  void paint(Canvas canvas, Size size) {
    final t = seconds.value * field.look.motionScale;

    _drawSkySparks(canvas, size, t);
    _drawFlowHaze(canvas, size, t);
    _drawGeometry(canvas, size, t);
    _drawRibbons(canvas, size, t);
    _drawFallout(canvas, size, t);
    _drawParticles(canvas, size, t);
  }

  void _drawSkySparks(Canvas canvas, Size size, double t) {
    final shortest = size.shortestSide;
    final render = field.render;
    final paint = Paint()..blendMode = BlendMode.plus;

    for (final spark in field.stars) {
      final twinkle =
          render.starTwinkleBase +
          render.starTwinkleAmplitude *
              math.sin(t * spark.twinkleFrequency + spark.phase);
      final x =
          spark.x * size.width +
          math.sin(t * render.starDriftFrequency + spark.phase) *
              shortest *
              render.starDriftScale;
      final alpha = spark.alpha * field.look.starAlpha * _clampUnit(twinkle);

      paint.color = spark.color.withValues(alpha: alpha);
      canvas.drawCircle(
        Offset(x, spark.y * size.height),
        spark.radius * shortest,
        paint,
      );
    }
  }

  void _drawFlowHaze(Canvas canvas, Size size, double t) {
    final shortest = size.shortestSide;
    final render = field.render;

    for (final layer in field.look.layers.reversed) {
      final path = _buildWavePath(layer, size, t, 0);
      final hazePaint = Paint()
        ..blendMode = BlendMode.plus
        ..color = layer.color.withValues(
          alpha: layer.alpha * render.hazeAlphaScale,
        )
        ..maskFilter = MaskFilter.blur(BlurStyle.normal, layer.hazeBlur)
        ..strokeCap = StrokeCap.round
        ..strokeJoin = StrokeJoin.round
        ..strokeWidth = layer.glowWidth * shortest
        ..style = PaintingStyle.stroke;

      canvas.drawPath(path, hazePaint);
    }
  }

  void _drawGeometry(Canvas canvas, Size size, double t) {
    if (field.look.meshAlpha <= 0) {
      return;
    }

    final shortest = size.shortestSide;
    final render = field.render;
    final meshPaint = Paint()
      ..blendMode = BlendMode.plus
      ..color = field.look.meshColor.withValues(alpha: field.look.meshAlpha)
      ..strokeWidth = math.max(
        render.meshMinimumStroke,
        shortest * render.meshStrokeScale,
      )
      ..style = PaintingStyle.stroke;

    for (final layer in field.look.layers) {
      for (final offset in render.meshOffsets) {
        final path = _buildWavePath(
          layer,
          size,
          t,
          offset * layer.spread * shortest,
        );
        canvas.drawPath(path, meshPaint);
      }
    }
  }

  void _drawRibbons(Canvas canvas, Size size, double t) {
    final shortest = size.shortestSide;
    final rect = Offset.zero & size;
    final render = field.render;

    for (final layer in field.look.layers) {
      final path = _buildWavePath(layer, size, t, 0);
      final glowPaint = Paint()
        ..blendMode = BlendMode.plus
        ..color = layer.color.withValues(
          alpha: layer.alpha * render.ribbonGlowAlphaScale,
        )
        ..maskFilter = MaskFilter.blur(BlurStyle.normal, render.ribbonGlowBlur)
        ..strokeCap = StrokeCap.round
        ..strokeJoin = StrokeJoin.round
        ..strokeWidth = math.max(
          render.ribbonGlowMinimumStroke,
          layer.lineWidth * shortest * render.ribbonGlowWidthScale,
        )
        ..style = PaintingStyle.stroke;
      final linePaint = Paint()
        ..blendMode = BlendMode.plus
        ..shader = LinearGradient(
          colors: <Color>[
            layer.color.withValues(alpha: 0),
            layer.color.withValues(
              alpha: layer.alpha * render.ribbonLineAlphaScale,
            ),
            field.look.highlight.withValues(
              alpha: layer.alpha * render.ribbonHighlightAlphaScale,
            ),
            layer.color.withValues(
              alpha: layer.alpha * render.ribbonTrailingAlphaScale,
            ),
            layer.color.withValues(alpha: 0),
          ],
          stops: render.ribbonGradientStops,
        ).createShader(rect)
        ..strokeCap = StrokeCap.round
        ..strokeJoin = StrokeJoin.round
        ..strokeWidth = math.max(
          render.ribbonLineMinimumStroke,
          layer.lineWidth * shortest,
        )
        ..style = PaintingStyle.stroke;

      canvas.drawPath(path, glowPaint);
      canvas.drawPath(path, linePaint);
    }

    if (field.look.warmAccentAlpha > 0) {
      final primary = field.look.layers.first;
      final accentPath = _buildWavePathRange(
        primary,
        size,
        t,
        primary.spread * shortest * render.warmPathVerticalOffsetScale,
        render.warmPathStart,
        render.warmPathEnd,
      );
      final warmPaint = Paint()
        ..blendMode = BlendMode.plus
        ..shader = LinearGradient(
          colors: <Color>[
            field.look.warmParticle.withValues(alpha: 0),
            field.look.warmParticle.withValues(
              alpha: field.look.warmAccentAlpha,
            ),
            field.look.warmParticle.withValues(alpha: 0),
          ],
          stops: render.warmGradientStops,
        ).createShader(rect)
        ..maskFilter = MaskFilter.blur(BlurStyle.normal, render.warmGlowBlur)
        ..strokeCap = StrokeCap.round
        ..strokeJoin = StrokeJoin.round
        ..strokeWidth = math.max(
          render.warmMinimumStroke,
          shortest * render.warmStrokeScale,
        )
        ..style = PaintingStyle.stroke;

      canvas.drawPath(accentPath, warmPaint);
    }
  }

  void _drawFallout(Canvas canvas, Size size, double t) {
    final shortest = size.shortestSide;
    final primary = field.look.layers.first;
    final render = field.render;
    final paint = Paint()..blendMode = BlendMode.plus;

    for (final particle in field.fallout) {
      final u = (particle.progress + t * particle.speed) % 1;
      final base = _pointFor(primary, u, size, t, 0);
      final sway =
          math.sin(t * render.falloutSwayFrequency + particle.phase) *
          shortest *
          render.falloutSwayScale;
      final y = base.dy + particle.drop * size.height;
      if (y > size.height) {
        continue;
      }

      final fade = _clampUnit(1.0 - particle.drop * render.falloutFadeScale);
      paint.color = particle.color.withValues(
        alpha: particle.alpha * field.look.particleAlpha * fade,
      );
      canvas.drawCircle(
        Offset(base.dx + sway, y),
        particle.radius * shortest,
        paint,
      );
    }
  }

  void _drawParticles(Canvas canvas, Size size, double t) {
    final shortest = size.shortestSide;
    final paint = Paint()..blendMode = BlendMode.plus;

    for (final particle in field.particles) {
      final layer = field.look.layers[particle.layerIndex];
      final u = (particle.progress + t * particle.speed) % 1;
      final offset = particle.bandOffset * layer.spread * shortest;
      final point = _pointFor(layer, u, size, t, offset);
      final shimmer =
          field.render.particleShimmerBase +
          field.render.particleShimmerAmplitude *
              math.sin(t * particle.shimmerFrequency + particle.phase);
      final alpha =
          particle.alpha * field.look.particleAlpha * _clampUnit(shimmer);

      paint.color = particle.color.withValues(alpha: alpha);
      canvas.drawCircle(point, particle.radius * shortest, paint);
    }
  }

  Path _buildWavePath(_WaveLayer layer, Size size, double t, double yOffset) =>
      _buildWavePathRange(layer, size, t, yOffset, 0, 1);

  Path _buildWavePathRange(
    _WaveLayer layer,
    Size size,
    double t,
    double yOffset,
    double start,
    double end,
  ) {
    final path = Path();
    for (var i = 0; i <= field.render.pathSegments; i++) {
      final u = start + (end - start) * i / field.render.pathSegments;
      final point = _pointFor(layer, u, size, t, yOffset);
      if (i == 0) {
        path.moveTo(point.dx, point.dy);
      } else {
        path.lineTo(point.dx, point.dy);
      }
    }
    return path;
  }

  Offset _pointFor(
    _WaveLayer layer,
    double u,
    Size size,
    double t,
    double yOffset,
  ) {
    final y = _waveY(layer, u, t) * size.height + yOffset;
    return Offset(u * size.width, y);
  }

  double _waveY(_WaveLayer layer, double u, double t) {
    final primary =
        (u * layer.frequency + t * layer.speed + layer.phase) * math.pi * 2;
    final secondary =
        (u * layer.secondaryFrequency -
            t * layer.speed * field.render.waveSecondaryTimeScale +
            layer.phase) *
        math.pi *
        2;
    return layer.baseY +
        math.sin(primary) * layer.amplitude +
        math.sin(secondary) * layer.secondaryAmplitude;
  }

  @override
  bool shouldRepaint(_FlowPainter oldDelegate) => oldDelegate.field != field;
}

final class _Atmosphere extends StatelessWidget {
  const _Atmosphere({
    required this.look,
    required this.render,
    required this.spotlight,
  });

  final _FlowLook look;
  final ConsoleAmbientRenderSpec render;
  final Color spotlight;

  @override
  Widget build(BuildContext context) => Stack(
    fit: StackFit.expand,
    children: <Widget>[
      DecoratedBox(
        decoration: BoxDecoration(
          gradient: RadialGradient(
            center: render.atmosphereSpotlightCenter,
            radius: render.atmosphereSpotlightRadius,
            colors: <Color>[
              spotlight.withValues(alpha: render.atmosphereSpotlightAlpha),
              look.warmParticle.withValues(
                alpha: look.warmAccentAlpha * render.atmosphereWarmAlphaScale,
              ),
              spotlight.withValues(alpha: 0),
            ],
            stops: render.atmosphereSpotlightStops,
          ),
        ),
      ),
      DecoratedBox(
        decoration: BoxDecoration(
          gradient: LinearGradient(
            begin: Alignment.topCenter,
            end: Alignment.bottomCenter,
            colors: <Color>[
              look.shadow.withValues(alpha: render.atmosphereShadowTopAlpha),
              Colors.transparent,
              look.shadow.withValues(alpha: render.atmosphereShadowBottomAlpha),
            ],
            stops: <double>[0, render.atmosphereShadowMiddleStop, 1],
          ),
        ),
      ),
    ],
  );
}

double _range(math.Random random, double min, double max) =>
    min + random.nextDouble() * (max - min);

double _rangeValues(math.Random random, RangeValues range) =>
    _range(random, range.start, range.end);

double _clampUnit(double value) => value.clamp(0.0, 1.0).toDouble();

Color _flowParticleColor(
  double roll,
  _FlowLook look,
  ConsoleAmbientRenderSpec render,
) {
  if (roll < look.warmParticleChance) {
    return look.warmParticle;
  }

  if (roll < render.primaryParticleThreshold) {
    return look.primaryParticle;
  }

  return look.secondaryParticle;
}

int _flowLayerIndex(double roll, ConsoleAmbientRenderSpec render) {
  assert(render.layerThresholds.length == 2);
  if (roll < render.layerThresholds[0]) {
    return 0;
  }

  if (roll < render.layerThresholds[1]) {
    return 1;
  }

  return 2;
}
