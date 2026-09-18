import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/emulator/data/registry/managed_standalone_runtime_provisioner.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_catalog.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_unpacker.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_manager.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_provision.dart';

final class _FakeDownload implements DownloadClient {
  _FakeDownload({this.fail = false});

  final bool fail;
  int calls = 0;
  Uri? url;

  @override
  Stream<int> download({required Uri url, required File destination}) async* {
    calls++;
    this.url = url;
    if (fail) {
      throw const DownloadNetworkError();
    }
    await destination.parent.create(recursive: true);
    await destination.writeAsBytes(<int>[1, 2, 3]);
    yield 3;
  }
}

final class _FakeUnpacker implements RuntimeUnpacker {
  _FakeUnpacker({this.fail = false});

  final bool fail;
  final List<RuntimeArchiveKind> kinds = <RuntimeArchiveKind>[];
  final List<String> destinations = <String>[];

  @override
  Future<void> unpack({
    required File archive,
    required RuntimeArchiveKind kind,
    required String destinationPath,
  }) async {
    if (fail) {
      throw const RuntimeUnpackException('Could not unpack DuckStation.');
    }
    kinds.add(kind);
    destinations.add(destinationPath);
    await Directory(destinationPath).create(recursive: true);
  }
}

void main() {
  late Directory tmp;
  late Directory runtimesRoot;
  late RuntimeArtifact duckStationArtifact;

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_managed_runtime_test');
    runtimesRoot = Directory(p.join(tmp.path, 'runtimes'));
    duckStationArtifact = RuntimeArtifact(
      url: Uri.parse('https://example.invalid/duckstation.zip'),
      kind: RuntimeArchiveKind.zipApp,
      installRelativePath: 'DuckStation.app',
      sha256:
          '039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81',
    );
  });

  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  ManagedStandaloneRuntimeProvisioner build({
    required _FakeDownload download,
    required _FakeUnpacker unpacker,
    RuntimeArtifactResolver? artifactResolver,
    InstalledRuntimeValidator? installedRuntimeValidator,
  }) => ManagedStandaloneRuntimeProvisioner(
    runtimeDescriptor: duckStationRuntimeDescriptor,
    displayName: 'DuckStation',
    artifactResolver:
        artifactResolver ??
        (({required String os, required String arch}) => duckStationArtifact),
    downloadClient: download,
    unpacker: unpacker,
    runtimesRoot: runtimesRoot,
    operatingSystem: 'macos',
    architecture: 'arm64',
    installedRuntimeValidator: installedRuntimeValidator,
  );

  test('cold install downloads and unpacks the runtime app', () async {
    final download = _FakeDownload();
    final unpacker = _FakeUnpacker();

    final events = await build(
      download: download,
      unpacker: unpacker,
    ).ensure().toList();

    expect(events.first, isA<RuntimeProvisionStarted>());
    expect(events.whereType<RuntimeProvisionDownloading>(), isNotEmpty);
    expect(events.whereType<RuntimeProvisionUnpacking>(), hasLength(1));
    expect(download.calls, 1);
    expect(download.url, Uri.parse('https://example.invalid/duckstation.zip'));
    expect(unpacker.kinds, <RuntimeArchiveKind>[RuntimeArchiveKind.zipApp]);
    expect(unpacker.destinations, <String>[
      p.join(runtimesRoot.path, 'duckstation', 'DuckStation.app'),
    ]);

    final ready = events.last as RuntimeExecutableReady;
    expect(
      ready.executablePath,
      p.join(runtimesRoot.path, 'duckstation', 'DuckStation.app'),
    );
    expect(Directory(ready.executablePath).existsSync(), isTrue);
    expect(
      File(
        p.join(runtimesRoot.path, 'duckstation', '.romd-runtime-artifact'),
      ).readAsStringSync(),
      duckStationArtifact.installFingerprint,
    );
  });

  test(
    'idempotent: current already-installed runtime skips download',
    () async {
      Directory(
        p.join(runtimesRoot.path, 'duckstation', 'DuckStation.app'),
      ).createSync(recursive: true);
      File(p.join(runtimesRoot.path, 'duckstation', '.romd-runtime-artifact'))
        ..createSync(recursive: true)
        ..writeAsStringSync(duckStationArtifact.installFingerprint);
      final download = _FakeDownload();

      final events = await build(
        download: download,
        unpacker: _FakeUnpacker(),
      ).ensure().toList();

      expect(download.calls, 0);
      expect(events.whereType<RuntimeProvisionDownloading>(), isEmpty);
      expect(events.last, isA<RuntimeExecutableReady>());
    },
  );

  test('markerless already-installed runtime is replaced', () async {
    Directory(
      p.join(runtimesRoot.path, 'duckstation', 'DuckStation.app'),
    ).createSync(recursive: true);
    final download = _FakeDownload();
    final unpacker = _FakeUnpacker();

    final events = await build(
      download: download,
      unpacker: unpacker,
    ).ensure().toList();

    expect(download.calls, 1);
    expect(unpacker.destinations, <String>[
      p.join(runtimesRoot.path, 'duckstation', 'DuckStation.app'),
    ]);
    expect(events.last, isA<RuntimeExecutableReady>());
  });

  test('stale managed runtime marker is replaced', () async {
    Directory(
      p.join(runtimesRoot.path, 'duckstation', 'DuckStation.app'),
    ).createSync(recursive: true);
    File(p.join(runtimesRoot.path, 'duckstation', '.romd-runtime-artifact'))
      ..createSync(recursive: true)
      ..writeAsStringSync('old-release');
    final download = _FakeDownload();

    final events = await build(
      download: download,
      unpacker: _FakeUnpacker(),
    ).ensure().toList();

    expect(download.calls, 1);
    expect(events.last, isA<RuntimeExecutableReady>());
    expect(
      File(
        p.join(runtimesRoot.path, 'duckstation', '.romd-runtime-artifact'),
      ).readAsStringSync(),
      duckStationArtifact.installFingerprint,
    );
  });

  test(
    'a mutated installed runtime is replaced before it is trusted',
    () async {
      final installedPath = p.join(
        runtimesRoot.path,
        'duckstation',
        'DuckStation.app',
      );
      Directory(installedPath).createSync(recursive: true);
      File(p.join(runtimesRoot.path, 'duckstation', '.romd-runtime-artifact'))
        ..createSync(recursive: true)
        ..writeAsStringSync(duckStationArtifact.installFingerprint);
      final download = _FakeDownload();
      var validationCalls = 0;

      final events = await build(
        download: download,
        unpacker: _FakeUnpacker(),
        installedRuntimeValidator: (path) async {
          expect(path, installedPath);
          validationCalls++;
          return validationCalls > 1;
        },
      ).ensure().toList();

      expect(validationCalls, 2);
      expect(download.calls, 1);
      expect(events.last, isA<RuntimeExecutableReady>());
    },
  );

  test('a newly installed runtime must pass executable verification', () async {
    final events = await build(
      download: _FakeDownload(),
      unpacker: _FakeUnpacker(),
      installedRuntimeValidator: (_) async => false,
    ).ensure().toList();

    expect(events.last, isA<RuntimeProvisionFailed>());
    expect(
      (events.last as RuntimeProvisionFailed).message,
      contains('verification'),
    );
    expect(
      File(
        p.join(runtimesRoot.path, 'duckstation', '.romd-runtime-artifact'),
      ).existsSync(),
      isFalse,
    );
  });

  test('unsupported platform fails without downloading', () async {
    final download = _FakeDownload();

    final events = await build(
      download: download,
      unpacker: _FakeUnpacker(),
      artifactResolver: ({required String os, required String arch}) => null,
    ).ensure().toList();

    expect(download.calls, 0);
    expect(events.last, isA<RuntimeProvisionFailed>());
  });

  test('download failure surfaces as RuntimeProvisionFailed', () async {
    final events = await build(
      download: _FakeDownload(fail: true),
      unpacker: _FakeUnpacker(),
    ).ensure().toList();

    expect(events.last, isA<RuntimeProvisionFailed>());
  });

  test('digest mismatch fails before unpacking or marker write', () async {
    duckStationArtifact = RuntimeArtifact(
      url: duckStationArtifact.url,
      kind: duckStationArtifact.kind,
      installRelativePath: duckStationArtifact.installRelativePath,
      sha256: '0' * 64,
    );
    final unpacker = _FakeUnpacker();

    final events = await build(
      download: _FakeDownload(),
      unpacker: unpacker,
    ).ensure().toList();

    expect(
      (events.last as RuntimeProvisionFailed).message,
      contains('verification'),
    );
    expect(unpacker.destinations, isEmpty);
    expect(
      File(
        p.join(runtimesRoot.path, 'duckstation', '.romd-runtime-artifact'),
      ).existsSync(),
      isFalse,
    );
  });

  test('unpack failure surfaces as RuntimeProvisionFailed', () async {
    final events = await build(
      download: _FakeDownload(),
      unpacker: _FakeUnpacker(fail: true),
    ).ensure().toList();

    expect((events.last as RuntimeProvisionFailed).message, contains('unpack'));
  });
}
