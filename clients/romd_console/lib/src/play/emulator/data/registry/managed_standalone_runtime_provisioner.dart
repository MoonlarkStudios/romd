import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_catalog.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_unpacker.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_manager.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_provision.dart';

typedef RuntimeArtifactResolver =
    RuntimeArtifact? Function({required String os, required String arch});
typedef InstalledRuntimeValidator = Future<bool> Function(String runtimePath);

/// Ensures a ROMD-managed standalone runtime executable is installed.
///
/// The provisioner only materializes files under
/// `<runtimesRoot>/<runtimeId>/`. Discovery remains owned by [RuntimeManager],
/// which already checks that managed root before env vars, PATH, and common
/// install locations.
final class ManagedStandaloneRuntimeProvisioner
    implements RuntimeExecutableProvisioner {
  ManagedStandaloneRuntimeProvisioner({
    required RuntimeDescriptor runtimeDescriptor,
    required String displayName,
    required RuntimeArtifactResolver artifactResolver,
    required DownloadClient downloadClient,
    required RuntimeUnpacker unpacker,
    required Directory runtimesRoot,
    required String operatingSystem,
    required String architecture,
    InstalledRuntimeValidator? installedRuntimeValidator,
  }) : _runtimeDescriptor = runtimeDescriptor,
       _displayName = displayName,
       _artifactResolver = artifactResolver,
       _downloads = downloadClient,
       _unpacker = unpacker,
       _runtimesRoot = runtimesRoot,
       _os = operatingSystem,
       _arch = architecture,
       _installedRuntimeValidator = installedRuntimeValidator;

  final RuntimeDescriptor _runtimeDescriptor;
  final String _displayName;
  final RuntimeArtifactResolver _artifactResolver;
  final DownloadClient _downloads;
  final RuntimeUnpacker _unpacker;
  final Directory _runtimesRoot;
  final String _os;
  final String _arch;
  final InstalledRuntimeValidator? _installedRuntimeValidator;

  @override
  Stream<RuntimeProvisionProgress> ensure() async* {
    yield const RuntimeProvisionStarted();

    final installRoot = Directory(
      p.join(_runtimesRoot.path, _runtimeDescriptor.runtimeId),
    );
    final artifact = _artifactResolver(os: _os, arch: _arch);
    final existing = await _firstExistingExecutable(installRoot);
    if (existing != null &&
        (artifact == null ||
            await _isCurrentInstall(installRoot, artifact, existing))) {
      yield RuntimeExecutableReady(executablePath: existing);
      return;
    }

    if (artifact == null) {
      yield RuntimeProvisionFailed(
        "$_displayName isn't available for this platform yet.",
      );
      return;
    }

    final tmp = await Directory.systemTemp.createTemp('romd_rt_dl_');
    try {
      final download = File(p.join(tmp.path, 'artifact'));

      yield RuntimeProvisionDownloading(label: _displayName, receivedBytes: 0);
      var downloadFailed = false;
      try {
        await for (final received in _downloads.download(
          url: artifact.url,
          destination: download,
        )) {
          yield RuntimeProvisionDownloading(
            label: _displayName,
            receivedBytes: received,
          );
        }
      } on DownloadException {
        downloadFailed = true;
      }
      if (downloadFailed) {
        yield RuntimeProvisionFailed("Couldn't download $_displayName.");
        return;
      }

      if (artifact.sha256 case final expectedSha256?) {
        final digest = await sha256.bind(download.openRead()).first;
        if (digest.toString().toLowerCase() != expectedSha256.toLowerCase()) {
          yield RuntimeProvisionFailed(
            'The downloaded $_displayName archive failed verification.',
          );
          return;
        }
      }

      yield RuntimeProvisionUnpacking(_displayName);
      String? unpackError;
      try {
        await _unpacker.unpack(
          archive: download,
          kind: artifact.kind,
          destinationPath: p.join(
            installRoot.path,
            artifact.installRelativePath,
          ),
        );
      } on RuntimeUnpackException catch (e) {
        unpackError = e.message;
      }
      if (unpackError != null) {
        yield RuntimeProvisionFailed(unpackError);
        return;
      }
    } finally {
      if (await tmp.exists()) {
        await tmp.delete(recursive: true);
      }
    }

    final installed = await _firstExistingExecutable(installRoot);
    if (installed == null) {
      yield const RuntimeProvisionFailed(
        "The runtime install didn't complete.",
      );
      return;
    }
    if (!await _isValidInstalledRuntime(installed)) {
      yield RuntimeProvisionFailed(
        'The installed $_displayName runtime failed verification.',
      );
      return;
    }
    await _writeInstallMarker(installRoot, artifact);
    yield RuntimeExecutableReady(executablePath: installed);
  }

  Future<String?> _firstExistingExecutable(Directory installRoot) async {
    for (final name in _runtimeDescriptor.executableNames) {
      final candidate = p.join(installRoot.path, name);
      if (await FileSystemEntity.type(candidate) !=
          FileSystemEntityType.notFound) {
        return candidate;
      }
    }
    return null;
  }

  Future<bool> _isCurrentInstall(
    Directory installRoot,
    RuntimeArtifact artifact,
    String executablePath,
  ) async {
    final marker = File(_installMarkerPath(installRoot));
    if (!await marker.exists()) {
      return false;
    }
    return await marker.readAsString() == artifact.installFingerprint &&
        await _isValidInstalledRuntime(executablePath);
  }

  Future<bool> _isValidInstalledRuntime(String executablePath) async =>
      await _installedRuntimeValidator?.call(executablePath) ?? true;

  Future<void> _writeInstallMarker(
    Directory installRoot,
    RuntimeArtifact artifact,
  ) async {
    await installRoot.create(recursive: true);
    await File(
      _installMarkerPath(installRoot),
    ).writeAsString(artifact.installFingerprint);
  }

  String _installMarkerPath(Directory installRoot) =>
      p.join(installRoot.path, '.romd-runtime-artifact');
}
