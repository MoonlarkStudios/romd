import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/content/data/drift_local_install_repository.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';

sealed class LaunchAuthorizationReadResult {
  const LaunchAuthorizationReadResult();
}

final class LaunchAuthorizationFound extends LaunchAuthorizationReadResult {
  const LaunchAuthorizationFound(this.snapshot);

  final LaunchAuthorizationSnapshot snapshot;
}

final class LaunchAuthorizationNotFound extends LaunchAuthorizationReadResult {
  const LaunchAuthorizationNotFound();
}

final class LaunchAuthorizationInconsistent
    extends LaunchAuthorizationReadResult {
  const LaunchAuthorizationInconsistent();
}

final class LaunchAuthorizationSnapshot {
  const LaunchAuthorizationSnapshot({
    required this.authorization,
    required this.acquiredAt,
    required this.lastCheckedAt,
    required this.install,
  });

  final ProfileGameAuthorization authorization;
  final DateTime acquiredAt;
  final DateTime lastCheckedAt;
  final LaunchInstallIdentity install;

  bool hasSameState(LaunchAuthorizationSnapshot other) =>
      authorization == other.authorization &&
      acquiredAt == other.acquiredAt &&
      lastCheckedAt == other.lastCheckedAt &&
      install.hasSameIdentity(other.install);
}

final class LaunchInstallIdentity {
  const LaunchInstallIdentity({
    required this.serverInstanceId,
    required this.releaseId,
    required this.titleId,
    required this.platformId,
    required this.platformShortName,
    required this.contentRoot,
    required this.launchRelativePath,
    required this.manifestFingerprint,
  });

  final String serverInstanceId;
  final String releaseId;
  final String titleId;
  final String platformId;
  final String platformShortName;
  final String contentRoot;
  final String launchRelativePath;
  final String manifestFingerprint;

  bool matchesRow(LocalInstallRow row) =>
      row.serverInstanceId == serverInstanceId &&
      row.releaseId == releaseId &&
      row.titleId == titleId &&
      row.platformId == platformId &&
      row.platformShortName == platformShortName &&
      row.contentRoot == contentRoot &&
      row.launchRelativePath == launchRelativePath &&
      row.manifestFingerprint == manifestFingerprint &&
      row.state == InstallState.installed.name;

  bool hasSameIdentity(LaunchInstallIdentity other) =>
      serverInstanceId == other.serverInstanceId &&
      releaseId == other.releaseId &&
      titleId == other.titleId &&
      platformId == other.platformId &&
      platformShortName == other.platformShortName &&
      contentRoot == other.contentRoot &&
      launchRelativePath == other.launchRelativePath &&
      manifestFingerprint == other.manifestFingerprint;
}

enum LaunchAuthorizationWriteResult {
  updated,
  authorityChanged,
  stale,
  inconsistent,
}

/// Coherent Drift boundary for the local portion of launch authorization.
/// Reads and compare-and-swap writes include the selected authority, cached
/// profile grant, and exact active install identity in one SQLite snapshot.
final class DriftLaunchAuthorizationStore {
  const DriftLaunchAuthorizationStore({
    required AppDatabase database,
    required DriftLocalInstallRepository installs,
  }) : _database = database,
       _installs = installs;

  final AppDatabase _database;
  final DriftLocalInstallRepository _installs;

  Future<LaunchAuthorizationReadResult> read({
    required InstallAuthorityContext authority,
    required RomdPublicId releaseId,
    required RomdPublicId titleId,
  }) => _database.transaction(
    () => _read(authority: authority, releaseId: releaseId, titleId: titleId),
  );

  Future<LaunchAuthorizationWriteResult> compareAndSet({
    required InstallAuthorityContext authority,
    required RomdPublicId releaseId,
    required RomdPublicId titleId,
    required LaunchAuthorizationSnapshot expected,
    required ProfileGameAuthorization authorization,
    required DateTime checkedAt,
  }) => _database.transaction(() async {
    final current = await _read(
      authority: authority,
      releaseId: releaseId,
      titleId: titleId,
    );
    if (current is LaunchAuthorizationNotFound) {
      return LaunchAuthorizationWriteResult.authorityChanged;
    }
    if (current is LaunchAuthorizationInconsistent) {
      return LaunchAuthorizationWriteResult.inconsistent;
    }
    final snapshot = (current as LaunchAuthorizationFound).snapshot;
    if (!snapshot.hasSameState(expected)) {
      return LaunchAuthorizationWriteResult.stale;
    }

    final normalizedCheckedAt = _storageSecond(checkedAt);
    final effectiveCheckedAt =
        normalizedCheckedAt.isAfter(expected.lastCheckedAt)
        ? normalizedCheckedAt
        // Drift's default SQLite DateTime mapping is second-granular. Advancing
        // from the normalized expected value guarantees the compare-and-swap
        // token remains strictly newer after a storage round trip.
        : _storageSecond(
            expected.lastCheckedAt,
          ).add(const Duration(seconds: 1));
    final changed =
        await (_database.update(_database.profileLocalGames)..where(
              (row) =>
                  row.localProfileId.equals(authority.localProfileId) &
                  row.serverInstanceId.equals(
                    authority.connection.instanceId.value,
                  ) &
                  row.releaseId.equals(releaseId.value) &
                  row.titleId.equals(titleId.value) &
                  row.authorizationState.equals(expected.authorization.name) &
                  row.acquiredAt.equals(expected.acquiredAt) &
                  row.lastCheckedAt.equals(expected.lastCheckedAt),
            ))
            .write(
              ProfileLocalGamesCompanion(
                authorizationState: Value(authorization.name),
                lastCheckedAt: Value(effectiveCheckedAt),
              ),
            );
    return changed == 1
        ? LaunchAuthorizationWriteResult.updated
        : LaunchAuthorizationWriteResult.stale;
  });

  Future<LaunchAuthorizationReadResult> _read({
    required InstallAuthorityContext authority,
    required RomdPublicId releaseId,
    required RomdPublicId titleId,
  }) async {
    final profiles = await (_database.select(
      _database.localProfiles,
    )..where((row) => row.id.equals(authority.localProfileId))).get();
    if (profiles.length != 1) {
      return profiles.isEmpty
          ? const LaunchAuthorizationNotFound()
          : const LaunchAuthorizationInconsistent();
    }
    final profile = profiles.single;
    if (profile.selectedServerInstanceId !=
            authority.connection.instanceId.value ||
        profile.serverSelectionGeneration != authority.generation) {
      return const LaunchAuthorizationNotFound();
    }

    final connections =
        await (_database.select(_database.serverConnections)..where(
              (row) =>
                  row.instanceId.equals(authority.connection.instanceId.value),
            ))
            .get();
    if (connections.length != 1 ||
        connections.single.lastKnownOrigin !=
            authority.connection.origin.toString()) {
      return const LaunchAuthorizationNotFound();
    }

    final games =
        await (_database.select(_database.profileLocalGames)..where(
              (row) =>
                  row.localProfileId.equals(authority.localProfileId) &
                  row.serverInstanceId.equals(
                    authority.connection.instanceId.value,
                  ) &
                  row.releaseId.equals(releaseId.value),
            ))
            .get();
    if (games.isEmpty) return const LaunchAuthorizationNotFound();
    if (games.length != 1) return const LaunchAuthorizationInconsistent();
    final game = games.single;
    final parsedTitleId = RomdPublicId.tryParse(game.titleId);
    final authorization = ProfileGameAuthorization.values
        .asNameMap()[game.authorizationState];
    if (parsedTitleId != titleId ||
        authorization == null ||
        game.lastCheckedAt.isBefore(game.acquiredAt)) {
      return const LaunchAuthorizationInconsistent();
    }

    final installs =
        await (_database.select(_database.localInstalls)..where(
              (row) =>
                  row.serverInstanceId.equals(
                    authority.connection.instanceId.value,
                  ) &
                  row.releaseId.equals(releaseId.value),
            ))
            .get();
    if (installs.isEmpty) return const LaunchAuthorizationInconsistent();
    if (installs.length != 1) return const LaunchAuthorizationInconsistent();
    final install = installs.single;
    try {
      final strict = _installs.parseStoredRow(install);
      final computedFingerprint = const ContentVerifier().manifestFingerprint(
        strict.items.map(
          (item) => (
            relativePath: item.relativePath,
            sizeBytes: item.sizeBytes,
            sha256: item.sha256,
          ),
        ),
      );
      if (strict.titleId != titleId.value ||
          strict.state != InstallState.installed ||
          strict.manifestFingerprint != computedFingerprint) {
        return const LaunchAuthorizationInconsistent();
      }
    } on Object {
      return const LaunchAuthorizationInconsistent();
    }

    return LaunchAuthorizationFound(
      LaunchAuthorizationSnapshot(
        authorization: authorization,
        acquiredAt: game.acquiredAt,
        lastCheckedAt: game.lastCheckedAt,
        install: LaunchInstallIdentity(
          serverInstanceId: install.serverInstanceId,
          releaseId: install.releaseId,
          titleId: install.titleId,
          platformId: install.platformId,
          platformShortName: install.platformShortName,
          contentRoot: install.contentRoot,
          launchRelativePath: install.launchRelativePath,
          manifestFingerprint: install.manifestFingerprint,
        ),
      ),
    );
  }

  DateTime _storageSecond(DateTime value) =>
      DateTime.fromMillisecondsSinceEpoch(
        (value.millisecondsSinceEpoch ~/ Duration.millisecondsPerSecond) *
            Duration.millisecondsPerSecond,
        isUtc: value.isUtc,
      );
}
