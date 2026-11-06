import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_ambient_background.dart';

void main() {
  testWidgets('renders every ambient background concept', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    for (final concept in ConsoleAmbientBackgroundConcept.values) {
      await tester.pumpWidget(
        MaterialApp(
          theme: RomdSkins.baselineDark(),
          home: SizedBox.expand(
            child: ConsoleAmbientBackground(concept: concept),
          ),
        ),
      );
      await tester.pump(const Duration(milliseconds: 16));

      expect(find.byType(ConsoleAmbientBackground), findsOneWidget);
    }

    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets('reduced motion freezes the ambient ticker', (tester) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: const MediaQuery(
          data: MediaQueryData(disableAnimations: true),
          child: SizedBox.expand(child: ConsoleAmbientBackground()),
        ),
      ),
    );

    // With the flow ticker stopped the tree settles; left running, pumpAndSettle
    // would time out — so reaching here proves reduced motion froze the field.
    await tester.pumpAndSettle();
    expect(find.byType(ConsoleAmbientBackground), findsOneWidget);

    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets('dimmed in-app ambience is static and resumes when undimmed', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() async {
      await tester.binding.setSurfaceSize(null);
    });

    Future<void> pumpAmbient({required bool dimmed}) => tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: SizedBox.expand(child: ConsoleAmbientBackground(dimmed: dimmed)),
      ),
    );

    await pumpAmbient(dimmed: true);
    await tester.pumpAndSettle();
    expect(find.byType(ConsoleAmbientBackground), findsOneWidget);

    await pumpAmbient(dimmed: false);
    await tester.pump(const Duration(milliseconds: 16));
    expect(tester.binding.hasScheduledFrame, isTrue);

    await pumpAmbient(dimmed: true);
    await tester.pumpAndSettle();
    expect(tester.binding.hasScheduledFrame, isFalse);

    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets(
    'cross-fades tone changes and applies reduced-motion changes immediately',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1280, 720));
      addTearDown(() async {
        await tester.binding.setSurfaceSize(null);
      });
      const firstTone = Color(0xff10242a);
      const secondTone = Color(0xff201a2c);

      Future<void> pumpTone(Color tone, {required bool reducedMotion}) =>
          tester.pumpWidget(
            MaterialApp(
              theme: RomdSkins.baselineDark(),
              home: MediaQuery(
                data: MediaQueryData(disableAnimations: reducedMotion),
                child: SizedBox.expand(
                  child: ConsoleAmbientBackground(tone: tone),
                ),
              ),
            ),
          );

      await pumpTone(firstTone, reducedMotion: false);
      await pumpTone(secondTone, reducedMotion: false);

      var switcher = tester.widget<AnimatedSwitcher>(
        find.descendant(
          of: find.byType(ConsoleAmbientBackground),
          matching: find.byType(AnimatedSwitcher),
        ),
      );
      expect(switcher.duration, const Duration(milliseconds: 220));
      expect(find.byKey(const ValueKey<Color>(firstTone)), findsOneWidget);
      expect(find.byKey(const ValueKey<Color>(secondTone)), findsOneWidget);
      expect(switcher.child?.key, const ValueKey<Color>(secondTone));

      await pumpTone(secondTone, reducedMotion: true);
      await pumpTone(firstTone, reducedMotion: true);
      await tester.pump();

      switcher = tester.widget<AnimatedSwitcher>(
        find.descendant(
          of: find.byType(ConsoleAmbientBackground),
          matching: find.byType(AnimatedSwitcher),
        ),
      );
      expect(switcher.duration, Duration.zero);
      expect(switcher.child?.key, const ValueKey<Color>(firstTone));

      await tester.pumpWidget(const SizedBox.shrink());
    },
  );
}
