import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/emulator/data/registry/retroarch_provisioner.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_catalog.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_unpacker.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_provision.dart';

final class _FakeDownload implements DownloadClient {
  _FakeDownload({this.fail = false});
  final bool fail;
  int calls = 0;

  @override
  Stream<int> download({required Uri url, required File destination}) async* {
    calls++;
    if (fail) {
      throw const DownloadNetworkError();
    }
    await destination.parent.create(recursive: true);
    await destination.writeAsBytes(<int>[1, 2, 3]);
    yield 3;
  }
}

/// Simulates placement so the provisioner's structural validation passes.
final class _FakeUnpacker implements RuntimeUnpacker {
  final List<RuntimeArchiveKind> unpacked = <RuntimeArchiveKind>[];

  @override
  Future<void> unpack({
    required File archive,
    required RuntimeArchiveKind kind,
    required String destinationPath,
  }) async {
    unpacked.add(kind);
    if (kind == RuntimeArchiveKind.dmgApp) {
      await Directory(destinationPath).create(recursive: true);
    } else {
      await Directory(p.dirname(destinationPath)).create(recursive: true);
      await File(destinationPath).writeAsString('runtime');
    }
  }
}

void main() {
  final frontendArtifact = RuntimeArtifact(
    url: Uri.parse('https://example.invalid/retroarch.dmg'),
    kind: RuntimeArchiveKind.dmgApp,
    installRelativePath: 'RetroArch.app',
    sha256: '039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81',
  );
  final coreArtifact = RuntimeArtifact(
    url: Uri.parse('https://example.invalid/snes9x.zip'),
    kind: RuntimeArchiveKind.dylibZip,
    installRelativePath: p.join('cores', 'snes9x_libretro.dylib'),
  );
  final linuxFrontendArtifact = RuntimeArtifact(
    url: Uri.parse('https://example.invalid/retroarch.7z'),
    kind: RuntimeArchiveKind.sevenZipAppImage,
    installRelativePath: 'RetroArch-Linux-x86_64.AppImage',
    sha256: '039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81',
  );
  final linuxCoreArtifact = RuntimeArtifact(
    url: Uri.parse('https://example.invalid/snes9x-linux.zip'),
    kind: RuntimeArchiveKind.soZip,
    installRelativePath: p.join('cores', 'snes9x_libretro.so'),
  );
  late Directory tmp;
  late Directory runtimesRoot;

  RetroArchProvisioner build(
    _FakeDownload download,
    _FakeUnpacker unpacker, {
    String os = 'macos',
  }) => RetroArchProvisioner(
    downloadClient: download,
    unpacker: unpacker,
    catalog: const RuntimeCatalog(),
    runtimesRoot: runtimesRoot,
    operatingSystem: os,
    architecture: os == 'linux' ? 'x86_64' : 'arm64',
    frontendArtifactResolver: ({required String os, required String arch}) =>
        switch (os) {
          'macos' => frontendArtifact,
          'linux' when arch == 'x86_64' => linuxFrontendArtifact,
          _ => null,
        },
    coreArtifactResolver:
        ({required String coreId, required String os, required String arch}) =>
            switch (os) {
              'macos' => coreArtifact,
              'linux' when arch == 'x86_64' => linuxCoreArtifact,
              _ => null,
            },
  );

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_prov_test');
    runtimesRoot = Directory(p.join(tmp.path, 'runtimes'));
  });

  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  test('cold install downloads + unpacks RetroArch then the core', () async {
    final download = _FakeDownload();
    final unpacker = _FakeUnpacker();

    final events = await build(
      download,
      unpacker,
    ).ensure(coreId: 'snes9x').toList();

    expect(events.first, isA<RuntimeProvisionStarted>());
    expect(events.whereType<RuntimeProvisionDownloading>(), isNotEmpty);
    expect(events.whereType<RuntimeProvisionUnpacking>().length, 2);
    expect(download.calls, 2);
    expect(unpacker.unpacked, <RuntimeArchiveKind>[
      RuntimeArchiveKind.dmgApp,
      RuntimeArchiveKind.dylibZip,
    ]);

    final ready = events.last as RuntimeProvisionReady;
    expect(
      ready.retroArchPath,
      p.join(runtimesRoot.path, 'retroarch', 'RetroArch.app'),
    );
    expect(
      ready.corePath,
      p.join(runtimesRoot.path, 'retroarch', 'cores', 'snes9x_libretro.dylib'),
    );
    expect(Directory(ready.retroArchPath).existsSync(), isTrue);
    expect(File(ready.corePath).existsSync(), isTrue);
    expect(
      File(
        p.join(
          runtimesRoot.path,
          'retroarch',
          '.romd-retroarch-frontend-artifact',
        ),
      ).readAsStringSync(),
      frontendArtifact.installFingerprint,
    );
  });

  test('idempotent: already-installed skips downloads', () async {
    // Pre-place both artifacts.
    final install = Directory(p.join(runtimesRoot.path, 'retroarch'));
    Directory(
      p.join(install.path, 'RetroArch.app'),
    ).createSync(recursive: true);
    File(p.join(install.path, '.romd-retroarch-frontend-artifact'))
      ..createSync(recursive: true)
      ..writeAsStringSync(frontendArtifact.installFingerprint);
    File(p.join(install.path, 'cores', 'snes9x_libretro.dylib'))
      ..createSync(recursive: true)
      ..writeAsStringSync('dylib');

    final download = _FakeDownload();
    final events = await build(
      download,
      _FakeUnpacker(),
    ).ensure(coreId: 'snes9x').toList();

    expect(download.calls, 0);
    expect(events.whereType<RuntimeProvisionDownloading>(), isEmpty);
    expect(events.last, isA<RuntimeProvisionReady>());
  });

  test('Linux install uses AppImage and .so artifact paths', () async {
    final download = _FakeDownload();
    final unpacker = _FakeUnpacker();

    final events = await build(
      download,
      unpacker,
      os: 'linux',
    ).ensure(coreId: 'snes9x').toList();

    expect(download.calls, 2);
    expect(unpacker.unpacked, <RuntimeArchiveKind>[
      RuntimeArchiveKind.sevenZipAppImage,
      RuntimeArchiveKind.soZip,
    ]);
    final ready = events.last as RuntimeProvisionReady;
    expect(
      ready.retroArchPath,
      p.join(runtimesRoot.path, 'retroarch', 'RetroArch-Linux-x86_64.AppImage'),
    );
    expect(
      ready.corePath,
      p.join(runtimesRoot.path, 'retroarch', 'cores', 'snes9x_libretro.so'),
    );
    expect(File(ready.retroArchPath).existsSync(), isTrue);
    expect(File(ready.corePath).existsSync(), isTrue);
  });

  test('markerless installed frontend is replaced', () async {
    final install = Directory(p.join(runtimesRoot.path, 'retroarch'));
    Directory(
      p.join(install.path, 'RetroArch.app'),
    ).createSync(recursive: true);
    File(p.join(install.path, 'cores', 'snes9x_libretro.dylib'))
      ..createSync(recursive: true)
      ..writeAsStringSync('dylib');

    final download = _FakeDownload();
    final events = await build(
      download,
      _FakeUnpacker(),
    ).ensure(coreId: 'snes9x').toList();

    expect(download.calls, 1);
    expect(events.last, isA<RuntimeProvisionReady>());
    expect(
      File(
        p.join(install.path, '.romd-retroarch-frontend-artifact'),
      ).readAsStringSync(),
      frontendArtifact.installFingerprint,
    );
  });

  test('stale installed frontend marker is replaced', () async {
    final install = Directory(p.join(runtimesRoot.path, 'retroarch'));
    Directory(
      p.join(install.path, 'RetroArch.app'),
    ).createSync(recursive: true);
    File(p.join(install.path, '.romd-retroarch-frontend-artifact'))
      ..createSync(recursive: true)
      ..writeAsStringSync('old-release');
    File(p.join(install.path, 'cores', 'snes9x_libretro.dylib'))
      ..createSync(recursive: true)
      ..writeAsStringSync('dylib');

    final download = _FakeDownload();
    final events = await build(
      download,
      _FakeUnpacker(),
    ).ensure(coreId: 'snes9x').toList();

    expect(download.calls, 1);
    expect(events.last, isA<RuntimeProvisionReady>());
  });

  test('frontend digest mismatch fails before unpacking or marker', () async {
    final mismatchedArtifact = RuntimeArtifact(
      url: frontendArtifact.url,
      kind: frontendArtifact.kind,
      installRelativePath: frontendArtifact.installRelativePath,
      sha256: '0' * 64,
    );
    final download = _FakeDownload();
    final unpacker = _FakeUnpacker();
    final provisioner = RetroArchProvisioner(
      downloadClient: download,
      unpacker: unpacker,
      catalog: const RuntimeCatalog(),
      runtimesRoot: runtimesRoot,
      operatingSystem: 'macos',
      architecture: 'arm64',
      frontendArtifactResolver: ({required String os, required String arch}) =>
          mismatchedArtifact,
      coreArtifactResolver:
          ({
            required String coreId,
            required String os,
            required String arch,
          }) => coreArtifact,
    );

    final events = await provisioner.ensure(coreId: 'snes9x').toList();

    expect(events.last, isA<RuntimeProvisionFailed>());
    expect(unpacker.unpacked, isEmpty);
    expect(
      File(
        p.join(
          runtimesRoot.path,
          'retroarch',
          '.romd-retroarch-frontend-artifact',
        ),
      ).existsSync(),
      isFalse,
    );
  });

  test('download failure surfaces as RuntimeProvisionFailed', () async {
    final events = await build(
      _FakeDownload(fail: true),
      _FakeUnpacker(),
    ).ensure(coreId: 'snes9x').toList();
    expect(events.last, isA<RuntimeProvisionFailed>());
  });

  test('unsupported platform fails without downloading', () async {
    final download = _FakeDownload();
    final events = await build(
      download,
      _FakeUnpacker(),
      os: 'windows',
    ).ensure(coreId: 'snes9x').toList();
    expect(download.calls, 0);
    expect(events.last, isA<RuntimeProvisionFailed>());
  });
}
