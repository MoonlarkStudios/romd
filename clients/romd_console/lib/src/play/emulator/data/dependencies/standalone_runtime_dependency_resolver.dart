import 'dart:io';

import 'package:romd_console/src/play/emulator/domain/runtime_manager.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_provision.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

import 'runtime_bios_resolver.dart';

typedef RuntimeBiosDirectory = Directory Function(ResolvedPlayTarget target);

/// Dependency resolver for standalone runtimes: an executable, plus optional
/// BIOS files. Libretro cores and managed core provisioning intentionally stay
/// in the RetroArch-specific resolver.
final class StandaloneRuntimeDependencyResolver
    implements RuntimeDependencyResolver {
  const StandaloneRuntimeDependencyResolver({
    required RuntimeDescriptor runtimeDescriptor,
    required RuntimeManager runtimeManager,
    RuntimeBiosResolver? biosResolver,
    RuntimeBiosDirectory? biosDirectory,
    RuntimeExecutableProvisioner? provisioner,
  }) : _runtimeDescriptor = runtimeDescriptor,
       _runtimeManager = runtimeManager,
       _biosResolver = biosResolver,
       _biosDirectory = biosDirectory,
       _provisioner = provisioner;

  final RuntimeDescriptor _runtimeDescriptor;
  final RuntimeManager _runtimeManager;
  final RuntimeBiosResolver? _biosResolver;
  final RuntimeBiosDirectory? _biosDirectory;
  final RuntimeExecutableProvisioner? _provisioner;

  @override
  Stream<DependencyProgress> resolve(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  ) async* {
    final executableRequirement = profile.executableRequirement;
    final provisioner = _provisioner;
    if (provisioner != null) {
      await for (final progress in provisioner.ensure()) {
        switch (progress) {
          case RuntimeProvisionStarted():
          case RuntimeExecutableReady():
          case RuntimeProvisionReady():
            break;
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
          case RuntimeProvisionFailed(:final message):
            yield DependencyResolutionFailed(message);
            return;
        }
      }
    }

    final resolution = await _runtimeManager.resolve(_runtimeDescriptor);
    final executablePath = resolution.executablePath;
    if (executablePath == null) {
      yield DependencyExecutableMissing(
        resolution.diagnostics,
        executableName: executableRequirement?.displayName,
      );
      return;
    }

    final dependencies = <ResolvedRuntimeDependency>[
      if (executableRequirement case final requirement?)
        ResolvedRuntimeDependency(
          requirementId: requirement.id,
          path: executablePath,
          provenance: resolution.source == RuntimeResolutionSource.managed
              ? RuntimeDependencyProvenance.managed
              : RuntimeDependencyProvenance.external,
        ),
    ];

    final biosRequirement = profile.biosRequirement;
    if (biosRequirement != null) {
      final biosResolver = _biosResolver;
      final biosDirectory = _biosDirectory;
      if (biosResolver == null || biosDirectory == null) {
        yield DependencyResolutionFailed(
          '${profile.displayName} needs BIOS files, but no BIOS resolver is configured.',
        );
        return;
      }

      final biosDir = biosDirectory(target);
      await for (final progress in biosResolver.resolve(
        requirement: biosRequirement,
        platformShortName: target.platformShortName.trim().toLowerCase(),
        biosDir: biosDir,
      )) {
        yield progress;
        if (progress is DependencyResolutionFailed) {
          return;
        }
      }
      dependencies.add(
        ResolvedRuntimeDependency(
          requirementId: biosRequirement.id,
          path: biosDir.path,
        ),
      );
    }

    yield DependenciesReady(dependencies);
  }
}
