import 'package:flutter/widgets.dart';

import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';

/// The active glyph family — the chosen controller template's label style —
/// available to every route (provided above the Navigator).
final class GlyphFamilyScope
    extends InheritedNotifier<ValueNotifier<GlyphFamily>> {
  const GlyphFamilyScope({
    required ValueNotifier<GlyphFamily> notifier,
    required super.child,
    super.key,
  }) : super(notifier: notifier);

  static GlyphFamily of(BuildContext context) =>
      context
          .dependOnInheritedWidgetOfExactType<GlyphFamilyScope>()
          ?.notifier
          ?.value ??
      GlyphFamily.generic;

  /// The backing notifier, for the Controllers screen to push a new family
  /// when the user picks a template. Null outside the scope (tests).
  static ValueNotifier<GlyphFamily>? maybeNotifierOf(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<GlyphFamilyScope>()?.notifier;
}

/// Translates an Xbox-position face-button glyph (`A`/`B`/`X`/`Y`) into the
/// label the family physically prints at that position. Positions are
/// vendor-stable; labels are not: Nintendo mirrors the letters (south is B,
/// not A), PlayStation uses shapes. Non-face tokens pass through.
String faceButtonGlyph(String xboxToken, GlyphFamily family) =>
    switch (family) {
      GlyphFamily.xbox ||
      GlyphFamily.generic ||
      GlyphFamily.keyboard => xboxToken,
      GlyphFamily.nintendo => switch (xboxToken) {
        'A' => 'B',
        'B' => 'A',
        'X' => 'Y',
        'Y' => 'X',
        _ => xboxToken,
      },
      GlyphFamily.playstation => switch (xboxToken) {
        'A' => '✕',
        'B' => '○',
        'X' => '□',
        'Y' => '△',
        _ => xboxToken,
      },
    };
