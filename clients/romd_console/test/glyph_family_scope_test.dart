import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/input/console_input_mode.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/presentation/glyph_family_scope.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_hint_bar.dart';

void main() {
  group('faceButtonGlyph', () {
    test('xbox, generic, and keyboard keep the authored tokens', () {
      for (final family in <GlyphFamily>[
        GlyphFamily.xbox,
        GlyphFamily.generic,
        GlyphFamily.keyboard,
      ]) {
        expect(faceButtonGlyph('A', family), 'A');
        expect(faceButtonGlyph('Y', family), 'Y');
      }
    });

    test('nintendo mirrors the letters positionally', () {
      expect(faceButtonGlyph('A', GlyphFamily.nintendo), 'B');
      expect(faceButtonGlyph('B', GlyphFamily.nintendo), 'A');
      expect(faceButtonGlyph('X', GlyphFamily.nintendo), 'Y');
      expect(faceButtonGlyph('Y', GlyphFamily.nintendo), 'X');
    });

    test('playstation uses shapes', () {
      expect(faceButtonGlyph('A', GlyphFamily.playstation), '✕');
      expect(faceButtonGlyph('B', GlyphFamily.playstation), '○');
      expect(faceButtonGlyph('X', GlyphFamily.playstation), '□');
      expect(faceButtonGlyph('Y', GlyphFamily.playstation), '△');
    });

    test('non-face tokens pass through untouched', () {
      expect(faceButtonGlyph('D-PAD', GlyphFamily.nintendo), 'D-PAD');
      expect(faceButtonGlyph('Esc', GlyphFamily.playstation), 'Esc');
    });
  });

  testWidgets('hint bar renders controller positions as family labels', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: ConsoleInputModeScope(
          notifier: ValueNotifier<ConsoleInputMode>(ConsoleInputMode.gamepad),
          child: GlyphFamilyScope(
            notifier: ValueNotifier<GlyphFamily>(GlyphFamily.nintendo),
            child: const Scaffold(
              body: ConsoleHintBar(
                hints: <ConsoleHint>[
                  ConsoleHint(glyph: '↵', gamepadGlyph: 'A', label: 'Select'),
                  ConsoleHint(glyph: 'Esc', gamepadGlyph: 'B', label: 'Back'),
                  ConsoleHint(glyph: 'C', gamepadGlyph: 'X', label: 'Controls'),
                  ConsoleHint(glyph: 'D', gamepadGlyph: 'Y', label: 'Details'),
                ],
              ),
            ),
          ),
        ),
      ),
    );

    expect(find.text('B'), findsOneWidget);
    expect(find.text('A'), findsOneWidget);
    expect(find.text('Y'), findsOneWidget);
    expect(find.text('X'), findsOneWidget);
    expect(find.text('Select'), findsOneWidget);
    expect(find.text('Back'), findsOneWidget);
    expect(find.text('Controls'), findsOneWidget);
    expect(find.text('Details'), findsOneWidget);

    expect(
      tester.getTopLeft(find.text('B')).dx,
      lessThan(tester.getTopLeft(find.text('Select')).dx),
    );
    expect(
      tester.getTopLeft(find.text('A')).dx,
      lessThan(tester.getTopLeft(find.text('Back')).dx),
    );
    expect(
      tester.getTopLeft(find.text('Y')).dx,
      lessThan(tester.getTopLeft(find.text('Controls')).dx),
    );
    expect(
      tester.getTopLeft(find.text('X')).dx,
      lessThan(tester.getTopLeft(find.text('Details')).dx),
    );
  });

  testWidgets('keyboard mode ignores the family entirely', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: GlyphFamilyScope(
          notifier: ValueNotifier<GlyphFamily>(GlyphFamily.playstation),
          child: const Scaffold(
            body: ConsoleHintBar(
              hints: <ConsoleHint>[
                ConsoleHint(glyph: '↵', gamepadGlyph: 'A', label: 'Select'),
              ],
            ),
          ),
        ),
      ),
    );

    expect(find.text('↵'), findsOneWidget);
    expect(find.text('✕'), findsNothing);
  });
}
