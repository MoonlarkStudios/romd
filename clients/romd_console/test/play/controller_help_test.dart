import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/presentation/glyph_family_scope.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/controller_help_screen.dart';

void main() {
  Future<void> pumpHelp(
    WidgetTester tester, {
    GlyphFamily? family,
    TextScaler textScaler = TextScaler.noScaling,
    ValueNotifier<ThemeMode>? themeMode,
  }) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    final home = family == null
        ? const ControllerHelpScreen()
        : GlyphFamilyScope(
            notifier: ValueNotifier<GlyphFamily>(family),
            child: const ControllerHelpScreen(),
          );
    Widget buildApp(ThemeMode? activeThemeMode) => MaterialApp(
      theme: activeThemeMode == null
          ? RomdSkins.baselineDark()
          : RomdSkins.seamTestTheme(),
      darkTheme: activeThemeMode == null ? null : RomdSkins.baselineDark(),
      themeMode: activeThemeMode,
      themeAnimationDuration: Duration.zero,
      home: MediaQuery(
        data: MediaQueryData(textScaler: textScaler),
        child: home,
      ),
    );
    await tester.pumpWidget(
      themeMode == null
          ? buildApp(null)
          : ValueListenableBuilder<ThemeMode>(
              valueListenable: themeMode,
              builder: (context, value, _) => buildApp(value),
            ),
    );
    await tester.pump();
  }

  testWidgets('renders the SELECT-modifier in-game control scheme', (
    tester,
  ) async {
    await pumpHelp(tester);

    expect(find.text('In-game shortcuts'), findsOneWidget);
    expect(find.text('Hold SELECT, then press:'), findsOneWidget);
    expect(find.text('SELECT + START'), findsOneWidget);
    expect(find.text('Menu'), findsOneWidget);
    expect(find.text('SELECT + R'), findsOneWidget);
    expect(find.text('Save State'), findsOneWidget);
    // Screenshot rides the north face button — 'Y' in the default (generic,
    // Xbox-lettered) family.
    expect(find.text('SELECT + Y'), findsOneWidget);
    expect(find.text('Screenshot'), findsOneWidget);
  });

  testWidgets('the screenshot chord label follows the glyph family', (
    tester,
  ) async {
    await pumpHelp(tester, family: GlyphFamily.nintendo);
    expect(find.text('SELECT + X'), findsOneWidget);

    await pumpHelp(tester, family: GlyphFamily.playstation);
    expect(find.text('SELECT + △'), findsOneWidget);
  });

  testWidgets('controller help remains overflow-free at 2x text scale', (
    tester,
  ) async {
    await pumpHelp(tester, textScaler: const TextScaler.linear(2));

    expect(find.text('In-game shortcuts'), findsOneWidget);
    expect(find.text('Save State'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('mounted controller help re-resolves its visual skin', (
    tester,
  ) async {
    final themeMode = ValueNotifier<ThemeMode>(ThemeMode.dark);
    addTearDown(themeMode.dispose);
    await pumpHelp(tester, themeMode: themeMode);

    final keycap = find
        .ancestor(
          of: find.text('SELECT + START'),
          matching: find.byType(Container),
        )
        .first;
    final title = find.text('In-game shortcuts');
    final keycapElement = tester.element(keycap);
    final titleElement = tester.element(title);
    final scaffoldBefore = tester.widget<Scaffold>(find.byType(Scaffold));
    final keycapBefore = tester.widget<Container>(keycap);
    final decorationBefore = keycapBefore.decoration! as BoxDecoration;
    final titleBefore = tester.widget<Text>(title);

    themeMode.value = ThemeMode.light;
    await tester.pump();

    final scaffoldAfter = tester.widget<Scaffold>(find.byType(Scaffold));
    final keycapAfter = tester.widget<Container>(keycap);
    final decorationAfter = keycapAfter.decoration! as BoxDecoration;
    final titleAfter = tester.widget<Text>(title);
    expect(identical(keycapElement, tester.element(keycap)), isTrue);
    expect(identical(titleElement, tester.element(title)), isTrue);
    expect(
      scaffoldAfter.backgroundColor,
      isNot(scaffoldBefore.backgroundColor),
    );
    expect(keycapAfter.constraints, isNot(keycapBefore.constraints));
    expect(keycapAfter.padding, isNot(keycapBefore.padding));
    expect(decorationAfter.color, isNot(decorationBefore.color));
    expect(decorationAfter.borderRadius, isNot(decorationBefore.borderRadius));
    expect(titleAfter.style!.fontFamily, isNot(titleBefore.style!.fontFamily));
    expect(titleAfter.style!.fontSize, isNot(titleBefore.style!.fontSize));
  });
}
