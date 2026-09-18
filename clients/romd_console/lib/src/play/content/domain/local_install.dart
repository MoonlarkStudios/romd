import 'package:romd_console/src/domain/console_artwork.dart';

import 'install_state.dart';

/// One file of an install, stripped to content-identity fields. This is the
/// grant-free snapshot persisted with an install and used for per-file repair
/// (does each recorded file still exist at the right size?).
final class InstalledItem {
  const InstalledItem({
    required this.relativePath,
    required this.sizeBytes,
    required this.sha256,
  });

  final String relativePath;
  final int sizeBytes;
  final String? sha256;
}

/// Device-local record of an installed/cached release.
final class LocalInstall {
  const LocalInstall({
    required this.serverInstanceId,
    required this.releaseId,
    required this.titleId,
    required this.titleName,
    required this.platformId,
    required this.platformName,
    required this.platformShortName,
    required this.coverUrl,
    required this.releaseName,
    required this.releaseRevision,
    required this.contentRoot,
    required this.launchRelativePath,
    required this.sizeBytes,
    required this.primarySha256,
    required this.manifestFingerprint,
    required this.state,
    required this.installMode,
    required this.items,
    required this.installedAt,
    required this.lastPlayedAt,
    this.artwork = const [],
  });

  final String serverInstanceId;
  final String releaseId;
  final String titleId;
  final String titleName;
  final String? platformId;
  final String? platformName;
  final String platformShortName;
  final Uri? coverUrl;
  final List<ConsoleArtwork> artwork;
  final String releaseName;
  final String? releaseRevision;

  /// Absolute path to the materialized content directory.
  final String contentRoot;

  /// Launch target relative to [contentRoot] (from `runtime.launch`).
  final String launchRelativePath;
  final int sizeBytes;
  final String? primarySha256;

  /// Fingerprint of the manifest item set at install time; compared against a
  /// freshly issued manifest to detect drift.
  final String manifestFingerprint;
  final InstallState state;

  /// Forward-compat only in v1 (always `permanent`); no eviction acts on it yet.
  final String installMode;

  /// The grant-stripped item set — the source of truth for per-file repair.
  final List<InstalledItem> items;
  final DateTime installedAt;
  final DateTime? lastPlayedAt;
}
