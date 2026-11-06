import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_catalog.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_unpacker.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_provision.dart';

typedef _Step = ({
  RuntimeArtifact artifact,
  String label,
  bool writesFrontendMarker,
});
typedef RetroArchArtifactResolver =
    RuntimeArtifact? Function({required String os, required String arch});
typedef RetroArchCoreArtifactResolver =
    RuntimeArtifact? Function({
      required String coreId,
      required String os,
      required String arch,
    });

/// Ensures a ROMD-managed RetroArch + libretro core are installed, downloading
/// from the catalog (libretro buildbot) on first use. Idempotent: present
/// artifacts are skipped. Installs under `<runtimesRoot>/retroarch/`.
final class RetroArchProvisioner implements RuntimeProvisioner {
  RetroArchProvisioner({
    required DownloadClient downloadClient,
    required RuntimeUnpacker unpacker,
    required RuntimeCatalog catalog,
    required Directory runtimesRoot,
    required String operatingSystem,
    required String architecture,
    RetroArchArtifactResolver? frontendArtifactResolver,
    RetroArchCoreArtifactResolver? coreArtifactResolver,
  }) : _downloads = downloadClient,
       _unpacker = unpacker,
       _runtimesRoot = runtimesRoot,
       _os = operatingSystem,
       _arch = architecture,
       _frontendArtifactResolver =
           frontendArtifactResolver ?? catalog.retroArch,
       _coreArtifactResolver = coreArtifactResolver ?? catalog.core;

  final DownloadClient _downloads;
  final RuntimeUnpacker _unpacker;
  final Directory _runtimesRoot;
  final String _os;
  final String _arch;
  final RetroArchArtifactResolver _frontendArtifactResolver;
  final RetroArchCoreArtifactResolver _coreArtifactResolver;

  static const String _frontendMarkerName = '.romd-retroarch-frontend-artifact';

  @override
  Stream<RuntimeProvisionProgress> ensure({required String coreId}) async* {
    yield const RuntimeProvisionStarted();

    final installRoot = Directory(p.join(_runtimesRoot.path, 'retroarch'));
    final frontendArtifact = _frontendArtifactResolver(os: _os, arch: _arch);
    if (frontendArtifact == null) {
      yield const RuntimeProvisionFailed(
        "RetroArch isn't available for this platform yet.",
      );
      return;
    }
    final coreArtifact = _coreArtifactResolver(
      coreId: coreId,
      os: _os,
      arch: _arch,
    );
    if (coreArtifact == null) {
      yield const RuntimeProvisionFailed(
        "This core isn't available for this platform yet.",
      );
      return;
    }

    final retroArchPath = p.join(
      installRoot.path,
      frontendArtifact.installRelativePath,
    );
    final corePath = p.join(installRoot.path, coreArtifact.installRelativePath);

    // Resolve missing cores and any frontend whose provenance is absent or
    // stale. An app bundle by itself is not evidence of the catalog artifact.
    final steps = <_Step>[];
    if (!await _entityExists(retroArchPath) ||
        !await _isCurrentFrontend(installRoot, frontendArtifact)) {
      steps.add((
        artifact: frontendArtifact,
        label: 'RetroArch',
        writesFrontendMarker: true,
      ));
    }
    if (!await File(corePath).exists()) {
      steps.add((
        artifact: coreArtifact,
        label: '$coreId core',
        writesFrontendMarker: false,
      ));
    }

    for (final step in steps) {
      final tmp = await Directory.systemTemp.createTemp('romd_rt_dl_');
      try {
        final download = File(p.join(tmp.path, 'artifact'));

        yield RuntimeProvisionDownloading(label: step.label, receivedBytes: 0);
        var downloadFailed = false;
        try {
          await for (final received in _downloads.download(
            url: step.artifact.url,
            destination: download,
          )) {
            yield RuntimeProvisionDownloading(
              label: step.label,
              receivedBytes: received,
            );
          }
        } on DownloadException {
          downloadFailed = true;
        }
        if (downloadFailed) {
          yield RuntimeProvisionFailed("Couldn't download ${step.label}.");
          return;
        }

        if (step.artifact.sha256 case final expectedSha256?) {
          final digest = await sha256.bind(download.openRead()).first;
          if (digest.toString().toLowerCase() != expectedSha256.toLowerCase()) {
            yield RuntimeProvisionFailed(
              'The downloaded ${step.label} archive failed verification.',
            );
            return;
          }
        }

        yield RuntimeProvisionUnpacking(step.label);
        String? unpackError;
        try {
          await _unpacker.unpack(
            archive: download,
            kind: step.artifact.kind,
            destinationPath: p.join(
              installRoot.path,
              step.artifact.installRelativePath,
            ),
          );
        } on RuntimeUnpackException catch (e) {
          unpackError = e.message;
        }
        if (unpackError != null) {
          yield RuntimeProvisionFailed(unpackError);
          return;
        }
        if (step.writesFrontendMarker) {
          await _writeFrontendMarker(installRoot, step.artifact);
        }
      } finally {
        if (await tmp.exists()) {
          await tmp.delete(recursive: true);
        }
      }
    }

    if (!await _entityExists(retroArchPath) || !await File(corePath).exists()) {
      yield const RuntimeProvisionFailed(
        "The runtime install didn't complete.",
      );
      return;
    }
    yield RuntimeProvisionReady(
      retroArchPath: retroArchPath,
      corePath: corePath,
    );
  }

  Future<bool> _isCurrentFrontend(
    Directory installRoot,
    RuntimeArtifact artifact,
  ) async {
    final marker = File(p.join(installRoot.path, _frontendMarkerName));
    return await marker.exists() &&
        await marker.readAsString() == artifact.installFingerprint;
  }

  Future<void> _writeFrontendMarker(
    Directory installRoot,
    RuntimeArtifact artifact,
  ) async {
    await installRoot.create(recursive: true);
    await File(
      p.join(installRoot.path, _frontendMarkerName),
    ).writeAsString(artifact.installFingerprint);
  }

  Future<bool> _entityExists(String path) async =>
      await FileSystemEntity.type(path, followLinks: false) !=
      FileSystemEntityType.notFound;
}
