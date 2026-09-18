import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

import 'local_install.dart';

/// Why an install could not complete.
enum InstallFailureKind {
  /// The selected Library explicitly denied this release.
  accessNotGranted,

  /// The selected profile/server authority changed while work was in flight.
  authorityChanged,

  /// A download failed at the transport/HTTP level.
  downloadFailed,

  /// A downloaded file failed SHA-256 verification (content was discarded).
  hashMismatch,

  /// A local filesystem error during staging/materialization.
  ioError,

  /// The release manifest could not be issued.
  manifestUnavailable,

  /// The manifest's shape isn't supported (e.g. not `direct_files`, or no
  /// launch target) — the client refuses to guess.
  unsupportedManifest,

  /// The release isn't fully obtainable (incomplete, or an item lacks a
  /// hash/grant).
  contentUnavailable,
}

/// Immutable authority captured when the selected server graph is composed.
/// Lower layers must never infer this identity from origin or presentation
/// state.
final class InstallAuthorityContext {
  const InstallAuthorityContext({
    required this.localProfileId,
    required this.connection,
    required this.generation,
  });

  final String localProfileId;
  final ProfileServerConnection connection;
  final int generation;

  bool hasSameAuthority(InstallAuthorityContext other) =>
      localProfileId == other.localProfileId &&
      connection.instanceId == other.connection.instanceId &&
      connection.origin == other.connection.origin &&
      generation == other.generation;
}

/// A caller-owned lease for one acquisition attempt.
///
/// The lease is deliberately domain-facing: install code can fail closed when
/// the profile/session operation that initiated the work is invalidated,
/// without depending on presentation state or types.
abstract interface class InstallOperationLease {
  bool get isCurrent;
}

/// Progress emitted while installing a release.
sealed class InstallProgress {
  const InstallProgress();
}

final class InstallStarted extends InstallProgress {
  const InstallStarted();
}

final class InstallDownloading extends InstallProgress {
  const InstallDownloading({
    required this.receivedBytes,
    required this.totalBytes,
  });
  final int receivedBytes;
  final int totalBytes;
}

final class InstallVerifying extends InstallProgress {
  const InstallVerifying();
}

/// Terminal success — carries the resolved launch target so the caller can
/// launch directly without re-reading the database.
final class InstallCompleted extends InstallProgress {
  const InstallCompleted(this.resolved);
  final ResolvedPlayTarget resolved;
}

/// Terminal failure with a fixed, sanitized [message].
final class InstallFailed extends InstallProgress {
  const InstallFailed(this.kind, this.message);
  final InstallFailureKind kind;
  final String message;
}

/// Downloads, verifies, and materializes a release's content, then reports a
/// resolved launch target. Consumes the access token internally so it never
/// reaches the presentation layer beyond this call.
abstract interface class InstallService {
  Future<LocalInstall?> findInstall(String releaseId);

  Future<List<LocalInstall>> listInstalled();

  Future<List<LocalInstall>> listInstalledForTitle(String titleId);

  Stream<List<LocalInstall>> watchInstalled();

  Stream<List<LocalInstall>> watchInstalledForTitle(String titleId);

  /// The set of release ids currently installed on this device, emitted live as
  /// installs complete. Drives the library's "on device" markers.
  Stream<Set<String>> watchInstalledReleaseIds();

  Stream<InstallProgress> install(
    PlayTarget target, {
    required InstallOperationLease operation,
    String? accessToken,
  });

  /// Removes the active profile's reference. Shared physical content is
  /// reclaimed only after no profile references this exact server release.
  /// The source-compatible name remains until the launcher copy is revised.
  Future<void> uninstall(String releaseId, {InstallOperationLease? operation});
}
