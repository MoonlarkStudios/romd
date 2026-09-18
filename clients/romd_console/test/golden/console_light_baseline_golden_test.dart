import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/presentation/local_profile_selection_screen.dart';
import 'package:romd_console/src/presentation/profile_settings_screen.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/console_ambient_background.dart';
import 'package:romd_console/src/presentation/widgets/create_profile_screen.dart';
import 'package:romd_console/src/presentation/widgets/emulators_screen.dart';
import 'package:romd_console/src/presentation/widgets/storage_screen.dart';

import 'console_golden_fixtures.dart';
import 'console_golden_harness.dart';

void main() {
  configureConsoleGoldenTests();

  testWidgets('baseline light profile selection', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
      theme: RomdSkins.baselineLight(),
      child: ConsoleAmbientBackground(
        child: LocalProfileSelectionScreen(
          repository: GoldenProfileRepository(goldenProfiles),
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          showClock: false,
          animateIntro: false,
          onSelected: (_) {},
          onBack: () {},
        ),
      ),
    );

    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_light/profile_selection.png'),
    );
  });

  testWidgets('baseline light profile creation', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
      theme: RomdSkins.baselineLight(),
      child: CreateProfileScreen(
        initialRomdServerOrigin: RomdServerOrigins.defaultUri,
      ),
    );

    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_light/profile_creation.png'),
    );
  });

  testWidgets('baseline light settings root', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
      theme: RomdSkins.baselineLight(),
      child: ConsoleAmbientBackground(
        dimmed: true,
        child: ProfileSettingsScreen(
          localProfile: goldenProfiles.first,
          connected: true,
          onConnect: () {},
          onChangeServer: () {},
          onRemoveServer: () {},
          onSignOut: () {},
          onStorage: () {},
          onControllers: () {},
          onEmulation: () {},
          onSwitchProfile: () {},
          onBack: () {},
          clockNow: () => ConsoleGoldenHarness.fixedNow,
        ),
      ),
    );

    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_light/settings_root.png'),
    );

    await tester.tap(
      find.byKey(const ValueKey<String>('settings-account-row')),
    );
    await tester.pumpAndSettle();
    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile(
        '../goldens/baseline_light/settings_profile_account.png',
      ),
    );
  });

  testWidgets('baseline light secondary settings surface', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
      theme: RomdSkins.baselineLight(),
      child: ConsoleAmbientBackground(
        dimmed: true,
        child: EmulatorsScreen(
          launcher: null,
          clockNow: () => ConsoleGoldenHarness.fixedNow,
        ),
      ),
    );

    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_light/settings_secondary.png'),
    );
  });

  testWidgets('baseline light catalog components', (tester) async {
    final coverImage = await tester.runAsync(
      ConsoleGoldenHarness.createCoverImage,
    );
    expect(coverImage, isNotNull);
    final navNodes = <FocusNode>[FocusNode(), FocusNode(), FocusNode()];
    addTearDown(() {
      for (final node in navNodes) {
        node.dispose();
      }
    });

    await ConsoleGoldenHarness.pump(
      tester,
      theme: RomdSkins.baselineLight(),
      child: GoldenCatalogComponentBoard(
        coverImage: coverImage!,
        navNodes: navNodes,
      ),
    );
    await tester.runAsync(
      () => precacheImage(
        coverImage,
        tester.element(find.byKey(consoleGoldenSurfaceKey)),
      ),
    );
    await tester.pump();
    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_light/catalog_components.png'),
    );
  });

  testWidgets('baseline light platform marks', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
      theme: RomdSkins.baselineLight(),
      child: const GoldenPlatformLogoBoard(),
    );
    await tester.pump();
    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_light/platform_marks.png'),
    );
  });

  testWidgets('baseline light settings storage', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
      theme: RomdSkins.baselineLight(),
      child: ConsoleAmbientBackground(
        dimmed: true,
        child: StorageScreen(
          installService: GoldenInstallService(goldenInstalls),
          onBack: () {},
          clockNow: () => ConsoleGoldenHarness.fixedNow,
        ),
      ),
    );
    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_light/settings_storage.png'),
    );
  });

  testWidgets('baseline light system components', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
      theme: RomdSkins.baselineLight(),
      child: const GoldenSystemComponentsBoard(),
    );
    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_light/system_components.png'),
    );
  });
}
