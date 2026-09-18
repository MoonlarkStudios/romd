import 'dart:math';

import 'package:drift/drift.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/session/domain/play_activity.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

final class DriftPlayActivityRepository implements PlayActivityRepository {
  DriftPlayActivityRepository({
    required AppDatabase database,
    required InstallAuthorityContext authority,
    DateTime Function()? now,
    Random? random,
  }) : _database = database,
       _authority = authority,
       _now = now ?? DateTime.now,
       _random = random ?? Random.secure();

  static const String clientId = 'ottercade.console';

  final AppDatabase _database;
  final InstallAuthorityContext _authority;
  final DateTime Function() _now;
  final Random _random;

  @override
  Future<bool> isSyncEnabled() async {
    final row = await _preference();
    return row?.enabled ?? false;
  }

  @override
  Future<void> setSyncEnabled({
    required bool enabled,
    PlayActivityEnableScope scope = PlayActivityEnableScope.futureOnly,
  }) => _database.transaction(() async {
    final now = _storageSecond(_now());
    await _database
        .into(_database.playActivitySyncPreferences)
        .insertOnConflictUpdate(
          PlayActivitySyncPreferencesCompanion.insert(
            localProfileId: _authority.localProfileId,
            serverInstanceId: _authority.connection.instanceId.value,
            enabled: Value(enabled),
            updatedAt: now,
          ),
        );

    final scopedSessions = _database.update(_database.localPlaySessions)
      ..where(
        (row) =>
            row.localProfileId.equals(_authority.localProfileId) &
            row.serverInstanceId.equals(_authority.connection.instanceId.value),
      );
    if (!enabled) {
      await scopedSessions.write(
        const LocalPlaySessionsCompanion(syncEligible: Value(false)),
      );
      await (_database.delete(_database.playActivityOutbox)..where(
            (row) =>
                row.localProfileId.equals(_authority.localProfileId) &
                row.serverInstanceId.equals(
                  _authority.connection.instanceId.value,
                ),
          ))
          .go();
      return;
    }

    if (scope == PlayActivityEnableScope.includeStoredSessions) {
      await scopedSessions.write(
        const LocalPlaySessionsCompanion(syncEligible: Value(true)),
      );
      final sessions =
          await (_database.select(_database.localPlaySessions)..where(
                (row) =>
                    row.localProfileId.equals(_authority.localProfileId) &
                    row.serverInstanceId.equals(
                      _authority.connection.instanceId.value,
                    ),
              ))
              .get();
      for (final session in sessions) {
        await _enqueue(session.sessionId, now);
      }
    }
  });

  @override
  Future<LocalPlaySession?> recordStarted({
    required ResolvedPlayTarget target,
    required DateTime startedAt,
  }) async {
    if (!_matchesAuthority(target)) return null;
    return _database.transaction(() async {
      final sessionId = _uuidV4();
      final now = _storageSecond(_now());
      final persistedStartedAt = _storageSecond(startedAt);
      final syncEnabled = await isSyncEnabled();
      await _database
          .into(_database.localPlaySessions)
          .insert(
            LocalPlaySessionsCompanion.insert(
              sessionId: sessionId,
              localProfileId: target.localProfileId,
              serverInstanceId: target.serverInstanceId,
              clientId: clientId,
              titleId: target.titleId,
              releaseId: target.releaseId,
              startedAt: persistedStartedAt,
              syncEligible: Value(syncEnabled),
              createdAt: now,
              updatedAt: now,
            ),
          );
      if (syncEnabled) await _enqueue(sessionId, now);
      return LocalPlaySession(
        sessionId: sessionId,
        localProfileId: target.localProfileId,
        serverInstanceId: _authority.connection.instanceId,
        clientId: clientId,
        titleId: target.titleId,
        releaseId: target.releaseId,
        startedAt: persistedStartedAt,
        endedAt: null,
        activeDurationSeconds: null,
      );
    });
  }

  @override
  Future<void> recordEnded({
    required String sessionId,
    required DateTime endedAt,
    required int activeDurationSeconds,
  }) => _database.transaction(() async {
    final row =
        await (_database.select(_database.localPlaySessions)..where(
              (row) =>
                  row.sessionId.equals(sessionId) &
                  row.localProfileId.equals(_authority.localProfileId) &
                  row.serverInstanceId.equals(
                    _authority.connection.instanceId.value,
                  ),
            ))
            .getSingleOrNull();
    if (row == null || row.endedAt != null) return;
    final persistedEndedAt = _storageSecond(endedAt);
    if (persistedEndedAt.isBefore(row.startedAt)) return;
    final boundedDuration = activeDurationSeconds.clamp(0, 31622400);
    await (_database.update(
      _database.localPlaySessions,
    )..where((candidate) => candidate.sessionId.equals(sessionId))).write(
      LocalPlaySessionsCompanion(
        endedAt: Value(persistedEndedAt),
        activeDurationSeconds: Value(boundedDuration),
        updatedAt: Value(_storageSecond(_now())),
      ),
    );
    if (row.syncEligible && await isSyncEnabled()) {
      await _enqueue(sessionId, _storageSecond(_now()));
    }
  });

  @override
  Future<List<LocalPlaySession>> pendingSnapshots({int limit = 50}) async {
    final now = _storageSecond(_now());
    final outbox =
        await (_database.select(_database.playActivityOutbox)
              ..where(
                (row) =>
                    row.localProfileId.equals(_authority.localProfileId) &
                    row.serverInstanceId.equals(
                      _authority.connection.instanceId.value,
                    ) &
                    row.nextAttemptAt.isSmallerOrEqualValue(now),
              )
              ..orderBy(<OrderingTerm Function(PlayActivityOutbox)>[
                (row) => OrderingTerm.asc(row.queuedAt),
              ])
              ..limit(limit))
            .get();
    final snapshots = <LocalPlaySession>[];
    for (final pending in outbox) {
      final session =
          await (_database.select(_database.localPlaySessions)
                ..where((row) => row.sessionId.equals(pending.sessionId)))
              .getSingleOrNull();
      if (session != null) snapshots.add(_toDomain(session));
    }
    return snapshots;
  }

  @override
  Future<void> markSynced(String sessionId) =>
      (_database.delete(_database.playActivityOutbox)..where(
            (row) =>
                row.sessionId.equals(sessionId) &
                row.localProfileId.equals(_authority.localProfileId) &
                row.serverInstanceId.equals(
                  _authority.connection.instanceId.value,
                ),
          ))
          .go();

  @override
  Future<void> markAttemptFailed(String sessionId, DateTime retryAt) =>
      (_database.update(_database.playActivityOutbox)..where(
            (row) =>
                row.sessionId.equals(sessionId) &
                row.localProfileId.equals(_authority.localProfileId) &
                row.serverInstanceId.equals(
                  _authority.connection.instanceId.value,
                ),
          ))
          .write(
            PlayActivityOutboxCompanion.custom(
              attemptCount:
                  _database.playActivityOutbox.attemptCount + const Constant(1),
              nextAttemptAt: Variable(_storageSecond(retryAt)),
            ),
          );

  @override
  Future<void> deleteSession(String sessionId) =>
      (_database.delete(_database.localPlaySessions)..where(
            (row) =>
                row.sessionId.equals(sessionId) &
                row.localProfileId.equals(_authority.localProfileId) &
                row.serverInstanceId.equals(
                  _authority.connection.instanceId.value,
                ),
          ))
          .go();

  @override
  Future<void> clearSessions() =>
      (_database.delete(_database.localPlaySessions)..where(
            (row) =>
                row.localProfileId.equals(_authority.localProfileId) &
                row.serverInstanceId.equals(
                  _authority.connection.instanceId.value,
                ),
          ))
          .go();

  Future<PlayActivitySyncPreferenceRow?> _preference() =>
      (_database.select(_database.playActivitySyncPreferences)..where(
            (row) =>
                row.localProfileId.equals(_authority.localProfileId) &
                row.serverInstanceId.equals(
                  _authority.connection.instanceId.value,
                ),
          ))
          .getSingleOrNull();

  Future<void> _enqueue(String sessionId, DateTime now) => _database
      .into(_database.playActivityOutbox)
      .insertOnConflictUpdate(
        PlayActivityOutboxCompanion.insert(
          sessionId: sessionId,
          localProfileId: _authority.localProfileId,
          serverInstanceId: _authority.connection.instanceId.value,
          queuedAt: now,
          nextAttemptAt: now,
        ),
      );

  bool _matchesAuthority(ResolvedPlayTarget target) =>
      target.localProfileId == _authority.localProfileId &&
      target.serverInstanceId == _authority.connection.instanceId.value;

  LocalPlaySession _toDomain(LocalPlaySessionRow row) => LocalPlaySession(
    sessionId: row.sessionId,
    localProfileId: row.localProfileId,
    serverInstanceId: RomdServerInstanceId.tryParse(row.serverInstanceId)!,
    clientId: row.clientId,
    titleId: row.titleId,
    releaseId: row.releaseId,
    startedAt: row.startedAt,
    endedAt: row.endedAt,
    activeDurationSeconds: row.activeDurationSeconds,
  );

  DateTime _storageSecond(DateTime value) =>
      DateTime.fromMillisecondsSinceEpoch(
        value.toUtc().millisecondsSinceEpoch ~/ 1000 * 1000,
        isUtc: true,
      );

  String _uuidV4() {
    final bytes = List<int>.generate(16, (_) => _random.nextInt(256));
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    final hex = bytes
        .map((byte) => byte.toRadixString(16).padLeft(2, '0'))
        .join();
    return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-${hex.substring(12, 16)}-'
        '${hex.substring(16, 20)}-${hex.substring(20)}';
  }
}
