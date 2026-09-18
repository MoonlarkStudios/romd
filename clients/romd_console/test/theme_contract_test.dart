import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/theme/console_theme_context.dart';
import 'package:romd_console/src/presentation/theme/console_theme_extensions.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';

void main() {
  final skins = <String, ThemeData>{
    'baseline dark': RomdSkins.baselineDark(),
    'baseline light': RomdSkins.baselineLight(),
    'seam test': RomdSkins.seamTestTheme(),
  };

  group('registered skins', () {
    for (final MapEntry(key: name, value: theme) in skins.entries) {
      test('$name registers the complete Flutter-native contract', () {
        expect(theme.extension<ConsoleColors>(), isNotNull);
        expect(theme.extension<ConsoleLayoutTheme>(), isNotNull);
        expect(theme.extension<ConsoleElevationTheme>(), isNotNull);
        expect(theme.extension<ConsoleMotionTheme>(), isNotNull);
        expect(theme.extension<ConsoleArtworkTheme>(), isNotNull);
      });

      test('$name keeps essential text at the couch-readable floor', () {
        final roles = _textRoles(theme.textTheme);
        expect(roles, hasLength(15));
        for (final MapEntry(key: roleName, value: role) in roles.entries) {
          expect(role.fontSize, isNotNull, reason: '$roleName has no size');
          expect(
            role.fontSize,
            greaterThanOrEqualTo(14),
            reason: '$roleName is below the couch-readable floor',
          );
          expect(role.fontFamily, isNotNull, reason: '$roleName has no family');
        }
      });

      test('$name exposes an ordered, meaning-addressable type hierarchy', () {
        final text = theme.textTheme;
        for (final family in <(String, List<TextStyle>)>[
          (
            'display',
            <TextStyle>[
              text.displayLarge!,
              text.displayMedium!,
              text.displaySmall!,
            ],
          ),
          (
            'headline',
            <TextStyle>[
              text.headlineLarge!,
              text.headlineMedium!,
              text.headlineSmall!,
            ],
          ),
          (
            'title',
            <TextStyle>[text.titleLarge!, text.titleMedium!, text.titleSmall!],
          ),
          (
            'body',
            <TextStyle>[text.bodyLarge!, text.bodyMedium!, text.bodySmall!],
          ),
          (
            'label',
            <TextStyle>[text.labelLarge!, text.labelMedium!, text.labelSmall!],
          ),
        ]) {
          final sizes = family.$2.map((style) => style.fontSize!).toList();
          expect(
            sizes[0],
            greaterThanOrEqualTo(sizes[1]),
            reason: '${family.$1} Large must be >= Medium',
          );
          expect(
            sizes[1],
            greaterThanOrEqualTo(sizes[2]),
            reason: '${family.$1} Medium must be >= Small',
          );
        }

        expect(text.hero, same(text.displayLarge));
        expect(text.spotlightTitle, same(text.displayMedium));
        expect(text.pageHeading, same(text.displayMedium));
        expect(text.sectionHeading, same(text.displaySmall));
        expect(text.cardTitle, same(text.headlineLarge));
        expect(text.chromeStatus, same(text.headlineMedium));
        expect(text.supportingTitle, same(text.headlineSmall));
        expect(text.sectionLabel, same(text.titleMedium));
        expect(text.metadataStrong, same(text.titleSmall));
        expect(text.body, same(text.bodyLarge));
        expect(text.bodyCompact, same(text.bodyMedium));
        expect(text.metadata, same(text.bodySmall));
        expect(text.action, same(text.labelLarge));
        expect(text.chipLabel, same(text.labelMedium));
        expect(text.eyebrow, same(text.labelSmall));
        expect(text.utilityLabel, same(text.titleLarge));
        expect(
          text.sectionLabel.color,
          isNot(theme.colorScheme.primary),
          reason: 'typography owns shape; semantic color is applied separately',
        );
      });

      test('$name preserves semantic state strength', () {
        final colors = theme.extension<ConsoleColors>()!;
        final layout = theme.extension<ConsoleLayoutTheme>()!;
        final motion = theme.extension<ConsoleMotionTheme>()!;
        final elevation = theme.extension<ConsoleElevationTheme>()!;

        expect(colors.focusGlow.a, greaterThan(colors.focusFill.a));
        expect(colors.selectionBorder.a, greaterThan(colors.selectionFill.a));
        expect(colors.panelSurface, isNot(colors.focusBorder));
        expect(colors.panelBorder, isNot(colors.focusBorder));
        expect(layout.disabledOpacity, inInclusiveRange(0.2, 0.5));
        expect(colors.scrim.a, greaterThan(0.35));
        expect(layout.controlRadius, lessThan(layout.dialogRadius));
        expect(motion.focus, lessThan(motion.contentTransition));
        expect(motion.contentTransition, lessThan(motion.intro));
        expect(
          elevation.focusGlow.map((shadow) => shadow.color),
          everyElement(colors.focusGlow),
          reason: 'focus glow has one semantic color source',
        );
      });

      test('$name artwork contract is structurally valid', () {
        final artwork = theme.extension<ConsoleArtworkTheme>()!;
        expect(artwork.validate(), isEmpty);
      });
    }
  });

  test('baseline spacing is an ascending 4 px scale', () {
    final layout = RomdSkins.baselineDark().extension<ConsoleLayoutTheme>()!;
    final values = <double>[
      layout.xxs,
      layout.xs,
      layout.sm,
      layout.md,
      layout.lg,
      layout.xl,
      layout.xxl,
      layout.xxxl,
    ];

    expect(values, orderedEquals(<double>[4, 8, 12, 16, 24, 32, 48, 64]));
    expect(values.every((value) => value % 4 == 0), isTrue);
  });

  test('baseline stage gutter is the TV-safe anchor', () {
    final layout = RomdSkins.baselineDark().extension<ConsoleLayoutTheme>()!;

    // One shared left edge for band title, content, and footer caption.
    // 64 logical px is the ~5% action-safe margin at the 1280x720 canvas.
    expect(layout.screenGutter, 64);
    expect(layout.screenGutter % 4, 0);
  });

  test('baseline ColorScheme and console roles share one semantic mapping', () {
    final theme = RomdSkins.baselineDark();
    final scheme = theme.colorScheme;
    final colors = theme.extension<ConsoleColors>()!;

    expect(scheme.primary, colors.focusBorder);
    expect(scheme.secondary, colors.catalogAccent);
    expect(scheme.error, colors.warning);
    expect(scheme.surface, colors.dialogSurface);
    expect(scheme.scrim, colors.scrim);
  });

  test(
    'skin composition keeps density and motion independent from palette',
    () {
      final baseline = RomdSkins.baselineDark();
      final seam = RomdSkins.seamTestTheme();
      final recomposed = RomdSkins.compose(
        brightness: seam.brightness,
        colorScheme: seam.colorScheme,
        scaffoldBackgroundColor: seam.scaffoldBackgroundColor,
        fontFamily: seam.textTheme.bodyLarge!.fontFamily!,
        textTheme: seam.textTheme,
        colors: seam.extension<ConsoleColors>()!,
        elevation: seam.extension<ConsoleElevationTheme>()!,
        artwork: seam.extension<ConsoleArtworkTheme>()!,
      );

      expect(
        recomposed.extension<ConsoleLayoutTheme>(),
        same(baseline.extension<ConsoleLayoutTheme>()),
      );
      expect(
        recomposed.extension<ConsoleMotionTheme>(),
        same(baseline.extension<ConsoleMotionTheme>()),
      );
      expect(
        recomposed.extension<ConsoleColors>(),
        same(seam.extension<ConsoleColors>()),
      );
    },
  );

  test('skin composition refuses structurally invalid artwork', () {
    final baseline = RomdSkins.baselineDark();
    final invalidArtwork = baseline.extension<ConsoleArtworkTheme>()!.copyWith(
      ambientGeometry: const <ConsoleAmbientGeometrySpec>[],
    );

    expect(
      () => RomdSkins.compose(
        brightness: baseline.brightness,
        colorScheme: baseline.colorScheme,
        scaffoldBackgroundColor: baseline.scaffoldBackgroundColor,
        fontFamily: baseline.textTheme.bodyLarge!.fontFamily!,
        textTheme: baseline.textTheme,
        colors: baseline.extension<ConsoleColors>()!,
        elevation: baseline.extension<ConsoleElevationTheme>()!,
        artwork: invalidArtwork,
      ),
      throwsArgumentError,
    );
  });

  testWidgets('motion resolver honors reduced motion', (tester) async {
    late BuildContext context;
    final theme = RomdSkins.baselineDark();
    await tester.pumpWidget(
      MaterialApp(
        theme: theme,
        home: MediaQuery(
          data: const MediaQueryData(disableAnimations: true),
          child: Builder(
            builder: (builderContext) {
              context = builderContext;
              return const SizedBox.shrink();
            },
          ),
        ),
      ),
    );

    final motion = theme.extension<ConsoleMotionTheme>()!;
    expect(motion.resolve(context, motion.intro), Duration.zero);
  });

  testWidgets('motion resolver preserves timing by default', (tester) async {
    late BuildContext context;
    final theme = RomdSkins.baselineDark();
    await tester.pumpWidget(
      MaterialApp(
        theme: theme,
        home: Builder(
          builder: (builderContext) {
            context = builderContext;
            return const SizedBox.shrink();
          },
        ),
      ),
    );

    final motion = theme.extension<ConsoleMotionTheme>()!;
    expect(
      motion.resolve(context, motion.contentTransition),
      motion.contentTransition,
    );
  });
}

Map<String, TextStyle> _textRoles(TextTheme text) => <String, TextStyle>{
  'displayLarge': text.displayLarge!,
  'displayMedium': text.displayMedium!,
  'displaySmall': text.displaySmall!,
  'headlineLarge': text.headlineLarge!,
  'headlineMedium': text.headlineMedium!,
  'headlineSmall': text.headlineSmall!,
  'titleLarge': text.titleLarge!,
  'titleMedium': text.titleMedium!,
  'titleSmall': text.titleSmall!,
  'bodyLarge': text.bodyLarge!,
  'bodyMedium': text.bodyMedium!,
  'bodySmall': text.bodySmall!,
  'labelLarge': text.labelLarge!,
  'labelMedium': text.labelMedium!,
  'labelSmall': text.labelSmall!,
};
