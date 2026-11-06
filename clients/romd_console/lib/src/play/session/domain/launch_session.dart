import 'launch_service.dart';

/// Result of trying to start a launch session.
sealed class LaunchStartResult {
  const LaunchStartResult();
}

/// The target started and is now represented by [session].
final class LaunchStarted extends LaunchStartResult {
  const LaunchStarted(this.session);
  final LaunchSession session;
}

/// The target never started; no session capabilities exist.
final class LaunchNotStarted extends LaunchStartResult {
  const LaunchNotStarted(this.result);
  final LaunchResult result;
}

/// A live or observed launch session.
abstract interface class LaunchSession {
  /// Resolves once for the terminal launch outcome.
  Future<LaunchResult> get completed;

  /// Present only when the provider can terminate the running software.
  LaunchTermination? get termination;

  /// Present only when the provider can bring the running software forward.
  LaunchForegroundControl? get foreground;
}

abstract interface class LaunchTermination {
  /// Sends a termination signal.
  ///
  /// Returns true when the signal was delivered. Session death is confirmed
  /// separately by [LaunchSession.completed] resolving.
  Future<bool> terminate();
}

abstract interface class LaunchForegroundControl {
  Future<void> bringToForeground();
}
