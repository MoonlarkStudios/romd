import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/session/data/play_activity_sync_service.dart';
import 'package:romd_console/src/play/session/domain/play_activity.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

void main() {
  test('disabled sync never calls the server', () async {
    final repository = _Repository(enabled: false, pending: [_snapshot()]);
    final api = _Api();

    await _service(repository, api).syncPending();

    expect(api.uploaded, isEmpty);
    expect(repository.synced, isEmpty);
    expect(repository.failed, isEmpty);
  });

  test('successful upload removes the exact outbox entry', () async {
    final snapshot = _snapshot();
    final repository = _Repository(enabled: true, pending: [snapshot]);
    final api = _Api();

    await _service(repository, api).syncPending();

    expect(api.uploaded, [snapshot.sessionId]);
    expect(repository.synced, [snapshot.sessionId]);
    expect(repository.failed, isEmpty);
  });

  test('failed upload remains queued with bounded retry time', () async {
    final snapshot = _snapshot();
    final repository = _Repository(enabled: true, pending: [snapshot]);
    final api = _Api(throwOnUpload: true);
    final now = DateTime.utc(2026, 9, 14, 12);

    await _service(repository, api, now: now).syncPending();

    expect(repository.synced, isEmpty);
    expect(repository.failed.single.sessionId, snapshot.sessionId);
    expect(
      repository.failed.single.retryAt,
      now.add(const Duration(seconds: 30)),
    );
  });
}

PlayActivitySyncService _service(
  _Repository repository,
  _Api api, {
  DateTime? now,
}) => PlayActivitySyncService(
  repository: repository,
  apiClient: api,
  authenticatedSession: _Session(),
  now: now == null ? null : () => now,
);

LocalPlaySession _snapshot() => LocalPlaySession(
  sessionId: '11111111-2222-4333-8444-555555555555',
  localProfileId: 'profile-1',
  serverInstanceId: RomdServerInstanceId.tryParse(
    '11111111-1111-4111-8111-111111111111',
  )!,
  clientId: 'ottercade.console',
  titleId: 'title-1',
  releaseId: 'release-1',
  startedAt: DateTime.utc(2026, 9, 14, 12),
  endedAt: DateTime.utc(2026, 9, 14, 12, 2),
  activeDurationSeconds: 120,
);

final class _Repository implements PlayActivityRepository {
  _Repository({required this.enabled, required this.pending});

  bool enabled;
  final List<LocalPlaySession> pending;
  final List<String> synced = [];
  final List<({String sessionId, DateTime retryAt})> failed = [];

  @override
  Future<bool> isSyncEnabled() async => enabled;

  @override
  Future<List<LocalPlaySession>> pendingSnapshots({int limit = 50}) async =>
      pending.take(limit).toList();

  @override
  Future<void> markSynced(String sessionId) async => synced.add(sessionId);

  @override
  Future<void> markAttemptFailed(String sessionId, DateTime retryAt) async =>
      failed.add((sessionId: sessionId, retryAt: retryAt));

  @override
  Future<void> clearSessions() async {}

  @override
  Future<void> deleteSession(String sessionId) async {}

  @override
  Future<void> recordEnded({
    required String sessionId,
    required DateTime endedAt,
    required int activeDurationSeconds,
  }) async {}

  @override
  Future<LocalPlaySession?> recordStarted({
    required ResolvedPlayTarget target,
    required DateTime startedAt,
  }) async => null;

  @override
  Future<void> setSyncEnabled({
    required bool enabled,
    PlayActivityEnableScope scope = PlayActivityEnableScope.futureOnly,
  }) async => this.enabled = enabled;
}

final class _Api implements PlayActivityApiClient {
  _Api({this.throwOnUpload = false});

  final bool throwOnUpload;
  final List<String> uploaded = [];

  @override
  Future<void> upsertPlaySession({
    required String accessToken,
    required LocalPlaySession session,
  }) async {
    uploaded.add(session.sessionId);
    if (throwOnUpload) throw StateError('offline');
  }
}

final class _Session implements AuthenticatedRequestExecutor {
  @override
  int get credentialGeneration => 1;

  @override
  ConsumerLoginSession? get session => null;

  @override
  Future<T> execute<T>({
    required Future<T> Function(String accessToken) request,
    AuthenticatedReplayPolicy replayPolicy = AuthenticatedReplayPolicy.never,
    InvalidTokenErrorTest? isInvalidTokenError,
    bool Function(T result)? isInvalidTokenResult,
  }) => request('token');
}
