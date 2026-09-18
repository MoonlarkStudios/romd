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
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/launch_authorization.dart';
import 'package:romd_console/src/play/session/domain/launch_provider.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/play_services.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
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

final class _Api implements ConsumerApiClient {
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
    media: <ConsoleMediaRef>[],
    releases: <ConsoleRelease>[],
    defaultReleaseId: _releaseId.value,
  );

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _StubInstall
    implements InstallService, InstalledPlayTargetResolver {
  _StubInstall({required this.installed, this.resolved});

  final List<LocalInstall> installed;
  final ResolvedPlayTarget? resolved;

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
  }) async => resolved;

  @override
  Stream<InstallProgress> install(
    PlayTarget target, {
    required InstallOperationLease operation,
    String? accessToken,
  }) => const Stream<InstallProgress>.empty();

  @override
  Future<void> uninstall(
    String releaseId, {
    InstallOperationLease? operation,
  }) async {}

  LocalInstall? _findInstall(String releaseId) {
    for (final install in installed) {
      if (install.releaseId == releaseId) {
        return install;
      }
    }
    return null;
  }
}

final class _MissingExecutableResolver implements RuntimeDependencyResolver {
  _MissingExecutableResolver(this.executableName);

  final String executableName;
  final List<RuntimeProfile> profiles = <RuntimeProfile>[];

  @override
  Stream<DependencyProgress> resolve(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  ) {
    profiles.add(profile);
    return Stream<DependencyProgress>.value(
      DependencyExecutableMissing(const <String>[
        'nothing on PATH',
      ], executableName: executableName),
    );
  }
}

/// Always finds the managed RetroArch — the picker tests exercise rule
/// persistence and re-resolution, not executable discovery.
final class _FoundRuntimeManager implements RuntimeManager {
  @override
  Future<RuntimeResolution> resolve(RuntimeDescriptor descriptor) async =>
      const RuntimeResolution(
        runtimeId: 'retroarch',
        source: RuntimeResolutionSource.managed,
        executablePath: '/rt/RetroArch.app',
      );
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

/// Records which core ids were provisioned so a test can prove the launch
/// re-resolved against the just-persisted rule.
final class _StubProvisioner implements RuntimeProvisioner {
  final List<String> coreIds = <String>[];

  @override
  Stream<RuntimeProvisionProgress> ensure({required String coreId}) async* {
    coreIds.add(coreId);
    yield RuntimeProvisionReady(
      retroArchPath: '/rt/RetroArch.app',
      corePath: '/rt/cores/${coreId}_libretro.dylib',
    );
  }
}

typedef _RuleWrite = ({
  RuntimeOverrideScope scope,
  String scopeValue,
  String profileId,
});

/// In-memory rule store shared between the picker's write seam and the
/// resolver, so a persisted pick flows through resolution like production.
final class _FakeRules implements RuntimeOverrideRuleRepository {
  _FakeRules({this.failWrites = false});

  final bool failWrites;
  final List<_RuleWrite> writes = <_RuleWrite>[];
  final List<RuntimeOverrideRule> _rules = <RuntimeOverrideRule>[];

  @override
  Future<void> setRule({
    required RuntimeOverrideScope scope,
    required String scopeValue,
    required RuntimeProfileId profileId,
  }) async {
    if (failWrites) {
      throw StateError('rule store unavailable');
    }
    writes.add((
      scope: scope,
      scopeValue: scopeValue,
      profileId: profileId.value,
    ));
    _rules
      ..removeWhere(
        (rule) => rule.scope == scope && rule.scopeValue == scopeValue,
      )
      ..add(
        RuntimeOverrideRule(
          scope: scope,
          scopeValue: scopeValue,
          profileId: profileId,
          updatedAt: DateTime.utc(2026, 7, 5),
        ),
      );
  }

  @override
  Future<List<RuntimeOverrideRule>> rulesMatching({
    required String platformShortName,
    required String titleId,
    required String releaseId,
  }) async {
    final platform = platformShortName.trim().toLowerCase();
    return _rules
        .where(
          (rule) => switch (rule.scope) {
            RuntimeOverrideScope.platform => rule.scopeValue == platform,
            RuntimeOverrideScope.title => rule.scopeValue == titleId,
            RuntimeOverrideScope.release => rule.scopeValue == releaseId,
          },
        )
        .toList(growable: false);
  }
}

/// The real coordinator + dependency resolver over the shared rule store, so
/// picking a runtime and the follow-up launch exercise the app pipeline.
PlayServices _services({
  required _StubInstall install,
  required _FakeRules rules,
  required _StubProvisioner provisioner,
  required RecordingLaunch launch,
  RuntimeDependencyResolver? duckStationDependencyResolver,
}) {
  final resolver = LocalRuntimeResolver(rules: rules);
  final retroArchDependencyResolver = RetroArchDependencyResolver(
    provisioner: provisioner,
    runtimeManager: _FoundRuntimeManager(),
    biosCatalog: _EmptyBiosCatalog(),
    // Never touched: the snes profile carries no BIOS requirement.
    biosRoot: Directory('/unused/bios'),
    downloadClient: _NoDownloads(),
  );
  return PlayServices(
    install: install,
    localLibrary: FakeProfileLocalLibraryRepository(),
    playHistory: RecordingProfilePlayHistoryRepository(),
    coordinator: RomdPlayCoordinator(
      resolver: resolver,
      launchProviders: <LaunchProvider>[
        ScriptedLaunchProvider(
          dependencyResolver: retroArchDependencyResolver,
          recorder: launch,
        ),
        if (duckStationDependencyResolver != null)
          ScriptedLaunchProvider(
            adapterId: BuiltinRuntimeProfiles.duckStationAdapterId,
            dependencyResolver: duckStationDependencyResolver,
            recorder: launch,
          ),
      ],
      playHistory: RecordingProfilePlayHistoryRepository(),
      authorizer: _AllowAuthorizer(),
      installedTargetResolver: install,
      mutationSerializer: InstallMutationSerializer(),
      runtimeRules: rules,
    ),
    runtimeResolver: resolver,
    runtimeRules: rules,
    activeLaunchSession: ActiveLaunchSession(),
    controllerPreferences: InMemoryControllerPreferences(),
    controllerSlotClaims: SessionControllerSlotClaims(),
  );
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

final _psxResolved = ResolvedPlayTarget(
  serverInstanceId: _instanceId.value,
  releaseId: _releaseId.value,
  titleId: _titleId.value,
  platformShortName: 'psx',
  displayName: 'Ape Escape',
  localProfileId: 'profile-1',
  contentRoot: '/c',
  launchAbsolutePath: '/c/ape.cue',
  saveRoot: '/s',
  stateRoot: '/st',
  configRoot: '/cfg',
);

LocalInstall _install({
  String platformShortName = 'snes',
  String? platformName = 'SNES',
}) => LocalInstall(
  serverInstanceId: _instanceId.value,
  releaseId: _releaseId.value,
  titleId: _titleId.value,
  titleName: 'Chrono Trigger',
  platformId: 'p1',
  platformName: platformName,
  platformShortName: platformShortName,
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

const _snes9xKey = ValueKey<String>('runtime-candidate-retroarch:snes:snes9x');
const _bsnesKey = ValueKey<String>('runtime-candidate-retroarch:snes:bsnes');

void main() {
  Future<void> pumpDetail(
    WidgetTester tester,
    PlayServices services, {
    TextScaler textScaler = TextScaler.noScaling,
  }) async {
    await tester.binding.setSurfaceSize(const Size(1280, 720));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: RomdSkins.baselineDark(),
        home: MediaQuery(
          data: MediaQueryData(textScaler: textScaler),
          child: GameDetailScreen(
            game: _game,
            consumerApiClient: _Api(),
            operation: CatalogOperationContext(
              authority: _authority,
              accessToken: 'token',
              epoch: 1,
              isCurrent: () => true,
            ),
            playServices: services,
            localProfileId: 'profile-1',
            heroTag: 'hero',
          ),
        ),
      ),
    );
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
  }

  Future<void> pumpFrames(WidgetTester tester, [int count = 8]) async {
    for (var i = 0; i < count; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
  }

  Finder runWithButton() => find.widgetWithText(OutlinedButton, 'Run with');

  Future<void> openPicker(WidgetTester tester) async {
    await tester.tap(runWithButton());
    await pumpFrames(tester);
  }

  testWidgets('runtime picker remains overflow-free at 1.5x text scale', (
    tester,
  ) async {
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          installed: <LocalInstall>[_install()],
          resolved: _resolved,
        ),
        rules: _FakeRules(),
        provisioner: _StubProvisioner(),
        launch: RecordingLaunch(const LaunchExited(0)),
      ),
      textScaler: const TextScaler.linear(1.5),
    );

    await openPicker(tester);

    expect(find.text('RUN WITH'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets("no 'Run with' action when the platform has one runtime", (
    tester,
  ) async {
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          installed: <LocalInstall>[
            _install(platformShortName: 'n64', platformName: 'Nintendo 64'),
          ],
          resolved: _resolved,
        ),
        rules: _FakeRules(),
        provisioner: _StubProvisioner(),
        launch: RecordingLaunch(const LaunchExited(0)),
      ),
    );

    expect(find.widgetWithText(FilledButton, 'Play'), findsOneWidget);
    expect(runWithButton(), findsNothing);
  });

  testWidgets("'Run with' appears when multiple runtimes play the platform", (
    tester,
  ) async {
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          installed: <LocalInstall>[_install()],
          resolved: _resolved,
        ),
        rules: _FakeRules(),
        provisioner: _StubProvisioner(),
        launch: RecordingLaunch(const LaunchExited(0)),
      ),
    );

    expect(runWithButton(), findsOneWidget);
  });

  testWidgets(
    'the picker lists candidates in shipped order with the preferred marked',
    (tester) async {
      await pumpDetail(
        tester,
        _services(
          install: _StubInstall(
            installed: <LocalInstall>[_install()],
            resolved: _resolved,
          ),
          rules: _FakeRules(),
          provisioner: _StubProvisioner(),
          launch: RecordingLaunch(const LaunchExited(0)),
        ),
      );

      await openPicker(tester);

      expect(find.text('RUN WITH'), findsOneWidget);
      // Shipped order: the built-in default first, the alternate below it.
      expect(
        tester.getTopLeft(find.byKey(_snes9xKey)).dy,
        lessThan(tester.getTopLeft(find.byKey(_bsnesKey)).dy),
      );
      // Only the preferred candidate carries the check.
      expect(
        find.descendant(
          of: find.byKey(_snes9xKey),
          matching: find.byIcon(Icons.check_circle),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: find.byKey(_bsnesKey),
          matching: find.byIcon(Icons.check_circle),
        ),
        findsNothing,
      );
    },
  );

  testWidgets('picking bsnes persists the platform rule and launches with it', (
    tester,
  ) async {
    final rules = _FakeRules();
    final provisioner = _StubProvisioner();
    final launch = RecordingLaunch(const LaunchExited(0));
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          installed: <LocalInstall>[_install()],
          resolved: _resolved,
        ),
        rules: rules,
        provisioner: provisioner,
        launch: launch,
      ),
    );

    // Play → Details → Shortcuts → Run with, then open the picker.
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await pumpFrames(tester);
    expect(find.text('RUN WITH'), findsOneWidget);

    // ▼ to bsnes, A to pick it.
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await pumpFrames(tester, 10);

    expect(rules.writes, <_RuleWrite>[
      (
        scope: RuntimeOverrideScope.platform,
        scopeValue: 'snes',
        profileId: 'retroarch:snes:bsnes',
      ),
    ]);
    expect(find.text('RUN WITH'), findsNothing);
    expect(
      find.text('SNES games will now use RetroArch (bsnes).'),
      findsOneWidget,
    );
    // The launch re-resolved and provisioned the just-picked core.
    expect(provisioner.coreIds, <String>['bsnes']);
    expect(launch.launches, 1);
  });

  testWidgets(
    'picking DuckStation names DuckStation when its executable is missing',
    (tester) async {
      final rules = _FakeRules();
      final duckStationResolver = _MissingExecutableResolver('DuckStation');
      final launch = RecordingLaunch(const LaunchExited(0));
      await pumpDetail(
        tester,
        _services(
          install: _StubInstall(
            installed: <LocalInstall>[
              _install(platformShortName: 'psx', platformName: 'PlayStation'),
            ],
            resolved: _psxResolved,
          ),
          rules: rules,
          provisioner: _StubProvisioner(),
          launch: launch,
          duckStationDependencyResolver: duckStationResolver,
        ),
      );

      await openPicker(tester);
      expect(find.text('RUN WITH'), findsOneWidget);

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pump();
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await pumpFrames(tester, 10);

      expect(rules.writes, <_RuleWrite>[
        (
          scope: RuntimeOverrideScope.platform,
          scopeValue: 'psx',
          profileId: 'duckstation:psx:standalone',
        ),
      ]);
      expect(
        duckStationResolver.profiles.single.id.value,
        'duckstation:psx:standalone',
      );
      expect(
        find.text(
          "Couldn't find DuckStation. Install DuckStation to play this title.",
        ),
        findsOneWidget,
      );
      expect(find.textContaining("Couldn't find RetroArch"), findsNothing);
      expect(launch.launches, 0);
    },
  );

  testWidgets('Escape closes the picker without persisting or launching', (
    tester,
  ) async {
    final rules = _FakeRules();
    final launch = RecordingLaunch(const LaunchExited(0));
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          installed: <LocalInstall>[_install()],
          resolved: _resolved,
        ),
        rules: rules,
        provisioner: _StubProvisioner(),
        launch: launch,
      ),
    );

    await openPicker(tester);
    expect(find.text('RUN WITH'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await pumpFrames(tester);

    expect(find.text('RUN WITH'), findsNothing);
    expect(rules.writes, isEmpty);
    expect(launch.launches, 0);
    // The picker closed without popping the detail screen underneath.
    expect(find.widgetWithText(FilledButton, 'Play'), findsOneWidget);
  });

  testWidgets('a failed rule write shows the error and does not launch', (
    tester,
  ) async {
    final provisioner = _StubProvisioner();
    final launch = RecordingLaunch(const LaunchExited(0));
    await pumpDetail(
      tester,
      _services(
        install: _StubInstall(
          installed: <LocalInstall>[_install()],
          resolved: _resolved,
        ),
        rules: _FakeRules(failWrites: true),
        provisioner: provisioner,
        launch: launch,
      ),
    );

    await openPicker(tester);
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await pumpFrames(tester);

    expect(find.text("Couldn't save the runtime preference."), findsOneWidget);
    expect(provisioner.coreIds, isEmpty);
    expect(launch.launches, 0);
  });
}
