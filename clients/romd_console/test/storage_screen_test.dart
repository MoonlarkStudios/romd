import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/storage_screen.dart';

void main() {
  Future<void> pumpStorage(
    WidgetTester tester, {
    required _FakeInstallService service,
    StorageInstallSort initialSort = StorageInstallSort.size,
    VoidCallback? onBack,
  }) async {
    addTearDown(service.close);
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: StorageScreen(
          installService: service,
          onBack: onBack ?? () {},
          initialSort: initialSort,
          clockNow: () => DateTime(2026, 7, 21, 14, 30),
        ),
      ),
    );
    await tester.pump();
  }

  testWidgets('shows loading then a truthful manifest-recorded summary', (
    tester,
  ) async {
    final service = _FakeInstallService();
    await pumpStorage(tester, service: service);

    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    expect(find.textContaining('free space'), findsNothing);

    service.emit(<LocalInstall>[
      _install(id: 'small', title: 'Alpha', sizeBytes: 512 * 1024 * 1024),
      _install(
        id: 'large',
        title: 'Zeta',
        sizeBytes: 2 * 1024 * 1024 * 1024,
        releaseName: 'Collector Edition',
        revision: 'Rev 2',
        platform: 'Nintendo GameCube',
        lastPlayedAt: DateTime(2026, 7, 20, 12),
      ),
    ]);
    await tester.pump();

    expect(find.text('THIS DEVICE'), findsOneWidget);
    expect(find.text('2.5 GB'), findsOneWidget);
    expect(find.text('across 2 installed games'), findsOneWidget);
    expect(
      find.text(
        'Totals come from release manifests, not device capacity. '
        'Removing a game keeps its saves.',
      ),
      findsOneWidget,
    );
    expect(find.text('Zeta'), findsOneWidget);
    expect(find.text('Alpha'), findsOneWidget);
    expect(
      find.textContaining('Nintendo GameCube · Collector Edition · Rev 2'),
      findsOneWidget,
    );
    expect(find.textContaining('Last played Jul 20, 2026'), findsOneWidget);
    expect(find.textContaining('Never played'), findsOneWidget);
  });

  testWidgets('offers all sort modes and applies the selected ordering', (
    tester,
  ) async {
    final service = _FakeInstallService();
    await pumpStorage(tester, service: service);
    service.emit(<LocalInstall>[
      _install(
        id: 'zeta',
        title: 'Zeta',
        sizeBytes: 200,
        installedAt: DateTime(2026, 7, 18, 12),
      ),
      _install(
        id: 'alpha',
        title: 'Alpha',
        sizeBytes: 100,
        installedAt: DateTime(2026, 7, 20, 12),
      ),
    ]);
    await tester.pump();

    expect(_rowTop(tester, 'zeta'), lessThan(_rowTop(tester, 'alpha')));

    await tester.tap(find.byKey(const ValueKey<String>('storage-sort')));
    await tester.pumpAndSettle();
    expect(find.text('Size'), findsWidgets);
    expect(find.text('Title'), findsOneWidget);
    expect(find.text('Installed date'), findsOneWidget);
    expect(find.text('Last played'), findsOneWidget);

    await tester.tap(find.text('Title'));
    await tester.pumpAndSettle();

    expect(_rowTop(tester, 'alpha'), lessThan(_rowTop(tester, 'zeta')));
    expect(find.text('Title'), findsOneWidget);
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'storage-sort');
  });

  testWidgets('renders an explicit empty state without capacity claims', (
    tester,
  ) async {
    final service = _FakeInstallService();
    await pumpStorage(tester, service: service);
    service.emit(const <LocalInstall>[]);
    await tester.pump();

    expect(find.text('0 B'), findsOneWidget);
    expect(
      find.text(
        'This profile has no installed games. Install one from its game page.',
      ),
      findsOneWidget,
    );
    expect(find.textContaining('Available'), findsNothing);
    expect(find.textContaining('capacity'), findsOneWidget);
  });

  testWidgets('recovers from a watch error with a focusable retry', (
    tester,
  ) async {
    final service = _FakeInstallService();
    await pumpStorage(tester, service: service);
    service.emitError(StateError('database unavailable'));
    await tester.pump();
    await tester.pump();

    expect(find.text('Installed content unavailable'), findsOneWidget);
    expect(find.text('Try again'), findsOneWidget);
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'storage-retry');

    await tester.tap(find.byKey(const ValueKey<String>('storage-retry')));
    await tester.pump();
    expect(service.watchCount, 2);
    expect(find.byType(CircularProgressIndicator), findsOneWidget);

    service.emit(<LocalInstall>[_install(id: 'ready', title: 'Ready')]);
    await tester.pump();
    expect(find.text('Ready'), findsOneWidget);
  });

  testWidgets('confirms profile-scoped removal and restores adjacent focus', (
    tester,
  ) async {
    final service = _FakeInstallService();
    await pumpStorage(tester, service: service);
    service.emit(<LocalInstall>[
      _install(id: 'large', title: 'Large', sizeBytes: 300),
      _install(id: 'middle', title: 'Middle', sizeBytes: 200),
      _install(id: 'small', title: 'Small', sizeBytes: 100),
    ]);
    await tester.pump();

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'storage-install-large',
    );
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();

    expect(find.text('Remove Large?'), findsOneWidget);
    expect(
      find.textContaining('removes the game from the active profile'),
      findsOneWidget,
    );
    expect(find.textContaining('Saves remain'), findsOneWidget);
    expect(
      find.textContaining('Shared physical content may remain'),
      findsOneWidget,
    );

    await tester.tap(find.widgetWithText(TextButton, 'Remove'));
    await tester.pump();
    await tester.pump();

    expect(service.uninstalled, <String>['large']);
    expect(find.text('Large'), findsNothing);
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'storage-install-middle',
    );
    expect(find.text('Large removed from the active profile.'), findsOneWidget);
  });

  testWidgets('serializes removal and reports failure without losing focus', (
    tester,
  ) async {
    final service = _FakeInstallService()..uninstallGate = Completer<void>();
    await pumpStorage(tester, service: service);
    service.emit(<LocalInstall>[
      _install(id: 'first', title: 'First', sizeBytes: 200),
      _install(id: 'second', title: 'Second', sizeBytes: 100),
    ]);
    await tester.pump();

    await tester.tap(
      find.byKey(const ValueKey<String>('storage-install-first')),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(TextButton, 'Remove'));
    await tester.pump();

    expect(find.text('Removing…'), findsOneWidget);
    expect(service.uninstalled, <String>['first']);
    await tester.tap(
      find.byKey(const ValueKey<String>('storage-install-second')),
    );
    await tester.pump();
    expect(find.text('Remove Second?'), findsNothing);
    expect(service.uninstalled, <String>['first']);

    service.uninstallError = StateError('cannot remove');
    service.uninstallGate!.complete();
    await tester.pump();
    await tester.pump();

    expect(
      find.text("Couldn't remove First from the active profile. Try again."),
      findsOneWidget,
    );
    expect(
      FocusManager.instance.primaryFocus?.debugLabel,
      'storage-install-first',
    );
    expect(find.text('First'), findsOneWidget);
  });

  testWidgets('escape uses the shared settings back contract', (tester) async {
    final service = _FakeInstallService();
    var backCount = 0;
    await pumpStorage(tester, service: service, onBack: () => backCount++);
    service.emit(const <LocalInstall>[]);
    await tester.pump();

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pump();
    expect(backCount, 1);
  });
}

double _rowTop(WidgetTester tester, String releaseId) => tester
    .getTopLeft(find.byKey(ValueKey<String>('storage-install-$releaseId')))
    .dy;

LocalInstall _install({
  required String id,
  required String title,
  int sizeBytes = 1024,
  String releaseName = 'Standard Edition',
  String? revision,
  String platform = 'Nintendo Entertainment System',
  DateTime? installedAt,
  DateTime? lastPlayedAt,
}) => LocalInstall(
  serverInstanceId: 'server',
  releaseId: id,
  titleId: 'title-$id',
  titleName: title,
  platformId: 'platform',
  platformName: platform,
  platformShortName: 'NES',
  coverUrl: null,
  releaseName: releaseName,
  releaseRevision: revision,
  contentRoot: '/content/$id',
  launchRelativePath: '$id.rom',
  sizeBytes: sizeBytes,
  primarySha256: null,
  manifestFingerprint: 'fingerprint-$id',
  state: InstallState.installed,
  installMode: 'permanent',
  items: const <InstalledItem>[],
  installedAt: installedAt ?? DateTime(2026, 7, 19, 12),
  lastPlayedAt: lastPlayedAt,
);

final class _FakeInstallService implements InstallService {
  final StreamController<List<LocalInstall>> _controller =
      StreamController<List<LocalInstall>>.broadcast();
  List<LocalInstall> _installs = const <LocalInstall>[];
  final List<String> uninstalled = <String>[];
  int watchCount = 0;
  Completer<void>? uninstallGate;
  Object? uninstallError;

  void emit(List<LocalInstall> installs) {
    _installs = List<LocalInstall>.of(installs);
    _controller.add(_installs);
  }

  void emitError(Object error) => _controller.addError(error);

  Future<void> close() => _controller.close();

  @override
  Future<LocalInstall?> findInstall(String releaseId) async {
    for (final install in _installs) {
      if (install.releaseId == releaseId) return install;
    }
    return null;
  }

  @override
  Stream<InstallProgress> install(
    PlayTarget target, {
    required InstallOperationLease operation,
    String? accessToken,
  }) => const Stream<InstallProgress>.empty();

  @override
  Future<List<LocalInstall>> listInstalled() async => _installs;

  @override
  Future<List<LocalInstall>> listInstalledForTitle(String titleId) async =>
      _installs
          .where((install) => install.titleId == titleId)
          .toList(growable: false);

  @override
  Future<void> uninstall(
    String releaseId, {
    InstallOperationLease? operation,
  }) async {
    uninstalled.add(releaseId);
    await uninstallGate?.future;
    if (uninstallError case final error?) throw error;
    _installs = _installs
        .where((install) => install.releaseId != releaseId)
        .toList(growable: false);
    _controller.add(_installs);
  }

  @override
  Stream<List<LocalInstall>> watchInstalled() {
    watchCount++;
    return _controller.stream;
  }

  @override
  Stream<List<LocalInstall>> watchInstalledForTitle(String titleId) =>
      _controller.stream.map(
        (installs) => installs
            .where((install) => install.titleId == titleId)
            .toList(growable: false),
      );

  @override
  Stream<Set<String>> watchInstalledReleaseIds() => _controller.stream.map(
    (installs) => installs.map((install) => install.releaseId).toSet(),
  );
}
