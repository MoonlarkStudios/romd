import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/emulator/data/adapters/pcsx2/pcsx2_adapter.dart';
import 'package:romd_console/src/play/emulator/data/adapters/pcsx2/pcsx2_user_directory.dart';
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
  }) async => const ProcessRunResult(exitCode: 0, stdout: '', stderr: '');
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
  id: RuntimeProfileId('pcsx2:ps2:standalone'),
  adapterId: RuntimeAdapterId('pcsx2'),
  displayName: 'PCSX2',
  supportedPlatforms: <String>{'ps2'},
  requirements: <RuntimeDependencyRequirement>[
    ExecutableRequirement(id: 'executable', displayName: 'PCSX2'),
    BiosSetRequirement(
      id: 'bios',
      displayName: 'PlayStation 2 BIOS',
      files: <BiosFileSpec>[],
      satisfaction: BiosSatisfaction.anyOne,
      acceptsAnyPlatformBios: true,
    ),
  ],
);

ResolvedRuntimeDependency _executableDependency(String path) =>
    ResolvedRuntimeDependency(requirementId: 'executable', path: path);

void main() {
  late Directory tmp;
  late Pcsx2UserDirectory userDirectory;
  late ResolvedPlayTarget target;
  late EmulatorLaunchPlan plan;

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_pcsx2_adapter_test');
    userDirectory = Pcsx2UserDirectory(
      root: Directory(p.join(tmp.path, 'pcsx2-user')),
      operatingSystem: 'macos',
    );
    target = ResolvedPlayTarget(
      releaseId: 'rel-1',
      titleId: 'title-1',
      platformShortName: 'ps2',
      displayName: 'Ridge Racer V',
      localProfileId: 'profile-1',
      contentRoot: p.join(tmp.path, 'content'),
      launchAbsolutePath: p.join(tmp.path, 'content', 'ridge racer v.iso'),
      saveRoot: p.join(tmp.path, 'saves'),
      stateRoot: p.join(tmp.path, 'states'),
      configRoot: p.join(tmp.path, 'config'),
    );
    plan = EmulatorLaunchPlan(
      target: target,
      profile: _profile,
      dependencies: <ResolvedRuntimeDependency>[
        _executableDependency('/Applications/PCSX2.app'),
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

  test('buildCommand maps a macOS app bundle and boots with Qt flags', () {
    final adapter = Pcsx2Adapter(userDirectory: userDirectory);

    final command = adapter.buildCommand(
      plan,
      '/Applications/PCSX2-v2.6.3.app',
    );

    expect(
      command.executable,
      '/Applications/PCSX2-v2.6.3.app/Contents/MacOS/PCSX2',
    );
    expect(command.arguments, <String>[
      '-batch',
      '-fastboot',
      '-fullscreen',
      '--',
      target.launchAbsolutePath,
    ]);
    expect(command.workingDirectory, target.contentRoot);
    expect(command.environment, <String, String>{
      'HOME': userDirectory.rootPath,
    });
  });

  test('command does not inject a cross-domain SDL mapping', () {
    final adapter = Pcsx2Adapter(userDirectory: userDirectory);
    final command = adapter.buildCommand(plan, '/Applications/PCSX2.app');

    expect(command.environment, isNot(contains('SDL_GAMECONTROLLERCONFIG')));
  });

  test('prepare creates ROMD directories and PCSX2 config', () async {
    final adapter = Pcsx2Adapter(
      userDirectory: userDirectory,
      processRunner: _RecordingProcessRunner(),
    );

    await adapter.prepare(plan);

    expect(Directory(target.saveRoot).existsSync(), isTrue);
    expect(Directory(target.stateRoot).existsSync(), isTrue);
    expect(Directory(target.configRoot).existsSync(), isTrue);
    expect(Directory(userDirectory.dataDirectoryPath).existsSync(), isTrue);
    expect(Directory(userDirectory.biosDirectoryPath).existsSync(), isTrue);
    expect(File(userDirectory.settingsPath).existsSync(), isTrue);
    expect(
      File(userDirectory.settingsPath).readAsStringSync(),
      contains('SetupWizardIncomplete = false'),
    );
    final settings = File(userDirectory.settingsPath).readAsStringSync();
    expect(settings, contains('MemoryCards = ${target.saveRoot}'));
    expect(settings, contains('Savestates = ${target.stateRoot}'));
  });

  test(
    'launch starts PCSX2 and completed reports the process exit code',
    () async {
      final runner = _RecordingProcessRunner(
        process: _FakeRunningProcess(exitCodeValue: 17),
      );
      final adapter = Pcsx2Adapter(
        userDirectory: userDirectory,
        processRunner: runner,
      );

      final result = await adapter.launch(plan);

      expect(result, isA<LaunchStarted>());
      final completed = await (result as LaunchStarted).session.completed;
      expect(completed, isA<LaunchExited>());
      expect((completed as LaunchExited).exitCode, 17);
      expect(runner.executable, '/Applications/PCSX2.app/Contents/MacOS/PCSX2');
      expect(runner.arguments, isNot(contains('-datapath')));
      expect(runner.arguments, contains(target.launchAbsolutePath));
      expect(runner.environment, containsPair('HOME', userDirectory.rootPath));
    },
  );

  test(
    'launch returns runtimeMissing when executable dependency is absent',
    () async {
      final adapter = Pcsx2Adapter(userDirectory: userDirectory);
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
      expect((missing as LaunchRuntimeMissing).runtimeName, 'PCSX2');
    },
  );
}
