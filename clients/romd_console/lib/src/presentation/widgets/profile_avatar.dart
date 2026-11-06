import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';

/// The procedural avatar "looks" a profile can choose from. Stored as the
/// profile's `avatarKey`; rendered live so they retint with the accent color.
enum ProfileAvatarStyle {
  orbit,
  pulse,
  wave,
  prism,
  comet,
  hex;

  static ProfileAvatarStyle fromKey(String key) =>
      ProfileAvatarStyle.values.firstWhere(
        (style) => style.name == key,
        orElse: () => ProfileAvatarStyle.orbit,
      );
}

/// A circular, asset-free avatar painted from an [avatarKey] and [accentColor].
final class ProfileAvatar extends StatelessWidget {
  const ProfileAvatar({
    required this.avatarKey,
    required this.accentColor,
    required this.size,
    super.key,
  });

  final String avatarKey;
  final Color accentColor;
  final double size;

  @override
  Widget build(BuildContext context) => SizedBox.square(
    dimension: size,
    child: ClipOval(
      child: CustomPaint(
        painter: _AvatarPainter(
          style: ProfileAvatarStyle.fromKey(avatarKey),
          accent: accentColor,
          highlight: context.artwork.highlight,
          shadow: context.artwork.shadow,
          rim: context.artwork.avatarRim,
        ),
        size: Size.square(size),
      ),
    ),
  );
}

final class _AvatarPainter extends CustomPainter {
  const _AvatarPainter({
    required this.style,
    required this.accent,
    required this.highlight,
    required this.shadow,
    required this.rim,
  });

  final ProfileAvatarStyle style;
  final Color accent;
  final Color highlight;
  final Color shadow;
  final Color rim;

  Color get _bright => Color.lerp(accent, highlight, 0.32)!;

  @override
  void paint(Canvas canvas, Size size) {
    final rect = Offset.zero & size;
    final center = rect.center;
    final radius = size.shortestSide / 2;

    // Base disk: a soft top-left lit gradient over a deep accent shade.
    canvas.drawRect(
      rect,
      Paint()
        ..shader = RadialGradient(
          center: const Alignment(-0.45, -0.55),
          radius: 1.3,
          colors: <Color>[
            Color.lerp(accent, shadow, 0.42)!,
            Color.lerp(accent, shadow, 0.8)!,
          ],
        ).createShader(rect),
    );

    switch (style) {
      case ProfileAvatarStyle.orbit:
        _paintOrbit(canvas, center, radius);
      case ProfileAvatarStyle.pulse:
        _paintPulse(canvas, center, radius);
      case ProfileAvatarStyle.wave:
        _paintWave(canvas, rect, center, radius);
      case ProfileAvatarStyle.prism:
        _paintPrism(canvas, center, radius);
      case ProfileAvatarStyle.comet:
        _paintComet(canvas, center, radius);
      case ProfileAvatarStyle.hex:
        _paintHex(canvas, center, radius);
    }

    // Inner rim to seat the art inside the circular frame.
    canvas.drawCircle(
      center,
      radius - 0.5,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1
        ..color = rim,
    );
  }

  void _paintOrbit(Canvas canvas, Offset center, double radius) {
    final ring = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = radius * 0.07;
    for (var i = 0; i < 3; i++) {
      final r = radius * (0.32 + i * 0.22);
      canvas.drawCircle(
        center,
        r,
        ring..color = accent.withValues(alpha: 0.55 - i * 0.13),
      );
    }
    final node = Offset(
      center.dx + radius * 0.54 * math.cos(-0.9),
      center.dy + radius * 0.54 * math.sin(-0.9),
    );
    canvas.drawCircle(node, radius * 0.14, Paint()..color = _bright);
  }

  void _paintPulse(Canvas canvas, Offset center, double radius) {
    for (var i = 3; i >= 1; i--) {
      canvas.drawCircle(
        center,
        radius * 0.26 * i,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = radius * 0.06
          ..color = accent.withValues(alpha: 0.22 + (3 - i) * 0.16),
      );
    }
    canvas.drawCircle(center, radius * 0.2, Paint()..color = _bright);
  }

  void _paintWave(Canvas canvas, Rect rect, Offset center, double radius) {
    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = radius * 0.08
      ..strokeCap = StrokeCap.round;
    for (var line = 0; line < 3; line++) {
      final path = Path();
      final baseY = center.dy + (line - 1) * radius * 0.46;
      for (var x = 0.0; x <= rect.width; x += rect.width / 24) {
        final y =
            baseY +
            math.sin(x / rect.width * math.pi * 2 + line) * radius * 0.2;
        if (x == 0) {
          path.moveTo(x, y);
        } else {
          path.lineTo(x, y);
        }
      }
      canvas.drawPath(
        path,
        paint..color = (line == 1 ? _bright : accent).withValues(alpha: 0.7),
      );
    }
  }

  void _paintPrism(Canvas canvas, Offset center, double radius) {
    final paint = Paint()..blendMode = BlendMode.plus;
    for (var i = 0; i < 3; i++) {
      // Offset each equilateral triangle by 36° (not a multiple of its 120°
      // symmetry) so the layers form a prism star instead of overlapping exactly.
      final angle = i * math.pi / 5 - math.pi / 2;
      final path = Path();
      for (var v = 0; v < 3; v++) {
        final a = angle + v * 2 * math.pi / 3;
        final point = Offset(
          center.dx + radius * 0.66 * math.cos(a),
          center.dy + radius * 0.66 * math.sin(a),
        );
        v == 0
            ? path.moveTo(point.dx, point.dy)
            : path.lineTo(point.dx, point.dy);
      }
      path.close();
      canvas.drawPath(path, paint..color = accent.withValues(alpha: 0.34));
    }
  }

  void _paintComet(Canvas canvas, Offset center, double radius) {
    final rect = Rect.fromCircle(center: center, radius: radius * 0.6);
    canvas.drawArc(
      rect,
      math.pi * 0.75,
      math.pi * 1.1,
      false,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = radius * 0.12
        ..strokeCap = StrokeCap.round
        ..shader = SweepGradient(
          startAngle: math.pi * 0.75,
          endAngle: math.pi * 1.85,
          colors: <Color>[accent.withValues(alpha: 0), _bright],
        ).createShader(rect),
    );
    final head = Offset(
      center.dx + radius * 0.6 * math.cos(math.pi * 1.85),
      center.dy + radius * 0.6 * math.sin(math.pi * 1.85),
    );
    canvas.drawCircle(head, radius * 0.15, Paint()..color = _bright);
  }

  void _paintHex(Canvas canvas, Offset center, double radius) {
    Path hexagon(double r) {
      final path = Path();
      for (var i = 0; i < 6; i++) {
        final a = i * math.pi / 3 - math.pi / 2;
        final point = Offset(
          center.dx + r * math.cos(a),
          center.dy + r * math.sin(a),
        );
        i == 0
            ? path.moveTo(point.dx, point.dy)
            : path.lineTo(point.dx, point.dy);
      }
      return path..close();
    }

    canvas.drawPath(
      hexagon(radius * 0.66),
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = radius * 0.08
        ..color = accent.withValues(alpha: 0.6),
    );
    canvas.drawPath(hexagon(radius * 0.34), Paint()..color = _bright);
  }

  @override
  bool shouldRepaint(_AvatarPainter oldDelegate) =>
      oldDelegate.style != style ||
      oldDelegate.accent != accent ||
      oldDelegate.highlight != highlight ||
      oldDelegate.shadow != shadow ||
      oldDelegate.rim != rim;
}
