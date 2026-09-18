import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/data/drift_local_install_repository.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/profile_play_history.dart';

final class DriftProfilePlayHistoryRepository
    implements ProfilePlayHistoryRepository {
  const DriftProfilePlayHistoryRepository({
    required AppDatabase database,
    required DriftLocalInstallRepository installs,
    required InstallAuthorityContext? authority,
  }) : _database = database,
       _installs = installs,
       _authority = authority;

  static const int _maxSqliteInteger = 9223372036854775807;

  final AppDatabase _database;
  final DriftLocalInstallRepository _installs;
  final InstallAuthorityContext? _authority;

  @override
  Future<ProfilePlayHistoryRecordResult> recordEnded({
    required ResolvedPlayTarget target,
    required DateTime endedAt,
  }) async {
    final authority = _authority;
    final instanceId = RomdServerInstanceId.tryParse(target.serverInstanceId);
    final titleId = RomdPublicId.tryParse(target.titleId);
    final releaseId = RomdPublicId.tryParse(target.releaseId);
    if (authority == null ||
        target.localProfileId.isEmpty ||
        target.localProfileId != authority.localProfileId ||
        instanceId == null ||
        instanceId != authority.connection.instanceId ||
        titleId == null ||
        releaseId == null) {
      return ProfilePlayHistoryRecordResult.inconsistent;
    }

    try {
      return await _database.transaction(() async {
        final profile =
            await (_database.select(_database.localProfiles)
                  ..where((row) => row.id.equals(target.localProfileId)))
                .getSingleOrNull();
        final server =
            await (_database.select(_database.serverConnections)
                  ..where((row) => row.instanceId.equals(instanceId.value)))
                .getSingleOrNull();
        if (profile == null || server == null) {
          return ProfilePlayHistoryRecordResult.ownerMissing;
        }

        final rows =
            await (_database.select(_database.profilePlayHistories)..where(
                  (row) =>
                      row.localProfileId.equals(target.localProfileId) &
                      row.serverInstanceId.equals(instanceId.value) &
                      row.titleId.equals(titleId.value),
                ))
                .get();
        if (rows.length > 1) {
          return ProfilePlayHistoryRecordResult.inconsistent;
        }

        final persistedEndedAt = _storageSecond(endedAt);
        if (rows.isEmpty) {
          await _database
              .into(_database.profilePlayHistories)
              .insert(
                ProfilePlayHistoriesCompanion.insert(
                  localProfileId: target.localProfileId,
                  serverInstanceId: instanceId.value,
                  titleId: titleId.value,
                  lastReleaseId: releaseId.value,
                  lastPlayedAt: persistedEndedAt,
                  playCount: 1,
                ),
              );
          return ProfilePlayHistoryRecordResult.recorded;
        }

        final current = _parse(rows.single);
        if (current == null || current.playCount == _maxSqliteInteger) {
          return ProfilePlayHistoryRecordResult.inconsistent;
        }
        final isNewer = persistedEndedAt.isAfter(current.lastPlayedAt);
        final isEqual = persistedEndedAt.compareTo(current.lastPlayedAt) == 0;
        final selectedRelease = isNewer
            ? releaseId
            : isEqual &&
                  releaseId.value.compareTo(current.lastReleaseId.value) < 0
            ? releaseId
            : current.lastReleaseId;
        final selectedTime = isNewer ? persistedEndedAt : current.lastPlayedAt;

        await (_database.update(_database.profilePlayHistories)..where(
              (row) =>
                  row.localProfileId.equals(target.localProfileId) &
                  row.serverInstanceId.equals(instanceId.value) &
                  row.titleId.equals(titleId.value),
            ))
            .write(
              ProfilePlayHistoriesCompanion(
                lastReleaseId: Value(selectedRelease.value),
                lastPlayedAt: Value(selectedTime),
                playCount: Value(current.playCount + 1),
              ),
            );
        return ProfilePlayHistoryRecordResult.recorded;
      });
    } on Object {
      return ProfilePlayHistoryRecordResult.inconsistent;
    }
  }

  @override
  Future<ProfilePlayHistoryReadResult> find({
    required String localProfileId,
    required RomdServerInstanceId serverInstanceId,
    required RomdPublicId titleId,
  }) async {
    try {
      final rows =
          await (_database.select(_database.profilePlayHistories)..where(
                (row) =>
                    row.localProfileId.equals(localProfileId) &
                    row.serverInstanceId.equals(serverInstanceId.value) &
                    row.titleId.equals(titleId.value),
              ))
              .get();
      if (rows.isEmpty) return const ProfilePlayHistoryNotFound();
      if (rows.length != 1) return const ProfilePlayHistoryInconsistent();
      final parsed = _parse(rows.single);
      return parsed == null
          ? const ProfilePlayHistoryInconsistent()
          : ProfilePlayHistoryFound(parsed);
    } on Object {
      return const ProfilePlayHistoryInconsistent();
    }
  }

  @override
  Stream<ProfileRecentGamesResult> watchRecent() {
    final authority = _authority;
    if (authority == null) {
      return Stream<ProfileRecentGamesResult>.value(
        const ProfileRecentGamesReady(<ProfileRecentGame>[]),
      );
    }

    final history = _database.profilePlayHistories;
    final games = _database.profileLocalGames;
    final installs = _database.localInstalls;
    final query =
        _database.select(history).join([
          leftOuterJoin(
            games,
            games.localProfileId.equalsExp(history.localProfileId) &
                games.serverInstanceId.equalsExp(history.serverInstanceId) &
                games.titleId.equalsExp(history.titleId) &
                games.authorizationState.equals(
                  ProfileGameAuthorization.authorized.name,
                ),
          ),
          leftOuterJoin(
            installs,
            installs.serverInstanceId.equalsExp(history.serverInstanceId) &
                installs.releaseId.equalsExp(games.releaseId) &
                installs.titleId.equalsExp(history.titleId) &
                installs.state.equals(InstallState.installed.name),
          ),
        ])..where(
          history.localProfileId.equals(authority.localProfileId) &
              history.serverInstanceId.equals(
                authority.connection.instanceId.value,
              ),
        );

    return query.watch().map((rows) {
      try {
        final byTitle = <String, _RecentAccumulator>{};
        for (final row in rows) {
          final historyRow = row.readTable(history);
          final parsedHistory = _parse(historyRow);
          if (parsedHistory == null ||
              parsedHistory.localProfileId != authority.localProfileId ||
              parsedHistory.serverInstanceId !=
                  authority.connection.instanceId) {
            return const ProfileRecentGamesInconsistent();
          }
          final accumulator = byTitle.putIfAbsent(
            parsedHistory.titleId.value,
            () => _RecentAccumulator(parsedHistory),
          );
          if (!_sameHistory(accumulator.history, parsedHistory)) {
            return const ProfileRecentGamesInconsistent();
          }

          final game = row.readTableOrNull(games);
          final installRow = row.readTableOrNull(installs);
          if (game == null && installRow == null) continue;
          if (game == null ||
              game.localProfileId != authority.localProfileId ||
              game.serverInstanceId != authority.connection.instanceId.value ||
              game.titleId != parsedHistory.titleId.value ||
              game.authorizationState !=
                  ProfileGameAuthorization.authorized.name ||
              game.lastCheckedAt.isBefore(game.acquiredAt)) {
            return const ProfileRecentGamesInconsistent();
          }
          final gameRelease = RomdPublicId.tryParse(game.releaseId);
          final gameTitle = RomdPublicId.tryParse(game.titleId);
          if (gameRelease == null || gameTitle != parsedHistory.titleId) {
            return const ProfileRecentGamesInconsistent();
          }
          // A valid acquired release without an installed projection is a
          // normal ineligible candidate. History remains persisted and may
          // become visible again after installation completes.
          if (installRow == null) continue;
          final install = _installs.parseStoredRow(installRow);
          if (install.serverInstanceId !=
                  authority.connection.instanceId.value ||
              install.releaseId != gameRelease.value ||
              install.titleId != parsedHistory.titleId.value ||
              install.state != InstallState.installed) {
            return const ProfileRecentGamesInconsistent();
          }
          accumulator.candidates.add(
            _RecentCandidate(install: install, acquiredAt: game.acquiredAt),
          );
        }

        final projected = <ProfileRecentGame>[];
        for (final accumulator in byTitle.values) {
          if (accumulator.candidates.isEmpty) continue;
          final preferred = accumulator.candidates.where(
            (candidate) =>
                candidate.install.releaseId ==
                accumulator.history.lastReleaseId.value,
          );
          final selected = preferred.isNotEmpty
              ? preferred.single
              : (_sortedCandidates(accumulator.candidates).first);
          projected.add(
            ProfileRecentGame(
              install: selected.install,
              lastPlayedAt: accumulator.history.lastPlayedAt,
              playCount: accumulator.history.playCount,
              eligibleReleaseCount: accumulator.candidates.length,
            ),
          );
        }
        projected.sort((left, right) {
          final time = right.lastPlayedAt.compareTo(left.lastPlayedAt);
          return time != 0
              ? time
              : left.install.titleId.compareTo(right.install.titleId);
        });
        return ProfileRecentGamesReady(
          List<ProfileRecentGame>.unmodifiable(projected),
        );
      } on Object {
        return const ProfileRecentGamesInconsistent();
      }
    });
  }

  ProfilePlayHistory? _parse(ProfilePlayHistoryRow row) {
    final instanceId = RomdServerInstanceId.tryParse(row.serverInstanceId);
    final titleId = RomdPublicId.tryParse(row.titleId);
    final releaseId = RomdPublicId.tryParse(row.lastReleaseId);
    if (row.localProfileId.isEmpty ||
        instanceId == null ||
        titleId == null ||
        releaseId == null ||
        row.playCount < 1) {
      return null;
    }
    return ProfilePlayHistory(
      localProfileId: row.localProfileId,
      serverInstanceId: instanceId,
      titleId: titleId,
      lastReleaseId: releaseId,
      lastPlayedAt: row.lastPlayedAt,
      playCount: row.playCount,
    );
  }

  static List<_RecentCandidate> _sortedCandidates(
    List<_RecentCandidate> candidates,
  ) => candidates.toList(growable: false)
    ..sort((left, right) {
      final acquired = right.acquiredAt.compareTo(left.acquiredAt);
      if (acquired != 0) return acquired;
      final installed = right.install.installedAt.compareTo(
        left.install.installedAt,
      );
      if (installed != 0) return installed;
      return left.install.releaseId.compareTo(right.install.releaseId);
    });

  static bool _sameHistory(ProfilePlayHistory left, ProfilePlayHistory right) =>
      left.localProfileId == right.localProfileId &&
      left.serverInstanceId == right.serverInstanceId &&
      left.titleId == right.titleId &&
      left.lastReleaseId == right.lastReleaseId &&
      left.lastPlayedAt == right.lastPlayedAt &&
      left.playCount == right.playCount;

  static DateTime _storageSecond(DateTime value) =>
      DateTime.fromMillisecondsSinceEpoch(
        (value.millisecondsSinceEpoch ~/ Duration.millisecondsPerSecond) *
            Duration.millisecondsPerSecond,
        isUtc: value.isUtc,
      );
}

final class _RecentAccumulator {
  _RecentAccumulator(this.history);

  final ProfilePlayHistory history;
  final List<_RecentCandidate> candidates = <_RecentCandidate>[];
}

final class _RecentCandidate {
  const _RecentCandidate({required this.install, required this.acquiredAt});

  final LocalInstall install;
  final DateTime acquiredAt;
}
