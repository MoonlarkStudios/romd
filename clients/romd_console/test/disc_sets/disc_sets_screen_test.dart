import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/disc_sets/disc_set_playback.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_failure.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_library.dart';
import 'package:romd_console/src/play/disc_sets/domain/disc_set_models.dart';
import 'package:romd_console/src/play/session/domain/play_coordinator.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/disc_sets_screen.dart';

import '../helpers/in_memory_controller_preferences.dart';
import '../helpers/play_fakes.dart';
import '../support/fake_profile_local_library_repository.dart';
import 'disc_set_fixture.dart';

final class _UnusedInstall implements InstallService {
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _UnusedCoordinator implements PlayCoordinator {
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _UnusedRules implements RuntimeOverrideRuleRepository {
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

PlayServices services(Harness h, DiscSetLibrary library) => PlayServices(
  install: _UnusedInstall(),
  localLibrary: FakeProfileLocalLibraryRepository(),
  coordinator: _UnusedCoordinator(),
  playHistory: RecordingProfilePlayHistoryRepository(),
  runtimeResolver: const LocalRuntimeResolver(),
  runtimeRules: _UnusedRules(),
  activeLaunchSession: h.service.activeSession,
  controllerPreferences: InMemoryControllerPreferences(),
  controllerSlotClaims: SessionControllerSlotClaims(),
  discSets: library,
  discSetPlayback: DiscSetPlayback(service: h.service, providers: []),
);

final class _Library implements DiscSetLibrary {
  bool partial = false;
  int selects = 0;
  List<LocalDiscSet> local = [];
  DiscSetPlan? repaired;
  @override
  Future<bool> current() async => true;
  @override
  Future<List<DiscSetStatus>> list(String titleId) async => [
    DiscSetStatus(statusFixture(unavailable: partial)),
  ];
  @override
  Future<List<LocalDiscSet>> installed({String? titleId}) async => local;
  @override
  Future<DiscSetPlan?> pending(String set) async => null;
  @override
  Future<DiscSetPlan> select(
    DiscSetStatus status, {
    required bool partial,
  }) async {
    selects++;
    throw const DiscSetException(DiscSetFailureKind.unavailable);
  }

  @override
  Future<void> install(
    DiscSetPlan plan, {
    required String titleName,
    required InstallOperationLease operation,
    void Function(int, int)? progress,
  }) async {
    repaired = plan;
  }

  @override
  Future<void> uninstall(LocalDiscSet install) async {}
  @override
  void close() {}
}

void main() {
  late Harness h;
  late _Library library;
  setUp(() async {
    h = await Harness.create();
    library = _Library();
  });
  tearDown(() => h.close());
  Future<void> show(WidgetTester tester, {bool localOnly = false}) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        builder: (context, child) => MediaQuery(
          data: const MediaQueryData(disableAnimations: true),
          child: child!,
        ),
        home: DiscSetsScreen(
          services: services(h, library),
          titleId: localOnly ? null : titleId,
          titleName: localOnly ? null : 'Game',
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> cleanup(WidgetTester tester) =>
      tester.pumpWidget(const SizedBox());
  testWidgets(
    'unknown total is explicit; keyboard activates focused group install action',
    (tester) async {
      await show(tester);
      expect(find.text('2 known discs; total unknown'), findsOneWidget);
      final button = find.widgetWithText(OutlinedButton, 'Install known discs');
      expect(button, findsOneWidget);
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      // A user action requests a server selection; it never starts an emulator.
      expect(library.selects, 1);
      expect(
        find.text(
          'Installation paused. Retry to resume the same selection when access is available.',
        ),
        findsOneWidget,
      );
      await cleanup(tester);
    },
  );
  testWidgets(
    'missing descriptor requires exact partial confirmation; cancel issues no plan',
    (tester) async {
      library.partial = true;
      await show(tester);
      await tester.tap(find.text('Choose partial install'));
      await tester.pumpAndSettle();
      expect(find.text('Install a partial disc set?'), findsOneWidget);
      expect(find.textContaining('Disc 1: Disc 1'), findsWidgets);
      expect(find.textContaining('descriptor required'), findsWidgets);
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();
      expect(library.selects, 0);
      expect(h.downloads.count, 0);
      await cleanup(tester);
    },
  );
  testWidgets(
    'local group route shows installed identity with production playback gate',
    (tester) async {
      library.local = [
        LocalDiscSet(
          plan(),
          'Game',
          '/group/content',
          true,
          DateTime(2026),
          'revision',
        ),
      ];
      await show(tester, localOnly: true);
      expect(find.text('2 installed discs; total unknown'), findsOneWidget);
      final button = tester.widget<OutlinedButton>(
        find.widgetWithText(OutlinedButton, 'Playback qualification pending'),
      );
      expect(button.onPressed, isNull);
      expect(find.text('Remove from profile'), findsOneWidget);
      await cleanup(tester);
    },
  );
  testWidgets(
    'local repair keeps the exact installed snapshot rather than selecting a new set',
    (tester) async {
      final installed = plan();
      library.local = [
        LocalDiscSet(
          installed,
          'Game',
          '/group/content',
          true,
          DateTime(2026),
          'revision',
        ),
      ];
      await show(tester, localOnly: true);
      await tester.tap(find.text('Repair installed selection'));
      await tester.pumpAndSettle();
      expect(library.repaired!.snapshotId, installed.snapshotId);
      expect(library.selects, 0);
      await cleanup(tester);
    },
  );
}
