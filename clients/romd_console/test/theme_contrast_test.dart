import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/profile_identity_palette.dart';
import 'package:romd_console/src/presentation/theme/console_theme_extensions.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';

void main() {
  // The seam-test theme is deliberately garish and is a runtime-contract
  // fixture, not a user-facing skin. Every production skin belongs here.
  final productionSkins = <String, ThemeData>{
    'baseline dark': RomdSkins.baselineDark(),
    'baseline light': RomdSkins.baselineLight(),
  };

  for (final MapEntry(key: name, value: theme) in productionSkins.entries) {
    group('$name contrast contract', () {
      final scheme = theme.colorScheme;
      final colors = theme.extension<ConsoleColors>()!;
      final scaffold = theme.scaffoldBackgroundColor;
      final panel = _composite(colors.panelSurface, scaffold);
      final dialog = _composite(colors.dialogSurface, scaffold);
      final footer = _composite(colors.footerSurface, scaffold);
      final hintKeycap = _composite(colors.hintKeycapSurface, footer);

      test('essential text remains readable on supported surfaces', () {
        final surfaces = <String, Color>{
          'scaffold': scaffold,
          'panel': panel,
          'dialog': dialog,
          'footer': footer,
        };
        final foregrounds = <String, Color>{
          'textStrong': colors.textStrong,
          'textBody': colors.textBody,
          'textMuted': colors.textMuted,
        };

        for (final MapEntry(key: foregroundName, value: foreground)
            in foregrounds.entries) {
          for (final MapEntry(key: surfaceName, value: surface)
              in surfaces.entries) {
            _expectContrast(
              foreground,
              surface,
              4.5,
              '$foregroundName on $surfaceName',
            );
          }
        }

        _expectContrast(colors.hintText, footer, 4.5, 'hintText on footer');
        _expectContrast(
          colors.keycapForeground,
          hintKeycap,
          4.5,
          'keycapForeground on hintKeycapSurface',
        );
        _expectContrast(
          colors.onMediaOverlay,
          colors.mediaBackdrop,
          4.5,
          'onMediaOverlay on mediaBackdrop',
        );
      });

      test('ColorScheme on-colors satisfy WCAG text contrast', () {
        final pairs = <String, (Color, Color)>{
          'primary': (scheme.onPrimary, scheme.primary),
          'primaryContainer': (
            scheme.onPrimaryContainer,
            scheme.primaryContainer,
          ),
          'secondary': (scheme.onSecondary, scheme.secondary),
          'secondaryContainer': (
            scheme.onSecondaryContainer,
            scheme.secondaryContainer,
          ),
          'tertiary': (scheme.onTertiary, scheme.tertiary),
          'tertiaryContainer': (
            scheme.onTertiaryContainer,
            scheme.tertiaryContainer,
          ),
          'error': (scheme.onError, scheme.error),
          'errorContainer': (scheme.onErrorContainer, scheme.errorContainer),
          'surface': (scheme.onSurface, scheme.surface),
        };

        for (final MapEntry(key: role, value: pair) in pairs.entries) {
          _expectContrast(pair.$1, pair.$2, 4.5, 'on$role on $role');
        }
      });

      test('semantic status and interaction colors remain distinguishable', () {
        _expectContrast(colors.onWarning, colors.warning, 4.5, 'onWarning');
        _expectContrast(colors.connected, panel, 3, 'connected on panel');
        _expectContrast(
          colors.catalogAccent,
          panel,
          3,
          'catalogAccent on panel',
        );
        _expectContrast(colors.focusBorder, scaffold, 3, 'focus on scaffold');
        _expectContrast(colors.focusBorder, panel, 3, 'focus on panel');
        _expectContrast(colors.selectionBorder, panel, 3, 'selection on panel');
      });

      test('media chrome stays readable over any artwork', () {
        // Chips float on covers/screenshots the skin cannot recolor, so the
        // scrim pill must carry the accent over both extremes of art.
        _expectContrast(
          colors.onMediaAccent,
          _composite(colors.mediaChipSurface, Colors.white),
          4.5,
          'onMediaAccent over media chip on white art',
        );
        _expectContrast(
          colors.onMediaAccent,
          _composite(colors.mediaChipSurface, Colors.black),
          4.5,
          'onMediaAccent over media chip on black art',
        );
      });
    });
  }

  test('profile identity foregrounds remain readable user content', () {
    for (final accent in ProfileIdentityPalette.accents) {
      _expectContrast(
        ProfileIdentityPalette.foregroundFor(accent),
        accent,
        4.5,
        'identity ${accent.toARGB32().toRadixString(16)}',
      );
    }
  });
}

Color _composite(Color foreground, Color background) =>
    foreground.a >= 1 ? foreground : Color.alphaBlend(foreground, background);

double _contrastRatio(Color foreground, Color background) {
  final opaqueBackground = _composite(background, Colors.black);
  final opaqueForeground = _composite(foreground, opaqueBackground);
  final foregroundLuminance = opaqueForeground.computeLuminance();
  final backgroundLuminance = opaqueBackground.computeLuminance();
  final lighter = foregroundLuminance > backgroundLuminance
      ? foregroundLuminance
      : backgroundLuminance;
  final darker = foregroundLuminance > backgroundLuminance
      ? backgroundLuminance
      : foregroundLuminance;
  return (lighter + 0.05) / (darker + 0.05);
}

void _expectContrast(
  Color foreground,
  Color background,
  double minimum,
  String role,
) {
  final ratio = _contrastRatio(foreground, background);
  expect(
    ratio,
    greaterThanOrEqualTo(minimum),
    reason:
        '$role has ${ratio.toStringAsFixed(2)}:1 contrast; '
        '${minimum.toStringAsFixed(1)}:1 is required',
  );
}
