import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';

/// Shared text-entry caret that follows the active motion and color skin.
///
/// The animation controller refreshes in [State.didChangeDependencies], so a
/// mounted editor responds to runtime theme and reduced-motion changes.
final class ConsoleBlinkingCaret extends StatefulWidget {
  const ConsoleBlinkingCaret({
    required this.height,
    this.margin = EdgeInsets.zero,
    this.color,
    super.key,
  });

  final double height;
  final EdgeInsetsGeometry margin;
  final Color? color;

  @override
  State<ConsoleBlinkingCaret> createState() => ConsoleBlinkingCaretState();
}

final class ConsoleBlinkingCaretState extends State<ConsoleBlinkingCaret>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController(vsync: this);

  @visibleForTesting
  Duration? get debugDuration => _controller.duration;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final motion = context.motion;
    final duration = motion.resolve(context, motion.pulse);
    if (duration == Duration.zero) {
      _controller
        ..stop()
        ..value = 0;
      return;
    }
    _controller.duration = duration;
    if (!_controller.isAnimating) {
      _controller.repeat(reverse: true);
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => FadeTransition(
    opacity: _controller.drive(Tween<double>(begin: 1, end: 0.1)),
    child: Container(
      width: context.layout.focusRingStroke,
      height: widget.height,
      margin: widget.margin,
      color: widget.color ?? context.consoleColors.focusBorder,
    ),
  );
}
