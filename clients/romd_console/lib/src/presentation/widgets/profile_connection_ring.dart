import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';
import 'connection_status_indicator.dart';

/// Profile-scoped connection chrome. Connected is a quiet complete ring;
/// connecting is a rotating arc, frozen in place when reduced motion is on.
final class ProfileConnectionRing extends StatefulWidget {
  const ProfileConnectionRing({
    required this.status,
    required this.child,
    this.indicatorKey,
    super.key,
  });

  final ConnectionStatus status;
  final Widget child;
  final Key? indicatorKey;

  @override
  State<ProfileConnectionRing> createState() => _ProfileConnectionRingState();
}

final class _ProfileConnectionRingState extends State<ProfileConnectionRing>
    with SingleTickerProviderStateMixin {
  late final AnimationController _rotation = AnimationController(vsync: this);

  @override
  void didUpdateWidget(covariant ProfileConnectionRing oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.status != widget.status) _syncAnimation();
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _syncAnimation();
  }

  @override
  void dispose() {
    _rotation.dispose();
    super.dispose();
  }

  void _syncAnimation() {
    final motion = context.motion;
    final duration = motion.resolve(context, motion.pulse);
    final animate =
        widget.status == ConnectionStatus.connecting &&
        duration != Duration.zero;
    if (animate) {
      _rotation.duration = duration;
      if (!_rotation.isAnimating) _rotation.repeat();
    } else {
      _rotation
        ..stop()
        ..value = 0;
    }
  }

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: _rotation,
    builder: (context, child) => CustomPaint(
      key: widget.indicatorKey,
      foregroundPainter: _ProfileConnectionRingPainter(
        status: widget.status,
        turns: _rotation.value,
        connectedColor: context.consoleColors.connected,
        connectingColor: context.consoleColors.catalogAccent,
        strokeWidth: context.layout.hairlineStroke,
      ),
      child: child,
    ),
    child: widget.child,
  );
}

final class _ProfileConnectionRingPainter extends CustomPainter {
  const _ProfileConnectionRingPainter({
    required this.status,
    required this.turns,
    required this.connectedColor,
    required this.connectingColor,
    required this.strokeWidth,
  });

  final ConnectionStatus status;
  final double turns;
  final Color connectedColor;
  final Color connectingColor;
  final double strokeWidth;

  @override
  void paint(Canvas canvas, Size size) {
    if (status != ConnectionStatus.connected &&
        status != ConnectionStatus.connecting) {
      return;
    }
    final rect = (Offset.zero & size).deflate(strokeWidth / 2);
    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = strokeWidth
      ..strokeCap = StrokeCap.round;
    if (status == ConnectionStatus.connected) {
      canvas.drawOval(rect, paint..color = connectedColor);
      return;
    }
    canvas.drawArc(
      rect,
      turns * 2 * math.pi - math.pi / 2,
      math.pi * 1.35,
      false,
      paint..color = connectingColor,
    );
  }

  @override
  bool shouldRepaint(_ProfileConnectionRingPainter oldDelegate) =>
      oldDelegate.status != status ||
      oldDelegate.turns != turns ||
      oldDelegate.connectedColor != connectedColor ||
      oldDelegate.connectingColor != connectingColor ||
      oldDelegate.strokeWidth != strokeWidth;
}
