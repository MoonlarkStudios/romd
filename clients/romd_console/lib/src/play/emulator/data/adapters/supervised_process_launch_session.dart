import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';

final class SupervisedProcessLaunchSession implements LaunchSession {
  SupervisedProcessLaunchSession(RunningProcess process)
    : termination = _RunningProcessTermination(process),
      _completed = _completedFrom(process);

  final Future<LaunchResult> _completed;

  @override
  Future<LaunchResult> get completed => _completed;

  @override
  final LaunchTermination? termination;

  @override
  LaunchForegroundControl? get foreground => null;

  static Future<LaunchResult> _completedFrom(RunningProcess process) async {
    try {
      return LaunchExited(await process.exitCode);
    } on Object {
      // Post-start exit-observation failure. Pre-start failures are represented
      // by LaunchNotStarted before a session is constructed.
      return const LaunchFailed("Couldn't launch the game.");
    }
  }
}

final class _RunningProcessTermination implements LaunchTermination {
  const _RunningProcessTermination(this._process);

  final RunningProcess _process;

  @override
  Future<bool> terminate() async => _process.kill();
}
