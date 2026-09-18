import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/emulator/domain/bios_catalog.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_manager.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_provision.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

import 'runtime_bios_resolver.dart';

/// Satisfies a RetroArch profile's requirements: the managed core via
/// [RuntimeProvisioner], BIOS files via [BiosCatalog] grants, the frontend
/// executable via [RuntimeManager] — in that order.
///
/// The executable keeps [RuntimeManager]'s resolution order as-is (managed
/// install → build-define → env var → PATH → common locations), including
/// that the managed install wins over `ROMD_RETROARCH_PATH` — pre-existing
/// semantics, deliberately unchanged here.
///
/// BIOS files are on-disk-is-truth: presence under
/// `<biosRoot>/<platform>/<fileName>` satisfies a spec, nothing is persisted
/// elsewhere. Missing files are matched to catalog listings by hash (sha1
/// preferred, md5 fallback) and downloaded through the same grant-download +
/// sha256-verify path the installer uses.
final class RetroArchDependencyResolver implements RuntimeDependencyResolver {
  RetroArchDependencyResolver({
    required RuntimeProvisioner provisioner,
    required RuntimeManager runtimeManager,
    required BiosCatalog biosCatalog,
    required Directory biosRoot,
    required DownloadClient downloadClient,
    ContentVerifier verifier = const ContentVerifier(),
    Map<String, String> coreOverridesByPlatform = const <String, String>{},
  }) : _provisioner = provisioner,
       _runtimeManager = runtimeManager,
       _biosResolver = RuntimeBiosResolver(
         biosCatalog: biosCatalog,
         downloadClient: downloadClient,
         verifier: verifier,
       ),
       _biosRoot = biosRoot,
       _coreOverridesByPlatform = coreOverridesByPlatform;

  final RuntimeProvisioner _provisioner;
  final RuntimeManager _runtimeManager;
  final RuntimeBiosResolver _biosResolver;
  final Directory _biosRoot;

  /// Build-define dev overrides keyed by platform short name; empty entries
  /// are filtered out by the caller.
  final Map<String, String> _coreOverridesByPlatform;

  @override
  Stream<DependencyProgress> resolve(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  ) async* {
    final platform = target.platformShortName.trim().toLowerCase();

    final coreRequirement = profile.coreRequirement;
    String? provisionedCorePath;
    if (coreRequirement != null) {
      // Managed provisioning always runs, even when a dev override supplies
      // the core path — the override only wins the path selection below.
      // Deliberately uncaught: thrown errors propagate to the coordinator's
      // defensive catch.
      await for (final progress in _provisioner.ensure(
        coreId: coreRequirement.coreId,
      )) {
        switch (progress) {
          case RuntimeProvisionReady(:final corePath):
            provisionedCorePath = corePath;
          case RuntimeProvisionFailed(:final message):
            yield DependencyResolutionFailed(message);
            return;
          case RuntimeProvisionDownloading(
            :final label,
            :final receivedBytes,
            :final totalBytes,
          ):
            yield DependencyDownloading(
              label: label,
              receivedBytes: receivedBytes,
              totalBytes: totalBytes,
            );
          case RuntimeProvisionUnpacking(:final label):
            yield DependencyUnpacking(label);
          case RuntimeProvisionStarted():
          case RuntimeExecutableReady():
            // A UI no-op before this resolver existed — not carried into the
            // dependency vocabulary.
            break;
        }
      }
    }

    final biosRequirement = profile.biosRequirement;
    Directory? biosDir;
    if (biosRequirement != null) {
      biosDir = Directory(p.join(_biosRoot.path, platform));
      await for (final progress in _biosResolver.resolve(
        requirement: biosRequirement,
        platformShortName: platform,
        biosDir: biosDir,
      )) {
        yield progress;
        if (progress is DependencyResolutionFailed) {
          return;
        }
      }
    }

    final resolution = await _runtimeManager.resolve(
      retroArchRuntimeDescriptor,
    );
    final executablePath = resolution.executablePath;
    if (executablePath == null) {
      yield DependencyExecutableMissing(
        resolution.diagnostics,
        executableName: profile.executableRequirement?.displayName,
      );
      return;
    }

    final corePath = _coreOverridesByPlatform[platform] ?? provisionedCorePath;

    // A provision stream that completes without a ready event leaves the core
    // unresolved; its entry is omitted and the adapter reports the missing
    // core.
    yield DependenciesReady(<ResolvedRuntimeDependency>[
      if (profile.executableRequirement case final requirement?)
        ResolvedRuntimeDependency(
          requirementId: requirement.id,
          path: executablePath,
          provenance: resolution.source == RuntimeResolutionSource.managed
              ? RuntimeDependencyProvenance.managed
              : RuntimeDependencyProvenance.external,
        ),
      if (coreRequirement != null && corePath != null)
        ResolvedRuntimeDependency(
          requirementId: coreRequirement.id,
          path: corePath,
        ),
      if (biosRequirement != null && biosDir != null)
        ResolvedRuntimeDependency(
          requirementId: biosRequirement.id,
          path: biosDir.path,
        ),
    ]);
  }
}
