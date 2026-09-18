import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/controllers/data/sdl/sdl_native_dependency.dart';

final class _FakeDownload implements DownloadClient {
  _FakeDownload({required this.bytes});

  final List<int> bytes;
  int calls = 0;
  Uri? url;

  @override
  Stream<int> download({required Uri url, required File destination}) async* {
    calls++;
    this.url = url;
    await destination.parent.create(recursive: true);
    await destination.writeAsBytes(bytes);
    yield bytes.length;
  }
}

final class _FakeInstaller implements SdlNativeInstaller {
  final installed = <String>[];

  @override
  Future<void> install({
    required File archive,
    required SdlNativeArtifact artifact,
    required Directory installRoot,
  }) async {
    installed.add(archive.path);
    await File(
      p.join(installRoot.path, artifact.libraryRelativePath),
    ).create(recursive: true);
  }
}

final class _RecordingCommandRunner implements SdlNativeCommandRunner {
  final runs = <List<String>>[];

  @override
  Future<SdlNativeCommandResult> run({
    required String executable,
    required List<String> arguments,
  }) async {
    runs.add(<String>[executable, ...arguments]);
    final attach =
        executable.endsWith('hdiutil') && arguments.contains('attach');
    return SdlNativeCommandResult(
      exitCode: 0,
      stdout: attach ? '/dev/disk9s1\tApple_HFS\t/Volumes/SDL3\n' : '',
      stderr: '',
    );
  }
}

void main() {
  late Directory tmp;
  late Directory nativeRoot;
  late List<int> artifactBytes;
  late SdlNativeArtifact artifact;

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_sdl_native_test');
    nativeRoot = Directory(p.join(tmp.path, 'native'));
    artifactBytes = utf8.encode('sdl3 artifact');
    artifact = SdlNativeArtifact(
      url: Uri.parse('https://example.invalid/SDL3.dmg'),
      archiveKind: SdlNativeArchiveKind.macosDmgFramework,
      sha256: sha256.convert(artifactBytes).toString(),
      installRelativePath: 'SDL3.framework',
      libraryRelativePath: p.join('SDL3.framework', 'SDL3'),
    );
  });

  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  SdlNativeDependencyProvisioner build({
    required _FakeDownload download,
    required SdlNativeInstaller installer,
    SdlNativeArtifactResolver? artifactResolver,
  }) => SdlNativeDependencyProvisioner(
    artifactResolver:
        artifactResolver ??
        (({required String os, required String arch}) => artifact),
    installer: installer,
    downloadClient: download,
    nativeRoot: nativeRoot,
    operatingSystem: 'macos',
    architecture: 'arm64',
  );

  test('catalog pins macOS SDL3 with a required hash', () {
    final resolved = const SdlNativeCatalog().sdl3(os: 'macos', arch: 'arm64');

    expect(resolved, isNotNull);
    expect(resolved!.url.toString(), contains('SDL3-3.4.12.dmg'));
    expect(resolved.archiveKind, SdlNativeArchiveKind.macosDmgFramework);
    expect(resolved.sha256, hasLength(64));
    expect(resolved.libraryRelativePath, p.join('SDL3.framework', 'SDL3'));
    expect(const SdlNativeCatalog().sdl3(os: 'linux', arch: 'arm64'), isNull);
  });

  test('cold install verifies hash, installs, and writes marker', () async {
    final download = _FakeDownload(bytes: artifactBytes);
    final installer = _FakeInstaller();

    final result = await build(
      download: download,
      installer: installer,
    ).ensureSdl3();

    expect(download.calls, 1);
    expect(download.url, artifact.url);
    expect(installer.installed, hasLength(1));
    expect(result, isA<SdlNativeReady>());
    expect(
      (result as SdlNativeReady).libraryPath,
      p.join(nativeRoot.path, 'sdl3', artifact.libraryRelativePath),
    );
    expect(
      File(
        p.join(nativeRoot.path, 'sdl3', '.romd-native-artifact'),
      ).readAsStringSync(),
      artifact.installFingerprint,
    );
  });

  test('current install skips download and install', () async {
    final installRoot = Directory(p.join(nativeRoot.path, 'sdl3'))
      ..createSync(recursive: true);
    File(
      p.join(installRoot.path, artifact.libraryRelativePath),
    ).createSync(recursive: true);
    File(
      p.join(installRoot.path, '.romd-native-artifact'),
    ).writeAsStringSync(artifact.installFingerprint);
    final download = _FakeDownload(bytes: artifactBytes);
    final installer = _FakeInstaller();

    final result = await build(
      download: download,
      installer: installer,
    ).ensureSdl3();

    expect(result, isA<SdlNativeReady>());
    expect(download.calls, 0);
    expect(installer.installed, isEmpty);
  });

  test('hash mismatch fails before install', () async {
    final download = _FakeDownload(bytes: utf8.encode('tampered'));
    final installer = _FakeInstaller();

    final result = await build(
      download: download,
      installer: installer,
    ).ensureSdl3();

    expect(result, isA<SdlNativeInstallFailed>());
    expect((result as SdlNativeInstallFailed).message, contains('hash'));
    expect(installer.installed, isEmpty);
  });

  test('unsupported platform fails before download', () async {
    final download = _FakeDownload(bytes: artifactBytes);

    final result = await build(
      download: download,
      installer: _FakeInstaller(),
      artifactResolver: ({required String os, required String arch}) => null,
    ).ensureSdl3();

    expect(result, isA<SdlNativeUnavailable>());
    expect(download.calls, 0);
  });

  test(
    'macOS installer mounts dmg, copies framework, strips quarantine',
    () async {
      final runner = _RecordingCommandRunner();
      final installer = MacosSdlNativeInstaller(commandRunner: runner);
      final archive = File(p.join(tmp.path, 'SDL3.dmg'))
        ..writeAsBytesSync(<int>[1]);
      final installRoot = Directory(p.join(tmp.path, 'install'));

      await installer.install(
        archive: archive,
        artifact: artifact,
        installRoot: installRoot,
      );

      expect(runner.runs[0], <String>[
        '/usr/bin/hdiutil',
        'attach',
        '-nobrowse',
        '-noverify',
        '-readonly',
        archive.path,
      ]);
      expect(runner.runs[1], <String>[
        '/usr/bin/ditto',
        p.join(
          '/Volumes/SDL3',
          'SDL3.xcframework',
          'macos-arm64_x86_64',
          'SDL3.framework',
        ),
        p.join(installRoot.path, 'SDL3.framework'),
      ]);
      expect(runner.runs[2], <String>[
        '/usr/bin/xattr',
        '-dr',
        'com.apple.quarantine',
        p.join(installRoot.path, 'SDL3.framework'),
      ]);
      expect(runner.runs[3], <String>[
        '/usr/bin/hdiutil',
        'detach',
        '/Volumes/SDL3',
        '-quiet',
      ]);
    },
  );
}
