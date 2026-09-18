import 'dart:ffi' show Abi;
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'package:romd_console/src/play/content/data/download_client.dart';

enum SdlNativeArchiveKind { macosDmgFramework }

final class SdlNativeArtifact {
  const SdlNativeArtifact({
    required this.url,
    required this.archiveKind,
    required this.sha256,
    required this.installRelativePath,
    required this.libraryRelativePath,
  });

  final Uri url;
  final SdlNativeArchiveKind archiveKind;
  final String sha256;
  final String installRelativePath;
  final String libraryRelativePath;

  String get installFingerprint => <String>[
    url.toString(),
    archiveKind.name,
    sha256,
    installRelativePath,
    libraryRelativePath,
  ].join('\n');
}

typedef SdlNativeArtifactResolver =
    SdlNativeArtifact? Function({required String os, required String arch});

final class SdlNativeCatalog {
  const SdlNativeCatalog();

  static const String sdl3Version = '3.4.12';

  SdlNativeArtifact? sdl3({required String os, required String arch}) {
    if (os != 'macos' || (arch != 'arm64' && arch != 'x86_64')) {
      return null;
    }
    return SdlNativeArtifact(
      url: Uri.parse(
        'https://github.com/libsdl-org/SDL/releases/download/'
        'release-$sdl3Version/SDL3-$sdl3Version.dmg',
      ),
      archiveKind: SdlNativeArchiveKind.macosDmgFramework,
      sha256:
          'c77d36d9393bb5481e38d222b75a1a63ab16274457b3d18c63fef90aaf5fc93b',
      installRelativePath: 'SDL3.framework',
      libraryRelativePath: p.join('SDL3.framework', 'SDL3'),
    );
  }
}

sealed class SdlNativeProvisionResult {
  const SdlNativeProvisionResult();
}

final class SdlNativeReady extends SdlNativeProvisionResult {
  const SdlNativeReady({required this.libraryPath});

  final String libraryPath;
}

final class SdlNativeUnavailable extends SdlNativeProvisionResult {
  const SdlNativeUnavailable(this.message);

  final String message;
}

final class SdlNativeInstallFailed extends SdlNativeProvisionResult {
  const SdlNativeInstallFailed(this.message);

  final String message;
}

final class SdlNativeInstallException implements Exception {
  const SdlNativeInstallException(this.message);

  final String message;
}

abstract interface class SdlNativeInstaller {
  Future<void> install({
    required File archive,
    required SdlNativeArtifact artifact,
    required Directory installRoot,
  });
}

final class SdlNativeDependencyProvisioner {
  const SdlNativeDependencyProvisioner({
    required SdlNativeArtifactResolver artifactResolver,
    required SdlNativeInstaller installer,
    required DownloadClient downloadClient,
    required Directory nativeRoot,
    required String operatingSystem,
    required String architecture,
  }) : _artifactResolver = artifactResolver,
       _installer = installer,
       _downloadClient = downloadClient,
       _nativeRoot = nativeRoot,
       _os = operatingSystem,
       _arch = architecture;

  final SdlNativeArtifactResolver _artifactResolver;
  final SdlNativeInstaller _installer;
  final DownloadClient _downloadClient;
  final Directory _nativeRoot;
  final String _os;
  final String _arch;

  Future<SdlNativeProvisionResult> ensureSdl3() async {
    final artifact = _artifactResolver(os: _os, arch: _arch);
    if (artifact == null) {
      return const SdlNativeUnavailable(
        "SDL3 isn't available for this platform yet.",
      );
    }

    final installRoot = Directory(p.join(_nativeRoot.path, 'sdl3'));
    final libraryPath = p.join(installRoot.path, artifact.libraryRelativePath);
    if (await _isCurrentInstall(installRoot, libraryPath, artifact)) {
      return SdlNativeReady(libraryPath: libraryPath);
    }

    final tmp = await Directory.systemTemp.createTemp('romd_sdl_dl_');
    try {
      final download = File(p.join(tmp.path, 'artifact'));
      try {
        await for (final _ in _downloadClient.download(
          url: artifact.url,
          destination: download,
        )) {}
      } on DownloadException {
        return const SdlNativeInstallFailed("Couldn't download SDL3.");
      }

      if (!await _matchesPinnedHash(download, artifact.sha256)) {
        return const SdlNativeInstallFailed(
          'The SDL3 download did not match its pinned hash.',
        );
      }

      try {
        await _installer.install(
          archive: download,
          artifact: artifact,
          installRoot: installRoot,
        );
      } on SdlNativeInstallException catch (e) {
        return SdlNativeInstallFailed(e.message);
      }
    } finally {
      if (await tmp.exists()) {
        await tmp.delete(recursive: true);
      }
    }

    if (!await File(libraryPath).exists()) {
      return const SdlNativeInstallFailed("The SDL3 install didn't complete.");
    }
    await _writeInstallMarker(installRoot, artifact);
    return SdlNativeReady(libraryPath: libraryPath);
  }

  Future<bool> _isCurrentInstall(
    Directory installRoot,
    String libraryPath,
    SdlNativeArtifact artifact,
  ) async {
    if (!await File(libraryPath).exists()) {
      return false;
    }
    final marker = File(_installMarkerPath(installRoot));
    if (!await marker.exists()) {
      return false;
    }
    return await marker.readAsString() == artifact.installFingerprint;
  }

  Future<bool> _matchesPinnedHash(File file, String expectedSha256) async {
    final digest = await sha256.bind(file.openRead()).first;
    return digest.toString().toLowerCase() == expectedSha256.toLowerCase();
  }

  Future<void> _writeInstallMarker(
    Directory installRoot,
    SdlNativeArtifact artifact,
  ) async {
    await installRoot.create(recursive: true);
    await File(
      _installMarkerPath(installRoot),
    ).writeAsString(artifact.installFingerprint);
  }

  String _installMarkerPath(Directory installRoot) =>
      p.join(installRoot.path, '.romd-native-artifact');
}

final class SdlNativeCommandResult {
  const SdlNativeCommandResult({
    required this.exitCode,
    required this.stdout,
    required this.stderr,
  });

  final int exitCode;
  final String stdout;
  final String stderr;

  bool get ok => exitCode == 0;
}

abstract interface class SdlNativeCommandRunner {
  Future<SdlNativeCommandResult> run({
    required String executable,
    required List<String> arguments,
  });
}

final class SystemSdlNativeCommandRunner implements SdlNativeCommandRunner {
  const SystemSdlNativeCommandRunner();

  @override
  Future<SdlNativeCommandResult> run({
    required String executable,
    required List<String> arguments,
  }) async {
    final result = await Process.run(
      executable,
      arguments,
      // Explicit security guarantee: native dependency commands are structured
      // arguments, never shell strings.
      // ignore: avoid_redundant_argument_values
      runInShell: false,
    );
    return SdlNativeCommandResult(
      exitCode: result.exitCode,
      stdout: result.stdout is String ? result.stdout as String : '',
      stderr: result.stderr is String ? result.stderr as String : '',
    );
  }
}

final class MacosSdlNativeInstaller implements SdlNativeInstaller {
  const MacosSdlNativeInstaller({required SdlNativeCommandRunner commandRunner})
    : _commandRunner = commandRunner;

  final SdlNativeCommandRunner _commandRunner;

  @override
  Future<void> install({
    required File archive,
    required SdlNativeArtifact artifact,
    required Directory installRoot,
  }) async {
    switch (artifact.archiveKind) {
      case SdlNativeArchiveKind.macosDmgFramework:
        await _installDmgFramework(
          archive,
          installRoot,
          artifact.installRelativePath,
        );
    }
  }

  Future<void> _installDmgFramework(
    File dmg,
    Directory installRoot,
    String installRelativePath,
  ) async {
    final attach = await _commandRunner.run(
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
      throw const SdlNativeInstallException('Could not mount the SDL3 image.');
    }
    final mount = _parseMountPoint(attach.stdout);
    if (mount == null) {
      throw const SdlNativeInstallException(
        'Could not locate the mounted SDL3 volume.',
      );
    }

    try {
      final destination = Directory(
        p.join(installRoot.path, installRelativePath),
      );
      await _deleteIfExists(destination);
      await destination.parent.create(recursive: true);
      final copy = await _commandRunner.run(
        executable: '/usr/bin/ditto',
        arguments: <String>[
          p.join(
            mount,
            'SDL3.xcframework',
            'macos-arm64_x86_64',
            'SDL3.framework',
          ),
          destination.path,
        ],
      );
      if (!copy.ok) {
        throw const SdlNativeInstallException('Could not copy SDL3.');
      }
      await _stripQuarantine(destination.path);
    } finally {
      await _commandRunner.run(
        executable: '/usr/bin/hdiutil',
        arguments: <String>['detach', mount, '-quiet'],
      );
    }
  }

  Future<void> _stripQuarantine(String path) => _commandRunner.run(
    executable: '/usr/bin/xattr',
    arguments: <String>['-dr', 'com.apple.quarantine', path],
  );

  Future<void> _deleteIfExists(FileSystemEntity entity) async {
    if (await entity.exists()) {
      await entity.delete(recursive: true);
    }
  }

  String? _parseMountPoint(String stdout) {
    for (final line in stdout.split('\n')) {
      final idx = line.indexOf('/Volumes/');
      if (idx >= 0) {
        return line.substring(idx).trim();
      }
    }
    return null;
  }
}

final class SystemSdlNativeEnvironment {
  const SystemSdlNativeEnvironment();

  String get operatingSystem => Platform.operatingSystem;

  String get architecture => switch (Abi.current()) {
    Abi.macosArm64 || Abi.linuxArm64 || Abi.windowsArm64 => 'arm64',
    Abi.macosX64 || Abi.linuxX64 || Abi.windowsX64 => 'x86_64',
    _ => 'unknown',
  };
}
