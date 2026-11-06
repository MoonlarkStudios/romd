import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/theme/console_theme_extensions.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_settings.dart';

void main() {
  Future<void> setConsoleSize(WidgetTester tester) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
  }

  testWidgets('wide hierarchy enters detail and restores pane focus', (
    tester,
  ) async {
    await setConsoleSize(tester);
    final firstDetail = FocusNode(debugLabel: 'first detail');
    final secondDetail = FocusNode(debugLabel: 'second detail');
    addTearDown(firstDetail.dispose);
    addTearDown(secondDetail.dispose);
    var backs = 0;

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: ConsoleSettingsHierarchicalPage(
          title: 'Settings',
          showClock: false,
          onBack: () => backs += 1,
          categories: <ConsoleSettingsCategory>[
            ConsoleSettingsCategory(
              id: 'account',
              icon: Icons.person,
              title: 'Account',
              child: ConsoleSettingsActionRow(
                icon: Icons.login,
                title: 'Connect',
                onPressed: () {},
              ),
            ),
            ConsoleSettingsCategory(
              id: 'storage',
              icon: Icons.storage,
              title: 'Storage',
              child: Column(
                children: <Widget>[
                  ConsoleSettingsActionRow(
                    icon: Icons.inventory_2,
                    title: 'Installed games',
                    focusNode: firstDetail,
                    onPressed: () {},
                  ),
                  ConsoleSettingsActionRow(
                    icon: Icons.delete_outline,
                    title: 'Remove game',
                    focusNode: secondDetail,
                    onPressed: () {},
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
    await tester.pumpAndSettle();

    final colors = RomdSkins.baselineDark().extension<ConsoleColors>()!;
    expect(
      tester.widget<Text>(find.text('Storage')).style?.color,
      colors.textFaint,
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pumpAndSettle();
    expect(
      tester.widget<Text>(find.text('Account')).style?.color,
      colors.textFaint,
    );
    expect(find.text('Installed games'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pumpAndSettle();
    expect(firstDetail.hasFocus, isTrue);

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pumpAndSettle();
    expect(secondDetail.hasFocus, isTrue);

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
    await tester.pumpAndSettle();
    expect(secondDetail.hasFocus, isFalse);

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pumpAndSettle();
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'settings-row-Connect',
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pumpAndSettle();
    expect(secondDetail.hasFocus, isTrue);

    await tester.sendKeyEvent(LogicalKeyboardKey.gameButtonB);
    await tester.pumpAndSettle();
    expect(backs, 1);
  });

  testWidgets('large text uses stacked hierarchy and Back returns to rail', (
    tester,
  ) async {
    await setConsoleSize(tester);
    var backs = 0;
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: const MediaQueryData(textScaler: TextScaler.linear(2)),
          child: ConsoleSettingsHierarchicalPage(
            title: 'Settings',
            showClock: false,
            onBack: () => backs += 1,
            categories: <ConsoleSettingsCategory>[
              ConsoleSettingsCategory(
                id: 'profile',
                icon: Icons.person,
                title: 'Profile',
                child: ConsoleSettingsActionRow(
                  icon: Icons.edit,
                  title: 'Edit profile',
                  onPressed: () {},
                ),
              ),
            ],
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Edit profile'), findsNothing);
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();
    expect(find.text('Edit profile'), findsOneWidget);
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'settings-row-Edit profile',
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.gameButtonB);
    await tester.pumpAndSettle();
    expect(find.text('Edit profile'), findsNothing);
    expect(backs, 0);

    await tester.sendKeyEvent(LogicalKeyboardKey.gameButtonB);
    await tester.pumpAndSettle();
    expect(backs, 1);
  });

  testWidgets('choice state is distinct from focus chrome', (tester) async {
    await setConsoleSize(tester);
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Scaffold(
          body: Column(
            children: <Widget>[
              ConsoleSettingsChoiceRow(
                icon: Icons.gamepad,
                title: 'Focused choice',
                selected: false,
                autofocus: true,
                onPressed: () {},
              ),
              ConsoleSettingsChoiceRow(
                icon: Icons.gamepad,
                title: 'Saved choice',
                selected: true,
                onPressed: () {},
              ),
            ],
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    final savedRow = find.ancestor(
      of: find.text('Saved choice'),
      matching: find.byType(ConsoleSettingsRow),
    );
    final container = tester.widget<AnimatedContainer>(
      find.descendant(of: savedRow, matching: find.byType(AnimatedContainer)),
    );
    final decoration = container.decoration! as BoxDecoration;
    final border = decoration.border! as Border;
    expect(border.top.color, Colors.transparent);
    expect(
      find.descendant(
        of: savedRow,
        matching: find.byIcon(Icons.radio_button_checked),
      ),
      findsOneWidget,
    );

    final colors = RomdSkins.baselineDark().extension<ConsoleColors>()!;
    final focusedRow = find.ancestor(
      of: find.text('Focused choice'),
      matching: find.byType(ConsoleSettingsRow),
    );
    final focusedContainer = tester.widget<AnimatedContainer>(
      find.descendant(of: focusedRow, matching: find.byType(AnimatedContainer)),
    );
    final focusedBorder = focusedContainer.decoration! as BoxDecoration;
    expect((focusedBorder.border! as Border).top.color, colors.focusBorder);
  });
}
