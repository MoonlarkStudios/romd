import 'dart:async';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/emulator/data/adapters/dolphin/dolphin_user_directory.dart';
import 'package:romd_console/src/play/emulator/data/adapters/dolphin/managed_dolphin_settings_launcher.dart';
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_provision.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/window_controller.dart';

final class _Provisioner implements RuntimeExecutableProvisioner {
  _Provisioner(this.events);

  final List<RuntimeProvisionProgress> events;

  @override
  Stream<RuntimeProvisionProgress> ensure() => Stream.fromIterable(events);
}

final class _GatedProvisioner implements RuntimeExecutableProvisioner {
  final started = Completer<void>();
  final release = Completer<void>();

  @override
  Stream<RuntimeProvisionProgress> ensure() async* {
    yield const RuntimeProvisionStarted();
    started.complete();
    await release.future;
    yield const RuntimeExecutableReady(executablePath: '/managed/Dolphin.app');
  }
}

final class _Runner implements ProcessRunner {
  final process = _Process();
  final started = Completer<void>();
  int starts = 0;
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
    starts++;
    if (!started.isCompleted) started.complete();
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

final class _Process implements RunningProcess {
  final exit = Completer<int>();

  @override
  Future<int> get exitCode => exit.future;

  @override
  Stream<List<int>> get stderr => const Stream<List<int>>.empty();

  @override
  Stream<List<int>> get stdout => const Stream<List<int>>.empty();

  @override
  bool kill() {
    if (!exit.isCompleted) exit.complete(-15);
    return true;
  }
}

final class _Window implements WindowController {
  int reclaims = 0;

  @override
  Future<void> reclaimForeground() async => reclaims++;
}

final class _HeldSession implements LaunchSession {
  const _HeldSession();

  @override
  Future<LaunchResult> get completed => Completer<LaunchResult>().future;

  @override
  LaunchForegroundControl? get foreground => null;

  @override
  LaunchTermination? get termination => null;
}

void main() {
  late Directory tmp;
  late DolphinUserDirectory userDirectory;
  late ActiveLaunchSession active;
  late _Runner runner;
  late _Window window;

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_dolphin_settings_test');
    userDirectory = DolphinUserDirectory(
      root: Directory(p.join(tmp.path, 'runtimes', 'dolphin', 'user')),
    );
    active = ActiveLaunchSession();
    runner = _Runner();
    window = _Window();
  });

  tearDown(() {
    if (tmp.existsSync()) tmp.deleteSync(recursive: true);
  });

  test('opens managed Dolphin UI without boot or batch arguments', () async {
    await userDirectory.prepareBase();
    await File(userDirectory.settingsPath).writeAsString('''
[Core]
CPUThread = True
[AutoUpdate]
UpdateTrack = dev
''');
    final launcher = ManagedDolphinSettingsLauncher(
      provisioner: _Provisioner(const <RuntimeProvisionProgress>[
        RuntimeProvisionStarted(),
        RuntimeExecutableReady(executablePath: '/managed/Dolphin.app'),
      ]),
      userDirectory: userDirectory,
      activeLaunchSession: active,
      windowController: window,
      processRunner: runner,
    );

    final pending = launcher.openDolphin();
    await runner.started.future;
    await Future<void>.delayed(Duration.zero);

    expect(active.hasSession, isTrue);
    expect(runner.executable, '/managed/Dolphin.app/Contents/MacOS/Dolphin');
    expect(runner.arguments, <String>['-u', userDirectory.rootPath]);
    expect(runner.arguments, isNot(contains('-b')));
    expect(runner.arguments, isNot(contains('-e')));
    expect(runner.workingDirectory, userDirectory.rootPath);
    expect(runner.environment, isEmpty);
    expect(Directory(userDirectory.configDirectoryPath).existsSync(), isTrue);
    final settings = await File(userDirectory.settingsPath).readAsString();
    expect(settings, contains('CPUThread = True'));
    expect(settings, contains('UpdateTrack = \n'));
    expect(settings, isNot(contains('UpdateTrack = dev')));
    expect(
      FileSystemEntity.typeSync(userDirectory.gameCubeSavePath),
      FileSystemEntityType.notFound,
    );

    runner.process.exit.complete(0);
    final result = await pending;

    expect(result, isA<LaunchExited>());
    expect((result as LaunchExited).exitCode, 0);
    expect(active.hasSession, isFalse);
    expect(window.reclaims, 1);
  });

  test('provision failure never starts Dolphin', () async {
    final result = await ManagedDolphinSettingsLauncher(
      provisioner: _Provisioner(const <RuntimeProvisionProgress>[
        RuntimeProvisionFailed("Couldn't download Dolphin."),
      ]),
      userDirectory: userDirectory,
      activeLaunchSession: active,
      windowController: window,
      processRunner: runner,
    ).openDolphin();

    expect(result, isA<LaunchFailed>());
    expect((result as LaunchFailed).message, "Couldn't download Dolphin.");
    expect(runner.starts, 0);
    expect(window.reclaims, 0);
  });

  test('a running emulator blocks settings before provisioning', () async {
    active.hold(const _HeldSession());
    final result = await ManagedDolphinSettingsLauncher(
      provisioner: _Provisioner(const <RuntimeProvisionProgress>[
        RuntimeExecutableReady(executablePath: '/managed/Dolphin.app'),
      ]),
      userDirectory: userDirectory,
      activeLaunchSession: active,
      windowController: window,
      processRunner: runner,
    ).openDolphin();

    expect(result, isA<LaunchFailed>());
    expect((result as LaunchFailed).message, contains('running emulator'));
    expect(runner.starts, 0);
  });

  test('provisioning reservation atomically blocks a second launch', () async {
    final provisioner = _GatedProvisioner();
    final launcher = ManagedDolphinSettingsLauncher(
      provisioner: provisioner,
      userDirectory: userDirectory,
      activeLaunchSession: active,
      windowController: window,
      processRunner: runner,
    );

    final first = launcher.openDolphin();
    await provisioner.started.future;
    expect(active.hasSession, isTrue);

    final second = await launcher.openDolphin();
    expect(second, isA<LaunchFailed>());
    expect(runner.starts, 0);

    provisioner.release.complete();
    await runner.started.future;
    await Future<void>.delayed(Duration.zero);
    runner.process.exit.complete(0);
    expect(await first, isA<LaunchExited>());
    expect(active.hasSession, isFalse);
  });
}
