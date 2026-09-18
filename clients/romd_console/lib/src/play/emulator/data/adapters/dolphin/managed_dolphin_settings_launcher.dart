import 'dart:async';

import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/adapters/supervised_process_launch_session.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_provision.dart';
import 'package:romd_console/src/play/session/domain/active_launch_session.dart';
import 'package:romd_console/src/play/session/domain/emulator_settings_launcher.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/window_controller.dart';

import 'dolphin_config_writer.dart';
import 'dolphin_executable.dart';
import 'dolphin_user_directory.dart';

/// Provisions and opens Dolphin's desktop settings UI without booting content.
final class ManagedDolphinSettingsLauncher implements EmulatorSettingsLauncher {
  ManagedDolphinSettingsLauncher({
    required RuntimeExecutableProvisioner provisioner,
    required DolphinUserDirectory userDirectory,
    required ActiveLaunchSession activeLaunchSession,
    required WindowController windowController,
    DolphinSettingsConfigWriter configWriter = const DolphinIniConfigWriter(),
    ProcessRunner processRunner = const SystemProcessRunner(),
  }) : _provisioner = provisioner,
       _userDirectory = userDirectory,
       _activeLaunchSession = activeLaunchSession,
       _windowController = windowController,
       _configWriter = configWriter,
       _processRunner = processRunner;

  final RuntimeExecutableProvisioner _provisioner;
  final DolphinUserDirectory _userDirectory;
  final ActiveLaunchSession _activeLaunchSession;
  final WindowController _windowController;
  final DolphinSettingsConfigWriter _configWriter;
  final ProcessRunner _processRunner;

  @override
  Future<LaunchResult> openDolphin() async {
    final reservation = _DolphinSettingsReservation();
    if (!_activeLaunchSession.tryHold(reservation)) {
      return const LaunchFailed(
        'Close the running emulator before opening Dolphin settings.',
      );
    }
    LaunchSession heldSession = reservation;
    try {
      String? executablePath;
      await for (final progress in _provisioner.ensure()) {
        switch (progress) {
          case RuntimeExecutableReady(executablePath: final readyPath):
            executablePath = readyPath;
          case RuntimeProvisionFailed(:final message):
            return LaunchFailed(message);
          case RuntimeProvisionStarted() ||
              RuntimeProvisionDownloading() ||
              RuntimeProvisionUnpacking() ||
              RuntimeProvisionReady():
            break;
        }
      }
      if (executablePath == null) {
        return const LaunchFailed("Couldn't prepare Dolphin settings.");
      }
      await _configWriter.prepareSettings(_userDirectory);
      final process = await _processRunner.start(
        executable: dolphinProcessExecutable(executablePath),
        arguments: <String>['-u', _userDirectory.rootPath],
        workingDirectory: _userDirectory.rootPath,
        environment: const <String, String>{},
      );
      process.stdout.listen((_) {}, onError: (_) {});
      process.stderr.listen((_) {}, onError: (_) {});
      final session = SupervisedProcessLaunchSession(process);
      if (!_activeLaunchSession.replaceIfCurrent(reservation, session)) {
        await session.termination?.terminate();
        return const LaunchFailed('Another emulator is already open.');
      }
      heldSession = session;
      final result = await session.completed;
      await _windowController.reclaimForeground();
      return switch (result) {
        LaunchFailed() => const LaunchFailed(
          "Couldn't observe Dolphin settings.",
        ),
        _ => result,
      };
    } on Object {
      return const LaunchFailed("Couldn't open Dolphin settings.");
    } finally {
      _activeLaunchSession.clearIfCurrent(heldSession);
    }
  }
}

final class _DolphinSettingsReservation implements LaunchSession {
  final Completer<LaunchResult> _never = Completer<LaunchResult>();

  @override
  Future<LaunchResult> get completed => _never.future;

  @override
  LaunchForegroundControl? get foreground => null;

  @override
  LaunchTermination? get termination => null;
}
