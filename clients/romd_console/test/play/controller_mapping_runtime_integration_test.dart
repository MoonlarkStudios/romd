import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/play/controllers/data/drift_controller_hardware_mapping_repository.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/emulator/data/adapters/dolphin/dolphin_adapter.dart';
import 'package:romd_console/src/play/emulator/data/adapters/dolphin/dolphin_user_directory.dart';
import 'package:romd_console/src/play/emulator/data/adapters/duckstation/duckstation_config_writer.dart';
import 'package:romd_console/src/play/emulator/data/adapters/duckstation/duckstation_user_directory.dart';
import 'package:romd_console/src/play/emulator/data/adapters/pcsx2/pcsx2_adapter.dart';
import 'package:romd_console/src/play/emulator/data/adapters/pcsx2/pcsx2_user_directory.dart';
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/adapters/retroarch/retroarch_config_writer.dart';
import 'package:romd_console/src/play/emulator/data/emulator_launch_provider.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_adapter.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

final class _WritingDuckStationAdapter implements RuntimeAdapter {
  _WritingDuckStationAdapter(this.userDirectory);

  final DuckStationUserDirectory userDirectory;
  final List<EmulatorLaunchPlan> plans = <EmulatorLaunchPlan>[];

  @override
  Future<void> prepare(EmulatorLaunchPlan plan) async {}

  @override
  CommandPlan buildCommand(EmulatorLaunchPlan plan, String executablePath) =>
      CommandPlan(
        executable: executablePath,
        arguments: const <String>[],
        workingDirectory: plan.target.contentRoot,
        environment: const <String, String>{},
      );

  @override
  Future<LaunchStartResult> launch(EmulatorLaunchPlan plan) async {
    plans.add(plan);
    await const DuckStationIniConfigWriter().write(
      userDirectory,
      controllerSetup: plan.controllerSetup,
    );
    return const LaunchStarted(_CompletedSession());
  }
}

final class _CompletedSession implements LaunchSession {
  const _CompletedSession();

  @override
  Future<LaunchResult> get completed async => const LaunchExited(0);

  @override
  LaunchForegroundControl? get foreground => null;

  @override
  LaunchTermination? get termination => null;
}

final class _RecordingProcessRunner implements ProcessRunner {
  Map<String, String>? environment;

  @override
  Future<ProcessRunResult> run({
    required String executable,
    required List<String> arguments,
    String? workingDirectory,
    Map<String, String> environment = const <String, String>{},
  }) async => const ProcessRunResult(exitCode: 0, stdout: '', stderr: '');

  @override
  Future<RunningProcess> start({
    required String executable,
    required List<String> arguments,
    required String workingDirectory,
    required Map<String, String> environment,
  }) async {
    this.environment = environment;
    return const _CompletedProcess();
  }
}

final class _CompletedProcess implements RunningProcess {
  const _CompletedProcess();

  @override
  Future<int> get exitCode async => 0;

  @override
  Stream<List<int>> get stderr => const Stream<List<int>>.empty();

  @override
  Stream<List<int>> get stdout => const Stream<List<int>>.empty();

  @override
  bool kill() => true;
}

final class _WritingRetroArchAdapter implements RuntimeAdapter {
  final List<EmulatorLaunchPlan> plans = <EmulatorLaunchPlan>[];
  String? configPath;

  @override
  Future<void> prepare(EmulatorLaunchPlan plan) async {}

  @override
  CommandPlan buildCommand(EmulatorLaunchPlan plan, String executablePath) =>
      CommandPlan(
        executable: executablePath,
        arguments: const <String>[],
        workingDirectory: plan.target.contentRoot,
        environment: const <String, String>{},
      );

  @override
  Future<LaunchStartResult> launch(EmulatorLaunchPlan plan) async {
    plans.add(plan);
    configPath = await const RetroArchConfigWriter().write(
      plan.target,
      controllerSetup: plan.controllerSetup,
    );
    return const LaunchStarted(_CompletedSession());
  }
}

final class _UnusedResolver implements RuntimeDependencyResolver {
  @override
  Stream<DependencyProgress> resolve(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  ) => const Stream<DependencyProgress>.empty();
}

void main() {
  late AppDatabase db;
  late DriftControllerHardwareMappingRepository repository;
  late Directory tmp;

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    repository = DriftControllerHardwareMappingRepository(
      database: db,
      now: () => DateTime.utc(2026, 7, 12),
    );
    tmp = Directory.systemTemp.createTempSync('romd_canonical_mapping_runtime');
  });

  tearDown(() async {
    await db.close();
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  test(
    'saved hardware mapping reaches verified DuckStation singleton bytes',
    () async {
      const guid = '03000000deadbeef0000000000000000';
      final mapping = CanonicalControllerMapping(
        const <CanonicalGamepadControl, RawGamepadInput>{
          CanonicalGamepadControl.faceSouth: RawButtonInput(3),
          CanonicalGamepadControl.faceEast: RawButtonInput(2),
          CanonicalGamepadControl.dpadUp: RawHatInput(0, 1),
          CanonicalGamepadControl.dpadDown: RawHatInput(0, 4),
          CanonicalGamepadControl.leftTrigger: RawAxisInput(
            4,
            direction: RawAxisDirection.positive,
          ),
          CanonicalGamepadControl.leftStickX: RawAxisInput(0),
          CanonicalGamepadControl.leftStickY: RawAxisInput(1, inverted: true),
        },
      );
      await repository.save(
        sdlPlatform: 'macOS',
        sdlGuid: guid,
        displayName: 'Saved Pad',
        mapping: mapping,
      );
      const controller = ConnectedGamepad(
        id: 'exact-pad',
        order: 0,
        identity: ControllerIdentity(
          displayName: 'Saved Pad',
          sdlGuid: guid,
          serial: 'serial-1',
        ),
      );
      final claims = SessionControllerSlotClaims.fromClaims(
        <ControllerSlotClaim?>[ControllerSlotClaim.fromGamepad(controller)],
      );
      final snapshot = ReviewedLaunchSnapshot.resolve(
        claimRevision: claims.revision,
        claims: claims.claimDetails,
        devices: const <ConnectedGamepad>[controller],
      );
      final userDirectory = DuckStationUserDirectory(
        root: Directory(p.join(tmp.path, 'duckstation-user')),
        operatingSystem: 'macos',
      );
      final adapter = _WritingDuckStationAdapter(userDirectory);
      const profile = RuntimeProfile(
        id: RuntimeProfileId('duckstation:integration'),
        adapterId: BuiltinRuntimeProfiles.duckStationAdapterId,
        displayName: 'DuckStation',
        supportedPlatforms: const <String>{'psx'},
        requirements: const <RuntimeDependencyRequirement>[
          ExecutableRequirement(id: 'executable', displayName: 'DuckStation'),
        ],
      );
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          BuiltinRuntimeProfiles.duckStationAdapterId:
              EmulatorRuntimeLaunchEntry(
                dependencyResolver: _UnusedResolver(),
                adapterFactory: () => adapter,
              ),
        },
        controllerHardwareMappings: repository,
        controllerSlotClaims: claims,
        gamepadLister: () async => const <ConnectedGamepad>[controller],
        operatingSystem: 'macos',
      );

      final result = await provider.launch(
        profile: profile,
        target: ResolvedPlayTarget(
          releaseId: 'release-1',
          titleId: 'title-1',
          platformShortName: 'psx',
          displayName: 'Test game',
          localProfileId: 'profile-1',
          contentRoot: p.join(tmp.path, 'content'),
          launchAbsolutePath: p.join(tmp.path, 'content', 'game.cue'),
          saveRoot: p.join(tmp.path, 'saves'),
          stateRoot: p.join(tmp.path, 'states'),
          configRoot: p.join(tmp.path, 'config'),
        ),
        dependencies: const <ResolvedRuntimeDependency>[
          ResolvedRuntimeDependency(
            requirementId: 'executable',
            path: '/managed/DuckStation.app',
            provenance: RuntimeDependencyProvenance.managed,
          ),
        ],
        controllerSnapshot: snapshot,
      );

      expect(result, isA<LaunchStarted>());
      expect(
        adapter.plans.single.playerControllers.single.gameplayMapping,
        isNotNull,
      );
      final database = File(
        userDirectory.gameControllerDatabasePath,
      ).readAsStringSync();
      expect(database, contains('$guid,Saved Pad,a:b3,b:b2'));
      expect(database, contains('dpup:h0.1'));
      expect(database, contains('lefttrigger:+a4'));
      expect(database, contains('lefty:a1~'));
      final ini = File(userDirectory.settingsPath).readAsStringSync();
      expect(ini, contains('Cross = SDL-0/A'));
      expect(ini, contains('Circle = SDL-0/B'));
      expect(ini, isNot(contains('Cross = SDL-3/')));
    },
  );

  test(
    'saved hardware mapping reaches approved RetroArch gameplay bytes',
    () async {
      const guid =
          RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid;
      final mapping = CanonicalControllerMapping(
        const <CanonicalGamepadControl, RawGamepadInput>{
          CanonicalGamepadControl.faceSouth: RawButtonInput(1),
          CanonicalGamepadControl.faceEast: RawButtonInput(0),
          CanonicalGamepadControl.faceWest: RawButtonInput(3),
          CanonicalGamepadControl.faceNorth: RawButtonInput(2),
          CanonicalGamepadControl.dpadUp: RawHatInput(0, 1),
          CanonicalGamepadControl.leftTrigger: RawAxisInput(4),
          CanonicalGamepadControl.leftStickX: RawAxisInput(2),
          CanonicalGamepadControl.leftStickY: RawAxisInput(3, inverted: true),
        },
      );
      await repository.save(
        sdlPlatform: 'macOS',
        sdlGuid: guid,
        displayName:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbDisplayName,
        mapping: mapping,
      );
      const controller = ConnectedGamepad(
        id: 'exact-pro2',
        order: 0,
        identity: ControllerIdentity(
          displayName: RuntimeControllerInputPolicies
              .retroArchMfi8BitDoPro2UsbDisplayName,
          sdlGuid: guid,
          serial: 'serial-1',
        ),
      );
      final claims = SessionControllerSlotClaims.fromClaims(
        <ControllerSlotClaim?>[ControllerSlotClaim.fromGamepad(controller)],
      );
      final snapshot = ReviewedLaunchSnapshot.resolve(
        claimRevision: claims.revision,
        claims: claims.claimDetails,
        devices: const <ConnectedGamepad>[controller],
      );
      final adapter = _WritingRetroArchAdapter();
      const profile = RuntimeProfile(
        id: RuntimeProfileId('retroarch:integration'),
        adapterId: BuiltinRuntimeProfiles.retroArchAdapterId,
        displayName: 'RetroArch',
        supportedPlatforms: <String>{'snes'},
        requirements: <RuntimeDependencyRequirement>[
          ExecutableRequirement(id: 'executable', displayName: 'RetroArch'),
        ],
      );
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          BuiltinRuntimeProfiles.retroArchAdapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _UnusedResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerHardwareMappings: repository,
        controllerSlotClaims: claims,
        gamepadLister: () async => const <ConnectedGamepad>[controller],
        operatingSystem: 'macos',
      );

      final result = await provider.launch(
        profile: profile,
        target: ResolvedPlayTarget(
          releaseId: 'release-2',
          titleId: 'title-2',
          platformShortName: 'snes',
          displayName: 'Test game',
          localProfileId: 'profile-1',
          contentRoot: p.join(tmp.path, 'content'),
          launchAbsolutePath: p.join(tmp.path, 'content', 'game.sfc'),
          saveRoot: p.join(tmp.path, 'saves'),
          stateRoot: p.join(tmp.path, 'states'),
          configRoot: p.join(tmp.path, 'config'),
        ),
        dependencies: const <ResolvedRuntimeDependency>[
          ResolvedRuntimeDependency(
            requirementId: 'executable',
            path: '/managed/RetroArch.app',
            provenance: RuntimeDependencyProvenance.managed,
          ),
        ],
        controllerSnapshot: snapshot,
      );

      expect(result, isA<LaunchStarted>());
      final player = adapter.plans.single.playerControllers.single;
      expect(
        player.inputReference.adapterPolicyId,
        RuntimeControllerInputPolicies
            .retroArchMacosSingleControllerMfi8BitDoPro2UsbGameplayV1,
      );
      expect(player.inputReference.canEmitControllerGameplay, isTrue);
      final config = File(adapter.configPath!).readAsStringSync();
      expect(config, contains('input_joypad_driver = "mfi"'));
      expect(config, contains('input_player1_joypad_index = "0"'));
      expect(config, contains('input_player1_b_btn = "8"'));
      expect(config, contains('input_player1_a_btn = "0"'));
      expect(config, contains('input_player1_y_btn = "9"'));
      expect(config, contains('input_player1_x_btn = "1"'));
      expect(config, contains('input_player1_up_btn = "4"'));
      expect(config, contains('input_player1_l2_btn = "12"'));
      expect(config, contains('input_player1_l_x_minus_axis = "-2"'));
      expect(config, contains('input_player1_l_y_minus_axis = "-3"'));
      expect(config, contains('input_player1_l_y_plus_axis = "+3"'));
    },
  );

  test(
    'saved hardware mapping reaches managed Dolphin SDL and GCPad bytes',
    () async {
      const guid = '03000000deadbeef0000000000000000';
      final mapping = CanonicalControllerMapping(
        const <CanonicalGamepadControl, RawGamepadInput>{
          CanonicalGamepadControl.faceSouth: RawButtonInput(3),
          CanonicalGamepadControl.faceEast: RawButtonInput(2),
          CanonicalGamepadControl.faceWest: RawButtonInput(1),
          CanonicalGamepadControl.faceNorth: RawButtonInput(0),
          CanonicalGamepadControl.leftTrigger: RawAxisInput(4),
          CanonicalGamepadControl.leftStickX: RawAxisInput(0),
          CanonicalGamepadControl.leftStickY: RawAxisInput(1, inverted: true),
          CanonicalGamepadControl.rightStickX: RawAxisInput(2),
          CanonicalGamepadControl.rightStickY: RawAxisInput(3),
        },
      );
      await repository.save(
        sdlPlatform: 'macOS',
        sdlGuid: guid,
        displayName: 'Saved Pad',
        mapping: mapping,
      );
      const controller = ConnectedGamepad(
        id: 'exact-pad',
        order: 4,
        identity: ControllerIdentity(
          displayName: 'Saved Pad',
          sdlGuid: guid,
          serial: 'serial-1',
        ),
      );
      final claims = SessionControllerSlotClaims.fromClaims(
        <ControllerSlotClaim?>[ControllerSlotClaim.fromGamepad(controller)],
      );
      final snapshot = ReviewedLaunchSnapshot.resolve(
        claimRevision: claims.revision,
        claims: claims.claimDetails,
        devices: const <ConnectedGamepad>[controller],
      );
      final runner = _RecordingProcessRunner();
      final dolphinUserDirectory = DolphinUserDirectory(
        root: Directory(p.join(tmp.path, 'dolphin-user')),
      );
      final adapter = DolphinAdapter(
        userDirectory: dolphinUserDirectory,
        processRunner: runner,
      );
      const profile = RuntimeProfile(
        id: RuntimeProfileId('dolphin:gc:standalone'),
        adapterId: BuiltinRuntimeProfiles.dolphinAdapterId,
        displayName: 'Dolphin',
        supportedPlatforms: <String>{'gc'},
        requirements: <RuntimeDependencyRequirement>[
          ExecutableRequirement(
            id: 'executable',
            displayName: 'Dolphin',
            managedArtifactId: 'dolphin',
          ),
        ],
      );
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          BuiltinRuntimeProfiles.dolphinAdapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _UnusedResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerHardwareMappings: repository,
        controllerSlotClaims: claims,
        gamepadLister: () async => const <ConnectedGamepad>[controller],
        operatingSystem: 'macos',
      );
      final target = ResolvedPlayTarget(
        releaseId: 'release-3',
        titleId: 'title-3',
        platformShortName: 'gc',
        displayName: 'Test game',
        localProfileId: 'profile-1',
        contentRoot: p.join(tmp.path, 'content'),
        launchAbsolutePath: p.join(tmp.path, 'content', 'game.iso'),
        saveRoot: p.join(tmp.path, 'saves'),
        stateRoot: p.join(tmp.path, 'states'),
        configRoot: p.join(tmp.path, 'config'),
      );

      final result = await provider.launch(
        profile: profile,
        target: target,
        dependencies: const <ResolvedRuntimeDependency>[
          ResolvedRuntimeDependency(
            requirementId: 'executable',
            path: '/managed/Dolphin.app',
            provenance: RuntimeDependencyProvenance.managed,
          ),
        ],
        controllerSnapshot: snapshot,
      );

      expect(result, isA<LaunchStarted>());
      expect(
        runner.environment?['SDL_GAMECONTROLLERCONFIG'],
        contains('$guid,Saved Pad,a:b3,b:b2,x:b1,y:b0'),
      );
      final config = File(
        dolphinUserDirectory.controllerSettingsPath,
      ).readAsStringSync();
      expect(config, contains('Device = SDL/0/Saved Pad'));
      expect(config, contains('Buttons/A = `Button S`'));
      expect(config, contains('Buttons/B = `Button E`'));
      expect(config, contains('Buttons/X = `Button W`'));
      expect(config, contains('Buttons/Y = `Button N`'));
      expect(config, isNot(contains('SDL/4/')));
      final hotkeys = File(
        dolphinUserDirectory.hotkeySettingsPath,
      ).readAsStringSync();
      expect(hotkeys, contains('Device = SDL/0/Saved Pad'));
      expect(hotkeys, contains('General/Stop = `Back` & `Start`'));
    },
  );

  test(
    'saved hardware mapping stays out of rejected PCSX2 input domain',
    () async {
      const guid = '03000000deadbeef0000000000000000';
      final mapping = CanonicalControllerMapping(
        const <CanonicalGamepadControl, RawGamepadInput>{
          CanonicalGamepadControl.faceSouth: RawButtonInput(3),
          CanonicalGamepadControl.faceEast: RawButtonInput(2),
          CanonicalGamepadControl.faceWest: RawButtonInput(1),
          CanonicalGamepadControl.faceNorth: RawButtonInput(0),
          CanonicalGamepadControl.leftShoulder: RawButtonInput(9),
          CanonicalGamepadControl.leftTrigger: RawAxisInput(4),
          CanonicalGamepadControl.leftStickX: RawAxisInput(0),
          CanonicalGamepadControl.leftStickY: RawAxisInput(1, inverted: true),
          CanonicalGamepadControl.rightStickX: RawAxisInput(2),
          CanonicalGamepadControl.rightStickY: RawAxisInput(3),
        },
      );
      await repository.save(
        sdlPlatform: 'macOS',
        sdlGuid: guid,
        displayName: 'Saved Pad',
        mapping: mapping,
      );
      const controller = ConnectedGamepad(
        id: 'exact-pad',
        order: 0,
        identity: ControllerIdentity(
          displayName: 'Saved Pad',
          sdlGuid: guid,
          serial: 'serial-1',
        ),
      );
      final claims = SessionControllerSlotClaims.fromClaims(
        <ControllerSlotClaim?>[ControllerSlotClaim.fromGamepad(controller)],
      );
      final snapshot = ReviewedLaunchSnapshot.resolve(
        claimRevision: claims.revision,
        claims: claims.claimDetails,
        devices: const <ConnectedGamepad>[controller],
      );
      final userDirectory = Pcsx2UserDirectory(
        root: Directory(p.join(tmp.path, 'pcsx2-user')),
        operatingSystem: 'macos',
      );
      File(userDirectory.settingsPath)
        ..createSync(recursive: true)
        ..writeAsStringSync('''
[InputSources]
SDL = true
SDLIOKitDriver = true
SDLMFIDriver = true

[Pad1]
Type = DualShock2
Cross = SDL-0/FaceEast
Circle = SDL-0/FaceSouth
UserOwned = true
''');
      File(userDirectory.gameControllerDatabasePath)
        ..createSync(recursive: true)
        ..writeAsStringSync('''
unrelated-entry
# ROMD managed controller mapping
$guid,Stale ROMD Pad,a:b3,platform:macOS,
# End ROMD managed controller mapping
''');
      final runner = _RecordingProcessRunner();
      final adapter = Pcsx2Adapter(
        userDirectory: userDirectory,
        processRunner: runner,
      );
      const profile = RuntimeProfile(
        id: RuntimeProfileId('pcsx2:integration'),
        adapterId: BuiltinRuntimeProfiles.pcsx2AdapterId,
        displayName: 'PCSX2',
        supportedPlatforms: <String>{'ps2'},
        requirements: <RuntimeDependencyRequirement>[
          ExecutableRequirement(id: 'executable', displayName: 'PCSX2'),
        ],
      );
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          BuiltinRuntimeProfiles.pcsx2AdapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _UnusedResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerHardwareMappings: repository,
        controllerSlotClaims: claims,
        gamepadLister: () async => const <ConnectedGamepad>[controller],
        operatingSystem: 'macos',
      );

      final result = await provider.launch(
        profile: profile,
        target: ResolvedPlayTarget(
          releaseId: 'release-3',
          titleId: 'title-3',
          platformShortName: 'ps2',
          displayName: 'Test game',
          localProfileId: 'profile-1',
          contentRoot: p.join(tmp.path, 'content'),
          launchAbsolutePath: p.join(tmp.path, 'content', 'game.iso'),
          saveRoot: p.join(tmp.path, 'saves'),
          stateRoot: p.join(tmp.path, 'states'),
          configRoot: p.join(tmp.path, 'config'),
        ),
        dependencies: const <ResolvedRuntimeDependency>[
          ResolvedRuntimeDependency(
            requirementId: 'executable',
            path: '/managed/PCSX2.app',
            provenance: RuntimeDependencyProvenance.managed,
          ),
        ],
        controllerSnapshot: snapshot,
      );

      expect(result, isA<LaunchStarted>());
      expect(runner.environment, isNot(contains('SDL_GAMECONTROLLERCONFIG')));
      expect(
        File(userDirectory.gameControllerDatabasePath).readAsStringSync(),
        'unrelated-entry\n',
      );
      final ini = File(userDirectory.settingsPath).readAsStringSync();
      expect(ini, contains('Cross = SDL-0/FaceEast'));
      expect(ini, contains('Circle = SDL-0/FaceSouth'));
      expect(ini, contains('SDLIOKitDriver = true'));
      expect(ini, contains('SDLMFIDriver = true'));
      expect(ini, contains('UserOwned = true'));
    },
  );
}
