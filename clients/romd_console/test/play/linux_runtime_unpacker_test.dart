import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_catalog.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_unpacker.dart';

final class _RecordingRunner implements ProcessRunner {
  _RecordingRunner({this.extractedFileName, this.failExtraction = false});

  final String? extractedFileName;
  final bool failExtraction;
  final List<List<String>> runs = <List<String>>[];

  @override
  Future<ProcessRunResult> run({
    required String executable,
    required List<String> arguments,
    String? workingDirectory,
    Map<String, String> environment = const <String, String>{},
  }) async {
    runs.add(<String>[executable, ...arguments]);
    if (executable == '/usr/bin/bsdtar') {
      if (failExtraction) {
        return const ProcessRunResult(
          exitCode: 2,
          stdout: '',
          stderr: 'bad archive',
        );
      }
      final outputIndex = arguments.indexOf('-C') + 1;
      final fileName = extractedFileName;
      if (outputIndex > 0 && fileName != null) {
        final extracted = File(
          p.join(arguments[outputIndex], 'nested', fileName),
        );
        await extracted.parent.create(recursive: true);
        await extracted.writeAsString('runtime');
      }
    }
    return const ProcessRunResult(exitCode: 0, stdout: '', stderr: '');
  }

  @override
  Future<RunningProcess> start({
    required String executable,
    required List<String> arguments,
    required String workingDirectory,
    required Map<String, String> environment,
  }) => throw UnimplementedError();
}

Future<LinuxArchiveTool?> _bsdtar() async =>
    (executable: '/usr/bin/bsdtar', kind: LinuxArchiveToolKind.bsdtar);

Future<LinuxArchiveTool?> _missingTool() async => null;

void main() {
  late Directory tmp;

  setUp(() => tmp = Directory.systemTemp.createTempSync('romd_linux_unpack'));
  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  test('extracts the AppImage and marks it executable', () async {
    final runner = _RecordingRunner(
      extractedFileName: 'RetroArch-Linux-x86_64.AppImage',
    );
    final unpacker = LinuxRuntimeUnpacker(
      processRunner: runner,
      archiveToolResolver: _bsdtar,
    );
    final archive = File(p.join(tmp.path, 'RetroArch.7z'))
      ..writeAsBytesSync(<int>[1]);
    final destination = p.join(
      tmp.path,
      'runtime',
      'RetroArch-Linux-x86_64.AppImage',
    );

    await unpacker.unpack(
      archive: archive,
      kind: RuntimeArchiveKind.sevenZipAppImage,
      destinationPath: destination,
    );

    expect(runner.runs.first.take(4), <String>[
      '/usr/bin/bsdtar',
      '-xf',
      archive.path,
      '-C',
    ]);
    expect(runner.runs.last, <String>['/bin/chmod', 'u+x', destination]);
    expect(File(destination).existsSync(), isTrue);
  });

  test('extracts one libretro .so without chmod', () async {
    final runner = _RecordingRunner(extractedFileName: 'snes9x_libretro.so');
    final unpacker = LinuxRuntimeUnpacker(
      processRunner: runner,
      archiveToolResolver: _bsdtar,
    );
    final archive = File(p.join(tmp.path, 'core.zip'))
      ..writeAsBytesSync(<int>[1]);
    final destination = p.join(tmp.path, 'cores', 'snes9x_libretro.so');

    await unpacker.unpack(
      archive: archive,
      kind: RuntimeArchiveKind.soZip,
      destinationPath: destination,
    );

    expect(runner.runs, hasLength(1));
    expect(File(destination).existsSync(), isTrue);
  });

  test('fails clearly when no supported archive tool is installed', () async {
    final unpacker = LinuxRuntimeUnpacker(
      processRunner: _RecordingRunner(),
      archiveToolResolver: _missingTool,
    );

    await expectLater(
      unpacker.unpack(
        archive: File(p.join(tmp.path, 'RetroArch.7z')),
        kind: RuntimeArchiveKind.sevenZipAppImage,
        destinationPath: p.join(tmp.path, 'RetroArch.AppImage'),
      ),
      throwsA(
        isA<RuntimeUnpackException>().having(
          (error) => error.message,
          'message',
          contains('libarchive-tools'),
        ),
      ),
    );
  });

  test('failed extraction does not leave a destination', () async {
    final runner = _RecordingRunner(failExtraction: true);
    final unpacker = LinuxRuntimeUnpacker(
      processRunner: runner,
      archiveToolResolver: _bsdtar,
    );
    final destination = p.join(tmp.path, 'RetroArch.AppImage');

    await expectLater(
      unpacker.unpack(
        archive: File(p.join(tmp.path, 'RetroArch.7z')),
        kind: RuntimeArchiveKind.sevenZipAppImage,
        destinationPath: destination,
      ),
      throwsA(isA<RuntimeUnpackException>()),
    );
    expect(File(destination).existsSync(), isFalse);
  });
}
