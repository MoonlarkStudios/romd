import 'dart:math';

import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/session/data/drift_play_activity_repository.dart';
import 'package:romd_console/src/play/session/domain/play_activity.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

final _instanceId = RomdServerInstanceId.tryParse(
  '11111111-1111-4111-8111-111111111111',
)!;

void main() {
  late AppDatabase database;
  late DriftPlayActivityRepository repository;
  final now = DateTime.utc(2026, 9, 14, 12);

  setUp(() async {
    database = AppDatabase(NativeDatabase.memory());
    await database.customStatement('''
INSERT INTO server_connections VALUES (
  '${_instanceId.value}', 'https://library.example',
  ${now.millisecondsSinceEpoch}, ${now.millisecondsSinceEpoch}
)
''');
    await database.customStatement('''
INSERT INTO local_profiles (
  id, display_name, avatar_key, accent_color, romd_server_origin,
  entry_mode, created_at, updated_at, last_used_at,
  selected_server_instance_id, server_selection_generation
) VALUES (
  'profile-1', 'Player', 'default', 1, '', 'open',
  ${now.millisecondsSinceEpoch}, ${now.millisecondsSinceEpoch}, NULL,
  '${_instanceId.value}', 1
)
''');
    repository = DriftPlayActivityRepository(
      database: database,
      authority: InstallAuthorityContext(
        localProfileId: 'profile-1',
        connection: ProfileServerConnection(
          instanceId: _instanceId,
          origin: Uri.parse('https://library.example'),
          firstSeenAt: now,
          lastSeenAt: now,
        ),
        generation: 1,
      ),
      now: () => now,
      random: Random(42),
    );
  });

  tearDown(() => database.close());

  test(
    'sync is private by default and future-only does not upload history',
    () async {
      final stored = await repository.recordStarted(
        target: _target(),
        startedAt: now,
      );

      expect(stored, isNotNull);
      expect(await repository.isSyncEnabled(), isFalse);
      expect(await repository.pendingSnapshots(), isEmpty);

      await repository.setSyncEnabled(enabled: true);

      expect(await repository.pendingSnapshots(), isEmpty);
      final future = await repository.recordStarted(
        target: _target(),
        startedAt: now.add(const Duration(minutes: 1)),
      );
      expect(
        (await repository.pendingSnapshots()).map((item) => item.sessionId),
        <String>[future!.sessionId],
      );
    },
  );

  test(
    'one session snapshot is enriched on exit without duplicating it',
    () async {
      await repository.setSyncEnabled(enabled: true);
      final session = (await repository.recordStarted(
        target: _target(),
        startedAt: now,
      ))!;

      await repository.recordEnded(
        sessionId: session.sessionId,
        endedAt: now.add(const Duration(minutes: 3)),
        activeDurationSeconds: 179,
      );

      final pending = await repository.pendingSnapshots();
      expect(pending, hasLength(1));
      expect(pending.single.sessionId, session.sessionId);
      expect(
        pending.single.endedAt?.toUtc(),
        now.add(const Duration(minutes: 3)),
      );
      expect(pending.single.activeDurationSeconds, 179);
      expect(
        await database.select(database.localPlaySessions).get(),
        hasLength(1),
      );
    },
  );

  test('disabling cancels queued uploads and keeps later exit local', () async {
    await repository.setSyncEnabled(enabled: true);
    final session = (await repository.recordStarted(
      target: _target(),
      startedAt: now,
    ))!;
    expect(await repository.pendingSnapshots(), hasLength(1));

    await repository.setSyncEnabled(enabled: false);
    await repository.recordEnded(
      sessionId: session.sessionId,
      endedAt: now.add(const Duration(minutes: 1)),
      activeDurationSeconds: 60,
    );

    expect(await repository.pendingSnapshots(), isEmpty);
    final local =
        (await database.select(database.localPlaySessions).get()).single;
    expect(local.endedAt!.toUtc(), now.add(const Duration(minutes: 1)));
    expect(local.syncEligible, isFalse);
  });

  test(
    'include stored is explicit and hard deletion removes its outbox row',
    () async {
      final session = (await repository.recordStarted(
        target: _target(),
        startedAt: now,
      ))!;

      await repository.setSyncEnabled(
        enabled: true,
        scope: PlayActivityEnableScope.includeStoredSessions,
      );
      expect(await repository.pendingSnapshots(), hasLength(1));

      await repository.deleteSession(session.sessionId);

      expect(await database.select(database.localPlaySessions).get(), isEmpty);
      expect(await database.select(database.playActivityOutbox).get(), isEmpty);
    },
  );

  test(
    'rejects targets outside the exact profile and server authority',
    () async {
      expect(
        await repository.recordStarted(
          target: _target(localProfileId: 'another-profile'),
          startedAt: now,
        ),
        isNull,
      );
      expect(
        await repository.recordStarted(
          target: _target(
            serverInstanceId: '22222222-2222-4222-8222-222222222222',
          ),
          startedAt: now,
        ),
        isNull,
      );
      expect(await database.select(database.localPlaySessions).get(), isEmpty);
    },
  );
}

ResolvedPlayTarget _target({
  String localProfileId = 'profile-1',
  String? serverInstanceId,
}) => ResolvedPlayTarget(
  serverInstanceId: serverInstanceId ?? _instanceId.value,
  releaseId: 'release-1',
  titleId: 'title-1',
  platformShortName: 'snes',
  displayName: 'Some Game',
  localProfileId: localProfileId,
  contentRoot: '/content',
  launchAbsolutePath: '/content/game.sfc',
  saveRoot: '/save',
  stateRoot: '/state',
  configRoot: '/config',
);
