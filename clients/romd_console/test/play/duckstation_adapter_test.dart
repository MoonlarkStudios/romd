import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/emulator/data/adapters/duckstation/duckstation_adapter.dart';
import 'package:romd_console/src/play/emulator/data/adapters/duckstation/duckstation_user_directory.dart';
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
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
  String? workingDirectory;
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
    this.workingDirectory = workingDirectory;
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

const _profile = RuntimeProfile(
  id: RuntimeProfileId('duckstation:psx:standalone'),
  adapterId: RuntimeAdapterId('duckstation'),
  displayName: 'DuckStation',
  supportedPlatforms: <String>{'psx'},
  requirements: <RuntimeDependencyRequirement>[
    ExecutableRequirement(id: 'executable', displayName: 'DuckStation'),
    BiosSetRequirement(
      id: 'bios',
      displayName: 'PlayStation BIOS',
      files: <BiosFileSpec>[BiosFileSpec(fileName: 'scph5501.bin')],
      satisfaction: BiosSatisfaction.anyOne,
    ),
  ],
);

ResolvedRuntimeDependency _executableDependency(String path) =>
    ResolvedRuntimeDependency(requirementId: 'executable', path: path);

void main() {
  late Directory tmp;
  late DuckStationUserDirectory userDirectory;
  late ResolvedPlayTarget target;
  late EmulatorLaunchPlan plan;

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_duckstation_adapter_test');
    userDirectory = DuckStationUserDirectory(
      root: Directory(p.join(tmp.path, 'duckstation-user')),
      operatingSystem: 'macos',
    );
    target = ResolvedPlayTarget(
      releaseId: 'rel-1',
      titleId: 'title-1',
      platformShortName: 'psx',
      displayName: 'Ridge Racer',
      localProfileId: 'profile-1',
      contentRoot: p.join(tmp.path, 'content'),
      launchAbsolutePath: p.join(tmp.path, 'content', 'ridge racer.cue'),
      saveRoot: p.join(tmp.path, 'saves'),
      stateRoot: p.join(tmp.path, 'states'),
      configRoot: p.join(tmp.path, 'config'),
    );
    plan = EmulatorLaunchPlan(
      target: target,
      profile: _profile,
      dependencies: <ResolvedRuntimeDependency>[
        _executableDependency('/Applications/DuckStation.app'),
        ResolvedRuntimeDependency(
          requirementId: 'bios',
          path: userDirectory.biosDirectoryPath,
        ),
      ],
    );
  });

  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  test(
    'buildCommand maps a macOS app bundle and boots the target directly',
    () {
      final adapter = DuckStationAdapter(userDirectory: userDirectory);

      final command = adapter.buildCommand(
        plan,
        '/Applications/DuckStation.app',
      );

      expect(
        command.executable,
        '/Applications/DuckStation.app/Contents/MacOS/DuckStation',
      );
      expect(command.arguments, <String>[
        '-batch',
        '-fastboot',
        '-nogui',
        '-fullscreen',
        '--',
        target.launchAbsolutePath,
      ]);
      expect(command.workingDirectory, target.contentRoot);
    },
  );

  test('buildCommand scopes DuckStation to the ROMD-owned user directory', () {
    final adapter = DuckStationAdapter(userDirectory: userDirectory);

    final command = adapter.buildCommand(plan, '/usr/local/bin/duckstation');

    expect(command.executable, '/usr/local/bin/duckstation');
    expect(command.environment, <String, String>{
      'HOME': p.join(tmp.path, 'duckstation-user'),
    });
  });

  test('prepare creates ROMD directories and DuckStation config', () async {
    final adapter = DuckStationAdapter(userDirectory: userDirectory);

    await adapter.prepare(plan);

    expect(Directory(target.saveRoot).existsSync(), isTrue);
    expect(Directory(target.stateRoot).existsSync(), isTrue);
    expect(Directory(target.configRoot).existsSync(), isTrue);
    expect(Directory(userDirectory.biosDirectoryPath).existsSync(), isTrue);
    expect(File(userDirectory.settingsPath).existsSync(), isTrue);
    final settings = File(userDirectory.settingsPath).readAsStringSync();
    expect(settings, contains('SaveStates = ${target.stateRoot}'));
    expect(settings, contains('Directory = ${target.saveRoot}'));
  });

  test("prepare writes the plan's controller mapping into [Hotkeys]", () async {
    final adapter = DuckStationAdapter(userDirectory: userDirectory);
    final reboundPlan = EmulatorLaunchPlan(
      target: target,
      profile: _profile,
      dependencies: plan.dependencies,
      controllerMapping: BuiltinControllerTemplates.defaultMapping.overlaidWith(
        const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
          RomdAction.saveState: GamepadButtonPosition.faceEast,
        }),
      ),
    );

    await adapter.prepare(reboundPlan);

    final settings = File(userDirectory.settingsPath).readAsStringSync();
    expect(settings, contains('SaveSelectedSaveState = SDL-0/Back & SDL-0/B'));
  });

  test(
    'launch starts DuckStation and completed reports the process exit code',
    () async {
      final runner = _RecordingProcessRunner(
        process: _FakeRunningProcess(exitCodeValue: 17),
      );
      final adapter = DuckStationAdapter(
        userDirectory: userDirectory,
        processRunner: runner,
      );

      final result = await adapter.launch(plan);

      expect(result, isA<LaunchStarted>());
      final completed = await (result as LaunchStarted).session.completed;
      expect(completed, isA<LaunchExited>());
      expect((completed as LaunchExited).exitCode, 17);
      expect(
        runner.executable,
        '/Applications/DuckStation.app/Contents/MacOS/DuckStation',
      );
      expect(runner.arguments, contains(target.launchAbsolutePath));
      expect(runner.environment, containsPair('HOME', userDirectory.rootPath));
    },
  );

  test(
    'launch returns runtimeMissing when executable dependency is absent',
    () async {
      final adapter = DuckStationAdapter(userDirectory: userDirectory);
      final missingExecutable = EmulatorLaunchPlan(
        target: target,
        profile: _profile,
        dependencies: const <ResolvedRuntimeDependency>[
          ResolvedRuntimeDependency(requirementId: 'bios', path: '/bios'),
        ],
      );

      final result = await adapter.launch(missingExecutable);

      expect(result, isA<LaunchNotStarted>());
      final missing = (result as LaunchNotStarted).result;
      expect(missing, isA<LaunchRuntimeMissing>());
      expect((missing as LaunchRuntimeMissing).runtimeName, 'DuckStation');
    },
  );
}
