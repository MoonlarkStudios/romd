import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

/// Progress while satisfying a profile's requirements. The sealed split keeps
/// impossible states out of the UI: only [DependencyActivity] subtypes are
/// forwarded as visible progress; terminal variants are consumed by the
/// coordinator.
sealed class DependencyProgress {
  const DependencyProgress();
}

/// Non-terminal, safe to surface as UI progress.
sealed class DependencyActivity extends DependencyProgress {
  const DependencyActivity();
}

/// Downloading an artifact. [totalBytes] is null when the size is unknown —
/// show an indeterminate indicator.
final class DependencyDownloading extends DependencyActivity {
  const DependencyDownloading({
    required this.label,
    required this.receivedBytes,
    this.totalBytes,
  });
  final String label;
  final int receivedBytes;
  final int? totalBytes;
}

final class DependencyUnpacking extends DependencyActivity {
  const DependencyUnpacking(this.label);
  final String label;
}

/// Terminal success: every satisfiable requirement resolved to a path.
final class DependenciesReady extends DependencyProgress {
  const DependenciesReady(this.dependencies);
  final List<ResolvedRuntimeDependency> dependencies;
}

/// Terminal: the runtime executable could not be located anywhere.
/// [diagnostics] explain where the resolver looked. [executableName] is the
/// user-facing runtime requirement name, e.g. `RetroArch` or `DuckStation`.
final class DependencyExecutableMissing extends DependencyProgress {
  const DependencyExecutableMissing(this.diagnostics, {this.executableName});
  final List<String> diagnostics;
  final String? executableName;
}

/// Terminal failure with a fixed, sanitized message (maps to `LaunchFailed`).
final class DependencyResolutionFailed extends DependencyProgress {
  const DependencyResolutionFailed(this.message);
  final String message;
}

/// Satisfies every requirement of one profile for one target. Adapter-scoped:
/// dependency semantics are runtime-specific, so implementations register per
/// adapter id.
abstract interface class RuntimeDependencyResolver {
  Stream<DependencyProgress> resolve(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  );
}
