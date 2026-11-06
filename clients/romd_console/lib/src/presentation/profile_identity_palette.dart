import 'package:flutter/material.dart';

/// Persisted profile-identity choices, deliberately outside the theme seam.
///
/// A profile's accent is user content: changing skins must not silently change
/// the color the user selected. Widgets may use these values only to render
/// profile identity (avatars and the accent picker), never as general control
/// or focus styling.
abstract final class ProfileIdentityPalette {
  static const List<Color> accents = <Color>[
    Color(0xff4fe3b0),
    Color(0xff62b6cb),
    Color(0xff7b5cff),
    Color(0xffd65cff),
    Color(0xffff6b6b),
    Color(0xffe6c074),
  ];

  /// Returns a readable foreground for marks drawn directly on [background].
  static Color foregroundFor(Color background) {
    final backgroundLuminance = background.computeLuminance();
    final blackContrast = (backgroundLuminance + 0.05) / 0.05;
    final whiteContrast = 1.05 / (backgroundLuminance + 0.05);
    return whiteContrast >= blackContrast ? Colors.white : Colors.black;
  }
}
