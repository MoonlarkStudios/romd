/// Outcome of attempting to launch a resolved target.
sealed class LaunchResult {
  const LaunchResult();
}

/// The emulator process started and has now exited with [exitCode].
final class LaunchExited extends LaunchResult {
  const LaunchExited(this.exitCode);
  final int exitCode;
}

/// No runtime executable could be located.
/// Produced by dependency resolution (and as an adapter invariant guard) —
/// adapters do no discovery of their own. [runtimeName] is safe to show to the
/// user; [diagnostics] explain where the resolver looked (dev builds may
/// surface them).
final class LaunchRuntimeMissing extends LaunchResult {
  const LaunchRuntimeMissing(this.diagnostics, {this.runtimeName});
  final List<String> diagnostics;
  final String? runtimeName;
}

/// No adapter is registered for the plan's profile (client doesn't support it
/// yet).
final class LaunchUnsupportedPlatform extends LaunchResult {
  const LaunchUnsupportedPlatform(this.platformShortName);
  final String platformShortName;
}

/// The process failed to start or crashed before running. [message] is a fixed,
/// sanitized string (never raw error/path text).
final class LaunchFailed extends LaunchResult {
  const LaunchFailed(this.message);
  final String message;
}

/// Controller seating changed after the player reviewed it, so no process was
/// started. The UI should return to Players and obtain a fresh snapshot.
final class LaunchControllerReviewRequired extends LaunchResult {
  const LaunchControllerReviewRequired();
}

/// The profile has no local acquisition record for this exact server release.
final class LaunchAccessNotGranted extends LaunchResult {
  const LaunchAccessNotGranted();
}

/// The exact cached or live ROMD decision denies this release.
final class LaunchAccessRevoked extends LaunchResult {
  const LaunchAccessRevoked();
}
