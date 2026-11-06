import 'dart:math' as math;

import 'package:flutter/foundation.dart' show listEquals;
import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';

/// The console's strongest focus signal: an animated teal→mint→cyan sweep
/// border with a soft glow, wrapped around the focused control.
///
/// The gradient rotates slowly while focused, echoing the "alive" focus
/// language of console system UIs. Under reduced motion (or when unfocused)
/// the ring is static. Rest state draws a quiet hairline so focusing never
/// changes the control's footprint.
final class ConsoleFocusRing extends StatefulWidget {
  const ConsoleFocusRing({
    required this.focused,
    required this.child,
    this.borderRadius,
    this.shape = BoxShape.rectangle,
    this.restBorderColor,
    this.ringWidth,
    this.gap,
    super.key,
  });

  final bool focused;
  final Widget child;

  /// Corner radius of the wrapped control. Ignored for [BoxShape.circle].
  final double? borderRadius;
  final BoxShape shape;

  /// Border color when unfocused. Pass [Colors.transparent] for chromeless
  /// rest states (e.g. dock icons).
  final Color? restBorderColor;
  final double? ringWidth;

  /// Breathing room between the child and the ring.
  final double? gap;

  @override
  State<ConsoleFocusRing> createState() => _ConsoleFocusRingState();
}

final class _ConsoleFocusRingState extends State<ConsoleFocusRing>
    with SingleTickerProviderStateMixin {
  late final AnimationController _rotation = AnimationController(vsync: this);

  @override
  void didUpdateWidget(covariant ConsoleFocusRing oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.focused != widget.focused) _syncAnimation();
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _rotation.duration = context.motion.focusRotation;
    _syncAnimation();
  }

  @override
  void dispose() {
    _rotation.dispose();
    super.dispose();
  }

  void _syncAnimation() {
    final animate =
        widget.focused &&
        context.motion.resolve(context, context.motion.focusRotation) !=
            Duration.zero;
    if (animate && !_rotation.isAnimating) {
      _rotation.repeat();
    } else if (!animate && _rotation.isAnimating) {
      _rotation.stop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final layout = context.layout;
    final ringWidth = widget.ringWidth ?? layout.focusRingStroke;
    final gap = widget.gap ?? layout.focusRingGap;
    return AnimatedBuilder(
      animation: _rotation,
      builder: (context, child) => CustomPaint(
        foregroundPainter: _FocusRingPainter(
          focused: widget.focused,
          turns: _rotation.value,
          borderRadius: widget.borderRadius ?? layout.panelRadius,
          shape: widget.shape,
          restBorderColor:
              widget.restBorderColor ?? context.consoleColors.panelBorder,
          ringWidth: ringWidth,
          restStrokeWidth: layout.hairlineStroke,
          focusGradient: context.artwork.focusGradient,
          focusGlow: context.consoleColors.focusGlow,
          focusGlowBlur: layout.focusGlowBlur,
          focusGlowSpread: layout.focusGlowSpread,
          focusGlowInflate: layout.focusGlowInflate,
        ),
        child: child,
      ),
      child: Padding(
        padding: EdgeInsets.all(ringWidth + gap),
        child: widget.child,
      ),
    );
  }
}

final class _FocusRingPainter extends CustomPainter {
  const _FocusRingPainter({
    required this.focused,
    required this.turns,
    required this.borderRadius,
    required this.shape,
    required this.restBorderColor,
    required this.ringWidth,
    required this.restStrokeWidth,
    required this.focusGradient,
    required this.focusGlow,
    required this.focusGlowBlur,
    required this.focusGlowSpread,
    required this.focusGlowInflate,
  });

  final bool focused;
  final double turns;
  final double borderRadius;
  final BoxShape shape;
  final Color restBorderColor;
  final double ringWidth;
  final double restStrokeWidth;
  final List<Color> focusGradient;
  final Color focusGlow;
  final double focusGlowBlur;
  final double focusGlowSpread;
  final double focusGlowInflate;

  @override
  void paint(Canvas canvas, Size size) {
    final rect = Offset.zero & size;
    final strokeWidth = focused ? ringWidth : restStrokeWidth;
    final ringRect = rect.deflate(strokeWidth / 2);

    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = strokeWidth;

    if (focused) {
      paint.shader = SweepGradient(
        colors: focusGradient,
        transform: GradientRotation(turns * 2 * math.pi),
      ).createShader(rect);
    } else {
      if (restBorderColor.a == 0) return;
      paint.color = restBorderColor;
    }

    if (shape == BoxShape.circle) {
      if (focused) {
        canvas.drawCircle(
          rect.center,
          ringRect.shortestSide / 2,
          Paint()
            ..color = focusGlow
            ..maskFilter = MaskFilter.blur(BlurStyle.normal, focusGlowBlur),
        );
      }
      canvas.drawCircle(rect.center, ringRect.shortestSide / 2, paint);
      return;
    }

    final rrect = RRect.fromRectAndRadius(
      ringRect,
      Radius.circular(borderRadius),
    );
    if (focused) {
      canvas.drawRRect(
        rrect.inflate(focusGlowInflate),
        Paint()
          ..color = focusGlow
          ..style = PaintingStyle.stroke
          ..strokeWidth = strokeWidth + focusGlowSpread
          ..maskFilter = MaskFilter.blur(BlurStyle.normal, focusGlowBlur),
      );
    }
    canvas.drawRRect(rrect, paint);
  }

  @override
  bool shouldRepaint(covariant _FocusRingPainter oldDelegate) =>
      oldDelegate.focused != focused ||
      oldDelegate.turns != turns ||
      oldDelegate.borderRadius != borderRadius ||
      oldDelegate.shape != shape ||
      oldDelegate.restBorderColor != restBorderColor ||
      oldDelegate.ringWidth != ringWidth ||
      oldDelegate.restStrokeWidth != restStrokeWidth ||
      !listEquals(oldDelegate.focusGradient, focusGradient) ||
      oldDelegate.focusGlow != focusGlow ||
      oldDelegate.focusGlowBlur != focusGlowBlur ||
      oldDelegate.focusGlowSpread != focusGlowSpread ||
      oldDelegate.focusGlowInflate != focusGlowInflate;
}
