import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_catalog.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_unpacker.dart';

/// Records `run` invocations and returns success; `hdiutil attach` returns a
/// canned mount line so the unpacker can parse it.
final class _RecordingRunner implements ProcessRunner {
  _RecordingRunner({
    this.createAppOnZipExtract = false,
    this.createAppOnTarExtract = false,
  });

  final bool createAppOnZipExtract;
  final bool createAppOnTarExtract;
  final List<List<String>> runs = <List<String>>[];

  @override
  Future<ProcessRunResult> run({
    required String executable,
    required List<String> arguments,
    String? workingDirectory,
    Map<String, String> environment = const <String, String>{},
  }) async {
    runs.add(<String>[executable, ...arguments]);
    final isAttach =
        executable.endsWith('hdiutil') && arguments.contains('attach');
    final isZipExtract =
        executable.endsWith('ditto') &&
        arguments.length == 4 &&
        arguments[0] == '-x' &&
        arguments[1] == '-k';
    final isTarExtract =
        executable.endsWith('tar') &&
        arguments.length == 4 &&
        arguments[0] == '-xf' &&
        arguments[2] == '-C';
    if (isZipExtract && createAppOnZipExtract) {
      Directory(
        p.join(arguments[3], 'DuckStation.app'),
      ).createSync(recursive: true);
    }
    if (isTarExtract && createAppOnTarExtract) {
      Directory(
        p.join(arguments[3], 'PCSX2-v2.6.3.app'),
      ).createSync(recursive: true);
    }
    return ProcessRunResult(
      exitCode: 0,
      stdout: isAttach
          ? '/dev/disk9s2 \t Apple_HFSX \t /Volumes/RetroArch\n'
          : '',
      stderr: '',
    );
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

  setUp(() => tmp = Directory.systemTemp.createTempSync('romd_unpack_test'));
  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  test(
    'dmgApp: mount → ditto the app → detach → strip quarantine, in order',
    () async {
      final runner = _RecordingRunner();
      final unpacker = MacosRuntimeUnpacker(processRunner: runner);
      final dmg = File(p.join(tmp.path, 'RetroArch.dmg'))
        ..writeAsBytesSync(<int>[1]);
      final dest = p.join(tmp.path, 'install', 'RetroArch.app');

      await unpacker.unpack(
        archive: dmg,
        kind: RuntimeArchiveKind.dmgApp,
        destinationPath: dest,
      );

      expect(runner.runs[0], <String>[
        '/usr/bin/hdiutil',
        'attach',
        '-nobrowse',
        '-noverify',
        '-readonly',
        dmg.path,
      ]);
      expect(runner.runs[1], <String>[
        '/usr/bin/ditto',
        p.join('/Volumes/RetroArch', 'RetroArch.app'),
        dest,
      ]);
      expect(runner.runs[2], <String>[
        '/usr/bin/hdiutil',
        'detach',
        '/Volumes/RetroArch',
        '-quiet',
      ]);
      expect(runner.runs[3], <String>[
        '/usr/bin/xattr',
        '-dr',
        'com.apple.quarantine',
        dest,
      ]);
    },
  );

  test('dmgApp copies the destination app name for Dolphin', () async {
    final runner = _RecordingRunner();
    final unpacker = MacosRuntimeUnpacker(processRunner: runner);
    final dmg = File(p.join(tmp.path, 'Dolphin.dmg'))
      ..writeAsBytesSync(<int>[1]);
    final dest = p.join(tmp.path, 'install', 'Dolphin.app');

    await unpacker.unpack(
      archive: dmg,
      kind: RuntimeArchiveKind.dmgApp,
      destinationPath: dest,
    );

    expect(runner.runs[1], <String>[
      '/usr/bin/ditto',
      p.join('/Volumes/RetroArch', 'Dolphin.app'),
      dest,
    ]);
  });

  test('dylibZip: fails clearly when the archive yields no .dylib', () async {
    final runner = _RecordingRunner();
    final unpacker = MacosRuntimeUnpacker(processRunner: runner);
    final zip = File(p.join(tmp.path, 'core.zip'))..writeAsBytesSync(<int>[1]);

    await expectLater(
      unpacker.unpack(
        archive: zip,
        kind: RuntimeArchiveKind.dylibZip,
        destinationPath: p.join(tmp.path, 'cores', 'snes9x_libretro.dylib'),
      ),
      throwsA(isA<RuntimeUnpackException>()),
    );
    // ditto extract was attempted (last arg is a random temp out-dir).
    expect(runner.runs.first.take(4), <String>[
      '/usr/bin/ditto',
      '-x',
      '-k',
      zip.path,
    ]);
  });

  test(
    'zipApp: extracts app bundle, copies it, and strips quarantine',
    () async {
      final runner = _RecordingRunner(createAppOnZipExtract: true);
      final unpacker = MacosRuntimeUnpacker(processRunner: runner);
      final zip = File(p.join(tmp.path, 'DuckStation.zip'))
        ..writeAsBytesSync(<int>[1]);
      final dest = p.join(tmp.path, 'install', 'DuckStation.app');

      await unpacker.unpack(
        archive: zip,
        kind: RuntimeArchiveKind.zipApp,
        destinationPath: dest,
      );

      expect(runner.runs[0].take(4), <String>[
        '/usr/bin/ditto',
        '-x',
        '-k',
        zip.path,
      ]);
      expect(runner.runs[1].first, '/usr/bin/ditto');
      expect(runner.runs[1].last, dest);
      expect(runner.runs[2], <String>[
        '/usr/bin/xattr',
        '-dr',
        'com.apple.quarantine',
        dest,
      ]);
    },
  );

  test(
    'tarXzApp: extracts app bundle, copies it, and strips quarantine',
    () async {
      final runner = _RecordingRunner(createAppOnTarExtract: true);
      final unpacker = MacosRuntimeUnpacker(processRunner: runner);
      final archive = File(p.join(tmp.path, 'pcsx2.tar.xz'))
        ..writeAsBytesSync(<int>[1]);
      final dest = p.join(tmp.path, 'install', 'PCSX2.app');

      await unpacker.unpack(
        archive: archive,
        kind: RuntimeArchiveKind.tarXzApp,
        destinationPath: dest,
      );

      expect(runner.runs[0].take(3), <String>[
        '/usr/bin/tar',
        '-xf',
        archive.path,
      ]);
      expect(runner.runs[1].first, '/usr/bin/ditto');
      expect(runner.runs[1].last, dest);
      expect(runner.runs[2], <String>[
        '/usr/bin/xattr',
        '-dr',
        'com.apple.quarantine',
        dest,
      ]);
    },
  );
}
