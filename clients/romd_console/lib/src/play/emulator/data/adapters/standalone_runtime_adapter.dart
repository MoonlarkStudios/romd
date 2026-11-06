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

typedef StandaloneRuntimeArgumentsBuilder =
    List<String> Function(EmulatorLaunchPlan plan);

typedef StandaloneRuntimeDirectoriesBuilder =
    Iterable<String> Function(EmulatorLaunchPlan plan);

typedef StandaloneRuntimeEnvironmentBuilder =
    Map<String, String> Function(EmulatorLaunchPlan plan);

typedef StandaloneRuntimePrepareAction =
    Future<void> Function(EmulatorLaunchPlan plan);

typedef StandaloneRuntimeExecutableResolver =
    String Function(String executablePath);

/// Shared process-launch adapter for standalone emulators/source ports.
///
/// Runtime-specific code supplies command arguments, scoped environment, and
/// optional preparation; this class owns the invariant parts: directory
/// creation, executable dependency lookup, process start, logging, and exit
/// mapping.
final class StandaloneRuntimeAdapter implements RuntimeAdapter {
  const StandaloneRuntimeAdapter({
    required String logTag,
    required StandaloneRuntimeArgumentsBuilder argumentsBuilder,
    StandaloneRuntimeDirectoriesBuilder extraDirectories = _noExtraDirectories,
    StandaloneRuntimeEnvironmentBuilder environmentBuilder = _emptyEnvironment,
    StandaloneRuntimePrepareAction? prepareAction,
    StandaloneRuntimeExecutableResolver executableResolver =
        runtimeProcessExecutable,
    ProcessRunner processRunner = const SystemProcessRunner(),
  }) : _logTag = logTag,
       _argumentsBuilder = argumentsBuilder,
       _extraDirectories = extraDirectories,
       _environmentBuilder = environmentBuilder,
       _prepareAction = prepareAction,
       _executableResolver = executableResolver,
       _processRunner = processRunner;

  final String _logTag;
  final StandaloneRuntimeArgumentsBuilder _argumentsBuilder;
  final StandaloneRuntimeDirectoriesBuilder _extraDirectories;
  final StandaloneRuntimeEnvironmentBuilder _environmentBuilder;
  final StandaloneRuntimePrepareAction? _prepareAction;
  final StandaloneRuntimeExecutableResolver _executableResolver;
  final ProcessRunner _processRunner;

  @override
  Future<void> prepare(EmulatorLaunchPlan plan) async {
    for (final dir in <String>[
      plan.target.saveRoot,
      plan.target.stateRoot,
      plan.target.configRoot,
      ..._extraDirectories(plan),
    ]) {
      await Directory(dir).create(recursive: true);
    }
    await _prepareAction?.call(plan);
  }

  @override
  CommandPlan buildCommand(EmulatorLaunchPlan plan, String executablePath) =>
      CommandPlan(
        executable: _executableResolver(executablePath),
        arguments: _argumentsBuilder(plan),
        workingDirectory: plan.target.contentRoot,
        environment: _environmentBuilder(plan),
      );

  @override
  Future<LaunchStartResult> launch(EmulatorLaunchPlan plan) async {
    final executablePath = _executablePathOf(plan);
    if (executablePath == null) {
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

    _logOutput(process.stdout);
    _logOutput(process.stderr);
    return LaunchStarted(SupervisedProcessLaunchSession(process));
  }

  String? _executablePathOf(EmulatorLaunchPlan plan) {
    final executableRequirement = plan.profile.executableRequirement;
    return executableRequirement == null
        ? null
        : plan.dependencyPath(executableRequirement.id);
  }

  void _logOutput(Stream<List<int>> stream) {
    stream
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen(
          (line) {
            if (kDebugMode) {
              debugPrint('[$_logTag] $line');
            }
          },
          onError: (Object _) {},
          cancelOnError: false,
        );
  }
}

Iterable<String> _noExtraDirectories(EmulatorLaunchPlan plan) =>
    const <String>[];

Map<String, String> _emptyEnvironment(EmulatorLaunchPlan plan) =>
    const <String, String>{};
