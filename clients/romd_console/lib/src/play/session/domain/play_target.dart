import 'package:romd_console/src/domain/console_artwork.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

/// What the user asked to play, built in the UI from data it already has. It
/// carries no platform-selection role and no tokens — the stable platform key
/// and launch facts come from the manifest during install.
final class PlayTarget {
  const PlayTarget({
    required this.releaseId,
    required this.titleId,
    required this.displayName,
    required this.localProfileId,
    this.platformId,
    this.platformName,
    this.coverUrl,
    this.artwork = const [],
    this.releaseName,
    this.releaseRevision,
  });

  final String releaseId;
  final String titleId;
  final String displayName;
  final String localProfileId;
  final String? platformId;
  final String? platformName;
  final Uri? coverUrl;
  final List<ConsoleArtwork> artwork;
  final String? releaseName;
  final String? releaseRevision;
}

/// A release whose content is installed and verified, resolved to absolute
/// on-disk locations. Runtime-neutral: an adapter turns this into a process
/// invocation. [platformShortName] selects the runtime profile; [configRoot]
/// is the isolated emulator-config directory (kept out of the evictable
/// content root).
final class ResolvedPlayTarget {
  const ResolvedPlayTarget({
    required this.releaseId,
    required this.titleId,
    required this.platformShortName,
    required this.displayName,
    required this.contentRoot,
    required this.launchAbsolutePath,
    required this.saveRoot,
    required this.stateRoot,
    required this.configRoot,
    required this.localProfileId,
    this.serverInstanceId = '',
  });

  final String serverInstanceId;
  final String releaseId;
  final String titleId;
  final String platformShortName;
  final String displayName;
  final String localProfileId;
  final String contentRoot;
  final String launchAbsolutePath;
  final String saveRoot;
  final String stateRoot;
  final String configRoot;
}

/// Immutable, unresolved request accepted by the central launch boundary.
///
/// Presentation supplies only content identity and an optional per-launch
/// runtime choice. Profile/server authority is captured by the composed play
/// graph and cannot be supplied or substituted by a route.
final class PlayRequest {
  const PlayRequest({
    required this.releaseId,
    required this.titleId,
    required this.displayName,
    this.requestedRuntimeProfileId,
  });

  final String releaseId;
  final String titleId;
  final String displayName;
  final RuntimeProfileId? requestedRuntimeProfileId;
}
