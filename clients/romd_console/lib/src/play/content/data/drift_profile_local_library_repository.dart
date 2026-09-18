import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/data/drift_local_install_repository.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/content/domain/profile_local_library.dart';

final class DriftProfileLocalLibraryRepository
    implements ProfileLocalLibraryRepository {
  const DriftProfileLocalLibraryRepository({
    required AppDatabase database,
    required DriftLocalInstallRepository installs,
    required InstallAuthorityContext? authority,
  }) : _database = database,
       _installs = installs,
       _authority = authority;

  final AppDatabase _database;
  final DriftLocalInstallRepository _installs;
  final InstallAuthorityContext? _authority;

  @override
  Stream<ProfileLocalLibraryResult> watchLibrary({
    required ProfileLocalLibrarySort sort,
  }) {
    final authority = _authority;
    if (authority == null) {
      return Stream<ProfileLocalLibraryResult>.value(
        const ProfileLocalLibraryNoServer(),
      );
    }
    return _watchSnapshot(authority).map((snapshot) {
      if (snapshot == null) return const ProfileLocalLibraryUnavailable();
      try {
        final titles = _projectTitles(snapshot)..sort(_comparator(sort));
        return ProfileLocalLibraryReady(
          List<ProfileLocalLibraryTitle>.unmodifiable(titles),
        );
      } on Object {
        return const ProfileLocalLibraryUnavailable();
      }
    });
  }

  @override
  Stream<ProfileLocalReleasesResult> watchReleases() {
    final authority = _authority;
    if (authority == null) {
      return Stream<ProfileLocalReleasesResult>.value(
        const ProfileLocalReleasesNoServer(),
      );
    }
    return _watchSnapshot(authority).map((snapshot) {
      if (snapshot == null) return const ProfileLocalReleasesUnavailable();
      final releases =
          <ProfileLocalRelease>[
            for (final entry in snapshot.entries)
              if (entry.install case final install?)
                ProfileLocalRelease(
                  reference: entry.reference,
                  install: install,
                ),
          ]..sort((left, right) {
            final acquired = right.reference.acquiredAt.compareTo(
              left.reference.acquiredAt,
            );
            return acquired != 0
                ? acquired
                : left.reference.key.releaseId.value.compareTo(
                    right.reference.key.releaseId.value,
                  );
          });
      return ProfileLocalReleasesReady(
        List<ProfileLocalRelease>.unmodifiable(releases),
      );
    });
  }

  @override
  Future<ProfileLocalReleasesResult> readReleases() => watchReleases().first;

  Stream<_LibrarySnapshot?> _watchSnapshot(InstallAuthorityContext authority) {
    final profiles = _database.localProfiles;
    final connections = _database.serverConnections;
    final games = _database.profileLocalGames;
    final installs = _database.localInstalls;
    final histories = _database.profilePlayHistories;
    final instanceId = authority.connection.instanceId.value;
    final query =
        _database.select(profiles).join([
          innerJoin(
            connections,
            connections.instanceId.equals(instanceId) &
                connections.instanceId.equalsExp(
                  profiles.selectedServerInstanceId,
                ),
          ),
          leftOuterJoin(
            games,
            games.localProfileId.equalsExp(profiles.id) &
                games.serverInstanceId.equals(instanceId),
          ),
          leftOuterJoin(
            installs,
            installs.serverInstanceId.equals(instanceId) &
                installs.releaseId.equalsExp(games.releaseId),
          ),
          leftOuterJoin(
            histories,
            histories.localProfileId.equalsExp(profiles.id) &
                histories.serverInstanceId.equals(instanceId) &
                histories.titleId.equalsExp(games.titleId),
          ),
        ])..where(
          profiles.id.equals(authority.localProfileId) &
              profiles.selectedServerInstanceId.equals(instanceId) &
              profiles.serverSelectionGeneration.equals(authority.generation),
        );

    return query.watch().map((rows) {
      try {
        if (rows.isEmpty) return null;
        final entries = <_SnapshotEntry>[];
        final historyByTitle = <String, _History>{};
        for (final row in rows) {
          final profile = row.readTable(profiles);
          if (profile.id != authority.localProfileId ||
              profile.selectedServerInstanceId != instanceId ||
              profile.serverSelectionGeneration != authority.generation) {
            return null;
          }
          final gameRow = row.readTableOrNull(games);
          final installRow = row.readTableOrNull(installs);
          final historyRow = row.readTableOrNull(histories);
          if (gameRow == null) {
            if (installRow != null || historyRow != null || rows.length != 1) {
              return null;
            }
            continue;
          }
          final reference = _parseReference(gameRow);
          if (reference == null ||
              reference.key.localProfileId != authority.localProfileId ||
              reference.key.serverInstanceId !=
                  authority.connection.instanceId) {
            return null;
          }
          final LocalInstall? install;
          if (installRow == null) {
            install = null;
          } else {
            install = _installs.parseStoredRow(installRow);
            if (install.serverInstanceId != instanceId ||
                install.releaseId != reference.key.releaseId.value ||
                install.titleId != reference.titleId.value) {
              return null;
            }
          }
          final history = historyRow == null ? null : _parseHistory(historyRow);
          if (historyRow != null &&
              (history == null ||
                  history.localProfileId != authority.localProfileId ||
                  history.serverInstanceId != instanceId ||
                  history.titleId != reference.titleId.value)) {
            return null;
          }
          if (history != null) {
            final prior = historyByTitle[history.titleId];
            if (prior != null && !_sameHistory(prior, history)) return null;
            historyByTitle[history.titleId] = history;
          }
          entries.add(
            _SnapshotEntry(
              reference: reference,
              install: install,
              history: history,
            ),
          );
        }
        return _LibrarySnapshot(entries);
      } on Object {
        return null;
      }
    });
  }

  static ProfileLocalGame? _parseReference(ProfileLocalGameRow row) {
    final instanceId = RomdServerInstanceId.tryParse(row.serverInstanceId);
    final releaseId = RomdPublicId.tryParse(row.releaseId);
    final titleId = RomdPublicId.tryParse(row.titleId);
    final authorization = switch (row.authorizationState) {
      'authorized' => ProfileGameAuthorization.authorized,
      'revoked' => ProfileGameAuthorization.revoked,
      _ => null,
    };
    if (row.localProfileId.isEmpty ||
        row.localProfileId.trim() != row.localProfileId ||
        instanceId == null ||
        releaseId == null ||
        titleId == null ||
        authorization == null ||
        row.lastCheckedAt.isBefore(row.acquiredAt)) {
      return null;
    }
    return ProfileLocalGame(
      key: ProfileLocalGameKey(
        localProfileId: row.localProfileId,
        serverInstanceId: instanceId,
        releaseId: releaseId,
      ),
      titleId: titleId,
      authorization: authorization,
      acquiredAt: row.acquiredAt,
      lastCheckedAt: row.lastCheckedAt,
    );
  }

  static _History? _parseHistory(ProfilePlayHistoryRow row) {
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
    return _History(
      localProfileId: row.localProfileId,
      serverInstanceId: instanceId.value,
      titleId: titleId.value,
      lastReleaseId: releaseId.value,
      lastPlayedAt: row.lastPlayedAt,
      playCount: row.playCount,
    );
  }

  static List<ProfileLocalLibraryTitle> _projectTitles(
    _LibrarySnapshot snapshot,
  ) {
    final grouped = <String, _TitleAccumulator>{};
    for (final entry in snapshot.entries) {
      final key = entry.reference.titleId.value;
      final accumulator = grouped.putIfAbsent(key, () => _TitleAccumulator());
      final history = entry.history;
      if (history != null) {
        final current = accumulator.history;
        if (current != null && !_sameHistory(current, history)) {
          throw StateError(
            'Profile history projection changed within a title.',
          );
        }
        accumulator.history = history;
      }
      if (entry.install != null) accumulator.candidates.add(entry);
    }

    final result = <ProfileLocalLibraryTitle>[];
    for (final accumulator in grouped.values) {
      if (accumulator.candidates.isEmpty) continue;
      final authorized = accumulator.candidates
          .where(
            (entry) =>
                entry.reference.authorization ==
                ProfileGameAuthorization.authorized,
          )
          .toList(growable: false);
      final playable = authorized
          .where((entry) => entry.install!.state == InstallState.installed)
          .toList(growable: false);
      final _SnapshotEntry selected;
      if (playable.isNotEmpty) {
        final preferred = playable.where(
          (entry) =>
              entry.reference.key.releaseId.value ==
              accumulator.history?.lastReleaseId,
        );
        selected = preferred.isNotEmpty
            ? preferred.single
            : (_sortCandidates(playable).first);
      } else if (authorized.isNotEmpty) {
        final preferred = authorized.where(
          (entry) =>
              entry.reference.key.releaseId.value ==
              accumulator.history?.lastReleaseId,
        );
        selected = preferred.isNotEmpty
            ? preferred.single
            : (_sortCandidates(authorized).first);
      } else {
        final preferred = accumulator.candidates.where(
          (entry) =>
              entry.reference.key.releaseId.value ==
              accumulator.history?.lastReleaseId,
        );
        selected = preferred.isNotEmpty
            ? preferred.single
            : (_sortCandidates(accumulator.candidates).first);
      }
      final install = selected.install!;
      result.add(
        ProfileLocalLibraryTitle(
          install: install,
          authorization: selected.reference.authorization,
          state: _state(selected.reference.authorization, install.state),
          releaseCount: accumulator.candidates.length,
          acquiredAt: selected.reference.acquiredAt,
          lastPlayedAt: accumulator.history?.lastPlayedAt,
          playCount: accumulator.history?.playCount ?? 0,
        ),
      );
    }
    return result;
  }

  static List<_SnapshotEntry> _sortCandidates(List<_SnapshotEntry> values) =>
      values.toList(growable: false)..sort((left, right) {
        final acquired = right.reference.acquiredAt.compareTo(
          left.reference.acquiredAt,
        );
        if (acquired != 0) return acquired;
        final installed = right.install!.installedAt.compareTo(
          left.install!.installedAt,
        );
        if (installed != 0) return installed;
        return left.reference.key.releaseId.value.compareTo(
          right.reference.key.releaseId.value,
        );
      });

  static ProfileLocalLibraryState _state(
    ProfileGameAuthorization authorization,
    InstallState installState,
  ) {
    if (authorization == ProfileGameAuthorization.revoked) {
      return ProfileLocalLibraryState.accessRequired;
    }
    return switch (installState) {
      InstallState.installed => ProfileLocalLibraryState.ready,
      InstallState.corrupt ||
      InstallState.installFailed => ProfileLocalLibraryState.repairRequired,
      InstallState.remoteOnly ||
      InstallState.installing ||
      InstallState.verifying => ProfileLocalLibraryState.notReady,
    };
  }

  static Comparator<ProfileLocalLibraryTitle> _comparator(
    ProfileLocalLibrarySort sort,
  ) => switch (sort) {
    ProfileLocalLibrarySort.recentPlay => (left, right) {
      final played = _nullableDateDescending(
        left.lastPlayedAt,
        right.lastPlayedAt,
      );
      if (played != 0) return played;
      return _installAcquiredTitle(left, right);
    },
    ProfileLocalLibrarySort.recentInstall => _installAcquiredTitle,
    ProfileLocalLibrarySort.title => _titleOrder,
    ProfileLocalLibrarySort.system => (left, right) {
      final leftSystem =
          left.install.platformName ?? left.install.platformShortName;
      final rightSystem =
          right.install.platformName ?? right.install.platformShortName;
      final system = leftSystem.toLowerCase().compareTo(
        rightSystem.toLowerCase(),
      );
      return system != 0 ? system : _titleOrder(left, right);
    },
  };

  static int _installAcquiredTitle(
    ProfileLocalLibraryTitle left,
    ProfileLocalLibraryTitle right,
  ) {
    final installed = right.install.installedAt.compareTo(
      left.install.installedAt,
    );
    if (installed != 0) return installed;
    final acquired = right.acquiredAt.compareTo(left.acquiredAt);
    return acquired != 0 ? acquired : _titleOrder(left, right);
  }

  static int _titleOrder(
    ProfileLocalLibraryTitle left,
    ProfileLocalLibraryTitle right,
  ) {
    final title = left.install.titleName.toLowerCase().compareTo(
      right.install.titleName.toLowerCase(),
    );
    return title != 0
        ? title
        : left.install.titleId.compareTo(right.install.titleId);
  }

  static int _nullableDateDescending(DateTime? left, DateTime? right) {
    if (left == null) return right == null ? 0 : 1;
    if (right == null) return -1;
    return right.compareTo(left);
  }

  static bool _sameHistory(_History left, _History right) =>
      left.localProfileId == right.localProfileId &&
      left.serverInstanceId == right.serverInstanceId &&
      left.titleId == right.titleId &&
      left.lastReleaseId == right.lastReleaseId &&
      left.lastPlayedAt == right.lastPlayedAt &&
      left.playCount == right.playCount;
}

final class _LibrarySnapshot {
  const _LibrarySnapshot(this.entries);
  final List<_SnapshotEntry> entries;
}

final class _SnapshotEntry {
  const _SnapshotEntry({
    required this.reference,
    required this.install,
    required this.history,
  });
  final ProfileLocalGame reference;
  final LocalInstall? install;
  final _History? history;
}

final class _History {
  const _History({
    required this.localProfileId,
    required this.serverInstanceId,
    required this.titleId,
    required this.lastReleaseId,
    required this.lastPlayedAt,
    required this.playCount,
  });
  final String localProfileId;
  final String serverInstanceId;
  final String titleId;
  final String lastReleaseId;
  final DateTime lastPlayedAt;
  final int playCount;
}

final class _TitleAccumulator {
  _History? history;
  final List<_SnapshotEntry> candidates = <_SnapshotEntry>[];
}
