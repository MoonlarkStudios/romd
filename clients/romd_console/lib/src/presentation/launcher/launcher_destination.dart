import 'package:flutter/widgets.dart';

enum LauncherDestination { home, library }

abstract interface class LauncherDestinationFocus {
  void enterFromHeader();

  void restoreContentFocus();
}

bool moveLauncherFocus(TraversalDirection direction) =>
    FocusManager.instance.primaryFocus?.focusInDirection(direction) ?? false;
