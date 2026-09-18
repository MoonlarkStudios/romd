import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/local_profile.dart';
import 'package:romd_console/src/presentation/entry_stage.dart';
import 'package:romd_console/src/presentation/local_profile_selection_screen.dart';
import 'package:romd_console/src/presentation/profile_settings_screen.dart';
import 'package:romd_console/src/presentation/widgets/console_ambient_background.dart';
import 'package:romd_console/src/presentation/widgets/create_profile_screen.dart';
import 'package:romd_console/src/presentation/widgets/emulators_screen.dart';
import 'package:romd_console/src/presentation/widgets/ottercade_brand.dart';
import 'package:romd_console/src/presentation/widgets/storage_screen.dart';

import 'console_golden_fixtures.dart';
import 'console_golden_harness.dart';

void main() {
  configureConsoleGoldenTests();

  testWidgets('baseline dark Ottercade attract', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
      child: ConsoleAmbientBackground(
        effectIntensity: 0.25,
        child: EntryStage(
          repository: GoldenProfileRepository(goldenProfiles),
          defaultRomdServerOrigin: RomdServerOrigins.defaultUri,
          onSelected: (_) {},
          onStartedChanged: (_) {},
        ),
      ),
    );
    final context = tester.element(find.byType(EntryStage));
    await tester.runAsync(
      () => Future.wait(<Future<void>>[
        precacheImage(const AssetImage(ottercadeRompAsset), context),
        precacheImage(const AssetImage(ottercadeWordmarkCreamAsset), context),
      ]),
    );
    await tester.pump();

    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_dark/ottercade_attract.png'),
    );
  });

  testWidgets('baseline dark profile selection', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
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
      matchesGoldenFile('../goldens/baseline_dark/profile_selection.png'),
    );
  });

  testWidgets('baseline dark profile creation', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
      child: CreateProfileScreen(
        initialRomdServerOrigin: RomdServerOrigins.defaultUri,
      ),
    );

    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_dark/profile_creation.png'),
    );
  });

  testWidgets('baseline dark settings root', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
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
      matchesGoldenFile('../goldens/baseline_dark/settings_root.png'),
    );

    await tester.tap(
      find.byKey(const ValueKey<String>('settings-account-row')),
    );
    await tester.pumpAndSettle();
    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile(
        '../goldens/baseline_dark/settings_profile_account.png',
      ),
    );
  });

  testWidgets('baseline dark secondary settings surface', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
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
      matchesGoldenFile('../goldens/baseline_dark/settings_secondary.png'),
    );
  });

  testWidgets('baseline dark catalog components', (tester) async {
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
      matchesGoldenFile('../goldens/baseline_dark/catalog_components.png'),
    );
  });

  testWidgets('baseline dark platform marks', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
      child: const GoldenPlatformLogoBoard(),
    );
    await tester.pump();
    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_dark/platform_marks.png'),
    );
  });

  testWidgets('baseline dark settings storage', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
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
      matchesGoldenFile('../goldens/baseline_dark/settings_storage.png'),
    );
  });

  testWidgets('baseline dark system components', (tester) async {
    await ConsoleGoldenHarness.pump(
      tester,
      child: const GoldenSystemComponentsBoard(),
    );
    await expectLater(
      find.byKey(consoleGoldenSurfaceKey),
      matchesGoldenFile('../goldens/baseline_dark/system_components.png'),
    );
  });
}
