import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

/// Coordinator-only boundary for probing and resolving installed content.
/// Callers must hold the exact instance/release mutation lease.
abstract interface class InstalledPlayTargetResolver {
  Future<ResolvedPlayTarget?> resolveInstalledForLaunch({
    required InstallAuthorityContext authority,
    required String releaseId,
    required String titleId,
    required String displayName,
  });
}
