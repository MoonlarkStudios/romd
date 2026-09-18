import 'dart:io';

import 'package:path/path.dart' as p;

import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_catalog.dart';

final class RuntimeUnpackException implements Exception {
  const RuntimeUnpackException(this.message);
  final String message;
}

/// Unpacks a downloaded runtime artifact and places it at a destination path.
/// The OS mechanics live in the platform impl; the seam keeps the provisioner
/// unit-testable without real archives.
abstract interface class RuntimeUnpacker {
  /// Unpack [archive] (of [kind]) and place the result at [destinationPath] (an
  /// absolute `.app` dir or `.dylib` file). Throws [RuntimeUnpackException].
  Future<void> unpack({
    required File archive,
    required RuntimeArchiveKind kind,
    required String destinationPath,
  });
}

/// macOS unpacker: `.dmg` via `hdiutil` + `ditto`, `.zip` via `ditto`, with a
/// `xattr` quarantine strip (notarized builds pass Gatekeeper where available,
/// but stripping avoids a first-launch online check).
final class MacosRuntimeUnpacker implements RuntimeUnpacker {
  const MacosRuntimeUnpacker({required ProcessRunner processRunner})
    : _runner = processRunner;

  final ProcessRunner _runner;

  @override
  Future<void> unpack({
    required File archive,
    required RuntimeArchiveKind kind,
    required String destinationPath,
  }) => switch (kind) {
    RuntimeArchiveKind.dmgApp => _unpackDmgApp(archive, destinationPath),
    RuntimeArchiveKind.dylibZip => _unpackDylibZip(archive, destinationPath),
    RuntimeArchiveKind.zipApp => _unpackZipApp(archive, destinationPath),
    RuntimeArchiveKind.tarXzApp => _unpackTarXzApp(archive, destinationPath),
    RuntimeArchiveKind.sevenZipAppImage ||
    RuntimeArchiveKind.soZip => throw const RuntimeUnpackException(
      'This runtime archive is not supported on macOS.',
    ),
  };

  Future<void> _unpackDmgApp(File dmg, String destAppPath) async {
    final attach = await _runner.run(
      executable: '/usr/bin/hdiutil',
      arguments: <String>[
        'attach',
        '-nobrowse',
        '-noverify',
        '-readonly',
        dmg.path,
      ],
    );
    if (!attach.ok) {
      throw const RuntimeUnpackException('Could not mount the app image.');
    }
    final mount = _parseMountPoint(attach.stdout);
    if (mount == null) {
      throw const RuntimeUnpackException(
        'Could not locate the mounted app volume.',
      );
    }
    try {
      await _deleteIfExists(Directory(destAppPath));
      await Directory(p.dirname(destAppPath)).create(recursive: true);
      final copy = await _runner.run(
        executable: '/usr/bin/ditto',
        arguments: <String>[
          p.join(mount, p.basename(destAppPath)),
          destAppPath,
        ],
      );
      if (!copy.ok) {
        throw const RuntimeUnpackException('Could not copy the app.');
      }
    } finally {
      await _runner.run(
        executable: '/usr/bin/hdiutil',
        arguments: <String>['detach', mount, '-quiet'],
      );
    }
    await _stripQuarantine(destAppPath);
  }

  Future<void> _unpackDylibZip(File zip, String destDylibPath) async {
    final out = await Directory.systemTemp.createTemp('romd_core_unzip_');
    try {
      final unzip = await _runner.run(
        executable: '/usr/bin/ditto',
        arguments: <String>['-x', '-k', zip.path, out.path],
      );
      if (!unzip.ok) {
        throw const RuntimeUnpackException('Could not unpack the core.');
      }
      final dylibs = out
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.dylib'))
          .toList();
      if (dylibs.isEmpty) {
        throw const RuntimeUnpackException(
          'Core archive contained no library.',
        );
      }
      await Directory(p.dirname(destDylibPath)).create(recursive: true);
      await _deleteIfExists(File(destDylibPath));
      await dylibs.first.copy(destDylibPath);
      await _stripQuarantine(destDylibPath);
    } finally {
      await _deleteIfExists(out);
    }
  }

  Future<void> _unpackZipApp(File zip, String destAppPath) async {
    final out = await Directory.systemTemp.createTemp('romd_app_unzip_');
    try {
      final unzip = await _runner.run(
        executable: '/usr/bin/ditto',
        arguments: <String>['-x', '-k', zip.path, out.path],
      );
      if (!unzip.ok) {
        throw const RuntimeUnpackException('Could not unpack the app.');
      }
      await _copyFirstApp(out, destAppPath);
    } finally {
      await _deleteIfExists(out);
    }
  }

  Future<void> _unpackTarXzApp(File archive, String destAppPath) async {
    final out = await Directory.systemTemp.createTemp('romd_app_untar_');
    try {
      final unpack = await _runner.run(
        executable: '/usr/bin/tar',
        arguments: <String>['-xf', archive.path, '-C', out.path],
      );
      if (!unpack.ok) {
        throw const RuntimeUnpackException('Could not unpack the app.');
      }
      await _copyFirstApp(out, destAppPath);
    } finally {
      await _deleteIfExists(out);
    }
  }

  Future<void> _copyFirstApp(Directory unpackedRoot, String destAppPath) async {
    final apps = unpackedRoot
        .listSync(recursive: true)
        .whereType<Directory>()
        .where((d) => d.path.endsWith('.app'))
        .toList();
    if (apps.isEmpty) {
      throw const RuntimeUnpackException(
        'App archive contained no application.',
      );
    }
    await _deleteIfExists(Directory(destAppPath));
    await Directory(p.dirname(destAppPath)).create(recursive: true);
    final copy = await _runner.run(
      executable: '/usr/bin/ditto',
      arguments: <String>[apps.first.path, destAppPath],
    );
    if (!copy.ok) {
      throw const RuntimeUnpackException('Could not copy the app.');
    }
    await _stripQuarantine(destAppPath);
  }

  Future<void> _stripQuarantine(String path) => _runner.run(
    executable: '/usr/bin/xattr',
    arguments: <String>['-dr', 'com.apple.quarantine', path],
  );

  Future<void> _deleteIfExists(FileSystemEntity entity) async {
    if (await entity.exists()) {
      await entity.delete(recursive: true);
    }
  }

  String? _parseMountPoint(String hdiutilStdout) {
    for (final line in hdiutilStdout.split('\n')) {
      final idx = line.indexOf('/Volumes/');
      if (idx >= 0) {
        return line.substring(idx).trim();
      }
    }
    return null;
  }
}

enum LinuxArchiveToolKind { bsdtar, sevenZip }

typedef LinuxArchiveTool = ({String executable, LinuxArchiveToolKind kind});
typedef LinuxArchiveToolResolver = Future<LinuxArchiveTool?> Function();

/// Finds an archive tool without invoking a shell. `bsdtar` is supplied by the
/// common `libarchive-tools` package; 7-Zip is accepted as a fallback.
Future<LinuxArchiveTool?> findSystemLinuxArchiveTool() async {
  final pathDirectories = (Platform.environment['PATH'] ?? '')
      .split(':')
      .where((entry) => entry.isNotEmpty);
  for (final candidate in <({String name, LinuxArchiveToolKind kind})>[
    (name: 'bsdtar', kind: LinuxArchiveToolKind.bsdtar),
    (name: '7zz', kind: LinuxArchiveToolKind.sevenZip),
    (name: '7z', kind: LinuxArchiveToolKind.sevenZip),
  ]) {
    for (final directory in pathDirectories) {
      final executable = p.join(directory, candidate.name);
      if (await File(executable).exists()) {
        return (executable: executable, kind: candidate.kind);
      }
    }
  }
  return null;
}

/// Linux unpacker for the official RetroArch AppImage bundle and individual
/// libretro `.so.zip` core archives.
///
/// The stable frontend arrives as a 7-Zip archive, so Linux needs `bsdtar`
/// (`libarchive-tools`) or 7-Zip available. The extracted AppImage is marked
/// executable and later launched with AppImage's extract-and-run fallback,
/// avoiding a hard dependency on FUSE.
final class LinuxRuntimeUnpacker implements RuntimeUnpacker {
  const LinuxRuntimeUnpacker({
    required ProcessRunner processRunner,
    LinuxArchiveToolResolver archiveToolResolver = findSystemLinuxArchiveTool,
  }) : _runner = processRunner,
       _archiveToolResolver = archiveToolResolver;

  final ProcessRunner _runner;
  final LinuxArchiveToolResolver _archiveToolResolver;

  @override
  Future<void> unpack({
    required File archive,
    required RuntimeArchiveKind kind,
    required String destinationPath,
  }) => switch (kind) {
    RuntimeArchiveKind.sevenZipAppImage => _unpackSingleFile(
      archive: archive,
      destinationPath: destinationPath,
      expectedSuffix: '.AppImage',
      makeExecutable: true,
      missingMessage: 'The runtime archive contained no AppImage.',
    ),
    RuntimeArchiveKind.soZip => _unpackSingleFile(
      archive: archive,
      destinationPath: destinationPath,
      expectedSuffix: '.so',
      makeExecutable: false,
      missingMessage: 'The core archive contained no shared library.',
    ),
    RuntimeArchiveKind.dmgApp ||
    RuntimeArchiveKind.dylibZip ||
    RuntimeArchiveKind.zipApp ||
    RuntimeArchiveKind.tarXzApp => throw const RuntimeUnpackException(
      'This runtime archive is not supported on Linux.',
    ),
  };

  Future<void> _unpackSingleFile({
    required File archive,
    required String destinationPath,
    required String expectedSuffix,
    required bool makeExecutable,
    required String missingMessage,
  }) async {
    final tool = await _archiveToolResolver();
    if (tool == null) {
      throw const RuntimeUnpackException(
        'Linux runtime setup needs bsdtar (libarchive-tools) or 7-Zip.',
      );
    }

    final out = await Directory.systemTemp.createTemp('romd_linux_unpack_');
    try {
      final arguments = switch (tool.kind) {
        LinuxArchiveToolKind.bsdtar => <String>[
          '-xf',
          archive.path,
          '-C',
          out.path,
        ],
        LinuxArchiveToolKind.sevenZip => <String>[
          'x',
          '-y',
          '-o${out.path}',
          archive.path,
        ],
      };
      final unpack = await _runner.run(
        executable: tool.executable,
        arguments: arguments,
      );
      if (!unpack.ok) {
        throw const RuntimeUnpackException(
          'Could not unpack the Linux runtime archive.',
        );
      }

      final matches = out
          .listSync(recursive: true, followLinks: false)
          .whereType<File>()
          .where((file) => file.path.endsWith(expectedSuffix))
          .toList();
      if (matches.isEmpty) {
        throw RuntimeUnpackException(missingMessage);
      }

      final destination = File(destinationPath);
      await destination.parent.create(recursive: true);
      await _deleteIfExists(destination);
      await matches.first.copy(destination.path);

      if (makeExecutable) {
        final chmod = await _runner.run(
          executable: '/bin/chmod',
          arguments: <String>['u+x', destination.path],
        );
        if (!chmod.ok) {
          await _deleteIfExists(destination);
          throw const RuntimeUnpackException(
            'Could not make the Linux runtime executable.',
          );
        }
      }
    } finally {
      await _deleteIfExists(out);
    }
  }

  Future<void> _deleteIfExists(FileSystemEntity entity) async {
    if (await entity.exists()) {
      await entity.delete(recursive: true);
    }
  }
}
