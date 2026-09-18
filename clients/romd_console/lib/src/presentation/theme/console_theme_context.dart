import 'package:flutter/material.dart';

import 'console_theme_extensions.dart';

/// Runtime access to the active console skin.
///
/// Read these values in `build` for ordinary widgets. Stateful objects that
/// copy a themed value into a painter or animation controller must refresh it
/// in `didChangeDependencies`; resolving it only in `initState` leaves stale
/// state after a runtime theme switch.
extension ConsoleThemeContext on BuildContext {
  ThemeData get theme => Theme.of(this);
  ColorScheme get colors => Theme.of(this).colorScheme;
  TextTheme get text => Theme.of(this).textTheme;

  ConsoleColors get consoleColors => Theme.of(this).extension<ConsoleColors>()!;
  ConsoleLayoutTheme get layout =>
      Theme.of(this).extension<ConsoleLayoutTheme>()!;
  ConsoleElevationTheme get elevation =>
      Theme.of(this).extension<ConsoleElevationTheme>()!;
  ConsoleMotionTheme get motion =>
      Theme.of(this).extension<ConsoleMotionTheme>()!;
  ConsoleArtworkTheme get artwork =>
      Theme.of(this).extension<ConsoleArtworkTheme>()!;
}

/// Product-language projections over Flutter's [TextTheme].
///
/// Widgets select a role by meaning and never depend on where that role is
/// stored in the Material type ramp. A skin still supplies one Flutter-native
/// [TextTheme]; these accessors are aliases, not a parallel style registry.
/// This also lets the internal Material mapping evolve without another
/// component-wide migration.
extension ConsoleTextRoles on TextTheme {
  TextStyle get hero => displayLarge!;
  TextStyle get spotlightTitle => displayMedium!;
  TextStyle get pageHeading => displayMedium!;
  TextStyle get sectionHeading => displaySmall!;
  TextStyle get cardTitle => headlineLarge!;
  TextStyle get chromeStatus => headlineMedium!;
  TextStyle get supportingTitle => headlineSmall!;
  TextStyle get sectionLabel => titleMedium!;
  TextStyle get metadataStrong => titleSmall!;
  TextStyle get body => bodyLarge!;
  TextStyle get bodyCompact => bodyMedium!;
  TextStyle get metadata => bodySmall!;
  TextStyle get action => labelLarge!;
  TextStyle get chipLabel => labelMedium!;
  TextStyle get eyebrow => labelSmall!;
  TextStyle get utilityLabel => titleLarge!;
}
