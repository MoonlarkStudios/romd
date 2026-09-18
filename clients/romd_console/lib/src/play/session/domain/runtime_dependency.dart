import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

/// Trust boundary for a resolved runtime dependency.
///
/// Controller policies tied to a frozen emulator artifact must require
/// [managed]. Developer overrides and local discovery are [external]; callers
/// that do not propagate resolution provenance remain fail-closed as [unknown].
enum RuntimeDependencyProvenance { managed, external, unknown }

/// One resolved dependency: where a profile requirement was satisfied on disk.
final class ResolvedRuntimeDependency {
  const ResolvedRuntimeDependency({
    required this.requirementId,
    required this.path,
    this.provenance = RuntimeDependencyProvenance.unknown,
  });

  /// Matches [RuntimeDependencyRequirement.id] within the selected profile.
  final String requirementId;

  /// Absolute on-disk path where the requirement was satisfied.
  final String path;

  final RuntimeDependencyProvenance provenance;
}
