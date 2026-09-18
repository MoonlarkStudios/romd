import 'package:drift/drift.dart';

import '../../domain/local_profile.dart';
import '../../domain/profile_server_connection.dart';
import '../../domain/romd_server_instance_id.dart';
import 'app_database.dart';

abstract interface class ProfileServerConnectionRepository {
  Future<ProfileServerConnectionReadResult> read(String localProfileId);

  Future<ProfileServerMutationResult> setPendingLocator({
    required String localProfileId,
    required Uri origin,
  });

  Future<bool> recordPendingAttempt({
    required String localProfileId,
    required int expectedGeneration,
    required Uri expectedOrigin,
  });

  Future<ProfileServerMutationResult> activateDiscoveredServer({
    required String localProfileId,
    required int expectedGeneration,
    required Uri expectedPendingOrigin,
    required RomdServerInstanceId instanceId,
  });

  Future<ProfileServerMutationResult> selectKnownServer({
    required String localProfileId,
    required int expectedGeneration,
    required RomdServerInstanceId instanceId,
  });

  Future<ProfileServerMutationResult> clearSelection({
    required String localProfileId,
    required int expectedGeneration,
  });
}

final class DriftProfileServerConnectionRepository
    implements ProfileServerConnectionRepository {
  DriftProfileServerConnectionRepository({
    required AppDatabase database,
    DateTime Function()? now,
  }) : _database = database,
       _now = now ?? DateTime.now;

  final AppDatabase _database;
  final DateTime Function() _now;

  @override
  Future<ProfileServerConnectionReadResult> read(String localProfileId) =>
      _database.transaction(() => _read(localProfileId));

  Future<ProfileServerConnectionReadResult> _read(String localProfileId) async {
    final profiles = await (_database.select(
      _database.localProfiles,
    )..where((row) => row.id.equals(localProfileId))).get();
    if (profiles.isEmpty) {
      return const ProfileServerConnectionProfileNotFound();
    }
    if (profiles.length != 1) {
      return const ProfileServerConnectionProjectionInconsistent();
    }

    final profile = profiles.single;
    if (!_isValidProfileId(profile.id) ||
        profile.serverSelectionGeneration < 0) {
      return const ProfileServerConnectionProjectionInconsistent();
    }

    final pendingRows = await (_database.select(
      _database.pendingServerLocators,
    )..where((row) => row.localProfileId.equals(localProfileId))).get();
    if (pendingRows.length > 1) {
      return const ProfileServerConnectionProjectionInconsistent();
    }

    PendingProfileServerLocator? pending;
    if (pendingRows case [final row]) {
      final origin = RomdServerOrigins.tryParse(row.normalizedOrigin);
      if (origin == null ||
          origin.toString() != row.normalizedOrigin ||
          (row.lastAttemptAt?.isBefore(row.createdAt) ?? false)) {
        return const ProfileServerConnectionProjectionInconsistent();
      }
      pending = PendingProfileServerLocator(
        origin: origin,
        createdAt: row.createdAt,
        lastAttemptAt: row.lastAttemptAt,
      );
    }

    ProfileServerConnection? selected;
    final selectedId = profile.selectedServerInstanceId;
    if (selectedId != null) {
      final parsedId = RomdServerInstanceId.tryParse(selectedId);
      if (parsedId == null) {
        return const ProfileServerConnectionProjectionInconsistent();
      }
      final rows = await (_database.select(
        _database.serverConnections,
      )..where((row) => row.instanceId.equals(selectedId))).get();
      if (rows.length != 1) {
        return const ProfileServerConnectionProjectionInconsistent();
      }
      final row = rows.single;
      final origin = _parseConnectionOrigin(row);
      if (origin == null) {
        return const ProfileServerConnectionProjectionInconsistent();
      }
      selected = ProfileServerConnection(
        instanceId: parsedId,
        origin: origin,
        firstSeenAt: row.firstSeenAt,
        lastSeenAt: row.lastSeenAt,
      );
    }

    return ProfileServerConnectionFound(
      ProfileServerConnectionState(
        localProfileId: profile.id,
        generation: profile.serverSelectionGeneration,
        selectedConnection: selected,
        pendingLocator: pending,
      ),
    );
  }

  @override
  Future<ProfileServerMutationResult> setPendingLocator({
    required String localProfileId,
    required Uri origin,
  }) async {
    if (!_isValidProfileId(localProfileId)) {
      return const ProfileServerMutationProjectionInconsistent();
    }
    final normalized = _normalize(origin);
    final timestamp = _now();

    return _database.transaction(() async {
      final profile = await _profile(localProfileId);
      if (profile == null) {
        return const ProfileServerMutationProfileNotFound();
      }
      if (profile.serverSelectionGeneration < 0) {
        return const ProfileServerMutationProjectionInconsistent();
      }
      final nextGeneration = profile.serverSelectionGeneration + 1;
      final changed =
          await (_database.update(_database.localProfiles)..where(
                (row) =>
                    row.id.equals(localProfileId) &
                    row.serverSelectionGeneration.equals(
                      profile.serverSelectionGeneration,
                    ),
              ))
              .write(
                LocalProfilesCompanion(
                  serverSelectionGeneration: Value(nextGeneration),
                  updatedAt: Value(timestamp),
                ),
              );
      if (changed != 1) {
        return const ProfileServerMutationStale();
      }
      await _database
          .into(_database.pendingServerLocators)
          .insertOnConflictUpdate(
            PendingServerLocatorsCompanion.insert(
              localProfileId: localProfileId,
              normalizedOrigin: normalized.toString(),
              createdAt: timestamp,
              lastAttemptAt: const Value(null),
            ),
          );
      return ProfileServerMutationApplied(nextGeneration);
    });
  }

  @override
  Future<bool> recordPendingAttempt({
    required String localProfileId,
    required int expectedGeneration,
    required Uri expectedOrigin,
  }) async {
    if (expectedGeneration < 0) {
      return false;
    }
    final normalized = _normalize(expectedOrigin);
    return _database.transaction(() async {
      final profile = await _profile(localProfileId);
      if (profile == null ||
          profile.serverSelectionGeneration != expectedGeneration) {
        return false;
      }
      final changed =
          await (_database.update(_database.pendingServerLocators)..where(
                (row) =>
                    row.localProfileId.equals(localProfileId) &
                    row.normalizedOrigin.equals(normalized.toString()),
              ))
              .write(
                PendingServerLocatorsCompanion(lastAttemptAt: Value(_now())),
              );
      return changed == 1;
    });
  }

  @override
  Future<ProfileServerMutationResult> activateDiscoveredServer({
    required String localProfileId,
    required int expectedGeneration,
    required Uri expectedPendingOrigin,
    required RomdServerInstanceId instanceId,
  }) async {
    if (expectedGeneration < 0) {
      return const ProfileServerMutationStale();
    }
    final expected = _normalize(expectedPendingOrigin);
    final timestamp = _now();

    return _database.transaction(() async {
      final profile = await _profile(localProfileId);
      if (profile == null) {
        return const ProfileServerMutationProfileNotFound();
      }
      if (profile.serverSelectionGeneration != expectedGeneration) {
        return const ProfileServerMutationStale();
      }
      final pending = await (_database.select(
        _database.pendingServerLocators,
      )..where((row) => row.localProfileId.equals(localProfileId))).get();
      if (pending.length != 1 ||
          pending.single.normalizedOrigin != expected.toString()) {
        return const ProfileServerMutationStale();
      }

      final existing = await (_database.select(
        _database.serverConnections,
      )..where((row) => row.instanceId.equals(instanceId.value))).get();
      if (existing.length > 1 ||
          (existing.isNotEmpty &&
              _parseConnectionOrigin(existing.single) == null)) {
        return const ProfileServerMutationProjectionInconsistent();
      }
      final lastSeenAt =
          existing.isNotEmpty && existing.single.lastSeenAt.isAfter(timestamp)
          ? existing.single.lastSeenAt
          : timestamp;

      await _database
          .into(_database.serverConnections)
          .insert(
            ServerConnectionsCompanion.insert(
              instanceId: instanceId.value,
              lastKnownOrigin: expected.toString(),
              firstSeenAt: timestamp,
              lastSeenAt: lastSeenAt,
            ),
            onConflict: DoUpdate(
              (_) => ServerConnectionsCompanion(
                lastKnownOrigin: Value(expected.toString()),
                lastSeenAt: Value(lastSeenAt),
              ),
              target: <Column<Object>>[_database.serverConnections.instanceId],
            ),
          );

      final nextGeneration = expectedGeneration + 1;
      final changed =
          await (_database.update(_database.localProfiles)..where(
                (row) =>
                    row.id.equals(localProfileId) &
                    row.serverSelectionGeneration.equals(expectedGeneration),
              ))
              .write(
                LocalProfilesCompanion(
                  selectedServerInstanceId: Value(instanceId.value),
                  serverSelectionGeneration: Value(nextGeneration),
                  updatedAt: Value(timestamp),
                ),
              );
      if (changed != 1) {
        throw StateError('Profile selection changed during activation.');
      }
      final deleted =
          await (_database.delete(_database.pendingServerLocators)..where(
                (row) =>
                    row.localProfileId.equals(localProfileId) &
                    row.normalizedOrigin.equals(expected.toString()),
              ))
              .go();
      if (deleted != 1) {
        throw StateError('Pending server changed during activation.');
      }
      return ProfileServerMutationApplied(nextGeneration);
    });
  }

  @override
  Future<ProfileServerMutationResult> selectKnownServer({
    required String localProfileId,
    required int expectedGeneration,
    required RomdServerInstanceId instanceId,
  }) => _changeSelection(
    localProfileId: localProfileId,
    expectedGeneration: expectedGeneration,
    instanceId: instanceId,
  );

  @override
  Future<ProfileServerMutationResult> clearSelection({
    required String localProfileId,
    required int expectedGeneration,
  }) => _changeSelection(
    localProfileId: localProfileId,
    expectedGeneration: expectedGeneration,
    instanceId: null,
  );

  Future<ProfileServerMutationResult> _changeSelection({
    required String localProfileId,
    required int expectedGeneration,
    required RomdServerInstanceId? instanceId,
  }) async {
    if (expectedGeneration < 0) {
      return const ProfileServerMutationStale();
    }
    final timestamp = _now();
    return _database.transaction(() async {
      final profile = await _profile(localProfileId);
      if (profile == null) {
        return const ProfileServerMutationProfileNotFound();
      }
      if (profile.serverSelectionGeneration != expectedGeneration) {
        return const ProfileServerMutationStale();
      }
      if (instanceId != null) {
        final connection = await (_database.select(
          _database.serverConnections,
        )..where((row) => row.instanceId.equals(instanceId.value))).get();
        if (connection.length != 1) {
          return const ProfileServerMutationProjectionInconsistent();
        }
        if (_parseConnectionOrigin(connection.single) == null) {
          return const ProfileServerMutationProjectionInconsistent();
        }
      }
      final nextGeneration = expectedGeneration + 1;
      final changed =
          await (_database.update(_database.localProfiles)..where(
                (row) =>
                    row.id.equals(localProfileId) &
                    row.serverSelectionGeneration.equals(expectedGeneration),
              ))
              .write(
                LocalProfilesCompanion(
                  selectedServerInstanceId: Value(instanceId?.value),
                  serverSelectionGeneration: Value(nextGeneration),
                  updatedAt: Value(timestamp),
                ),
              );
      if (changed != 1) {
        return const ProfileServerMutationStale();
      }
      await (_database.delete(
        _database.pendingServerLocators,
      )..where((row) => row.localProfileId.equals(localProfileId))).go();
      return ProfileServerMutationApplied(nextGeneration);
    });
  }

  Future<LocalProfileRow?> _profile(String localProfileId) async {
    final rows = await (_database.select(
      _database.localProfiles,
    )..where((row) => row.id.equals(localProfileId))).get();
    return rows.length == 1 ? rows.single : null;
  }

  static bool _isValidProfileId(String value) =>
      value.isNotEmpty && value.trim() == value;

  static Uri _normalize(Uri origin) =>
      RomdServerOrigins.parse(origin.toString());

  static Uri? _parseConnectionOrigin(ServerConnectionRow row) {
    final origin = RomdServerOrigins.tryParse(row.lastKnownOrigin);
    if (origin == null ||
        origin.toString() != row.lastKnownOrigin ||
        row.lastSeenAt.isBefore(row.firstSeenAt)) {
      return null;
    }
    return origin;
  }
}
