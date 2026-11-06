import 'dart:async';

import 'package:flutter/material.dart';

import '../theme/console_theme_context.dart';

typedef ConsoleTimeFormatter = String Function(DateTime time);

String formatConsoleTime(DateTime time) {
  final hour = time.hour % 12 == 0 ? 12 : time.hour % 12;
  final minute = time.minute.toString().padLeft(2, '0');
  return '$hour:$minute ${time.hour < 12 ? 'AM' : 'PM'}';
}

/// A quiet clock for the top corner of console screens.
final class ConsoleClock extends StatefulWidget {
  const ConsoleClock({
    this.now,
    this.formatter = formatConsoleTime,
    this.compact = false,
    super.key,
  });

  /// Overridable clock source for tests.
  final DateTime Function()? now;
  final ConsoleTimeFormatter formatter;
  final bool compact;

  @override
  State<ConsoleClock> createState() => _ConsoleClockState();
}

final class _ConsoleClockState extends State<ConsoleClock> {
  late DateTime Function() _now = widget.now ?? DateTime.now;
  late DateTime _time = _now();
  Timer? _timer;

  @override
  void initState() {
    super.initState();
    _timer = Timer.periodic(const Duration(seconds: 20), (_) {
      setState(() => _time = _now());
    });
  }

  @override
  void didUpdateWidget(ConsoleClock oldWidget) {
    super.didUpdateWidget(oldWidget);
    _now = widget.now ?? DateTime.now;
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final motion = context.motion;
    return AnimatedDefaultTextStyle(
      duration: motion.resolve(context, motion.chromeTransition),
      curve: motion.spatialCurve,
      style: widget.compact
          ? context.text.utilityLabel
          : context.text.chromeStatus,
      child: Text(
        widget.formatter(_time),
        key: const ValueKey('console-clock-text'),
      ),
    );
  }
}
