import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

import 'runtime_dependency_resolver.dart';

/// Owns the provider-specific prepare and launch path for a selected profile.
///
/// The coordinator still selects a [RuntimeProfile] through [RuntimeResolver];
/// later checkpoints move more of that shape behind this provider seam.
abstract interface class LaunchProvider {
  bool supports(RuntimeProfile profile);

  Stream<DependencyProgress> prepare(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  );

  Future<LaunchStartResult> launch({
    required RuntimeProfile profile,
    required ResolvedPlayTarget target,
    required List<ResolvedRuntimeDependency> dependencies,
    ReviewedLaunchSnapshot? controllerSnapshot,
  });
}
