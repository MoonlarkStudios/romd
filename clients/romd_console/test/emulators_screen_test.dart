import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/session/domain/emulator_settings_launcher.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/emulators_screen.dart';

final class _Launcher implements EmulatorSettingsLauncher {
  final Completer<LaunchResult> result = Completer<LaunchResult>();
  int calls = 0;

  @override
  Future<LaunchResult> openDolphin() {
    calls++;
    return result.future;
  }
}

void main() {
  testWidgets('keyboard opens Dolphin and restores focus after exit', (
    tester,
  ) async {
    final launcher = _Launcher();
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: EmulatorsScreen(launcher: launcher),
      ),
    );
    await tester.pump();

    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'open-dolphin-settings',
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    expect(launcher.calls, 1);
    expect(find.text('Preparing or running Dolphin settings…'), findsOneWidget);
    expect(find.text('Dolphin settings are running'), findsOneWidget);

    launcher.result.complete(const LaunchExited(0));
    await tester.pump();
    await tester.pump();

    expect(find.text('Dolphin settings closed.'), findsOneWidget);
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'open-dolphin-settings',
    );
  });

  testWidgets('gamepad primary action activates the focused control', (
    tester,
  ) async {
    final launcher = _Launcher();
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: EmulatorsScreen(launcher: launcher),
      ),
    );
    await tester.pump();

    await tester.sendKeyEvent(LogicalKeyboardKey.gameButtonA);
    await tester.pump();

    expect(launcher.calls, 1);
    launcher.result.complete(const LaunchExited(0));
    await tester.pump();
  });

  testWidgets('runtime failures are reported without losing row focus', (
    tester,
  ) async {
    final launcher = _Launcher();
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: EmulatorsScreen(launcher: launcher),
      ),
    );
    await tester.pump();

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    launcher.result.complete(
      const LaunchRuntimeMissing(<String>[], runtimeName: 'Dolphin'),
    );
    await tester.pump();
    await tester.pump();

    expect(find.text('Dolphin is not available.'), findsOneWidget);
    expect(
      tester.binding.focusManager.primaryFocus?.debugLabel,
      'open-dolphin-settings',
    );
  });

  testWidgets('busy screen refuses dismissal until Dolphin exits', (
    tester,
  ) async {
    final launcher = _Launcher();
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: Builder(
          builder: (context) => TextButton(
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) => EmulatorsScreen(launcher: launcher),
              ),
            ),
            child: const Text('Open route'),
          ),
        ),
      ),
    );
    await tester.tap(find.text('Open route'));
    await tester.pumpAndSettle();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pump();
    expect(find.byType(EmulatorsScreen), findsOneWidget);

    launcher.result.complete(const LaunchExited(0));
    await tester.pump();
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pumpAndSettle();
    expect(find.byType(EmulatorsScreen), findsNothing);
  });

  testWidgets('1280x720 at 2x text and reduced motion stays contained', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: const MediaQuery(
          data: MediaQueryData(
            textScaler: TextScaler.linear(2),
            disableAnimations: true,
          ),
          child: EmulatorsScreen(launcher: null),
        ),
      ),
    );
    await tester.pump();

    expect(tester.takeException(), isNull);
    expect(find.text('Emulation'), findsOneWidget);
    expect(find.text('ADVANCED EMULATOR SETTINGS'), findsOneWidget);
    expect(find.text('Dolphin settings'), findsOneWidget);
    expect(find.text('GameCube · Wii'), findsOneWidget);
    expect(
      find.text('Advanced Dolphin settings are unavailable in this build.'),
      findsOneWidget,
    );
  });

  testWidgets('mounted emulator settings re-resolves its visual skin', (
    tester,
  ) async {
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
          home: const EmulatorsScreen(launcher: null),
        ),
      ),
    );
    await tester.pump();

    // Scoped to the row: the shell's page band contributes its own
    // AnimatedContainer chrome above the content.
    final action = find.descendant(
      of: find.byKey(const ValueKey<String>('open-dolphin-settings-row')),
      matching: find.byType(AnimatedContainer),
    );
    final separator = find.descendant(
      of: find.byKey(const ValueKey<String>('open-dolphin-settings-row')),
      matching: find.byType(ColoredBox),
    );
    final title = find.text('Emulation');
    final actionElement = tester.element(action);
    final separatorElement = tester.element(separator);
    final titleElement = tester.element(title);
    final sizeBefore = tester.getSize(action);
    final actionBefore = tester.widget<AnimatedContainer>(action);
    final decorationBefore = actionBefore.decoration! as BoxDecoration;
    final separatorBefore = tester.widget<ColoredBox>(separator);
    final titleBefore = tester.widget<Text>(title);

    themeMode.value = ThemeMode.light;
    await tester.pump();

    final actionAfter = tester.widget<AnimatedContainer>(action);
    final decorationAfter = actionAfter.decoration! as BoxDecoration;
    final separatorAfter = tester.widget<ColoredBox>(separator);
    final titleAfter = tester.widget<Text>(title);
    expect(identical(actionElement, tester.element(action)), isTrue);
    expect(identical(separatorElement, tester.element(separator)), isTrue);
    expect(identical(titleElement, tester.element(title)), isTrue);
    expect(tester.getSize(action), isNot(sizeBefore));
    expect(actionAfter.duration, isNot(actionBefore.duration));
    expect(decorationAfter.borderRadius, isNot(decorationBefore.borderRadius));
    expect(separatorAfter.color, isNot(separatorBefore.color));
    expect(titleAfter.style!.fontFamily, isNot(titleBefore.style!.fontFamily));
    expect(titleAfter.style!.fontSize, isNot(titleBefore.style!.fontSize));
  });
}
