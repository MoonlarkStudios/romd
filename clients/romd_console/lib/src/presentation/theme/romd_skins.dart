import 'package:flutter/material.dart';

import 'console_theme_extensions.dart';

/// Curated density is a separate axis from color and artwork skins. Custom
/// themes select a reviewed profile instead of authoring hundreds of metrics.
enum RomdDensity { baseline, seamProof }

/// Curated motion character is selected independently from palette/density.
enum RomdMotionCharacter { standard, seamProof }

/// Complete theme factories for the console.
///
/// Raw skin values are private to this file. Production ships the baseline
/// dark skin ("the archive at night") and the baseline light skin ("the
/// archive in daylight"). [seamTestTheme] is intentionally garish and exists
/// solely to prove runtime reskinning; it is not a product skin.
abstract final class RomdSkins {
  static const String _displayFamily = 'Archivo';
  static const String _monoFamily = 'IBM Plex Mono';
  // Deliberately exceeds every pill control's cross-axis extent. Flutter
  // clamps the effective corner, giving BorderRadius-based widgets the same
  // stadium semantics as StadiumBorder without a screen-specific radius.
  static const double _pillRadius = 999;

  static const Color _accent = Color(0xff4fe3b0);
  static const Color _catalogAccent = Color(0xffe6c074);
  static const Color _warning = Color(0xffef6a4a);
  static const Color _background = Color(0xff070d10);
  static const Color _surface = Color(0xff0d1519);
  static const Color _surfaceRaised = Color(0xff121d22);
  static const Color _surfaceHigh = Color(0xff18252b);
  static const Color _surfaceHighest = Color(0xff213138);
  static const Color _outline = Color(0xff789faa);
  static const Color _outlineVariant = Color(0xff34464c);
  static const Color _scrim = Color(0x99070d10);
  static const Color _textPrimary = Color(0xffeef4f5);
  static const Color _textBody = Color(0xffb3c0c3);
  static const Color _textSecondary = Color(0xff9fb0b4);
  static const Color _textFaint = Color(0xff6f8188);
  static const Color _textDisabled = Color(0xff566970);
  // Chrome-status and utility-label foregrounds. Same computed values the
  // ramp previously produced inline (white at 70% / 62%), now named so the
  // dark type ramp reads from primitives end to end.
  static const Color _chromeStatusText = Color.from(
    alpha: 0.7,
    red: 1,
    green: 1,
    blue: 1,
  );
  static const Color _utilityLabelText = Color.from(
    alpha: 0.62,
    red: 1,
    green: 1,
    blue: 1,
  );
  static const Color _focusGlow = Color(0x384fe3b0);
  static const Color _seamFocusGlow = Color(0x88ff6d00);

  // Light "archive in daylight" primitives. Same steel/teal/bronze language as
  // the dark skin, re-pitched as ink on cool paper: accents deepen to ink
  // weights that hold WCAG contrast on the pale ground, while bright variants
  // survive only inside gradients and artwork. The action accent is petrol
  // teal — the brand teal family shifted cyan so it reads as cool archival
  // water against the blue-leaning paper, not forest green. Pine teal remains
  // the connected-status green and the tertiary role.
  static const Color _lightAccent = Color(0xff0e7286);
  static const Color _lightAccentBright = Color(0xff17b8cb);
  static const Color _lightPine = Color(0xff0a7458);
  static const Color _lightCatalogAccent = Color(0xff8a6410);
  static const Color _lightWarning = Color(0xffb23c22);
  static const Color _lightSea = Color(0xff2b7fb8);
  static const Color _lightBackground = Color(0xffebf0f7);
  static const Color _lightSurface = Color(0xfff7fafd);
  static const Color _lightStructure = Color(0xffc3cad9);
  static const Color _lightInk = Color(0xff1c2733);
  static const Color _lightInkBody = Color(0xff33424f);
  static const Color _lightInkMuted = Color(0xff4a5a68);
  static const Color _lightInkFaint = Color(0xff5f7180);
  static const Color _lightInkDisabled = Color(0xff8492a3);
  static const Color _lightFocusGlow = Color(0x3d16a3b8);

  static final ThemeData _baselineDarkTheme = _buildBaselineDark();
  static final ThemeData _baselineLightTheme = _buildBaselineLight();
  static final ThemeData _seamTestTheme = _buildSeamTestTheme();

  /// Reviewed canonical dark skin for the console.
  static ThemeData baselineDark() => _baselineDarkTheme;

  /// Resolves independently-authored skin axes into Flutter-native ThemeData.
  ///
  /// This is the internal target for a future versioned custom-theme document:
  /// parsers validate primitives and selections, then call this method. Flutter
  /// objects never cross that serialization boundary.
  static ThemeData compose({
    required Brightness brightness,
    required ColorScheme colorScheme,
    required Color scaffoldBackgroundColor,
    required String fontFamily,
    required TextTheme textTheme,
    required ConsoleColors colors,
    required ConsoleElevationTheme elevation,
    required ConsoleArtworkTheme artwork,
    RomdDensity density = RomdDensity.baseline,
    RomdMotionCharacter motionCharacter = RomdMotionCharacter.standard,
  }) {
    final layout = switch (density) {
      RomdDensity.baseline => _baselineLayout,
      RomdDensity.seamProof => _testLayout,
    };
    final motion = switch (motionCharacter) {
      RomdMotionCharacter.standard => _baselineMotion,
      RomdMotionCharacter.seamProof => _testMotion,
    };
    final artworkIssues = artwork.validate();
    if (artworkIssues.isNotEmpty) {
      throw ArgumentError.value(
        artworkIssues,
        'artwork',
        'Invalid console artwork contract',
      );
    }

    final base = ThemeData(
      useMaterial3: true,
      brightness: brightness,
      colorScheme: colorScheme,
      scaffoldBackgroundColor: scaffoldBackgroundColor,
      fontFamily: fontFamily,
      textTheme: textTheme,
      extensions: <ThemeExtension<dynamic>>[
        colors,
        layout,
        elevation,
        motion,
        artwork,
      ],
    );
    return _withMaterialComponents(
      base,
      colors: colors,
      layout: layout,
      textTheme: textTheme,
    );
  }

  static ThemeData _buildBaselineDark() {
    const colorScheme = ColorScheme.dark(
      primary: _accent,
      onPrimary: _background,
      primaryContainer: Color(0xff123c33),
      onPrimaryContainer: _textPrimary,
      primaryFixed: Color(0xff9ff5d8),
      primaryFixedDim: _accent,
      onPrimaryFixed: _background,
      onPrimaryFixedVariant: Color(0xff123c33),
      secondary: _catalogAccent,
      onSecondary: _background,
      secondaryContainer: Color(0xff493e22),
      onSecondaryContainer: _textPrimary,
      secondaryFixed: Color(0xffffe19a),
      secondaryFixedDim: _catalogAccent,
      onSecondaryFixed: _background,
      onSecondaryFixedVariant: Color(0xff493e22),
      tertiary: Color(0xff62b6cb),
      onTertiary: _background,
      tertiaryContainer: Color(0xff163b45),
      onTertiaryContainer: _textPrimary,
      tertiaryFixed: Color(0xffb8e8f3),
      tertiaryFixedDim: Color(0xff62b6cb),
      onTertiaryFixed: _background,
      onTertiaryFixedVariant: Color(0xff163b45),
      error: _warning,
      errorContainer: Color(0xff5d2118),
      onErrorContainer: Colors.white,
      surface: _surface,
      onSurface: _textPrimary,
      surfaceDim: _background,
      surfaceBright: _surfaceHighest,
      surfaceContainerLowest: _background,
      surfaceContainerLow: _surface,
      surfaceContainer: _surfaceRaised,
      surfaceContainerHigh: _surfaceHigh,
      surfaceContainerHighest: _surfaceHighest,
      onSurfaceVariant: _textSecondary,
      outline: _outline,
      outlineVariant: _outlineVariant,
      shadow: Colors.black,
      scrim: _scrim,
      inverseSurface: _textPrimary,
      onInverseSurface: _background,
      inversePrimary: Color(0xff006b53),
      surfaceTint: Colors.transparent,
    );
    final base = ThemeData(
      useMaterial3: true,
      brightness: Brightness.dark,
      colorScheme: colorScheme,
      scaffoldBackgroundColor: _background,
      fontFamily: _displayFamily,
    );
    final textTheme = base.textTheme.copyWith(
      displayLarge: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 48,
        height: 1.08,
        fontWeight: FontWeight.w700,
        letterSpacing: -1.2,
        color: _textPrimary,
      ),
      displayMedium: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 36,
        fontWeight: FontWeight.w800,
        color: _textPrimary,
      ),
      displaySmall: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 24,
        height: 1.2,
        fontWeight: FontWeight.w800,
        letterSpacing: -0.3,
        color: _textPrimary,
      ),
      headlineLarge: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 24,
        height: 1.2,
        fontWeight: FontWeight.w700,
        letterSpacing: -0.2,
        color: _textPrimary,
      ),
      headlineMedium: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 18,
        height: 1.4,
        fontWeight: FontWeight.w600,
        letterSpacing: 1,
        leadingDistribution: TextLeadingDistribution.proportional,
        color: _chromeStatusText,
      ),
      headlineSmall: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 16,
        height: 1.3,
        fontWeight: FontWeight.w500,
        letterSpacing: 0,
        color: _textSecondary,
      ),
      titleLarge: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        height: 1.3,
        fontWeight: FontWeight.w600,
        letterSpacing: 0.4,
        leadingDistribution: TextLeadingDistribution.proportional,
        color: _utilityLabelText,
      ),
      titleMedium: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        height: 1.3,
        fontWeight: FontWeight.w700,
        letterSpacing: 0.6,
        color: _textSecondary,
      ),
      titleSmall: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        height: 1.3,
        fontWeight: FontWeight.w600,
        letterSpacing: 0.3,
        color: _textSecondary,
      ),
      bodyLarge: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 18,
        height: 1.4,
        fontWeight: FontWeight.w400,
        color: _textBody,
      ),
      bodyMedium: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 16,
        height: 1.4,
        fontWeight: FontWeight.w400,
        color: _textBody,
      ),
      bodySmall: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        height: 1.3,
        fontWeight: FontWeight.w500,
        letterSpacing: 0.3,
        color: _textSecondary,
      ),
      labelLarge: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 18,
        height: 1.2,
        fontWeight: FontWeight.w700,
        color: _textPrimary,
      ),
      labelMedium: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        height: 1.3,
        fontWeight: FontWeight.w500,
        letterSpacing: 0,
        color: _textSecondary,
      ),
      labelSmall: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        letterSpacing: 3,
        color: _textFaint,
      ),
    );
    return compose(
      brightness: Brightness.dark,
      colorScheme: colorScheme,
      scaffoldBackgroundColor: _background,
      fontFamily: _displayFamily,
      textTheme: textTheme,
      colors: _darkColors,
      elevation: _darkElevation,
      artwork: _darkArtwork,
    );
  }

  /// Reviewed canonical light skin: the archive in daylight. Ink text on cool
  /// paper, pine-teal action accents, bronze catalog emphasis, and saturated
  /// ink-ribbon ambient artwork over a pale sky.
  static ThemeData baselineLight() => _baselineLightTheme;

  static ThemeData _buildBaselineLight() {
    const colorScheme = ColorScheme.light(
      primary: _lightAccent,
      primaryContainer: Color(0xffcfe9ee),
      onPrimaryContainer: Color(0xff0b3a44),
      primaryFixed: Color(0xffa5e8f2),
      primaryFixedDim: _lightAccentBright,
      onPrimaryFixed: Color(0xff0b3a44),
      onPrimaryFixedVariant: Color(0xff0c5866),
      secondary: _lightCatalogAccent,
      onSecondary: Colors.white,
      secondaryContainer: Color(0xfff0e3c0),
      onSecondaryContainer: Color(0xff4a3607),
      secondaryFixed: Color(0xffffe19a),
      secondaryFixedDim: Color(0xffc9a24a),
      onSecondaryFixed: Color(0xff4a3607),
      onSecondaryFixedVariant: Color(0xff6d5210),
      tertiary: _lightPine,
      onTertiary: Colors.white,
      tertiaryContainer: Color(0xffcdeadf),
      onTertiaryContainer: Color(0xff0b3d2f),
      tertiaryFixed: Color(0xff9ff5d8),
      tertiaryFixedDim: Color(0xff14c08e),
      onTertiaryFixed: Color(0xff0b3d2f),
      onTertiaryFixedVariant: Color(0xff0a5a45),
      error: _lightWarning,
      errorContainer: Color(0xfff6d9d2),
      onErrorContainer: Color(0xff6e2415),
      surface: _lightSurface,
      onSurface: _lightInk,
      surfaceDim: _lightStructure,
      surfaceBright: Colors.white,
      surfaceContainerLowest: Colors.white,
      surfaceContainerLow: Color(0xfff2f6fa),
      surfaceContainer: Color(0xffe9eef6),
      surfaceContainerHigh: Color(0xffdfe6f0),
      surfaceContainerHighest: Color(0xffd3dbe7),
      onSurfaceVariant: _lightInkMuted,
      outline: Color(0xff6f8296),
      outlineVariant: _lightStructure,
      shadow: Color(0xff22303d),
      scrim: Color(0x66141d26),
      inverseSurface: Color(0xff22303d),
      onInverseSurface: _lightBackground,
      inversePrimary: _lightAccentBright,
      surfaceTint: Colors.transparent,
    );
    final base = ThemeData(
      useMaterial3: true,
      brightness: Brightness.light,
      colorScheme: colorScheme,
      scaffoldBackgroundColor: _lightBackground,
      fontFamily: _displayFamily,
    );
    // Identical ramp metrics to the dark skin; only the ink mapping changes.
    final textTheme = base.textTheme.copyWith(
      displayLarge: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 48,
        height: 1.08,
        fontWeight: FontWeight.w700,
        letterSpacing: -1.2,
        color: _lightInk,
      ),
      displayMedium: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 36,
        fontWeight: FontWeight.w800,
        color: _lightInk,
      ),
      displaySmall: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 24,
        height: 1.2,
        fontWeight: FontWeight.w800,
        letterSpacing: -0.3,
        color: _lightInk,
      ),
      headlineLarge: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 24,
        height: 1.2,
        fontWeight: FontWeight.w700,
        letterSpacing: -0.2,
        color: _lightInk,
      ),
      headlineMedium: TextStyle(
        fontFamily: _displayFamily,
        fontSize: 18,
        height: 1.4,
        fontWeight: FontWeight.w600,
        letterSpacing: 1,
        leadingDistribution: TextLeadingDistribution.proportional,
        color: _lightInk.withValues(alpha: 0.72),
      ),
      headlineSmall: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 16,
        height: 1.3,
        fontWeight: FontWeight.w500,
        letterSpacing: 0,
        color: _lightInkMuted,
      ),
      titleLarge: TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        height: 1.3,
        fontWeight: FontWeight.w600,
        letterSpacing: 0.4,
        leadingDistribution: TextLeadingDistribution.proportional,
        color: _lightInk.withValues(alpha: 0.66),
      ),
      titleMedium: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        height: 1.3,
        fontWeight: FontWeight.w700,
        letterSpacing: 0.6,
        color: _lightInkMuted,
      ),
      titleSmall: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        height: 1.3,
        fontWeight: FontWeight.w600,
        letterSpacing: 0.3,
        color: _lightInkMuted,
      ),
      bodyLarge: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 18,
        height: 1.4,
        fontWeight: FontWeight.w400,
        color: _lightInkBody,
      ),
      bodyMedium: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 16,
        height: 1.4,
        fontWeight: FontWeight.w400,
        color: _lightInkBody,
      ),
      bodySmall: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        height: 1.3,
        fontWeight: FontWeight.w500,
        letterSpacing: 0.3,
        color: _lightInkMuted,
      ),
      labelLarge: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 18,
        height: 1.2,
        fontWeight: FontWeight.w700,
        color: _lightInk,
      ),
      labelMedium: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        height: 1.3,
        fontWeight: FontWeight.w500,
        letterSpacing: 0,
        color: _lightInkMuted,
      ),
      labelSmall: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 14,
        letterSpacing: 3,
        color: _lightInkFaint,
      ),
    );
    return compose(
      brightness: Brightness.light,
      colorScheme: colorScheme,
      scaffoldBackgroundColor: _lightBackground,
      fontFamily: _displayFamily,
      textTheme: textTheme,
      colors: _lightColors,
      elevation: _lightElevation,
      artwork: _lightArtwork.copyWith(ambientGeometry: _lightAmbientGeometry),
    );
  }

  /// Deliberately non-production light skin used to exercise the runtime seam.
  static ThemeData seamTestTheme() => _seamTestTheme;

  static ThemeData _buildSeamTestTheme() {
    const colorScheme = ColorScheme.light(
      primary: Color(0xffff00ff),
      onPrimary: Color(0xff001b1b),
      secondary: Color(0xffff6d00),
      onSecondary: Color(0xff1f0800),
      error: Color(0xff9c0000),
      surface: Color(0xffffff00),
      onSurface: Color(0xff001b44),
      outline: Color(0xff6d00a8),
      outlineVariant: Color(0xff00a88f),
      scrim: Color(0x66000000),
    );
    final base = ThemeData(
      useMaterial3: true,
      brightness: Brightness.light,
      colorScheme: colorScheme,
      scaffoldBackgroundColor: const Color(0xffccff00),
      fontFamily: _monoFamily,
    );
    final textTheme = base.textTheme.copyWith(
      displayLarge: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 54,
        height: 1,
        fontWeight: FontWeight.w800,
        letterSpacing: 2,
        color: Color(0xff001b44),
      ),
      displayMedium: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 42,
        fontWeight: FontWeight.w900,
        letterSpacing: 1.5,
        color: Color(0xff001b44),
      ),
      displaySmall: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 29,
        height: 1.1,
        fontWeight: FontWeight.w900,
        letterSpacing: 1.4,
        color: Color(0xff001b44),
      ),
      headlineLarge: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 27,
        height: 1.15,
        fontWeight: FontWeight.w800,
        letterSpacing: 1,
        color: Color(0xff001b44),
      ),
      headlineMedium: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 21,
        height: 1.2,
        fontWeight: FontWeight.w800,
        letterSpacing: 2,
        color: Color(0xff001b44),
      ),
      headlineSmall: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 18,
        height: 1.2,
        fontWeight: FontWeight.w600,
        letterSpacing: 0.6,
        color: Color(0xff5b006d),
      ),
      titleLarge: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 20,
        height: 1.2,
        fontWeight: FontWeight.w700,
        letterSpacing: 1,
        color: Color(0xff5b006d),
      ),
      titleMedium: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 19,
        height: 1.25,
        fontWeight: FontWeight.w900,
        letterSpacing: 1.2,
        color: Color(0xff003f5c),
      ),
      titleSmall: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 17,
        height: 1.2,
        fontWeight: FontWeight.w800,
        letterSpacing: 1,
        color: Color(0xff5b006d),
      ),
      bodyLarge: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 22,
        height: 1.35,
        fontWeight: FontWeight.w700,
        letterSpacing: 0.6,
        color: Color(0xff003f5c),
      ),
      bodyMedium: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 18,
        height: 1.4,
        fontWeight: FontWeight.w600,
        letterSpacing: 0.7,
        color: Color(0xff003f5c),
      ),
      bodySmall: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 15,
        height: 1.4,
        fontWeight: FontWeight.w600,
        letterSpacing: 0.8,
        color: Color(0xff5b006d),
      ),
      labelLarge: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 20,
        height: 1.1,
        fontWeight: FontWeight.w800,
        color: Color(0xff001b44),
      ),
      labelMedium: const TextStyle(
        fontFamily: _monoFamily,
        fontSize: 18,
        height: 1.2,
        fontWeight: FontWeight.w700,
        letterSpacing: 0.8,
        color: Color(0xff5b006d),
      ),
      labelSmall: const TextStyle(
        fontFamily: _displayFamily,
        fontSize: 18,
        fontWeight: FontWeight.w900,
        letterSpacing: 4,
        color: Color(0xff705000),
      ),
    );
    final testArtwork = _testArtwork.copyWith(
      ambientGeometry: <ConsoleAmbientGeometrySpec>[
        _ambientGeometry.first.copyWith(
          seed: 917,
          backdropCenter: const Alignment(0.28, -0.72),
          motionScale: 1.4,
          particleCount: 320,
        ),
        ..._ambientGeometry.skip(1),
      ],
    );
    return compose(
      brightness: Brightness.light,
      colorScheme: colorScheme,
      scaffoldBackgroundColor: const Color(0xffccff00),
      fontFamily: _monoFamily,
      textTheme: textTheme,
      colors: _testColors,
      elevation: _testElevation,
      artwork: testArtwork,
      density: RomdDensity.seamProof,
      motionCharacter: RomdMotionCharacter.seamProof,
    );
  }

  static ThemeData _withMaterialComponents(
    ThemeData theme, {
    required ConsoleColors colors,
    required ConsoleLayoutTheme layout,
    required TextTheme textTheme,
  }) {
    final scheme = theme.colorScheme;
    final controlShape = RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(layout.controlRadius),
    );
    final panelShape = RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(layout.panelRadius),
      side: BorderSide(color: colors.panelBorder, width: layout.hairlineStroke),
    );
    final dialogShape = RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(layout.dialogRadius),
      side: BorderSide(color: colors.panelBorder, width: layout.hairlineStroke),
    );

    return theme.copyWith(
      snackBarTheme: SnackBarThemeData(
        behavior: SnackBarBehavior.floating,
        width: layout.surface.notificationWidth,
        backgroundColor: colors.dialogSurface,
        contentTextStyle: textTheme.titleSmall?.copyWith(
          fontWeight: textTheme.labelLarge?.fontWeight,
          color: colors.textStrong,
        ),
        shape: panelShape,
      ),
      iconTheme: IconThemeData(color: colors.iconStrong, size: layout.iconMd),
      iconButtonTheme: IconButtonThemeData(
        style: ButtonStyle(
          minimumSize: WidgetStatePropertyAll<Size>(
            Size.square(layout.circleButtonSize),
          ),
          shape: WidgetStatePropertyAll<OutlinedBorder>(controlShape),
          foregroundColor: WidgetStatePropertyAll<Color>(colors.iconStrong),
          overlayColor: WidgetStatePropertyAll<Color>(colors.focusFill),
        ),
      ),
      dividerTheme: DividerThemeData(
        color: colors.hairline,
        space: layout.hairlineStroke,
        thickness: layout.hairlineStroke,
      ),
      cardTheme: CardThemeData(
        color: colors.panelSurface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        margin: EdgeInsets.zero,
        shape: panelShape,
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: colors.dialogSurface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        shape: dialogShape,
        iconColor: colors.iconStrong,
        titleTextStyle: textTheme.headlineLarge,
        contentTextStyle: textTheme.bodyLarge?.copyWith(color: colors.textBody),
        barrierColor: colors.scrim,
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: ButtonStyle(
          shape: WidgetStatePropertyAll<OutlinedBorder>(controlShape),
          backgroundColor: WidgetStateProperty.resolveWith<Color?>((states) {
            if (states.contains(WidgetState.disabled)) {
              return colors.panelSurface;
            }
            return scheme.primary;
          }),
          foregroundColor: WidgetStateProperty.resolveWith<Color?>((states) {
            if (states.contains(WidgetState.disabled)) {
              return colors.textFaint;
            }
            return scheme.onPrimary;
          }),
          overlayColor: WidgetStatePropertyAll<Color>(colors.focusFill),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: ButtonStyle(
          shape: WidgetStatePropertyAll<OutlinedBorder>(controlShape),
          foregroundColor: WidgetStatePropertyAll<Color>(scheme.primary),
          overlayColor: WidgetStatePropertyAll<Color>(colors.focusFill),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: ButtonStyle(
          shape: WidgetStatePropertyAll<OutlinedBorder>(controlShape),
          foregroundColor: WidgetStatePropertyAll<Color>(scheme.primary),
          side: WidgetStatePropertyAll<BorderSide>(
            BorderSide(color: colors.panelBorder, width: layout.hairlineStroke),
          ),
          overlayColor: WidgetStatePropertyAll<Color>(colors.focusFill),
        ),
      ),
      progressIndicatorTheme: ProgressIndicatorThemeData(
        color: scheme.primary,
        linearTrackColor: colors.hairline,
        circularTrackColor: colors.hairline,
        strokeWidth: layout.focusStroke,
        constraints: BoxConstraints.tightFor(
          width: layout.progressIndicatorSize,
          height: layout.progressIndicatorSize,
        ),
      ),
      pageTransitionsTheme: const PageTransitionsTheme(
        builders: <TargetPlatform, PageTransitionsBuilder>{
          TargetPlatform.android: FadeForwardsPageTransitionsBuilder(),
          TargetPlatform.fuchsia: FadeForwardsPageTransitionsBuilder(),
          TargetPlatform.iOS: FadeForwardsPageTransitionsBuilder(),
          TargetPlatform.linux: FadeForwardsPageTransitionsBuilder(),
          TargetPlatform.macOS: FadeForwardsPageTransitionsBuilder(),
          TargetPlatform.windows: FadeForwardsPageTransitionsBuilder(),
        },
      ),
    );
  }

  static const ConsoleColors _darkColors = ConsoleColors(
    focusBorder: _accent,
    focusFill: Color(0x144fe3b0),
    focusGlow: _focusGlow,
    selectionFill: Color(0x1f4fe3b0),
    selectionBorder: Color(0x8c4fe3b0),
    controlRestFill: Color(0x29000000),
    connected: _accent,
    warning: _warning,
    catalogAccent: _catalogAccent,
    iconStrong: Color(0xffcdd6d8),
    textStrong: _textPrimary,
    textBody: _textBody,
    textMuted: _textSecondary,
    textFaint: _textFaint,
    textDisabled: _textDisabled,
    panelSurface: Color(0xbd0d1519),
    dialogSurface: _surface,
    panelBorder: Color(0x4278a0aa),
    hairline: Color.from(
      alpha: 0.12,
      red: 120 / 255,
      green: 160 / 255,
      blue: 170 / 255,
    ),
    borderStrong: Color.from(
      alpha: 0.26,
      red: 120 / 255,
      green: 160 / 255,
      blue: 170 / 255,
    ),
    scrim: _scrim,
    onMediaOverlay: Colors.white,
    mediaBackdrop: Colors.black,
    // Deep enough that catalog amber holds 4.5:1 even over pure-white art.
    mediaChipSurface: Color(0xbf070d10),
    onMediaAccent: _catalogAccent,
    keycapSurface: Color(0x59000000),
    keycapBorder: Color(0x664fe3b0),
    keycapForeground: _accent,
    hintText: Color(0xff8a9aa0),
    hintKeycapSurface: Color.from(
      alpha: 0.6,
      red: 15 / 255,
      green: 23 / 255,
      blue: 27 / 255,
    ),
    footerSurface: Color.from(
      alpha: 0.92,
      red: 7 / 255,
      green: 13 / 255,
      blue: 16 / 255,
    ),
    onWarning: Colors.black,
  );

  static const ConsoleColors _lightColors = ConsoleColors(
    focusBorder: _lightAccent,
    focusFill: Color(0x140e7286),
    focusGlow: _lightFocusGlow,
    selectionFill: Color(0x1f0e7286),
    // Higher alpha than the dark skin: the composited border must still hold
    // 3:1 against the pale panel surface.
    selectionBorder: Color(0xbf0e7286),
    controlRestFill: Color(0x1a37485a),
    connected: _lightPine,
    warning: _lightWarning,
    catalogAccent: _lightCatalogAccent,
    iconStrong: _lightInkBody,
    textStrong: _lightInk,
    textBody: _lightInkBody,
    textMuted: _lightInkMuted,
    textFaint: _lightInkFaint,
    textDisabled: _lightInkDisabled,
    panelSurface: Color(0xb8ffffff),
    dialogSurface: _lightSurface,
    panelBorder: Color(0x666f8296),
    hairline: Color(0x2937485a),
    borderStrong: Color(0x5237485a),
    scrim: Color(0x66141d26),
    onMediaOverlay: Colors.white,
    mediaBackdrop: Colors.black,
    // Media chrome is skin-stable: artwork is dark-backed in every skin, so
    // the chip keeps the dark skin's scrim and catalog amber.
    mediaChipSurface: Color(0xbf070d10),
    onMediaAccent: _catalogAccent,
    keycapSurface: Color(0x8cffffff),
    keycapBorder: Color(0x660e7286),
    keycapForeground: _lightAccent,
    hintText: Color(0xff43536b),
    hintKeycapSurface: Color(0xbfffffff),
    footerSurface: Color(0xebdfe6f0),
    onWarning: Colors.white,
  );

  static const ConsoleColors _testColors = ConsoleColors(
    focusBorder: Color(0xffff00ff),
    focusFill: Color(0x5500e5ff),
    focusGlow: _seamFocusGlow,
    selectionFill: Color(0x5500c853),
    selectionBorder: Color(0xff00c853),
    controlRestFill: Color(0x33001b44),
    connected: Color(0xff006400),
    warning: Color(0xff9c0000),
    catalogAccent: Color(0xffff6d00),
    iconStrong: Color(0xff001b44),
    textStrong: Color(0xff001b44),
    textBody: Color(0xff003f5c),
    textMuted: Color(0xff003f5c),
    textFaint: Color(0xff5b006d),
    textDisabled: Color(0xff705000),
    panelSurface: Color(0xfffff59d),
    dialogSurface: Color(0xffb2ff59),
    panelBorder: Color(0xff00a88f),
    hairline: Color(0xff009688),
    borderStrong: Color(0xff6d00a8),
    scrim: Color(0x66000000),
    onMediaOverlay: Colors.white,
    mediaBackdrop: Color(0xff21003d),
    mediaChipSurface: Color(0xcc21003d),
    onMediaAccent: Color(0xffffff00),
    keycapSurface: Color(0xfffff176),
    keycapBorder: Color(0xffff00ff),
    keycapForeground: Color(0xff001b44),
    hintText: Color(0xff003f5c),
    hintKeycapSurface: Color(0xff00e5ff),
    footerSurface: Color(0xeeccff00),
    onWarning: Colors.white,
  );

  static const ConsoleLayoutTheme _baselineLayout = ConsoleLayoutTheme(
    xxs: 4,
    xs: 8,
    sm: 12,
    md: 16,
    lg: 24,
    xl: 32,
    xxl: 48,
    xxxl: 64,
    // The single horizontal stage anchor: band title, content, and footer
    // caption all share this left edge. TV-safe at 1280x720 (5% ≈ 64).
    screenGutter: 64,
    sectionGap: 24,
    controlGap: 8,
    panelPadding: EdgeInsets.all(16),
    surface: ConsoleSurfaceMetrics(
      notificationWidth: 560,
      frameMargin: 48,
      headerIconSize: 26,
      iconActionSize: 40,
      emptyStateMinHeight: 380,
      compactControlPadding: EdgeInsets.symmetric(horizontal: 18, vertical: 12),
    ),
    discover: ConsoleDiscoverMetrics(
      // Aligned with the shared stage anchor and page-band height so the
      // Catalog chrome sits on the same spine as every other surface.
      frameMargin: 64,
      headerHeight: 72,
      footerHeight: 66,
      headerSectionGap: 40,
      contentTopInset: 16,
      featuredSpotlightMaxFraction: 0.49,
      // Keep the first shelf and focused caption above the 720p footer with
      // the shared 2:3 poster shape.
      featuredCardWidth: 156,
      // With the 64 px stage anchor the shelf keeps its designed six fully
      // visible portrait cards at 1280 by tightening the gap one step.
      featuredCardGap: 16,
      allGamesGridColumns: 4,
      allGamesInspectorWidth: 292,
      allGamesPaneGap: 28,
      systemsPlateWidth: 300,
      systemsPlateGap: 24,
      searchKeyboardFraction: 0.38,
      searchZoneGap: 40,
      compactBreakpoint: 1040,
      largeTextScaleBreakpoint: 1.6,
    ),
    status: ConsoleStatusMetrics(
      panelMaxWidth: 460,
      metadataIconSize: 13,
      dotSize: 9,
      labelMaxWidth: 180,
      feedbackIconSize: 40,
      loadingIndicatorWidth: 240,
    ),
    screenChrome: ConsoleScreenChromeMetrics(
      top: 16,
      side: 24,
      bottom: 18,
      clockInset: 8,
      navigationClockTop: 39,
    ),
    pageBand: ConsolePageBandMetrics(
      height: 72,
      eyebrowGap: 2,
      switcherGap: 28,
      segmentInset: 3,
      segmentPadding: EdgeInsets.symmetric(horizontal: 18, vertical: 7),
      clusterGap: 20,
    ),
    circleButtonSize: 42,
    circleButtonShape: BoxShape.circle,
    circleButtonRadius: _pillRadius,
    controllerBar: ConsoleControllerBarMetrics(height: 42, nameMaxWidth: 260),
    tint: ConsoleTintMetrics(fillOpacity: 0.08, borderOpacity: 0.4),
    navigationDock: ConsoleNavigationDockMetrics(
      itemExtent: 76,
      buttonSize: 52,
      iconSize: 24,
      buttonShape: BoxShape.circle,
      buttonRadius: _pillRadius,
      labelLaneHeight: 26,
      labelMaxWidth: 220,
    ),
    contentRail: ConsoleContentRailMetrics(
      tileWidth: 180,
      tileGap: 20,
      ringInset: 5,
      labelEdgeInset: 32,
      messageMinHeight: 40,
      skeletonOpacity: 0.35,
      fadeStart: 0.91,
    ),
    shortcutHelp: ConsoleShortcutHelpMetrics(
      sectionGap: 28,
      rowPadding: EdgeInsets.symmetric(vertical: 9),
      keycapWidth: 220,
      keycapPadding: EdgeInsets.symmetric(horizontal: 14, vertical: 11),
    ),
    hints: ConsoleHintMetrics(
      itemGap: 22,
      footerMinHeight: 42,
      keycapDiameter: 26,
      keycapMinWidth: 28,
      keycapRadius: 5,
      gamepadKeycapRadius: _pillRadius,
      keycapTextHeight: 1.1,
      stackBreakpoint: 760,
      leadingStackBreakpoint: 1180,
      stackTextScaleThreshold: 1.25,
    ),
    keyboard: ConsoleKeyboardMetrics(
      unitWidth: 48,
      keyHeight: 46,
      gap: 8,
      iconSize: 18,
    ),
    sessionCluster: ConsoleSessionClusterMetrics(
      height: 40,
      emptyWidth: 48,
      leadingWidth: 20,
      iconSlot: 28,
      iconSize: 20,
      radius: 9,
      attentionBadgeSize: 11,
      attentionBadgeIconSize: 8,
      attentionBadgeShape: BoxShape.circle,
      attentionBadgeRadius: _pillRadius,
    ),
    settings: ConsoleSettingsMetrics(
      contentMaxWidth: 880,
      hierarchyMaxWidth: 1180,
      hierarchyBreakpoint: 760,
      hierarchyTextScaleBreakpoint: 1.6,
      categoryRailWidth: 260,
      hierarchyPaneGap: 52,
      categoryRowHeight: 58,
      headerIconSize: 34,
      actionHeight: 58,
      compactActionHeight: 54,
      contextPanelWidth: 320,
      contextPanelBreakpoint: 1120,
    ),
    playersPanel: ConsolePlayersPanelMetrics(
      seatHeight: 102,
      seatTextScaleGrowth: 80,
      actionTextScaleGrowth: 36,
      claimTileHeight: 180,
    ),
    disabledOpacity: 0.38,
    entryLockupOffset: -18,
    entryBrandLift: 16,
    entryPromptOffset: 190,
    entrySelectionTranslate: 24,
    entryLockupSpacing: 12,
    entrySupportingGap: 8,
    entryRompSize: 218,
    entryWordmarkWidth: 360,
    profileSelectionLockupRise: 198,
    avatarMd: 84,
    gameRail: ConsoleGameRailMetrics(
      strip: ConsoleTileStripMetrics(tileWidth: 160, tileGap: 20),
      gridRunGap: 26,
      minimumHeight: 322,
      // Keeps grid covers near their designed 1280 presence (~193px at four
      // columns); wider viewports add columns instead of scaling art up.
      gridTileMaxWidth: 224,
    ),
    mediaRail: ConsoleTileStripMetrics(tileWidth: 200, tileGap: 14),
    releaseRail: ConsoleReleaseRailMetrics(
      strip: ConsoleTileStripMetrics(tileWidth: 180, tileGap: 10),
      height: 76,
    ),
    controlRadius: 8,
    chipRadius: 6,
    panelRadius: 12,
    dialogRadius: 16,
    pillRadius: _pillRadius,
    focusStroke: 2,
    focusRingStroke: 3,
    focusRingGap: 2,
    focusGlowBlur: 10,
    focusGlowSpread: 4,
    focusGlowInflate: 1,
    hairlineStroke: 1,
    iconSm: 16,
    iconMd: 20,
    iconLg: 48,
    avatarSm: 40,
    avatarLg: 104,
    avatarXl: 118,
    progressIndicatorSm: 18,
    progressIndicatorSize: 24,
  );

  static const ConsoleLayoutTheme _testLayout = ConsoleLayoutTheme(
    xxs: 6,
    xs: 10,
    sm: 14,
    md: 20,
    lg: 28,
    xl: 36,
    xxl: 52,
    xxxl: 72,
    screenGutter: 72,
    sectionGap: 28,
    controlGap: 10,
    panelPadding: EdgeInsets.all(20),
    surface: ConsoleSurfaceMetrics(
      notificationWidth: 620,
      frameMargin: 52,
      headerIconSize: 30,
      iconActionSize: 46,
      emptyStateMinHeight: 400,
      compactControlPadding: EdgeInsets.symmetric(horizontal: 22, vertical: 14),
    ),
    discover: ConsoleDiscoverMetrics(
      frameMargin: 52,
      headerHeight: 92,
      footerHeight: 72,
      headerSectionGap: 44,
      contentTopInset: 20,
      featuredSpotlightMaxFraction: 0.49,
      featuredCardWidth: 174,
      featuredCardGap: 24,
      allGamesGridColumns: 4,
      allGamesInspectorWidth: 320,
      allGamesPaneGap: 32,
      systemsPlateWidth: 332,
      systemsPlateGap: 28,
      searchKeyboardFraction: 0.4,
      searchZoneGap: 48,
      compactBreakpoint: 1120,
      largeTextScaleBreakpoint: 1.5,
    ),
    status: ConsoleStatusMetrics(
      panelMaxWidth: 430,
      metadataIconSize: 16,
      dotSize: 12,
      labelMaxWidth: 200,
      feedbackIconSize: 48,
      loadingIndicatorWidth: 300,
    ),
    screenChrome: ConsoleScreenChromeMetrics(
      top: 22,
      side: 32,
      bottom: 26,
      clockInset: 12,
      navigationClockTop: 46,
    ),
    pageBand: ConsolePageBandMetrics(
      height: 84,
      eyebrowGap: 4,
      switcherGap: 36,
      segmentInset: 5,
      segmentPadding: EdgeInsets.symmetric(horizontal: 24, vertical: 10),
      clusterGap: 26,
    ),
    circleButtonSize: 48,
    circleButtonShape: BoxShape.rectangle,
    circleButtonRadius: 8,
    controllerBar: ConsoleControllerBarMetrics(height: 48, nameMaxWidth: 280),
    tint: ConsoleTintMetrics(fillOpacity: 0.16, borderOpacity: 0.7),
    navigationDock: ConsoleNavigationDockMetrics(
      itemExtent: 84,
      buttonSize: 58,
      iconSize: 30,
      buttonShape: BoxShape.rectangle,
      buttonRadius: 10,
      labelLaneHeight: 32,
      labelMaxWidth: 260,
    ),
    contentRail: ConsoleContentRailMetrics(
      tileWidth: 192,
      tileGap: 28,
      ringInset: 8,
      labelEdgeInset: 38,
      messageMinHeight: 48,
      skeletonOpacity: 0.52,
      fadeStart: 0.82,
    ),
    shortcutHelp: ConsoleShortcutHelpMetrics(
      sectionGap: 36,
      rowPadding: EdgeInsets.symmetric(vertical: 12),
      keycapWidth: 250,
      keycapPadding: EdgeInsets.symmetric(horizontal: 20, vertical: 14),
    ),
    hints: ConsoleHintMetrics(
      itemGap: 30,
      footerMinHeight: 50,
      keycapDiameter: 32,
      keycapMinWidth: 36,
      keycapRadius: 12,
      gamepadKeycapRadius: 4,
      keycapTextHeight: 1.25,
      stackBreakpoint: 820,
      leadingStackBreakpoint: 1220,
      stackTextScaleThreshold: 1.2,
    ),
    keyboard: ConsoleKeyboardMetrics(
      unitWidth: 56,
      keyHeight: 52,
      gap: 10,
      iconSize: 22,
    ),
    sessionCluster: ConsoleSessionClusterMetrics(
      height: 48,
      emptyWidth: 58,
      leadingWidth: 24,
      iconSlot: 34,
      iconSize: 25,
      radius: 3,
      attentionBadgeSize: 15,
      attentionBadgeIconSize: 10,
      attentionBadgeShape: BoxShape.rectangle,
      attentionBadgeRadius: 2,
    ),
    settings: ConsoleSettingsMetrics(
      contentMaxWidth: 960,
      hierarchyMaxWidth: 1280,
      hierarchyBreakpoint: 840,
      hierarchyTextScaleBreakpoint: 1.5,
      categoryRailWidth: 300,
      hierarchyPaneGap: 60,
      categoryRowHeight: 68,
      headerIconSize: 42,
      actionHeight: 68,
      compactActionHeight: 60,
      contextPanelWidth: 348,
      contextPanelBreakpoint: 1180,
    ),
    playersPanel: ConsolePlayersPanelMetrics(
      seatHeight: 118,
      seatTextScaleGrowth: 92,
      actionTextScaleGrowth: 48,
      claimTileHeight: 210,
    ),
    disabledOpacity: 0.24,
    entryLockupOffset: -24,
    entryBrandLift: 24,
    entryPromptOffset: 214,
    entrySelectionTranslate: 34,
    entryLockupSpacing: 18,
    entrySupportingGap: 12,
    entryRompSize: 244,
    entryWordmarkWidth: 400,
    profileSelectionLockupRise: 214,
    avatarMd: 96,
    gameRail: ConsoleGameRailMetrics(
      strip: ConsoleTileStripMetrics(tileWidth: 172, tileGap: 24),
      gridRunGap: 30,
      minimumHeight: 340,
      gridTileMaxWidth: 260,
    ),
    mediaRail: ConsoleTileStripMetrics(tileWidth: 224, tileGap: 22),
    releaseRail: ConsoleReleaseRailMetrics(
      strip: ConsoleTileStripMetrics(tileWidth: 204, tileGap: 16),
      height: 88,
    ),
    controlRadius: 2,
    chipRadius: 12,
    panelRadius: 20,
    dialogRadius: 28,
    pillRadius: _pillRadius,
    focusStroke: 4,
    focusRingStroke: 5,
    focusRingGap: 4,
    focusGlowBlur: 18,
    focusGlowSpread: 7,
    focusGlowInflate: 2,
    hairlineStroke: 2,
    iconSm: 18,
    iconMd: 22,
    iconLg: 52,
    avatarSm: 48,
    avatarLg: 112,
    avatarXl: 128,
    progressIndicatorSm: 20,
    progressIndicatorSize: 28,
  );

  static const ConsoleElevationTheme _darkElevation = ConsoleElevationTheme(
    none: <BoxShadow>[],
    panel: <BoxShadow>[],
    dialog: <BoxShadow>[BoxShadow(color: Color(0x7a000000), blurRadius: 36)],
    focusGlow: <BoxShadow>[
      BoxShadow(color: _focusGlow, spreadRadius: 3),
      BoxShadow(color: _focusGlow, blurRadius: 24, spreadRadius: 1),
    ],
    selectionGlow: <BoxShadow>[BoxShadow(color: _focusGlow, spreadRadius: 3)],
    hero: <BoxShadow>[
      BoxShadow(color: Color(0x8a000000), blurRadius: 40, spreadRadius: 6),
    ],
    identityFocusGlowAlpha: 0.28,
  );

  // Light surfaces read depth from soft slate shadows where the dark skin
  // relies on luminance separation and glow.
  static const ConsoleElevationTheme _lightElevation = ConsoleElevationTheme(
    none: <BoxShadow>[],
    panel: <BoxShadow>[
      BoxShadow(color: Color(0x1f26323e), blurRadius: 16, spreadRadius: 1),
    ],
    dialog: <BoxShadow>[
      BoxShadow(color: Color(0x3d26323e), blurRadius: 36, spreadRadius: 2),
    ],
    focusGlow: <BoxShadow>[
      BoxShadow(color: _lightFocusGlow, spreadRadius: 3),
      BoxShadow(color: _lightFocusGlow, blurRadius: 24, spreadRadius: 1),
    ],
    selectionGlow: <BoxShadow>[
      BoxShadow(color: Color(0x290e7286), spreadRadius: 3),
    ],
    hero: <BoxShadow>[
      BoxShadow(color: Color(0x4726323e), blurRadius: 40, spreadRadius: 6),
    ],
    identityFocusGlowAlpha: 0.22,
  );

  static const ConsoleElevationTheme _testElevation = ConsoleElevationTheme(
    none: <BoxShadow>[],
    panel: <BoxShadow>[
      BoxShadow(color: Color(0x33006d77), blurRadius: 12, spreadRadius: 2),
    ],
    dialog: <BoxShadow>[
      BoxShadow(color: Color(0x55000000), blurRadius: 28, spreadRadius: 4),
    ],
    focusGlow: <BoxShadow>[
      BoxShadow(color: _seamFocusGlow, blurRadius: 30, spreadRadius: 3),
    ],
    selectionGlow: <BoxShadow>[
      BoxShadow(color: Color(0x9900e5ff), blurRadius: 12, spreadRadius: 5),
    ],
    hero: <BoxShadow>[
      BoxShadow(color: Color(0x6600e5ff), blurRadius: 44, spreadRadius: 8),
    ],
    identityFocusGlowAlpha: 0.52,
  );

  static const ConsoleMotionTheme _baselineMotion = ConsoleMotionTheme(
    focus: Duration(milliseconds: 150),
    selection: Duration(milliseconds: 150),
    contentTransition: Duration(milliseconds: 220),
    routeEnter: Duration(milliseconds: 220),
    routeExit: Duration(milliseconds: 150),
    intro: Duration(milliseconds: 400),
    pulse: Duration(milliseconds: 1500),
    focusRotation: Duration(milliseconds: 2800),
    screenTransition: Duration(milliseconds: 280),
    chromeTransition: Duration(milliseconds: 360),
    hintLabelTransition: Duration(milliseconds: 200),
    entryTransition: Duration(milliseconds: 480),
    entryAttractFadeFraction: 0.375,
    entrySelectionDelayFraction: 0.375,
    entryPromptDelayFraction: 0.21,
    entryPulseMinimumOpacity: 0.72,
    routeScaleBegin: 0.97,
    interaction: ConsoleInteractionSpec(
      focusedScale: 1.038,
      restScale: 0.97,
      subtleEnterScale: 0.985,
      rowActiveOpacity: 0.82,
      rowInactiveOpacity: 0.96,
    ),
    standardCurve: Curves.easeOut,
    emphasizedCurve: Curves.easeOutCubic,
    spatialCurve: Curves.easeInOutCubic,
  );

  static const ConsoleMotionTheme _testMotion = ConsoleMotionTheme(
    focus: Duration(milliseconds: 310),
    selection: Duration(milliseconds: 330),
    contentTransition: Duration(milliseconds: 510),
    routeEnter: Duration(milliseconds: 610),
    routeExit: Duration(milliseconds: 470),
    intro: Duration(milliseconds: 720),
    pulse: Duration(milliseconds: 1900),
    focusRotation: Duration(milliseconds: 4100),
    screenTransition: Duration(milliseconds: 520),
    chromeTransition: Duration(milliseconds: 640),
    hintLabelTransition: Duration(milliseconds: 430),
    entryTransition: Duration(milliseconds: 980),
    entryAttractFadeFraction: 0.3,
    entrySelectionDelayFraction: 0.22,
    entryPromptDelayFraction: 0.3,
    entryPulseMinimumOpacity: 0.18,
    routeScaleBegin: 0.9,
    interaction: ConsoleInteractionSpec(
      focusedScale: 1.08,
      restScale: 0.9,
      subtleEnterScale: 0.94,
      rowActiveOpacity: 0.55,
      rowInactiveOpacity: 0.78,
    ),
    standardCurve: Curves.linear,
    emphasizedCurve: Curves.bounceOut,
    spatialCurve: Curves.linear,
  );

  static const ConsoleAmbientRenderSpec _ambientRender =
      ConsoleAmbientRenderSpec(
        backdropRadius: 1.48,
        backdropMiddleStop: 0.58,
        pathSegments: 96,
        particleBandOffsetScale: 1.35,
        particleRadius: RangeValues(0.0014, 0.0048),
        particleAlpha: RangeValues(0.18, 0.78),
        particleSpeed: RangeValues(0.0015, 0.006),
        particleShimmerFrequency: RangeValues(0.6, 1.8),
        particleShimmerBase: 0.68,
        particleShimmerAmplitude: 0.32,
        primaryParticleThreshold: 0.64,
        layerThresholds: <double>[0.62, 0.86],
        falloutWarmChanceScale: 0.5,
        falloutDrop: RangeValues(0.03, 0.28),
        falloutRadius: RangeValues(0.0009, 0.0024),
        falloutAlpha: RangeValues(0.08, 0.32),
        falloutSpeed: RangeValues(0.001, 0.004),
        falloutSwayFrequency: 0.4,
        falloutSwayScale: 0.006,
        falloutFadeScale: 2.4,
        starY: RangeValues(0.05, 0.48),
        starRadius: RangeValues(0.0008, 0.0022),
        starAlpha: RangeValues(0.16, 0.68),
        starTwinkleFrequency: RangeValues(0.3, 0.9),
        starWarmChance: 0.1,
        starTwinkleBase: 0.56,
        starTwinkleAmplitude: 0.44,
        starDriftFrequency: 0.025,
        starDriftScale: 0.01,
        hazeAlphaScale: 0.14,
        meshMinimumStroke: 0.6,
        meshStrokeScale: 0.001,
        meshOffsets: <double>[-0.9, 0.15, 0.95],
        ribbonGlowAlphaScale: 0.36,
        ribbonGlowBlur: 10,
        ribbonGlowMinimumStroke: 1.5,
        ribbonGlowWidthScale: 3.8,
        ribbonLineMinimumStroke: 0.8,
        ribbonLineAlphaScale: 0.7,
        ribbonHighlightAlphaScale: 0.16,
        ribbonTrailingAlphaScale: 0.45,
        ribbonGradientStops: <double>[0, 0.22, 0.5, 0.74, 1],
        warmPathVerticalOffsetScale: 0.15,
        warmPathStart: 0.04,
        warmPathEnd: 0.64,
        warmGlowBlur: 5,
        warmMinimumStroke: 1.2,
        warmStrokeScale: 0.003,
        warmGradientStops: <double>[0, 0.45, 1],
        waveSecondaryTimeScale: 0.64,
        atmosphereSpotlightCenter: Alignment(-0.86, -1.08),
        atmosphereSpotlightRadius: 1.15,
        atmosphereSpotlightAlpha: 0.1,
        atmosphereWarmAlphaScale: 0.06,
        atmosphereSpotlightStops: <double>[0, 0.28, 0.58],
        atmosphereShadowTopAlpha: 0.05,
        atmosphereShadowBottomAlpha: 0.3,
        atmosphereShadowMiddleStop: 0.52,
      );

  // Shared by both production skins: key art and media stay dark-backed in
  // every mode, so backdrop shading and plate composition do not vary by skin.
  static const ConsoleCoverArtworkSpec _baselineCoverRendering =
      ConsoleCoverArtworkSpec(
        plateGradientMiddleStop: 0.52,
        plateRingSizeFactor: 0.52,
        plateRingAlpha: 0.18,
        plateMonogramInsetFactor: 0.12,
        plateMonogramSizeFactor: 0.32,
        plateMonogramAlpha: 0.7,
        hatchAlpha: 0.02,
        hatchStrokeWidth: 1,
        hatchSpacing: 8,
        backdropGradientMiddleStop: 0.42,
        backdropMonogramAlignment: Alignment(0, -0.45),
        backdropMonogramAlpha: 0.07,
        shadeCenter: Alignment(0, -0.3),
        shadeRadius: 1.1,
        shadeToneAlpha: 0.08,
        shadeOuterAlpha: 0.72,
        shadeRadialMiddleStop: 0.5,
        shadeTopAlpha: 0.12,
        shadeMiddleAlpha: 0.5,
        shadeBottomAlpha: 0.97,
        shadeLinearStops: <double>[0, 0.42, 0.78],
      );

  static const ConsoleCoverArtworkSpec _testCoverRendering =
      ConsoleCoverArtworkSpec(
        plateGradientMiddleStop: 0.34,
        plateRingSizeFactor: 0.68,
        plateRingAlpha: 0.42,
        plateMonogramInsetFactor: 0.18,
        plateMonogramSizeFactor: 0.4,
        plateMonogramAlpha: 0.9,
        hatchAlpha: 0.08,
        hatchStrokeWidth: 2,
        hatchSpacing: 12,
        backdropGradientMiddleStop: 0.6,
        backdropMonogramAlignment: Alignment(0.4, -0.2),
        backdropMonogramAlpha: 0.18,
        shadeCenter: Alignment(0.35, -0.5),
        shadeRadius: 1.35,
        shadeToneAlpha: 0.2,
        shadeOuterAlpha: 0.56,
        shadeRadialMiddleStop: 0.62,
        shadeTopAlpha: 0.04,
        shadeMiddleAlpha: 0.32,
        shadeBottomAlpha: 0.88,
        shadeLinearStops: <double>[0, 0.55, 0.9],
      );

  static const List<ConsoleAmbientGeometrySpec> _ambientGeometry =
      <ConsoleAmbientGeometrySpec>[
        ConsoleAmbientGeometrySpec(
          seed: 17,
          backdropCenter: Alignment(-0.48, -0.92),
          particleAlpha: 0.72,
          meshAlpha: 0.2,
          starAlpha: 0.2,
          warmParticleChance: 0.06,
          warmAccentAlpha: 0.12,
          motionScale: 1,
          particleCount: 460,
          falloutCount: 95,
          starCount: 58,
          waves: <ConsoleAmbientWaveSpec>[
            ConsoleAmbientWaveSpec(
              tone: ConsoleAmbientWaveTone.primary,
              baseY: 0.66,
              amplitude: 0.047,
              secondaryAmplitude: 0.018,
              frequency: 1.9,
              secondaryFrequency: 4.2,
              phase: 0.1,
              speed: 0.018,
              spread: 0.042,
              alpha: 0.62,
              glowWidth: 0.062,
              lineWidth: 0.0035,
              hazeBlur: 28,
            ),
            ConsoleAmbientWaveSpec(
              tone: ConsoleAmbientWaveTone.secondary,
              baseY: 0.59,
              amplitude: 0.04,
              secondaryAmplitude: 0.014,
              frequency: 1.42,
              secondaryFrequency: 3.3,
              phase: 0.58,
              speed: -0.014,
              spread: 0.05,
              alpha: 0.3,
              glowWidth: 0.09,
              lineWidth: 0.002,
              hazeBlur: 42,
            ),
            ConsoleAmbientWaveSpec(
              tone: ConsoleAmbientWaveTone.tertiary,
              baseY: 0.72,
              amplitude: 0.033,
              secondaryAmplitude: 0.012,
              frequency: 2.32,
              secondaryFrequency: 5,
              phase: 0.8,
              speed: 0.011,
              spread: 0.037,
              alpha: 0.24,
              glowWidth: 0.11,
              lineWidth: 0.002,
              hazeBlur: 48,
            ),
          ],
        ),
        ConsoleAmbientGeometrySpec(
          seed: 29,
          backdropCenter: Alignment(-0.34, -0.88),
          particleAlpha: 0.66,
          meshAlpha: 0.16,
          starAlpha: 0.24,
          warmParticleChance: 0.18,
          warmAccentAlpha: 0.34,
          motionScale: 0.94,
          particleCount: 420,
          falloutCount: 120,
          starCount: 72,
          waves: <ConsoleAmbientWaveSpec>[
            ConsoleAmbientWaveSpec(
              tone: ConsoleAmbientWaveTone.primary,
              baseY: 0.64,
              amplitude: 0.054,
              secondaryAmplitude: 0.017,
              frequency: 1.65,
              secondaryFrequency: 3.8,
              phase: 0.36,
              speed: 0.016,
              spread: 0.045,
              alpha: 0.52,
              glowWidth: 0.058,
              lineWidth: 0.0032,
              hazeBlur: 28,
            ),
            ConsoleAmbientWaveSpec(
              tone: ConsoleAmbientWaveTone.secondary,
              baseY: 0.58,
              amplitude: 0.058,
              secondaryAmplitude: 0.014,
              frequency: 1.22,
              secondaryFrequency: 3.1,
              phase: 0.05,
              speed: -0.012,
              spread: 0.045,
              alpha: 0.28,
              glowWidth: 0.072,
              lineWidth: 0.0025,
              hazeBlur: 36,
            ),
            ConsoleAmbientWaveSpec(
              tone: ConsoleAmbientWaveTone.tertiary,
              baseY: 0.72,
              amplitude: 0.032,
              secondaryAmplitude: 0.011,
              frequency: 2.2,
              secondaryFrequency: 4.8,
              phase: 0.72,
              speed: 0.009,
              spread: 0.036,
              alpha: 0.22,
              glowWidth: 0.11,
              lineWidth: 0.002,
              hazeBlur: 48,
            ),
          ],
        ),
        ConsoleAmbientGeometrySpec(
          seed: 43,
          backdropCenter: Alignment(-0.6, -0.96),
          particleAlpha: 0.58,
          meshAlpha: 0.32,
          starAlpha: 0.14,
          warmParticleChance: 0.03,
          warmAccentAlpha: 0.06,
          motionScale: 0.72,
          particleCount: 500,
          falloutCount: 70,
          starCount: 46,
          waves: <ConsoleAmbientWaveSpec>[
            ConsoleAmbientWaveSpec(
              tone: ConsoleAmbientWaveTone.primary,
              baseY: 0.68,
              amplitude: 0.04,
              secondaryAmplitude: 0.014,
              frequency: 2.08,
              secondaryFrequency: 4.4,
              phase: 0.2,
              speed: 0.012,
              spread: 0.035,
              alpha: 0.5,
              glowWidth: 0.05,
              lineWidth: 0.0028,
              hazeBlur: 24,
            ),
            ConsoleAmbientWaveSpec(
              tone: ConsoleAmbientWaveTone.secondary,
              baseY: 0.62,
              amplitude: 0.035,
              secondaryAmplitude: 0.012,
              frequency: 1.55,
              secondaryFrequency: 3.6,
              phase: 0.52,
              speed: -0.01,
              spread: 0.042,
              alpha: 0.28,
              glowWidth: 0.078,
              lineWidth: 0.002,
              hazeBlur: 34,
            ),
            ConsoleAmbientWaveSpec(
              tone: ConsoleAmbientWaveTone.tertiary,
              baseY: 0.73,
              amplitude: 0.028,
              secondaryAmplitude: 0.01,
              frequency: 2.4,
              secondaryFrequency: 5.2,
              phase: 0.9,
              speed: 0.008,
              spread: 0.033,
              alpha: 0.22,
              glowWidth: 0.1,
              lineWidth: 0.0018,
              hazeBlur: 44,
            ),
          ],
        ),
      ];

  /// The dark skin's luminous waves bloom against black; on the daylight
  /// ground there is no glow to carry them, so the light skin re-inks the
  /// shared topology with more pigment instead of new geometry.
  static final List<ConsoleAmbientGeometrySpec> _lightAmbientGeometry =
      _ambientGeometry
          .map(
            (concept) => concept.copyWith(
              particleAlpha: _emphasize(concept.particleAlpha, 1.35),
              meshAlpha: _emphasize(concept.meshAlpha, 1.5),
              starAlpha: _emphasize(concept.starAlpha, 1.3),
              warmAccentAlpha: _emphasize(concept.warmAccentAlpha, 1.4),
              waves: <ConsoleAmbientWaveSpec>[
                for (final wave in concept.waves)
                  wave.copyWith(alpha: _emphasize(wave.alpha, 1.55)),
              ],
            ),
          )
          .toList(growable: false);

  static double _emphasize(double alpha, double factor) =>
      (alpha * factor).clamp(0, 1).toDouble();

  static const ConsoleArtworkTheme _darkArtwork = ConsoleArtworkTheme(
    ambientPalettes: <ConsoleAmbientPalette>[
      ConsoleAmbientPalette(
        backdropTop: Color(0xff082b3a),
        backdropMiddle: Color(0xff06101a),
        backdropBottom: Color(0xff010308),
        particlePrimary: Color(0xff46efff),
        particleSecondary: _accent,
        particleWarm: Color(0xffffb86b),
        mesh: Color(0xff60dcff),
        star: Color(0xffd9fbff),
        wavePrimary: Color(0xff44efff),
        waveSecondary: Color(0xff1b75b7),
        waveTertiary: Color(0xff0b4158),
        spotlight: Color(0xfff4ecd8),
      ),
      ConsoleAmbientPalette(
        backdropTop: Color(0xff092638),
        backdropMiddle: Color(0xff070f18),
        backdropBottom: Color(0xff020306),
        particlePrimary: Color(0xff55eaff),
        particleSecondary: Color(0xff2f8ccb),
        particleWarm: Color(0xffff7a35),
        mesh: Color(0xffff9454),
        star: Color(0xfffff1d7),
        wavePrimary: Color(0xff36ddff),
        waveSecondary: Color(0xffff7a35),
        waveTertiary: Color(0xff0d3e56),
        spotlight: Color(0xfff4ecd8),
      ),
      ConsoleAmbientPalette(
        backdropTop: Color(0xff0a2d3d),
        backdropMiddle: Color(0xff07101a),
        backdropBottom: Color(0xff010206),
        particlePrimary: Color(0xff9af8ff),
        particleSecondary: Color(0xff35a9ff),
        particleWarm: Color(0xffffd8a8),
        mesh: Color(0xffaaf8ff),
        star: Color(0xffeefeff),
        wavePrimary: Color(0xff8cf8ff),
        waveSecondary: Color(0xff2571a3),
        waveTertiary: Color(0xff0c415b),
        spotlight: Color(0xfff4ecd8),
      ),
    ],
    ambientGeometry: _ambientGeometry,
    ambientRender: _ambientRender,
    ambientIntensity: 1,
    focusGradient: <Color>[
      _accent,
      Color(0xff9ff5d8),
      Color(0xff4fb8e3),
      _accent,
    ],
    coverToneFamilies: <List<Color>>[
      <Color>[Color(0xff10242a), Color(0xff1b3e40), Color(0xff62d9c0)],
      <Color>[Color(0xff271917), Color(0xff3f2622), Color(0xffe29a86)],
      <Color>[Color(0xff2a2415), Color(0xff43391d), _catalogAccent],
      <Color>[Color(0xff201a2c), Color(0xff33284a), Color(0xffb39ae3)],
      <Color>[Color(0xff142430), Color(0xff234056), Color(0xff7fb4e6)],
    ],
    coverRendering: _baselineCoverRendering,
    coverPlateBackdrop: Color(0xff0a1316),
    coverMonogramStyle: TextStyle(
      fontFamily: _displayFamily,
      fontWeight: FontWeight.w800,
      letterSpacing: 1,
      height: 1,
    ),
    highlight: Colors.white,
    shadow: Colors.black,
    // Preserve the painter's original 0.08 floating alpha exactly. Encoding
    // this as 0x14 rounds to 20/255 and creates a visible golden edge diff.
    avatarRim: Color.from(alpha: 0.08, red: 1, green: 1, blue: 1),
    verificationCodeStyle: TextStyle(
      fontFamily: _displayFamily,
      fontSize: 48,
      height: 1.08,
      fontWeight: FontWeight.w800,
      letterSpacing: 8,
      color: _textPrimary,
      fontFeatures: <FontFeature>[FontFeature.tabularFigures()],
    ),
    entryBackdrop: Color(0xff07101a),
    entryPromptAccent: Color(0xfff3b944),
    entryStartPromptStyle: TextStyle(
      fontSize: 18,
      fontWeight: FontWeight.w700,
      color: Color(0xfffff9ee),
    ),
  );

  // Daylight-archive artwork: the same wave/particle topology as the dark
  // skin (shared geometry and render specs) drawn as saturated teal, sea-blue,
  // and bronze ink over a pale sky. Placeholder cover plates lighten to
  // paper-and-ink tone families; key art shading stays dark-backed.
  static const ConsoleArtworkTheme _lightArtwork = ConsoleArtworkTheme(
    ambientPalettes: <ConsoleAmbientPalette>[
      ConsoleAmbientPalette(
        backdropTop: Color(0xfff4f8fc),
        backdropMiddle: Color(0xffe6edf6),
        backdropBottom: Color(0xffc9d5e4),
        particlePrimary: _lightAccent,
        particleSecondary: _lightSea,
        particleWarm: Color(0xffd97e2a),
        mesh: Color(0xff8fa3bd),
        star: Color(0xff6b7f96),
        wavePrimary: _lightAccent,
        waveSecondary: _lightSea,
        waveTertiary: Color(0xff93a7c0),
        spotlight: Color(0xfffff3d9),
      ),
      ConsoleAmbientPalette(
        backdropTop: Color(0xfff6f7f3),
        backdropMiddle: Color(0xffe8ece5),
        backdropBottom: Color(0xffcdd8cf),
        particlePrimary: Color(0xff128d9e),
        particleSecondary: Color(0xff3a7fb0),
        particleWarm: Color(0xffe0731f),
        mesh: Color(0xffd97e2a),
        star: Color(0xff7d7263),
        wavePrimary: Color(0xff128d9e),
        waveSecondary: Color(0xffd97e2a),
        waveTertiary: Color(0xff9aab9f),
        spotlight: Color(0xffffedc9),
      ),
      ConsoleAmbientPalette(
        backdropTop: Color(0xfff3f7fb),
        backdropMiddle: Color(0xffe4ebf5),
        backdropBottom: Color(0xffc7d3e2),
        particlePrimary: Color(0xff1b6fa8),
        particleSecondary: Color(0xff4a90c4),
        particleWarm: Color(0xffc9a24a),
        mesh: Color(0xff4a90c4),
        star: Color(0xff6b7f96),
        wavePrimary: Color(0xff1b6fa8),
        waveSecondary: Color(0xff4a90c4),
        waveTertiary: Color(0xff9db1c9),
        spotlight: Color(0xfffff3d9),
      ),
    ],
    ambientGeometry: _ambientGeometry,
    ambientRender: _ambientRender,
    ambientIntensity: 1,
    focusGradient: <Color>[
      _lightAccent,
      _lightAccentBright,
      _lightSea,
      _lightAccent,
    ],
    coverToneFamilies: <List<Color>>[
      <Color>[Color(0xffdff0ea), Color(0xffb9e2d3), Color(0xff0f8a68)],
      <Color>[Color(0xfff6e7e0), Color(0xffeccabb), Color(0xffb23c22)],
      <Color>[Color(0xfff4ecd8), Color(0xffe6d3a6), Color(0xff8a6410)],
      <Color>[Color(0xffe9e4f4), Color(0xffd3c6ec), Color(0xff6b4fa8)],
      <Color>[Color(0xffe2ecf6), Color(0xffc0d7ec), Color(0xff1b6fa8)],
    ],
    coverRendering: _baselineCoverRendering,
    coverPlateBackdrop: Color(0xffdfe6f0),
    coverMonogramStyle: TextStyle(
      fontFamily: _displayFamily,
      fontWeight: FontWeight.w800,
      letterSpacing: 1,
      height: 1,
    ),
    highlight: Colors.white,
    shadow: Color(0xff22303d),
    // Dark rim on the pale ground, mirroring the dark skin's 0.08 white rim.
    avatarRim: Color.from(
      alpha: 0.1,
      red: 34 / 255,
      green: 48 / 255,
      blue: 61 / 255,
    ),
    verificationCodeStyle: TextStyle(
      fontFamily: _displayFamily,
      fontSize: 48,
      height: 1.08,
      fontWeight: FontWeight.w800,
      letterSpacing: 8,
      color: _lightInk,
      fontFeatures: <FontFeature>[FontFeature.tabularFigures()],
    ),
    entryBackdrop: Color(0xff07101a),
    entryPromptAccent: Color(0xfff3b944),
    entryStartPromptStyle: TextStyle(
      fontSize: 18,
      fontWeight: FontWeight.w700,
      color: Color(0xfffff9ee),
    ),
  );

  static const ConsoleArtworkTheme _testArtwork = ConsoleArtworkTheme(
    ambientPalettes: <ConsoleAmbientPalette>[
      ConsoleAmbientPalette(
        backdropTop: Color(0xffffff00),
        backdropMiddle: Color(0xffff00ff),
        backdropBottom: Color(0xff00e5ff),
        particlePrimary: Color(0xff6d00a8),
        particleSecondary: Color(0xff00c853),
        particleWarm: Color(0xffff6d00),
        mesh: Color(0xff001b44),
        star: Color(0xffffffff),
        wavePrimary: Color(0xffff00ff),
        waveSecondary: Color(0xff00e5ff),
        waveTertiary: Color(0xffff6d00),
        spotlight: Color(0xffffff00),
      ),
      ConsoleAmbientPalette(
        backdropTop: Color(0xffb2ff59),
        backdropMiddle: Color(0xffff00ff),
        backdropBottom: Color(0xff00e5ff),
        particlePrimary: Color(0xffff6d00),
        particleSecondary: Color(0xff6d00a8),
        particleWarm: Color(0xffffff00),
        mesh: Color(0xff00c853),
        star: Color(0xffffffff),
        wavePrimary: Color(0xffff6d00),
        waveSecondary: Color(0xffff00ff),
        waveTertiary: Color(0xff00e5ff),
        spotlight: Color(0xffb2ff59),
      ),
      ConsoleAmbientPalette(
        backdropTop: Color(0xff00e5ff),
        backdropMiddle: Color(0xffffff00),
        backdropBottom: Color(0xffff00ff),
        particlePrimary: Color(0xff00c853),
        particleSecondary: Color(0xffff6d00),
        particleWarm: Color(0xff6d00a8),
        mesh: Color(0xffffffff),
        star: Color(0xff001b44),
        wavePrimary: Color(0xff00e5ff),
        waveSecondary: Color(0xffffff00),
        waveTertiary: Color(0xffff00ff),
        spotlight: Color(0xffffffff),
      ),
    ],
    ambientGeometry: _ambientGeometry,
    ambientRender: _ambientRender,
    ambientIntensity: 0.45,
    focusGradient: <Color>[
      Color(0xffff00ff),
      Color(0xffffff00),
      Color(0xff00e5ff),
      Color(0xffff00ff),
    ],
    coverToneFamilies: <List<Color>>[
      <Color>[Color(0xffff6d00), Color(0xffffff00), Color(0xff6d00a8)],
    ],
    coverRendering: _testCoverRendering,
    coverPlateBackdrop: Color(0xff001b44),
    coverMonogramStyle: TextStyle(
      fontFamily: _monoFamily,
      fontWeight: FontWeight.w900,
      letterSpacing: 4,
      height: 1.2,
    ),
    highlight: Color(0xffffff00),
    shadow: Color(0xff6d00a8),
    avatarRim: Color(0xffff00ff),
    verificationCodeStyle: TextStyle(
      fontFamily: _monoFamily,
      fontSize: 54,
      height: 1,
      fontWeight: FontWeight.w900,
      letterSpacing: 10,
      color: Color(0xff001b44),
      fontFeatures: <FontFeature>[FontFeature.tabularFigures()],
    ),
    entryBackdrop: Color(0xffffff00),
    entryPromptAccent: Color(0xffff00ff),
    entryStartPromptStyle: TextStyle(
      fontFamily: _monoFamily,
      fontSize: 22,
      fontWeight: FontWeight.w900,
      color: Color(0xff001b44),
    ),
  );
}
