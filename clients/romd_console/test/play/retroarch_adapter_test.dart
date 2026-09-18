import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/adapters/retroarch/retroarch_adapter.dart';
import 'package:romd_console/src/play/emulator/data/adapters/retroarch/retroarch_config_writer.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

final class _RecordingProcessRunner implements ProcessRunner {
  _RecordingProcessRunner({RunningProcess? process})
    : process = process ?? _FakeRunningProcess();

  final RunningProcess process;
  String? executable;
  List<String>? arguments;
  Map<String, String>? environment;

  @override
  Future<RunningProcess> start({
    required String executable,
    required List<String> arguments,
    required String workingDirectory,
    required Map<String, String> environment,
  }) async {
    this.executable = executable;
    this.arguments = arguments;
    this.environment = environment;
    return process;
  }

  @override
  Future<ProcessRunResult> run({
    required String executable,
    required List<String> arguments,
    String? workingDirectory,
    Map<String, String> environment = const <String, String>{},
  }) => throw UnimplementedError();
}

final class _FakeRunningProcess implements RunningProcess {
  _FakeRunningProcess({this.exitCodeValue = 0});

  final int exitCodeValue;

  @override
  Future<int> get exitCode async => exitCodeValue;
  @override
  Stream<List<int>> get stdout => const Stream<List<int>>.empty();
  @override
  Stream<List<int>> get stderr => const Stream<List<int>>.empty();
  @override
  bool kill() => true;
}

const _core = '/cores/snes9x_libretro.dylib';

const _profile = RuntimeProfile(
  id: RuntimeProfileId('retroarch:snes:snes9x'),
  adapterId: RuntimeAdapterId('retroarch'),
  displayName: 'RetroArch (Snes9x)',
  supportedPlatforms: <String>{'snes'},
  requirements: <RuntimeDependencyRequirement>[
    ExecutableRequirement(
      id: 'executable',
      displayName: 'RetroArch',
      managedArtifactId: 'retroarch',
    ),
    LibretroCoreRequirement(
      id: 'core',
      displayName: 'Snes9x',
      coreId: 'snes9x',
    ),
  ],
);

const _psxProfile = RuntimeProfile(
  id: RuntimeProfileId('retroarch:psx:swanstation'),
  adapterId: RuntimeAdapterId('retroarch'),
  displayName: 'RetroArch (SwanStation)',
  supportedPlatforms: <String>{'psx'},
  requirements: <RuntimeDependencyRequirement>[
    ExecutableRequirement(
      id: 'executable',
      displayName: 'RetroArch',
      managedArtifactId: 'retroarch',
    ),
    LibretroCoreRequirement(
      id: 'core',
      displayName: 'SwanStation',
      coreId: 'swanstation',
    ),
    BiosSetRequirement(
      id: 'bios',
      displayName: 'PlayStation BIOS',
      files: <BiosFileSpec>[BiosFileSpec(fileName: 'scph5500.bin')],
      satisfaction: BiosSatisfaction.anyOne,
    ),
  ],
);

const _genesisPlusGxProfile = RuntimeProfile(
  id: RuntimeProfileId('retroarch:genesis:genesis_plus_gx'),
  adapterId: RuntimeAdapterId('retroarch'),
  displayName: 'RetroArch (Genesis Plus GX)',
  supportedPlatforms: <String>{'genesis'},
  requirements: <RuntimeDependencyRequirement>[
    ExecutableRequirement(
      id: 'executable',
      displayName: 'RetroArch',
      managedArtifactId: 'retroarch',
    ),
    LibretroCoreRequirement(
      id: 'core',
      displayName: 'Genesis Plus GX',
      coreId: 'genesis_plus_gx',
    ),
  ],
);

ResolvedRuntimeDependency _executableDependency(String path) =>
    ResolvedRuntimeDependency(requirementId: 'executable', path: path);

LaunchControllerSetup _approvedGameplaySetup() {
  const slot = ResolvedControllerSlot(
    playerSlot: 0,
    controller: const ConnectedGamepad(
      id: 'exact-pad',
      order: 0,
      identity: ControllerIdentity(
        displayName:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbDisplayName,
        sdlGuid:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
        serial: 'pro2-serial',
      ),
    ),
    runtimeIndex: 0,
  );
  return LaunchControllerSetup(
    playerControllers: <LaunchPlayerController>[
      LaunchPlayerController(
        resolvedSlot: slot,
        inputReference: const RuntimeControllerInputReference(
          provider: RuntimeControllerInputProvider.sdl3,
          providerDeviceId: 'exact-pad',
          providerOrdinal: 0,
          correlation: RuntimeControllerInputCorrelation.verified,
          runtimeReference: 0,
          adapterPolicyId: RuntimeControllerInputPolicies
              .retroArchMacosSingleControllerMfi8BitDoPro2UsbGameplayV1,
          controllerShortcutsAllowed: true,
          controllerGameplayAllowed: true,
        ),
        mapping: BuiltinControllerTemplates.defaultMapping,
        templateId: BuiltinControllerTemplates.generic.id,
        capabilities: BuiltinControllerTemplates.generic.capabilities,
        gameplayMapping: ControllerHardwareMapping(
          sdlPlatform: 'macOS',
          sdlGuid:
              RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
          displayName: RuntimeControllerInputPolicies
              .retroArchMfi8BitDoPro2UsbDisplayName,
          mapping: CanonicalControllerMapping(
            <CanonicalGamepadControl, RawGamepadInput>{
              CanonicalGamepadControl.faceSouth: const RawButtonInput(1),
            },
          ),
          createdAt: DateTime.utc(2026, 7, 13),
          updatedAt: DateTime.utc(2026, 7, 13),
        ),
      ),
    ],
  );
}

void main() {
  late Directory tmp;
  late ResolvedPlayTarget target;
  late EmulatorLaunchPlan plan;
  late EmulatorLaunchPlan planWithoutCore;
  late EmulatorLaunchPlan planWithoutExecutable;

  EmulatorLaunchPlan planWith(List<ResolvedRuntimeDependency> dependencies) =>
      EmulatorLaunchPlan(
        target: target,
        profile: _profile,
        dependencies: dependencies,
      );

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_ra_test');
    target = ResolvedPlayTarget(
      releaseId: 'rel-1',
      titleId: 'title-1',
      platformShortName: 'snes',
      displayName: 'Chrono Trigger',
      localProfileId: 'profile-1',
      contentRoot: '${tmp.path}/content',
      launchAbsolutePath: '${tmp.path}/content/chrono.sfc',
      saveRoot: '${tmp.path}/saves',
      stateRoot: '${tmp.path}/states',
      configRoot: '${tmp.path}/config',
    );
    plan = planWith(<ResolvedRuntimeDependency>[
      _executableDependency('/usr/bin/retroarch'),
      const ResolvedRuntimeDependency(requirementId: 'core', path: _core),
    ]);
    planWithoutCore = planWith(<ResolvedRuntimeDependency>[
      _executableDependency('/Applications/RetroArch.app'),
    ]);
    planWithoutExecutable = planWith(const <ResolvedRuntimeDependency>[
      ResolvedRuntimeDependency(requirementId: 'core', path: _core),
    ]);
  });

  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  test(
    'buildCommand maps a macOS .app to its inner binary and loads the core',
    () {
      const adapter = RetroArchAdapter();
      final command = adapter.buildCommand(plan, '/Applications/RetroArch.app');

      expect(
        command.executable,
        '/Applications/RetroArch.app/Contents/MacOS/RetroArch',
      );
      expect(
        command.arguments,
        containsAllInOrder(<String>[
          '-L',
          _core,
          '${tmp.path}/content/chrono.sfc',
        ]),
      );
      expect(command.arguments, contains('--fullscreen'));
    },
  );

  test('buildCommand uses a standalone binary path directly', () {
    const adapter = RetroArchAdapter();
    final command = adapter.buildCommand(plan, '/usr/bin/retroarch');

    expect(command.executable, '/usr/bin/retroarch');
  });

  test('buildCommand uses AppImage extract-and-run without requiring FUSE', () {
    const adapter = RetroArchAdapter();
    final command = adapter.buildCommand(
      plan,
      '/runtimes/retroarch/RetroArch-Linux-x86_64.AppImage',
    );

    expect(
      command.executable,
      '/runtimes/retroarch/RetroArch-Linux-x86_64.AppImage',
    );
    expect(command.environment, <String, String>{
      'APPIMAGE_EXTRACT_AND_RUN': '1',
    });
  });

  test('buildCommand passes the ROMD-generated session config', () {
    const adapter = RetroArchAdapter();
    final command = adapter.buildCommand(plan, '/usr/bin/retroarch');

    expect(
      command.arguments,
      containsAllInOrder(<String>[
        '--config',
        const RetroArchConfigWriter().configPathFor(plan.target.configRoot),
        '-L',
        _core,
      ]),
    );
  });

  test(
    "prepare writes the plan's resolved BIOS dir as system_directory",
    () async {
      const adapter = RetroArchAdapter();
      final biosPlan = EmulatorLaunchPlan(
        target: target,
        profile: _psxProfile,
        dependencies: <ResolvedRuntimeDependency>[
          _executableDependency('/usr/bin/retroarch'),
          const ResolvedRuntimeDependency(requirementId: 'core', path: _core),
          const ResolvedRuntimeDependency(
            requirementId: 'bios',
            path: '/bios/psx',
          ),
        ],
      );

      await adapter.prepare(biosPlan);

      final cfg = await File(
        const RetroArchConfigWriter().configPathFor(target.configRoot),
      ).readAsString();
      expect(cfg, contains('system_directory = "/bios/psx"'));
    },
  );

  test(
    "prepare writes the plan's controller mapping as hotkey binds",
    () async {
      const adapter = RetroArchAdapter();
      final reboundPlan = EmulatorLaunchPlan(
        target: target,
        profile: _profile,
        dependencies: <ResolvedRuntimeDependency>[
          _executableDependency('/usr/bin/retroarch'),
          const ResolvedRuntimeDependency(requirementId: 'core', path: _core),
        ],
        controllerMapping: BuiltinControllerTemplates.defaultMapping
            .overlaidWith(
              const ControllerMapping.fixed(
                <RomdAction, GamepadButtonPosition?>{
                  RomdAction.saveState: GamepadButtonPosition.faceEast,
                },
              ),
            ),
      );

      await adapter.prepare(reboundPlan);

      final cfg = await File(
        const RetroArchConfigWriter().configPathFor(target.configRoot),
      ).readAsString();
      expect(cfg, contains('input_save_state_btn = "8"'));
    },
  );

  test(
    'prepare omits system_directory for profiles without a BIOS requirement',
    () async {
      const adapter = RetroArchAdapter();

      await adapter.prepare(plan);

      final cfg = await File(
        const RetroArchConfigWriter().configPathFor(target.configRoot),
      ).readAsString();
      expect(cfg, isNot(contains('system_directory')));
    },
  );

  test(
    'prepare propagates the selected libretro core id to the writer',
    () async {
      const adapter = RetroArchAdapter();
      final genesisTarget = ResolvedPlayTarget(
        releaseId: 'rel-genesis',
        titleId: 'title-genesis',
        platformShortName: 'genesis',
        displayName: 'ClayFighter',
        localProfileId: 'profile-1',
        contentRoot: '${tmp.path}/genesis/content',
        launchAbsolutePath: '${tmp.path}/genesis/content/clayfighter.bin',
        saveRoot: '${tmp.path}/genesis/saves',
        stateRoot: '${tmp.path}/genesis/states',
        configRoot: '${tmp.path}/genesis/config',
      );
      final genesisPlan = EmulatorLaunchPlan(
        target: genesisTarget,
        profile: _genesisPlusGxProfile,
        dependencies: <ResolvedRuntimeDependency>[
          _executableDependency('/usr/bin/retroarch'),
          const ResolvedRuntimeDependency(
            requirementId: 'core',
            path: '/cores/genesis_plus_gx_libretro.dylib',
          ),
        ],
        controllerSetup: _approvedGameplaySetup(),
      );

      await adapter.prepare(genesisPlan);

      final cfg = await File(
        const RetroArchConfigWriter().configPathFor(genesisTarget.configRoot),
      ).readAsString();
      expect(cfg, contains('input_libretro_device_p1 = "1"'));
      expect(cfg, contains('input_player1_analog_dpad_mode = "1"'));
    },
  );

  test('launch fails clearly when the core dependency is missing', () async {
    const adapter = RetroArchAdapter();

    final result = await adapter.launch(planWithoutCore);

    expect(result, isA<LaunchNotStarted>());
    final failure = (result as LaunchNotStarted).result;
    expect(failure, isA<LaunchFailed>());
    expect(
      (failure as LaunchFailed).message,
      'No RetroArch core is configured for this platform.',
    );
  });

  test(
    'launch returns runtimeMissing when the executable dependency is missing',
    () async {
      const adapter = RetroArchAdapter();

      final result = await adapter.launch(planWithoutExecutable);

      expect(result, isA<LaunchNotStarted>());
      final missing = (result as LaunchNotStarted).result;
      expect(missing, isA<LaunchRuntimeMissing>());
      expect((missing as LaunchRuntimeMissing).diagnostics, <String>[
        'No executable dependency was resolved for this launch.',
      ]);
      expect(missing.runtimeName, 'RetroArch');
    },
  );

  test('launch maps a resolved .app dependency to its inner binary', () async {
    final runner = _RecordingProcessRunner();
    final adapter = RetroArchAdapter(processRunner: runner);
    final appPlan = planWith(<ResolvedRuntimeDependency>[
      _executableDependency('/Applications/RetroArch.app'),
      const ResolvedRuntimeDependency(requirementId: 'core', path: _core),
    ]);

    final result = await adapter.launch(appPlan);

    expect(result, isA<LaunchStarted>());
    expect(
      runner.executable,
      '/Applications/RetroArch.app/Contents/MacOS/RetroArch',
    );
  });

  test(
    'launch starts RetroArch and completed reports the process exit code',
    () async {
      final runner = _RecordingProcessRunner(
        process: _FakeRunningProcess(exitCodeValue: 17),
      );
      final adapter = RetroArchAdapter(processRunner: runner);

      final result = await adapter.launch(plan);

      expect(result, isA<LaunchStarted>());
      final completed = await (result as LaunchStarted).session.completed;
      expect(completed, isA<LaunchExited>());
      expect((completed as LaunchExited).exitCode, 17);
      expect(runner.executable, '/usr/bin/retroarch');
      expect(
        runner.arguments,
        containsAllInOrder(<String>[
          '-L',
          _core,
          '${tmp.path}/content/chrono.sfc',
        ]),
      );
    },
  );
}
