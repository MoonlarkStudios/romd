import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/emulator/data/adapters/pcsx2/pcsx2_config_writer.dart';
import 'package:romd_console/src/play/emulator/data/adapters/pcsx2/pcsx2_user_directory.dart';
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';

final class _RecordingProcessRunner implements ProcessRunner {
  _RecordingProcessRunner({this.onRun});

  final Future<ProcessRunResult> Function()? onRun;
  final List<String> executables = <String>[];

  @override
  Future<ProcessRunResult> run({
    required String executable,
    required List<String> arguments,
    String? workingDirectory,
    Map<String, String> environment = const <String, String>{},
  }) async {
    executables.add(executable);
    return await onRun?.call() ??
        const ProcessRunResult(exitCode: 0, stdout: '', stderr: '');
  }

  @override
  Future<RunningProcess> start({
    required String executable,
    required List<String> arguments,
    required String workingDirectory,
    required Map<String, String> environment,
  }) => throw UnimplementedError();
}

void main() {
  late Directory tmp;
  late Pcsx2UserDirectory userDirectory;

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_pcsx2_config_test');
    userDirectory = Pcsx2UserDirectory(
      root: Directory(p.join(tmp.path, 'pcsx2-user')),
      operatingSystem: 'macos',
    );
  });

  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  test('write creates only ROMD non-controller settings', () async {
    await const Pcsx2IniConfigWriter().write(
      userDirectory: userDirectory,
      executablePath: null,
    );

    final settings = File(userDirectory.settingsPath).readAsStringSync();
    expect(settings, contains('SetupWizardIncomplete = false'));
    expect(settings, contains('Bios = bios'));
    expect(settings, isNot(contains('[InputSources]')));
    expect(settings, isNot(contains('[Pad]')));
    expect(settings, isNot(contains('[Pad1]')));
    expect(
      File(userDirectory.gameControllerDatabasePath).existsSync(),
      isFalse,
    );
  });

  test('write replaces profile-owned memory card and state roots', () async {
    const writer = Pcsx2IniConfigWriter();
    final firstSave = p.join(tmp.path, 'profiles', 'one', 'saves');
    final firstState = p.join(tmp.path, 'profiles', 'one', 'states');
    final secondSave = p.join(tmp.path, 'profiles', 'two', 'saves');
    final secondState = p.join(tmp.path, 'profiles', 'two', 'states');

    await writer.write(
      userDirectory: userDirectory,
      executablePath: null,
      saveRoot: firstSave,
      stateRoot: firstState,
    );
    await writer.write(
      userDirectory: userDirectory,
      executablePath: null,
      saveRoot: secondSave,
      stateRoot: secondState,
    );

    final settings = File(userDirectory.settingsPath).readAsStringSync();
    expect(settings, contains('MemoryCards = $secondSave'));
    expect(settings, contains('Savestates = $secondState'));
    expect(settings, isNot(contains(firstSave)));
    expect(settings, isNot(contains(firstState)));
  });

  test('write retains the existing first-run PCSX2 bootstrap', () async {
    final runner = _RecordingProcessRunner(
      onRun: () async {
        File(userDirectory.settingsPath)
          ..createSync(recursive: true)
          ..writeAsStringSync('''
[UI]
SettingsVersion = 1
SetupWizardIncomplete = true

[InputSources]
SDL = true
''');
        return const ProcessRunResult(exitCode: 0, stdout: '', stderr: '');
      },
    );

    await Pcsx2IniConfigWriter(processRunner: runner).write(
      userDirectory: userDirectory,
      executablePath: '/Applications/PCSX2.app/Contents/MacOS/PCSX2',
    );

    expect(runner.executables, <String>[
      '/Applications/PCSX2.app/Contents/MacOS/PCSX2',
    ]);
    final settings = File(userDirectory.settingsPath).readAsStringSync();
    expect(settings, contains('SetupWizardIncomplete = false'));
    expect(settings, contains('[InputSources]'));
    expect(settings, contains('SDL = true'));
    expect(settings, isNot(contains('[Pad1]')));
  });

  test('write preserves PCSX2-owned input and Pad configuration', () async {
    const inputConfiguration = '''
[InputSources]
Keyboard = true
SDL = true
SDLIOKitDriver = true
SDLMFIDriver = true

[Pad]
MultitapPort1 = false

[Pad1]
Type = DualShock2
Cross = SDL-0/FaceEast
Circle = SDL-0/FaceSouth
UserSetting = keep-me
''';
    File(userDirectory.settingsPath)
      ..createSync(recursive: true)
      ..writeAsStringSync(inputConfiguration);

    await const Pcsx2IniConfigWriter().write(
      userDirectory: userDirectory,
      executablePath: null,
    );

    final settings = File(userDirectory.settingsPath).readAsStringSync();
    expect(settings, contains(inputConfiguration.trim()));
    expect(settings, contains('SetupWizardIncomplete = false'));
    expect(settings, contains('UserSetting = keep-me'));
  });

  test('write removes only ROMD framed controller database entry', () async {
    File(userDirectory.gameControllerDatabasePath)
      ..createSync(recursive: true)
      ..writeAsStringSync('''
unrelated-controller-entry
# ROMD managed controller mapping
03000000deadbeef0000000000000000,Old Pad,a:b0,platform:macOS,
# End ROMD managed controller mapping
another-controller-entry
''');

    await const Pcsx2IniConfigWriter().write(
      userDirectory: userDirectory,
      executablePath: null,
    );

    expect(
      File(userDirectory.gameControllerDatabasePath).readAsStringSync(),
      'unrelated-controller-entry\nanother-controller-entry\n',
    );
  });

  test('unterminated managed marker is preserved fail closed', () async {
    const malformed = '''
# ROMD managed controller mapping
unrelated-controller-entry
''';
    File(userDirectory.gameControllerDatabasePath)
      ..createSync(recursive: true)
      ..writeAsStringSync(malformed);

    await const Pcsx2IniConfigWriter().write(
      userDirectory: userDirectory,
      executablePath: null,
    );

    expect(
      File(userDirectory.gameControllerDatabasePath).readAsStringSync(),
      malformed,
    );
  });

  test('nested managed markers preserve the database fail closed', () async {
    const malformed = '''
# ROMD managed controller mapping
unrelated-controller-entry
# ROMD managed controller mapping
03000000deadbeef0000000000000000,Old Pad,a:b0,platform:macOS,
# End ROMD managed controller mapping
''';
    File(userDirectory.gameControllerDatabasePath)
      ..createSync(recursive: true)
      ..writeAsStringSync(malformed);

    await const Pcsx2IniConfigWriter().write(
      userDirectory: userDirectory,
      executablePath: null,
    );

    expect(
      File(userDirectory.gameControllerDatabasePath).readAsStringSync(),
      malformed,
    );
  });
}
