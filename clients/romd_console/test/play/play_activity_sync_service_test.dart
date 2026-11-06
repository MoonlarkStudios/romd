import 'dart:async';

import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/consumer_api_client.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/profile_session_coordinator.dart';
import 'package:romd_console/src/domain/consumer_account.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/session/data/drift_play_activity_repository.dart';
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
  for (final startUploadFails in [false, true]) {
    test(
      'exit queued during ${startUploadFails ? 'failed' : 'successful'} start upload is drained',
      () async {
        final fixture = await _DriftFixture.create();
        addTearDown(fixture.database.close);
        await fixture.repository.setSyncEnabled(enabled: true);
        final started = (await fixture.repository.recordStarted(
          target: fixture.target,
          startedAt: fixture.now,
        ))!;
        final uploading = Completer<void>();
        final resume = Completer<void>();
        final api = _Api(
          onUpload: (snapshot) async {
            if (snapshot.endedAt == null) {
              uploading.complete();
              await resume.future;
              if (startUploadFails) throw StateError('offline');
            }
          },
        );
        final service = _service(fixture.repository, api, now: fixture.now);
        final sync = service.syncPending();
        await uploading.future;

        await fixture.repository.recordEnded(
          sessionId: started.sessionId,
          endedAt: fixture.now,
          activeDurationSeconds: 0,
        );
        final joinedSync = service.syncPending();
        resume.complete();
        await Future.wait([sync, joinedSync]);

        expect(api.sessions, hasLength(2));
        expect(api.sessions.first.endedAt, isNull);
        expect(api.sessions.last.endedAt?.toUtc(), fixture.now);
        expect(await fixture.repository.pendingSnapshots(), isEmpty);
        expect(
          await fixture.database
              .select(fixture.database.playActivityOutbox)
              .get(),
          isEmpty,
        );
      },
    );
  }

  test(
    'disabling while an upload is pending cancels subsequent queued uploads',
    () async {
      final fixture = await _DriftFixture.create();
      addTearDown(fixture.database.close);
      await fixture.repository.setSyncEnabled(enabled: true);
      final started = (await fixture.repository.recordStarted(
        target: fixture.target,
        startedAt: fixture.now,
      ))!;
      final uploading = Completer<void>();
      final resume = Completer<void>();
      final api = _Api(
        onUpload: (_) async {
          uploading.complete();
          await resume.future;
        },
      );
      final service = _service(fixture.repository, api);
      final sync = service.syncPending();
      await uploading.future;
      await fixture.repository.recordEnded(
        sessionId: started.sessionId,
        endedAt: fixture.now,
        activeDurationSeconds: 0,
      );
      await fixture.repository.setSyncEnabled(enabled: false);
      resume.complete();
      await sync;

      expect(api.sessions, hasLength(1));
      expect(await fixture.repository.pendingSnapshots(), isEmpty);
      final local =
          (await fixture.database
                  .select(fixture.database.localPlaySessions)
                  .get())
              .single;
      expect(local.endedAt?.toUtc(), fixture.now);
    },
  );
}

PlayActivitySyncService _service(
  PlayActivityRepository repository,
  PlayActivityApiClient api, {
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
  Future<void> markSynced(LocalPlaySession snapshot) async {
    synced.add(snapshot.sessionId);
    pending.remove(snapshot);
  }

  @override
  Future<void> markAttemptFailed(
    LocalPlaySession snapshot,
    DateTime retryAt,
  ) async {
    failed.add((sessionId: snapshot.sessionId, retryAt: retryAt));
    pending.remove(snapshot);
  }

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
  _Api({this.throwOnUpload = false, this.onUpload});

  final bool throwOnUpload;
  final Future<void> Function(LocalPlaySession)? onUpload;
  final List<String> uploaded = [];
  final List<LocalPlaySession> sessions = [];

  @override
  Future<void> upsertPlaySession({
    required String accessToken,
    required LocalPlaySession session,
  }) async {
    uploaded.add(session.sessionId);
    sessions.add(session);
    await onUpload?.call(session);
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

final class _DriftFixture {
  _DriftFixture(this.database, this.repository, this.now);

  final AppDatabase database;
  final DriftPlayActivityRepository repository;
  final DateTime now;

  ResolvedPlayTarget get target => ResolvedPlayTarget(
    serverInstanceId: _snapshot().serverInstanceId.value,
    releaseId: 'release-1',
    titleId: 'title-1',
    platformShortName: 'snes',
    displayName: 'Some Game',
    localProfileId: 'profile-1',
    contentRoot: '/content',
    launchAbsolutePath: '/content/game.sfc',
    saveRoot: '/save',
    stateRoot: '/state',
    configRoot: '/config',
  );

  static Future<_DriftFixture> create() async {
    final database = AppDatabase(NativeDatabase.memory());
    final now = DateTime.utc(2026, 9, 14, 12);
    final instance = _snapshot().serverInstanceId;
    await database.customStatement(
      'INSERT INTO server_connections VALUES (?, ?, ?, ?)',
      [
        instance.value,
        'https://library.example',
        now.millisecondsSinceEpoch,
        now.millisecondsSinceEpoch,
      ],
    );
    await database.customStatement(
      'INSERT INTO local_profiles ('
      'id, display_name, avatar_key, accent_color, romd_server_origin, '
      'entry_mode, created_at, updated_at, last_used_at, '
      'selected_server_instance_id, server_selection_generation'
      ') VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
      [
        'profile-1',
        'Player',
        'default',
        1,
        '',
        'open',
        now.millisecondsSinceEpoch,
        now.millisecondsSinceEpoch,
        null,
        instance.value,
        1,
      ],
    );
    final repository = DriftPlayActivityRepository(
      database: database,
      authority: InstallAuthorityContext(
        localProfileId: 'profile-1',
        connection: ProfileServerConnection(
          instanceId: instance,
          origin: Uri.parse('https://library.example'),
          firstSeenAt: now,
          lastSeenAt: now,
        ),
        generation: 1,
      ),
      now: () => now,
    );
    return _DriftFixture(database, repository, now);
  }
}
