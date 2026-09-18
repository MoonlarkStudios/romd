import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

enum PlayActivityEnableScope { futureOnly, includeStoredSessions }

final class LocalPlaySession {
  const LocalPlaySession({
    required this.sessionId,
    required this.localProfileId,
    required this.serverInstanceId,
    required this.clientId,
    required this.titleId,
    required this.releaseId,
    required this.startedAt,
    required this.endedAt,
    required this.activeDurationSeconds,
  });

  final String sessionId;
  final String localProfileId;
  final RomdServerInstanceId serverInstanceId;
  final String clientId;
  final String titleId;
  final String releaseId;
  final DateTime startedAt;
  final DateTime? endedAt;
  final int? activeDurationSeconds;
}

abstract interface class PlayActivityRepository {
  Future<bool> isSyncEnabled();

  Future<void> setSyncEnabled({
    required bool enabled,
    PlayActivityEnableScope scope = PlayActivityEnableScope.futureOnly,
  });

  Future<LocalPlaySession?> recordStarted({
    required ResolvedPlayTarget target,
    required DateTime startedAt,
  });

  Future<void> recordEnded({
    required String sessionId,
    required DateTime endedAt,
    required int activeDurationSeconds,
  });

  Future<List<LocalPlaySession>> pendingSnapshots({int limit = 50});

  Future<void> markSynced(String sessionId);

  Future<void> markAttemptFailed(String sessionId, DateTime retryAt);

  Future<void> deleteSession(String sessionId);

  Future<void> clearSessions();
}
