import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/domain/console_game.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/content/data/install_mutation_serializer.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/installed_play_target_resolver.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/emulator/data/dependencies/retroarch_dependency_resolver.dart';
import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_manager.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_provision.dart';
import 'package:romd_console/src/play/session/data/romd_play_coordinator.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';
import 'package:romd_console/src/play/session/domain/launch_authorization.dart';
import 'package:romd_console/src/play/session/domain/launch_provider.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_override_rules.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';
import 'package:romd_console/src/play/session/domain/runtime_resolver.dart';
import 'package:romd_console/src/presentation/catalog/catalog_operation_context.dart';
import 'package:romd_console/src/presentation/theme/romd_skins.dart';
import 'package:romd_console/src/presentation/widgets/game_detail.dart';

import '../helpers/in_memory_controller_preferences.dart';
import '../helpers/play_fakes.dart';
import '../support/fake_profile_local_library_repository.dart';

final _releaseId = RomdPublicId.tryParse(RomdPublicId.encode(101))!;
final _titleId = RomdPublicId.tryParse(RomdPublicId.encode(201))!;
final _instanceId = RomdServerInstanceId.tryParse(
  '11111111-1111-4111-8111-111111111111',
)!;
final _authority = InstallAuthorityContext(
  localProfileId: 'profile-1',
  connection: ProfileServerConnection(
    instanceId: _instanceId,
    origin: Uri.parse('https://library.example'),
    firstSeenAt: DateTime.utc(2026),
    lastSeenAt: DateTime.utc(2026),
  ),
  generation: 1,
);

final class _AllowAuthorizer implements LaunchAuthorizer {
  @override
  InstallAuthorityContext? get authority => _authority;

  @override
  Future<LaunchAuthorizationResult> authorize(PlayRequest request) async =>
      LaunchAuthorized(
        authority: _authority,
        releaseId: _releaseId,
        titleId: _titleId,
      );

  @override
  void close() {}
}

final class _DeniedAuthorizer implements LaunchAuthorizer {
  const _DeniedAuthorizer(this.result);

  final LaunchAuthorizationResult result;

  @override
  InstallAuthorityContext? get authority => _authority;

  @override
  Future<LaunchAuthorizationResult> authorize(PlayRequest request) async =>
      result;

  @override
  void close() {}
}

final class _Api implements ConsumerApiClient {
  _Api({this.media = const <ConsoleMediaRef>[]});

  final List<ConsoleMediaRef> media;

  @override
  Future<ConsoleGameDetail> getTitle({
    required String accessToken,
    required String titleId,
  }) async => ConsoleGameDetail(
    id: _titleId.value,
    platformId: 'p1',
    platformName: 'SNES',
    title: 'Chrono Trigger',
    description: 'A time-spanning RPG.',
    publisher: null,
    developer: null,
    genre: null,
    releaseDate: null,
    players: null,
    rating: null,
    media: media,
    releases: const <ConsoleRelease>[],
    defaultReleaseId: _releaseId.value,
  );

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _StubInstall
    implements InstallService, InstalledPlayTargetResolver {
  _StubInstall(
    this.events, {
    this.installed = const <LocalInstall>[],
    this.resolved,
    this.uninstallGate,
  });
  final List<InstallProgress> events;
  final List<LocalInstall> installed;
  final ResolvedPlayTarget? resolved;
  final Future<void>? uninstallGate;
  final Completer<void> uninstallStarted = Completer<void>();
  final List<String> uninstalled = <String>[];
  String? resolvedLocalProfileId;

  @override
  Future<LocalInstall?> findInstall(String releaseId) async =>
      _findInstall(releaseId);

  @override
  Future<List<LocalInstall>> listInstalled() async => installed;

  @override
  Future<List<LocalInstall>> listInstalledForTitle(String titleId) async =>
      installed.where((i) => i.titleId == titleId).toList(growable: false);

  @override
  Stream<List<LocalInstall>> watchInstalled() => Stream.value(installed);

  @override
  Stream<List<LocalInstall>> watchInstalledForTitle(String titleId) =>
      Stream.value(
        installed.where((i) => i.titleId == titleId).toList(growable: false),
      );

  @override
  Stream<Set<String>> watchInstalledReleaseIds() =>
      Stream.value(installed.map((i) => i.releaseId).toSet());

  @override
  Future<ResolvedPlayTarget?> resolveInstalledForLaunch({
    required InstallAuthorityContext authority,
    required String releaseId,
    required String titleId,
    required String displayName,
  }) async {
    resolvedLocalProfileId = authority.localProfileId;
    return resolved;
  }

  @override
  Stream<InstallProgress> install(
    PlayTarget target, {
    required InstallOperationLease operation,
    String? accessToken,
  }) => Stream<InstallProgress>.fromIterable(events);

  @override
  Future<void> uninstall(
    String releaseId, {
    InstallOperationLease? operation,
  }) async {
    if (!uninstallStarted.isCompleted) uninstallStarted.complete();
    final gate = uninstallGate;
    if (gate != null) await gate;
    if (operation != null && !operation.isCurrent) return;
    uninstalled.add(releaseId);
  }

  LocalInstall? _findInstall(String releaseId) {
    for (final install in installed) {
      if (install.releaseId == releaseId) {
        return install;
      }
    }
    return null;
  }
}

final class _EmptyBiosCatalog implements BiosCatalog {
  @override
  Future<List<BiosFileListing>> biosForPlatform(
    String platformShortName,
  ) async => const <BiosFileListing>[];
}

final class _NoDownloads implements DownloadClient {
  @override
  Stream<int> download({required Uri url, required File destination}) =>
      const Stream<int>.empty();
}

/// Always finds the managed RetroArch — the flow tests exercise dependency
/// resolution around the provisioner, not executable discovery.
final class _FoundRuntimeManager implements RuntimeManager {
  @override
  Future<RuntimeResolution> resolve(RuntimeDescriptor descriptor) async =>
      const RuntimeResolution(
        runtimeId: 'retroarch',
        source: RuntimeResolutionSource.managed,
        executablePath: '/rt/RetroArch.app',
      );
}

/// Rule store stub for flows that never touch the Run-with picker.
final class _NoopRules implements RuntimeOverrideRuleRepository {
  const _NoopRules();

  @override
  Future<void> setRule({
    required RuntimeOverrideScope scope,
    required String scopeValue,
    required RuntimeProfileId profileId,
  }) async {}

  @override
  Future<List<RuntimeOverrideRule>> rulesMatching({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) async => const <RuntimeOverrideRule>[];
}

/// The real coordinator + real dependency resolver over stubbed provisioning
/// and launch, so the widget flow under test exercises the same
/// resolve → dependencies → launch pipeline as the app wiring.
PlayServices _services({
  required _StubInstall install,
  required RecordingLaunch launch,
  required RuntimeProvisioner provisioner,
  LaunchAuthorizer? authorizer,
}) => PlayServices(
  install: install,
  localLibrary: FakeProfileLocalLibraryRepository(),
  playHistory: RecordingProfilePlayHistoryRepository(),
  coordinator: RomdPlayCoordinator(
    resolver: const LocalRuntimeResolver(),
    launchProviders: <LaunchProvider>[
      ScriptedLaunchProvider(
        dependencyResolver: RetroArchDependencyResolver(
          provisioner: provisioner,
          runtimeManager: _FoundRuntimeManager(),
          biosCatalog: _EmptyBiosCatalog(),
          // Never touched: the snes profile carries no BIOS requirement.
          biosRoot: Directory('/unused/bios'),
          downloadClient: _NoDownloads(),
        ),
        recorder: launch,
      ),
    ],
    playHistory: RecordingProfilePlayHistoryRepository(),
    authorizer: authorizer ?? _AllowAuthorizer(),
    installedTargetResolver: install,
    mutationSerializer: InstallMutationSerializer(),
    runtimeRules: const _NoopRules(),
  ),
  runtimeResolver: const LocalRuntimeResolver(),
  runtimeRules: const _NoopRules(),
  activeLaunchSession: ActiveLaunchSession(),
  controllerPreferences: InMemoryControllerPreferences(),
  controllerSlotClaims: SessionControllerSlotClaims(),
);

/// Emits a download event, then optionally parks on [readyGate] before
/// reporting ready — letting a test observe the "Installing RetroArch…" phase
/// deterministically (no timing race).
final class _StubProvisioner implements RuntimeProvisioner {
  _StubProvisioner({this.readyGate});
  final Future<void>? readyGate;
  final List<String> coreIds = <String>[];

  @override
  Stream<RuntimeProvisionProgress> ensure({required String coreId}) async* {
    coreIds.add(coreId);
    yield const RuntimeProvisionStarted();
    yield const RuntimeProvisionDownloading(
      label: 'RetroArch',
      receivedBytes: 0,
    );
    if (readyGate case final gate?) {
      await gate;
    }
    yield RuntimeProvisionReady(
      retroArchPath: '/rt/RetroArch.app',
      corePath: '/rt/cores/${coreId}_libretro.dylib',
    );
  }
}

final _game = ConsoleGame(
  id: _titleId.value,
  platformId: 'p1',
  platformName: 'SNES',
  title: 'Chrono Trigger',
  releaseDate: null,
  coverUrl: null,
  genre: null,
  rating: null,
  releaseCount: 1,
  defaultReleaseId: _releaseId.value,
);

final _resolved = ResolvedPlayTarget(
  serverInstanceId: _instanceId.value,
  releaseId: _releaseId.value,
  titleId: _titleId.value,
  platformShortName: 'snes',
  displayName: 'Chrono Trigger',
  localProfileId: 'profile-1',
  contentRoot: '/c',
  launchAbsolutePath: '/c/chrono.sfc',
  saveRoot: '/s',
  stateRoot: '/st',
  configRoot: '/cfg',
);

final _installed = LocalInstall(
  serverInstanceId: _instanceId.value,
  releaseId: _releaseId.value,
  titleId: _titleId.value,
  titleName: 'Chrono Trigger',
  platformId: 'p1',
  platformName: 'SNES',
  platformShortName: 'snes',
  coverUrl: null,
  releaseName: 'USA',
  releaseRevision: null,
  contentRoot: '/c',
  launchRelativePath: 'chrono.sfc',
  sizeBytes: 5,
  primarySha256: null,
  manifestFingerprint: 'fp',
  state: InstallState.installed,
  installMode: 'permanent',
  items: const <InstalledItem>[
    InstalledItem(relativePath: 'chrono.sfc', sizeBytes: 5, sha256: null),
  ],
  installedAt: DateTime.utc(2026, 6, 30),
  lastPlayedAt: null,
);

final _n64Resolved = ResolvedPlayTarget(
  serverInstanceId: _instanceId.value,
  releaseId: _releaseId.value,
  titleId: _titleId.value,
  platformShortName: 'n64',
  displayName: 'Super Mario 64',
  localProfileId: 'profile-1',
  contentRoot: '/c',
  launchAbsolutePath: '/c/mario.z64',
  saveRoot: '/s',
  stateRoot: '/st',
  configRoot: '/cfg',
);

final _unsupportedResolved = ResolvedPlayTarget(
  serverInstanceId: _instanceId.value,
  releaseId: _releaseId.value,
  titleId: _titleId.value,
  platformShortName: 'saturn',
  displayName: 'Panzer Dragoon',
  localProfileId: 'profile-1',
  contentRoot: '/c',
  launchAbsolutePath: '/c/panzer-dragoon.cue',
  saveRoot: '/s',
  stateRoot: '/st',
  configRoot: '/cfg',
);

typedef _SimpleCoreCase = ({
  String platformShortName,
  String displayName,
  String launchAbsolutePath,
  String coreId,
});

const List<_SimpleCoreCase> _simpleCoreCases = <_SimpleCoreCase>[
  (
    platformShortName: 'nes',
    displayName: 'Metroid',
    launchAbsolutePath: '/c/metroid.nes',
    coreId: 'mesen',
  ),
  (
    platformShortName: 'gb',
    displayName: 'Pokemon Red',
    launchAbsolutePath: '/c/pokemon-red.gb',
    coreId: 'sameboy',
  ),
  (
    platformShortName: 'gbc',
    displayName: 'Pokemon Crystal',
    launchAbsolutePath: '/c/pokemon-crystal.gbc',
    coreId: 'sameboy',
  ),
  (
    platformShortName: 'gba',
    displayName: 'Metroid Fusion',
    launchAbsolutePath: '/c/metroid-fusion.gba',
    coreId: 'mgba',
  ),
  (
    platformShortName: 'genesis',
    displayName: 'Sonic the Hedgehog',
    launchAbsolutePath: '/c/sonic.md',
    coreId: 'genesis_plus_gx',
  ),
];

void main() {
  Future<void> pumpDetail(
    WidgetTester tester,
    PlayServices services, {
    ConsumerApiClient? api,
    CatalogOperationContext? operation,
    Size surfaceSize = const Size(1280, 720),
    TextScaler textScaler = TextScaler.noScaling,
    ValueNotifier<ThemeMode>? themeMode,
  }) async {
    await tester.binding.setSurfaceSize(surfaceSize);
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final detail = MediaQuery(
      data: MediaQueryData(textScaler: textScaler),
      child: GameDetailScreen(
        game: _game,
        consumerApiClient: api ?? _Api(),
        operation:
            operation ??
            CatalogOperationContext(
              authority: _authority,
              accessToken: 'token',
              epoch: 1,
              isCurrent: () => true,
            ),
        playServices: services,
        localProfileId: 'profile-1',
        heroTag: 'hero',
      ),
    );
    Widget buildApp(ThemeMode? activeMode) => MaterialApp(
      theme: activeMode == null
          ? RomdSkins.baselineDark()
          : RomdSkins.seamTestTheme(),
      darkTheme: activeMode == null ? null : RomdSkins.baselineDark(),
      themeMode: activeMode,
      themeAnimationDuration: Duration.zero,
      home: detail,
    );
    await tester.pumpWidget(
      themeMode == null
          ? buildApp(null)
          : ValueListenableBuilder<ThemeMode>(
              valueListenable: themeMode,
              builder: (context, mode, child) => buildApp(mode),
            ),
    );
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
  }

  Future<void> tapInstall(WidgetTester tester) async {
    await tester.tap(find.widgetWithText(FilledButton, 'Install'));
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
  }

  Future<void> tapPlay(WidgetTester tester) async {
    await tester.tap(find.widgetWithText(FilledButton, 'Play'));
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
  }

  testWidgets(
    'mounted detail view and intro controller re-resolve the active skin',
    (tester) async {
      final mode = ValueNotifier<ThemeMode>(ThemeMode.dark);
      addTearDown(mode.dispose);
      await pumpDetail(
        tester,
        _services(
          install: _StubInstall(const <InstallProgress>[]),
          launch: RecordingLaunch(const LaunchExited(0)),
          provisioner: _StubProvisioner(),
        ),
        themeMode: mode,
      );

      final screen = find.byType(GameDetailScreen);
      final scaffold = find.descendant(
        of: screen,
        matching: find.byType(Scaffold),
      );
      final title = find.text('Chrono Trigger');
      final action = find.ancestor(
        of: find.widgetWithText(FilledButton, 'Install'),
        matching: find.byType(AnimatedContainer),
      );
      final introBuilder = find.ancestor(
        of: title,
        matching: find.byType(AnimatedBuilder),
      );
      final elementBefore = tester.element(screen);
      final scaffoldBefore = tester.widget<Scaffold>(scaffold);
      final titleBefore = tester.widget<Text>(title).style!;
      final actionBefore = tester.widget<AnimatedContainer>(action.first);
      final introAnimationBefore =
          tester.widget<AnimatedBuilder>(introBuilder.first).animation
              as CurvedAnimation;
      final controllerBefore =
          introAnimationBefore.parent as AnimationController;
      final introDurationBefore = controllerBefore.duration;

      mode.value = ThemeMode.light;
      await tester.pump();

      final scaffoldAfter = tester.widget<Scaffold>(scaffold);
      final titleAfter = tester.widget<Text>(title).style!;
      final actionAfter = tester.widget<AnimatedContainer>(action.first);
      final introAnimationAfter =
          tester.widget<AnimatedBuilder>(introBuilder.first).animation
              as CurvedAnimation;
      final controllerAfter = introAnimationAfter.parent as AnimationController;

      expect(identical(elementBefore, tester.element(screen)), isTrue);
      expect(
        scaffoldAfter.backgroundColor,
        isNot(scaffoldBefore.backgroundColor),
      );
      expect(titleAfter.fontFamily, isNot(titleBefore.fontFamily));
      expect(titleAfter.fontSize, isNot(titleBefore.fontSize));
      expect(actionAfter.duration, isNot(actionBefore.duration));
      expect(identical(controllerAfter, controllerBefore), isTrue);
      expect(controllerAfter.duration, isNot(introDurationBefore));
    },
  );

  testWidgets('selected action ring matches the themed button shape', (
    tester,
  ) async {
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(const <InstallProgress>[]),
        launch: RecordingLaunch(const LaunchExited(0)),
        provisioner: _StubProvisioner(),
      ),
    );

    final button = find.widgetWithText(FilledButton, 'Install');
    final action = find.ancestor(
      of: button,
      matching: find.byType(AnimatedContainer),
    );
    final decoration =
        tester.widget<AnimatedContainer>(action.first).decoration
            as BoxDecoration;
    final shape =
        Theme.of(
              tester.element(button),
            ).filledButtonTheme.style!.shape!.resolve(<WidgetState>{})
            as RoundedRectangleBorder;

    expect(decoration.borderRadius, shape.borderRadius);
  });

  testWidgets(
    'a failed install shows a sanitized message and recovers to idle',
    (tester) async {
      await pumpDetail(
        tester,
        _services(
          install: _StubInstall(const <InstallProgress>[
            InstallStarted(),
            InstallFailed(
              InstallFailureKind.downloadFailed,
              'Download failed. Check your connection and try again.',
            ),
          ]),
          launch: RecordingLaunch(const LaunchExited(0)),
          provisioner: _StubProvisioner(),
        ),
      );

      await tapInstall(tester);

      expect(
        find.text('Download failed. Check your connection and try again.'),
        findsOneWidget,
      );
      // No raw error / grant URL leaked into the UI.
      expect(find.textContaining('http'), findsNothing);
      // Recovered: the button is ready again.
      expect(find.widgetWithText(FilledButton, 'Install'), findsOneWidget);
    },
  );

  testWidgets('a missing runtime surfaces the install-RetroArch hint', (
    tester,
  ) async {
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          const <InstallProgress>[],
          installed: <LocalInstall>[_installed],
          resolved: _resolved,
        ),
        launch: RecordingLaunch.notStarted(
          const LaunchRuntimeMissing(<String>[
            'nothing on PATH',
          ], runtimeName: 'RetroArch'),
        ),
        provisioner: _StubProvisioner(),
      ),
    );

    await tapPlay(tester);

    expect(find.textContaining('RetroArch'), findsOneWidget);
  });

  testWidgets('installed titles launch directly for the active profile', (
    tester,
  ) async {
    final install = _StubInstall(
      const <InstallProgress>[],
      installed: <LocalInstall>[_installed],
      resolved: _resolved,
    );
    final launch = RecordingLaunch(const LaunchExited(0));
    await pumpDetail(
      tester,
      _services(
        install: install,
        launch: launch,
        provisioner: _StubProvisioner(),
      ),
    );

    await tapPlay(tester);

    expect(find.text('Existing save data found'), findsNothing);
    expect(install.resolvedLocalProfileId, 'profile-1');
    expect(launch.launched, isTrue);
  });

  for (final denied in <(LaunchAuthorizationResult, String)>[
    (
      const AuthorizationNotGranted(),
      'Add this game to this profile’s Local Library before playing.',
    ),
    (
      const AuthorizationRevoked(),
      'Access is required from this ROMD Library.',
    ),
  ]) {
    testWidgets('launch denial surfaces ${denied.$2}', (tester) async {
      final launch = RecordingLaunch(const LaunchExited(0));
      await pumpDetail(
        tester,
        _services(
          install: _StubInstall(
            const <InstallProgress>[],
            installed: <LocalInstall>[_installed],
            resolved: _resolved,
          ),
          launch: launch,
          provisioner: _StubProvisioner(),
          authorizer: _DeniedAuthorizer(denied.$1),
        ),
      );

      await tapPlay(tester);

      expect(find.text(denied.$2), findsOneWidget);
      expect(launch.plans, isEmpty);
    });
  }

  testWidgets('N64 titles provision the Mupen64Plus-Next core', (tester) async {
    final provisioner = _StubProvisioner();
    final launch = RecordingLaunch(const LaunchExited(0));
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          const <InstallProgress>[],
          installed: <LocalInstall>[_installed],
          resolved: _n64Resolved,
        ),
        launch: launch,
        provisioner: provisioner,
      ),
    );

    await tapPlay(tester);

    expect(provisioner.coreIds, <String>['mupen64plus_next']);
    expect(launch.launched, isTrue);
  });

  for (final coreCase in _simpleCoreCases) {
    testWidgets('${coreCase.displayName} provisions ${coreCase.coreId}', (
      tester,
    ) async {
      final provisioner = _StubProvisioner();
      final launch = RecordingLaunch(const LaunchExited(0));
      final resolved = ResolvedPlayTarget(
        serverInstanceId: _instanceId.value,
        releaseId: _releaseId.value,
        titleId: _titleId.value,
        platformShortName: coreCase.platformShortName,
        displayName: coreCase.displayName,
        localProfileId: 'profile-1',
        contentRoot: '/c',
        launchAbsolutePath: coreCase.launchAbsolutePath,
        saveRoot: '/s',
        stateRoot: '/st',
        configRoot: '/cfg',
      );
      await pumpDetail(
        tester,
        _services(
          install: _StubInstall(
            const <InstallProgress>[],
            installed: <LocalInstall>[_installed],
            resolved: resolved,
          ),
          launch: launch,
          provisioner: provisioner,
        ),
      );

      await tapPlay(tester);

      expect(provisioner.coreIds, <String>[coreCase.coreId]);
      expect(launch.launched, isTrue);
    });
  }

  testWidgets('unsupported platforms do not provision or launch', (
    tester,
  ) async {
    final provisioner = _StubProvisioner();
    final launch = RecordingLaunch(const LaunchExited(0));
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          const <InstallProgress>[],
          installed: <LocalInstall>[_installed],
          resolved: _unsupportedResolved,
        ),
        launch: launch,
        provisioner: provisioner,
      ),
    );

    await tapPlay(tester);

    expect(
      find.text('This platform is not playable on this device yet.'),
      findsOneWidget,
    );
    expect(provisioner.coreIds, isEmpty);
    expect(launch.launched, isFalse);
  });

  testWidgets('D-pad reaches Uninstall and A uninstalls the release', (
    tester,
  ) async {
    final install = _StubInstall(
      const <InstallProgress>[],
      installed: <LocalInstall>[_installed],
      resolved: _resolved,
    );
    await pumpDetail(
      tester,
      _services(
        install: install,
        launch: RecordingLaunch(const LaunchExited(0)),
        provisioner: _StubProvisioner(),
      ),
    );

    // Play → (no releases in this detail) → the cache panel's Uninstall.
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    // Removal is destructive, so it confirms before anything happens.
    expect(install.uninstalled, isEmpty);
    expect(find.textContaining('Remove Chrono Trigger?'), findsOneWidget);
    await tester.tap(find.widgetWithText(TextButton, 'Remove'));
    for (var i = 0; i < 6; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(install.uninstalled, <String>[_releaseId.value]);
  });

  testWidgets('cancelling the removal confirmation keeps the install', (
    tester,
  ) async {
    final install = _StubInstall(
      const <InstallProgress>[],
      installed: <LocalInstall>[_installed],
      resolved: _resolved,
    );
    await pumpDetail(
      tester,
      _services(
        install: install,
        launch: RecordingLaunch(const LaunchExited(0)),
        provisioner: _StubProvisioner(),
      ),
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();

    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    for (var i = 0; i < 6; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(install.uninstalled, isEmpty);
    expect(find.textContaining('Remove Chrono Trigger?'), findsNothing);
  });

  testWidgets(
    'Catalog authority invalidation cancels removal without stale feedback',
    (tester) async {
      var current = true;
      final changes = _CatalogChanges();
      final gate = Completer<void>();
      final install = _StubInstall(
        const <InstallProgress>[],
        installed: <LocalInstall>[_installed],
        resolved: _resolved,
        uninstallGate: gate.future,
      );
      addTearDown(changes.dispose);
      await pumpDetail(
        tester,
        _services(
          install: install,
          launch: RecordingLaunch(const LaunchExited(0)),
          provisioner: _StubProvisioner(),
        ),
        operation: CatalogOperationContext(
          authority: _authority,
          accessToken: 'token',
          epoch: 1,
          isCurrent: () => current,
          changes: changes,
        ),
      );

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pump();
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pump();
      await tester.tap(find.widgetWithText(TextButton, 'Remove'));
      await tester.pump();
      await install.uninstallStarted.future;

      current = false;
      changes.invalidate();
      await tester.pump();
      expect(
        find.byKey(const ValueKey<String>('catalog-privacy-shield')),
        findsOneWidget,
      );
      expect(find.text(_installed.releaseName), findsNothing);

      gate.complete();
      for (var i = 0; i < 8; i++) {
        await tester.pump(const Duration(milliseconds: 50));
      }

      expect(install.uninstalled, isEmpty);
      expect(find.textContaining('removed from this profile'), findsNothing);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets(
    'the synopsis zone opens the full description and Esc closes it',
    (tester) async {
      await pumpDetail(
        tester,
        _services(
          install: _StubInstall(const <InstallProgress>[]),
          launch: RecordingLaunch(const LaunchExited(0)),
          provisioner: _StubProvisioner(),
        ),
      );

      // Play → ▲ selects the synopsis; A opens the overlay.
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump();
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      for (var i = 0; i < 6; i++) {
        await tester.pump(const Duration(milliseconds: 50));
      }

      expect(find.text('ABOUT'), findsOneWidget);
      // Inline (clamped) + overlay (full) both show the text.
      expect(find.text('A time-spanning RPG.'), findsNWidgets(2));

      await tester.sendKeyEvent(LogicalKeyboardKey.escape);
      for (var i = 0; i < 6; i++) {
        await tester.pump(const Duration(milliseconds: 50));
      }

      expect(find.text('ABOUT'), findsNothing);
      // The overlay closed without popping the detail screen underneath.
      expect(find.text('A time-spanning RPG.'), findsOneWidget);
    },
  );

  testWidgets('selecting the media zone scrolls it into a short viewport', (
    tester,
  ) async {
    // A viewport shorter than the content column: with the cache panel and a
    // media row present, MEDIA starts below the fold.
    const surface = Size(1280, 460);
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          const <InstallProgress>[],
          installed: <LocalInstall>[_installed],
          resolved: _resolved,
        ),
        launch: RecordingLaunch(const LaunchExited(0)),
        provisioner: _StubProvisioner(),
      ),
      api: _Api(
        media: <ConsoleMediaRef>[
          ConsoleMediaRef(
            id: 'm1',
            type: 'screenshot',
            url: Uri.parse('https://media.local/1.png'),
            isPrimary: false,
          ),
        ],
      ),
      surfaceSize: surface,
    );

    final before = tester.getRect(find.text('MEDIA'));
    expect(before.bottom, greaterThan(surface.height));

    // Play → uninstall → media: the selection pulls the row into view.
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    final after = tester.getRect(find.text('MEDIA'));
    expect(after.bottom, lessThan(surface.height));
  });

  testWidgets('media tile decode size is stable across selection moves', (
    tester,
  ) async {
    // The selection border animates 1↔2px; if the decode width tracked the
    // measured size, the image-cache key would churn and every focus move
    // would re-decode the thumbnail and flash it in from transparent.
    ResizeImage mediaProvider() {
      final image = tester
          .widgetList<Image>(find.byType(Image))
          .firstWhere(
            (image) => switch (image.image) {
              ResizeImage(imageProvider: NetworkImage(:final url)) =>
                url.contains('media.local'),
              _ => false,
            },
          );
      return image.image as ResizeImage;
    }

    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          const <InstallProgress>[],
          installed: <LocalInstall>[_installed],
          resolved: _resolved,
        ),
        launch: RecordingLaunch(const LaunchExited(0)),
        provisioner: _StubProvisioner(),
      ),
      api: _Api(
        media: <ConsoleMediaRef>[
          ConsoleMediaRef(
            id: 'm1',
            type: 'screenshot',
            url: Uri.parse('https://media.local/1.png'),
            isPrimary: false,
          ),
        ],
      ),
    );

    final restingWidth = mediaProvider().width;

    // Play → uninstall → media: tile 0 becomes selected (border 1 → 2).
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(mediaProvider().width, restingWidth);
  });

  testWidgets('pressing C opens the in-game controller help', (tester) async {
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(const <InstallProgress>[InstallStarted()]),
        launch: RecordingLaunch(const LaunchExited(0)),
        provisioner: _StubProvisioner(),
      ),
    );

    await tester.sendKeyEvent(LogicalKeyboardKey.keyC);
    for (var i = 0; i < 6; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(find.text('In-game shortcuts'), findsOneWidget);
    expect(find.text('SELECT + START'), findsOneWidget);
  });

  testWidgets('Cancel during runtime install aborts and returns to idle', (
    tester,
  ) async {
    // Park the provisioner mid-download so Cancel hits an in-flight, parked
    // stream (the case a flag-and-break can't interrupt).
    final gate = Completer<void>();
    final launch = RecordingLaunch(const LaunchExited(0));
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          const <InstallProgress>[],
          installed: <LocalInstall>[_installed],
          resolved: _resolved,
        ),
        launch: launch,
        provisioner: _StubProvisioner(readyGate: gate.future),
      ),
    );

    await tester.tap(find.widgetWithText(FilledButton, 'Play'));
    for (var i = 0; i < 3; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.text('Installing RetroArch…'), findsOneWidget);
    expect(find.text('Cancel'), findsOneWidget);

    await tester.tap(find.text('Cancel'));
    for (var i = 0; i < 6; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    // Back to idle and ready to play again; launch was never reached.
    expect(find.widgetWithText(FilledButton, 'Play'), findsOneWidget);
    expect(find.text('Installing RetroArch…'), findsNothing);
    expect(launch.launched, isFalse);

    // Releasing the gate must not resurrect the aborted flow.
    gate.complete();
    for (var i = 0; i < 4; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(launch.launched, isFalse);
  });

  testWidgets('shows the Installing RetroArch phase before launch', (
    tester,
  ) async {
    final gate = Completer<void>();
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          const <InstallProgress>[],
          installed: <LocalInstall>[_installed],
          resolved: _resolved,
        ),
        launch: RecordingLaunch(const LaunchExited(0)),
        provisioner: _StubProvisioner(readyGate: gate.future),
      ),
    );

    await tester.tap(find.widgetWithText(FilledButton, 'Play'));
    // A few frames to subscribe and process the Downloading event; the
    // provisioner is parked on the gate, so the phase can't advance past it.
    for (var i = 0; i < 3; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.text('Installing RetroArch…'), findsOneWidget);

    // Release the runtime install; the flow proceeds through content install
    // and launch back to idle.
    gate.complete();
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.widgetWithText(FilledButton, 'Play'), findsOneWidget);
  });

  testWidgets('launch uses a silent black handoff instead of a status banner', (
    tester,
  ) async {
    final gate = Completer<void>();
    final launch = RecordingLaunch(const LaunchExited(0), gate: gate.future);
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          const <InstallProgress>[],
          installed: <LocalInstall>[_installed],
          resolved: _resolved,
        ),
        launch: launch,
        provisioner: _StubProvisioner(),
      ),
    );

    await tester.tap(find.widgetWithText(FilledButton, 'Play'));
    for (var i = 0; i < 6; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(launch.launched, isTrue);
    expect(
      find.byKey(const ValueKey<String>('game-detail-launch-handoff')),
      findsOneWidget,
    );
    expect(find.text('Launching…'), findsNothing);

    gate.complete();
    for (var i = 0; i < 6; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(
      find.byKey(const ValueKey<String>('game-detail-launch-handoff')),
      findsNothing,
    );
    expect(find.widgetWithText(FilledButton, 'Play'), findsOneWidget);
  });
}

final class _CatalogChanges extends ChangeNotifier {
  void invalidate() => notifyListeners();
}
