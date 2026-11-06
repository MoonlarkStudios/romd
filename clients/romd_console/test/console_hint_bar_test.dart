import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/input/console_input_mode.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_hint_bar.dart';

void main() {
  testWidgets('renders keyboard or gamepad glyphs from the active input mode', (
    tester,
  ) async {
    final inputMode = ValueNotifier<ConsoleInputMode>(
      ConsoleInputMode.keyboard,
    );
    addTearDown(inputMode.dispose);

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Scaffold(
          body: ConsoleInputModeScope(
            notifier: inputMode,
            child: const ConsoleHintBar(
              hints: <ConsoleHint>[
                ConsoleHint(glyph: '↵', gamepadGlyph: 'A', label: 'Select'),
                ConsoleHint(glyph: 'Esc', gamepadGlyph: 'B', label: 'Back'),
              ],
            ),
          ),
        ),
      ),
    );

    expect(find.text('↵'), findsOneWidget);
    expect(find.text('Esc'), findsOneWidget);
    expect(find.text('A'), findsNothing);
    expect(find.text('B'), findsNothing);

    inputMode.value = ConsoleInputMode.gamepad;
    await tester.pump();

    expect(find.text('A'), findsOneWidget);
    expect(find.text('B'), findsOneWidget);
    expect(find.text('↵'), findsNothing);
    expect(find.text('Esc'), findsNothing);
  });

  testWidgets(
    'mounted footer hints re-resolve chrome, metrics, type, and motion',
    (tester) async {
      final themeMode = ValueNotifier<ThemeMode>(ThemeMode.dark);
      addTearDown(themeMode.dispose);

      await tester.pumpWidget(
        ValueListenableBuilder<ThemeMode>(
          valueListenable: themeMode,
          builder: (context, value, _) => MaterialApp(
            theme: RomdSkins.seamTestTheme(),
            darkTheme: RomdSkins.baselineDark(),
            themeMode: value,
            themeAnimationDuration: Duration.zero,
            home: const Scaffold(
              body: Align(
                alignment: Alignment.bottomCenter,
                child: ConsoleFooterBar(
                  context: 'LIBRARY',
                  hints: <ConsoleHint>[
                    ConsoleHint(
                      glyph: '↵',
                      label: 'Open',
                      reserveLabel: 'Install now',
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      );
      await tester.pump();

      final footer = find
          .descendant(
            of: find.byType(ConsoleFooterBar),
            matching: find.byType(DecoratedBox),
          )
          .first;
      final keycap = find
          .ancestor(of: find.text('↵'), matching: find.byType(Container))
          .first;
      final label = find.text('Open');
      final switcher = find.byType(AnimatedSwitcher);
      final footerElement = tester.element(footer);
      final keycapElement = tester.element(keycap);
      final footerBefore = tester.widget<DecoratedBox>(footer);
      final footerDecorationBefore = footerBefore.decoration as BoxDecoration;
      final keycapBefore = tester.widget<Container>(keycap);
      final keycapDecorationBefore = keycapBefore.decoration! as BoxDecoration;
      final labelBefore = tester.widget<Text>(label);
      final switcherBefore = tester.widget<AnimatedSwitcher>(switcher);

      themeMode.value = ThemeMode.light;
      await tester.pump();

      final footerAfter = tester.widget<DecoratedBox>(footer);
      final footerDecorationAfter = footerAfter.decoration as BoxDecoration;
      final keycapAfter = tester.widget<Container>(keycap);
      final keycapDecorationAfter = keycapAfter.decoration! as BoxDecoration;
      final labelAfter = tester.widget<Text>(label);
      final switcherAfter = tester.widget<AnimatedSwitcher>(switcher);
      expect(identical(footerElement, tester.element(footer)), isTrue);
      expect(identical(keycapElement, tester.element(keycap)), isTrue);
      expect(footerDecorationAfter.color, isNot(footerDecorationBefore.color));
      expect(keycapAfter.constraints, isNot(keycapBefore.constraints));
      expect(keycapAfter.padding, isNot(keycapBefore.padding));
      expect(
        keycapDecorationAfter.borderRadius,
        isNot(keycapDecorationBefore.borderRadius),
      );
      expect(keycapDecorationAfter.color, isNot(keycapDecorationBefore.color));
      expect(labelAfter.style!.fontSize, isNot(labelBefore.style!.fontSize));
      expect(labelAfter.style!.color, isNot(labelBefore.style!.color));
      expect(switcherAfter.duration, isNot(switcherBefore.duration));
    },
  );
}
