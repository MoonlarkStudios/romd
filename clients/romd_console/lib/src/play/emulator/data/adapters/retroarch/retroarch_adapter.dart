import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/adapters/supervised_process_launch_session.dart';
import 'package:romd_console/src/play/emulator/data/registry/runtime_executable.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_adapter.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';

import 'retroarch_config_writer.dart';

/// Runs cartridge content through **RetroArch** with a libretro core.
///
/// RetroArch is a real CLI citizen: `retroarch -L <core> <rom> --fullscreen`
/// reads the ROM from `argv`, auto-loads it, and the process stays alive until
/// the user quits — so launching the binary directly gives clean supervision
/// (no macOS `open` document dance). On macOS the resolved `.app` bundle is
/// mapped to its inner executable.
///
/// Both the frontend executable and the libretro core (e.g.
/// `snes9x_libretro.dylib`) arrive through the plan's resolved dependencies —
/// the coordinator's dependency resolver satisfies the profile's requirements
/// (managed provisioning, discovery, or a dev build-define override).
final class RetroArchAdapter implements RuntimeAdapter {
  const RetroArchAdapter({
    ProcessRunner processRunner = const SystemProcessRunner(),
    RetroArchConfigWriter configWriter = const RetroArchConfigWriter(),
  }) : _processRunner = processRunner,
       _configWriter = configWriter;

  final ProcessRunner _processRunner;
  final RetroArchConfigWriter _configWriter;

  @override
  Future<void> prepare(EmulatorLaunchPlan plan) async {
    for (final dir in <String>[
      plan.target.saveRoot,
      plan.target.stateRoot,
      plan.target.configRoot,
    ]) {
      await Directory(dir).create(recursive: true);
    }
    // ROMD owns the session config: saves/states/screenshots land in our dirs.
    // A resolved BIOS dir becomes RetroArch's system_directory; the plan's
    // controller mapping becomes the hotkey chord binds; claimed player slots
    // become per-port joypad indices.
    await _configWriter.write(
      plan.target,
      systemDirectory: _biosDirOf(plan),
      controllerSetup: plan.controllerSetup,
      coreId: plan.profile.coreRequirement?.coreId,
    );
  }

  @override
  CommandPlan buildCommand(EmulatorLaunchPlan plan, String executablePath) {
    return CommandPlan(
      executable: runtimeProcessExecutable(executablePath),
      arguments: <String>[
        '--config',
        _configWriter.configPathFor(plan.target.configRoot),
        '-L',
        _corePathOf(plan)!,
        plan.target.launchAbsolutePath,
        '--fullscreen',
      ],
      workingDirectory: plan.target.contentRoot,
      // AppImage's documented extract-and-run fallback avoids requiring FUSE
      // on the test machine while preserving a directly supervised process.
      environment: executablePath.endsWith('.AppImage')
          ? const <String, String>{'APPIMAGE_EXTRACT_AND_RUN': '1'}
          : const <String, String>{},
    );
  }

  @override
  Future<LaunchStartResult> launch(EmulatorLaunchPlan plan) async {
    if (_corePathOf(plan) == null) {
      return const LaunchNotStarted(
        LaunchFailed('No RetroArch core is configured for this platform.'),
      );
    }

    final executablePath = _executablePathOf(plan);
    if (executablePath == null) {
      // Invariant guard: the dependency resolver reports a missing executable
      // before a plan reaches this adapter.
      return LaunchNotStarted(
        LaunchRuntimeMissing(const <String>[
          'No executable dependency was resolved for this launch.',
        ], runtimeName: plan.profile.executableRequirement?.displayName),
      );
    }

    final RunningProcess process;
    try {
      await prepare(plan);
      final command = buildCommand(plan, executablePath);
      process = await _processRunner.start(
        executable: command.executable,
        arguments: command.arguments,
        workingDirectory: command.workingDirectory,
        environment: command.environment,
      );
    } on Object {
      return const LaunchNotStarted(LaunchFailed("Couldn't launch the game."));
    }

    _logOutput(process.stdout, 'retroarch');
    _logOutput(process.stderr, 'retroarch');
    return LaunchStarted(SupervisedProcessLaunchSession(process));
  }

  /// The plan's resolved libretro core, or null when the profile has no core
  /// requirement or the coordinator couldn't satisfy it.
  String? _corePathOf(EmulatorLaunchPlan plan) {
    final coreRequirement = plan.profile.coreRequirement;
    return coreRequirement == null
        ? null
        : plan.dependencyPath(coreRequirement.id);
  }

  /// The plan's resolved frontend executable, or null when the profile has no
  /// executable requirement or the coordinator couldn't satisfy it.
  String? _executablePathOf(EmulatorLaunchPlan plan) {
    final executableRequirement = plan.profile.executableRequirement;
    return executableRequirement == null
        ? null
        : plan.dependencyPath(executableRequirement.id);
  }

  /// The plan's resolved BIOS directory, or null when the profile has no BIOS
  /// requirement — BIOS-free platforms keep their config unchanged.
  String? _biosDirOf(EmulatorLaunchPlan plan) {
    final biosRequirement = plan.profile.biosRequirement;
    return biosRequirement == null
        ? null
        : plan.dependencyPath(biosRequirement.id);
  }

  void _logOutput(Stream<List<int>> stream, String tag) {
    stream
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen(
          (line) {
            if (kDebugMode) {
              debugPrint('[$tag] $line');
            }
          },
          onError: (Object _) {},
          cancelOnError: false,
        );
  }
}
