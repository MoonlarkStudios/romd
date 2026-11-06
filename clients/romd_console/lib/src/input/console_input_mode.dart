import 'package:flutter/widgets.dart';

enum ConsoleInputMode { keyboard, gamepad }

final class ConsoleInputModeScope
    extends InheritedNotifier<ValueNotifier<ConsoleInputMode>> {
  const ConsoleInputModeScope({
    required ValueNotifier<ConsoleInputMode> notifier,
    required super.child,
    super.key,
  }) : super(notifier: notifier);

  static ConsoleInputMode of(BuildContext context) =>
      context
          .dependOnInheritedWidgetOfExactType<ConsoleInputModeScope>()
          ?.notifier
          ?.value ??
      ConsoleInputMode.keyboard;
}
