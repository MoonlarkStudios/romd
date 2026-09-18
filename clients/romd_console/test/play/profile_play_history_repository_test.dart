import 'dart:async';
import 'dart:io';

import 'package:drift/drift.dart' hide isNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/play/content/data/content_file_store.dart';
import 'package:romd_console/src/play/content/data/drift_local_install_repository.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/session/data/drift_profile_play_history_repository.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/profile_play_history.dart';

final _serverA = RomdServerInstanceId.tryParse(
  '11111111-1111-4111-8111-111111111111',
)!;
final _serverB = RomdServerInstanceId.tryParse(
  '22222222-2222-4222-8222-222222222222',
)!;
final _title = RomdPublicId.tryParse(RomdPublicId.encode(201))!;
final _titleB = RomdPublicId.tryParse(RomdPublicId.encode(202))!;
final _releaseA = RomdPublicId.tryParse(RomdPublicId.encode(101))!;
final _releaseB = RomdPublicId.tryParse(RomdPublicId.encode(102))!;
final _releaseC = RomdPublicId.tryParse(RomdPublicId.encode(103))!;
final _now = DateTime.utc(2026, 7, 18, 10);
const _sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';

void main() {
  late AppDatabase database;
  late Directory base;

  setUp(() async {
    database = AppDatabase(NativeDatabase.memory());
    base = await Directory.systemTemp.createTemp('romd-history-');
    await _seedAuthority(database, profileId: 'profile-a', server: _serverA);
  });

  tearDown(() async {
    await database.close();
    await base.delete(recursive: true);
  });

  DriftProfilePlayHistoryRepository repository({
    String profileId = 'profile-a',
    RomdServerInstanceId? server,
  }) {
    final selectedServer = server ?? _serverA;
    return DriftProfilePlayHistoryRepository(
      database: database,
      installs: _installs(database, base, selectedServer),
      authority: _authority(profileId, selectedServer),
    );
  }

  test('first and repeat completion count once with exact identity', () async {
    final subject = repository();
    final target = _target('profile-a', _serverA, _releaseA);

    expect(
      await subject.recordEnded(target: target, endedAt: _now),
      ProfilePlayHistoryRecordResult.recorded,
    );
    expect(
      await subject.recordEnded(
        target: target,
        endedAt: _now.add(const Duration(hours: 1)),
      ),
      ProfilePlayHistoryRecordResult.recorded,
    );

    final found = await subject.find(
      localProfileId: 'profile-a',
      serverInstanceId: _serverA,
      titleId: _title,
    );
    final history = (found as ProfilePlayHistoryFound).history;
    expect(history.playCount, 2);
    expect(history.localProfileId, 'profile-a');
    expect(history.serverInstanceId, _serverA);
    expect(history.titleId, _title);
    expect(history.lastReleaseId, _releaseA);
    expect(
      history.lastPlayedAt.isAtSameMomentAs(_now.add(const Duration(hours: 1))),
      isTrue,
    );
  });

  test(
    'clock rollback increments count and equal times choose canonical release',
    () async {
      final subject = repository();
      final releases = <RomdPublicId>[_releaseA, _releaseB]
        ..sort((a, b) => a.value.compareTo(b.value));
      final canonical = releases.first;
      final other = releases.last;
      await subject.recordEnded(
        target: _target('profile-a', _serverA, other),
        endedAt: _now,
      );
      await subject.recordEnded(
        target: _target('profile-a', _serverA, canonical),
        endedAt: _now.subtract(const Duration(days: 1)),
      );
      await subject.recordEnded(
        target: _target('profile-a', _serverA, canonical),
        endedAt: _now,
      );

      final history =
          (await subject.find(
                    localProfileId: 'profile-a',
                    serverInstanceId: _serverA,
                    titleId: _title,
                  )
                  as ProfilePlayHistoryFound)
              .history;
      expect(history.playCount, 3);
      expect(history.lastPlayedAt.isAtSameMomentAs(_now), isTrue);
      expect(history.lastReleaseId, canonical);
    },
  );

  test(
    'profile and server scopes remain isolated for reused public ids',
    () async {
      await _seedAuthority(database, profileId: 'profile-b', server: _serverA);
      await _seedAuthority(database, profileId: 'profile-a', server: _serverB);
      final a = repository();
      final profileB = repository(profileId: 'profile-b');
      final serverB = repository(server: _serverB);
      await a.recordEnded(
        target: _target('profile-a', _serverA, _releaseA),
        endedAt: _now,
      );
      await profileB.recordEnded(
        target: _target('profile-b', _serverA, _releaseA),
        endedAt: _now.add(const Duration(hours: 1)),
      );
      await serverB.recordEnded(
        target: _target('profile-a', _serverB, _releaseA),
        endedAt: _now.add(const Duration(hours: 2)),
      );

      expect(
        await database.select(database.profilePlayHistories).get(),
        hasLength(3),
      );
      final scoped =
          ((await a.find(
                    localProfileId: 'profile-a',
                    serverInstanceId: _serverA,
                    titleId: _title,
                  ))
                  as ProfilePlayHistoryFound)
              .history;
      expect(scoped.lastPlayedAt.isAtSameMomentAs(_now), isTrue);
    },
  );

  test(
    'profile deletion cascades history and completion never recreates it',
    () async {
      final subject = repository();
      final target = _target('profile-a', _serverA, _releaseA);
      await subject.recordEnded(target: target, endedAt: _now);
      await (database.delete(
        database.localProfiles,
      )..where((row) => row.id.equals('profile-a'))).go();

      expect(
        await database.select(database.profilePlayHistories).get(),
        isEmpty,
      );
      expect(
        await subject.recordEnded(
          target: target,
          endedAt: _now.add(const Duration(hours: 1)),
        ),
        ProfilePlayHistoryRecordResult.ownerMissing,
      );
      expect(
        await database.select(database.profilePlayHistories).get(),
        isEmpty,
      );
    },
  );

  test(
    'revocation hides recent history and restore reveals it unchanged',
    () async {
      final subject = repository();
      await _seedEligible(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseA,
        acquiredAt: _now,
        installedAt: _now,
      );
      await subject.recordEnded(
        target: _target('profile-a', _serverA, _releaseA),
        endedAt: _now,
      );
      final events = StreamIterator<ProfileRecentGamesResult>(
        subject.watchRecent(),
      );
      addTearDown(events.cancel);
      expect(
        (await _nextReady(events)).games.single.install.releaseId,
        _releaseA.value,
      );

      await database
          .update(database.profileLocalGames)
          .write(
            const ProfileLocalGamesCompanion(
              authorizationState: Value('revoked'),
            ),
          );
      expect((await _nextReady(events)).games, isEmpty);
      expect(
        await database.select(database.profilePlayHistories).get(),
        hasLength(1),
      );

      await database
          .update(database.profileLocalGames)
          .write(
            const ProfileLocalGamesCompanion(
              authorizationState: Value('authorized'),
            ),
          );
      final restored = await _nextReady(events);
      expect(restored.games.single.lastPlayedAt.isAtSameMomentAs(_now), isTrue);
      expect(restored.games.single.install.releaseId, _releaseA.value);

      await database.delete(database.profileLocalGames).go();
      expect((await _nextReady(events)).games, isEmpty);
      expect(
        await database.select(database.profilePlayHistories).get(),
        hasLength(1),
      );
    },
  );

  test(
    'missing last release chooses newest acquisition then install then id',
    () async {
      final subject = repository();
      for (final candidate
          in <({RomdPublicId release, DateTime acquired, DateTime installed})>[
            (
              release: _releaseB,
              acquired: _now.add(const Duration(hours: 2)),
              installed: _now,
            ),
            (
              release: _releaseC,
              acquired: _now.add(const Duration(hours: 2)),
              installed: _now.add(const Duration(hours: 1)),
            ),
          ]) {
        await _seedEligible(
          database,
          base,
          profileId: 'profile-a',
          server: _serverA,
          release: candidate.release,
          acquiredAt: candidate.acquired,
          installedAt: candidate.installed,
        );
      }
      await subject.recordEnded(
        target: _target('profile-a', _serverA, _releaseA),
        endedAt: _now,
      );

      final ready =
          await subject.watchRecent().first as ProfileRecentGamesReady;
      expect(ready.games.single.install.releaseId, _releaseC.value);
      expect(ready.games.single.eligibleReleaseCount, 2);
    },
  );

  test(
    'recent projection isolates reused ids by exact profile and server',
    () async {
      await _seedAuthority(database, profileId: 'profile-b', server: _serverB);
      await _seedEligible(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseA,
        acquiredAt: _now,
        installedAt: _now,
        titleName: 'Server A Title',
      );
      await _seedEligible(
        database,
        base,
        profileId: 'profile-b',
        server: _serverB,
        release: _releaseA,
        acquiredAt: _now,
        installedAt: _now,
        titleName: 'Server B Title',
      );
      await repository().recordEnded(
        target: _target('profile-a', _serverA, _releaseA),
        endedAt: _now,
      );
      await repository(profileId: 'profile-b', server: _serverB).recordEnded(
        target: _target('profile-b', _serverB, _releaseA),
        endedAt: _now,
      );

      final profileA =
          await repository().watchRecent().first as ProfileRecentGamesReady;
      final profileB =
          await repository(
                profileId: 'profile-b',
                server: _serverB,
              ).watchRecent().first
              as ProfileRecentGamesReady;
      final wrongAuthority =
          await repository(
                profileId: 'profile-b',
                server: _serverA,
              ).watchRecent().first
              as ProfileRecentGamesReady;

      expect(profileA.games.single.install.titleName, 'Server A Title');
      expect(profileA.games.single.install.serverInstanceId, _serverA.value);
      expect(profileB.games.single.install.titleName, 'Server B Title');
      expect(profileB.games.single.install.serverInstanceId, _serverB.value);
      expect(wrongAuthority.games, isEmpty);
    },
  );

  test(
    'non-installed candidate is omitted while other eligible history remains',
    () async {
      final subject = repository();
      await _seedEligible(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseA,
        acquiredAt: _now,
        installedAt: _now,
        state: InstallState.installing,
      );
      await _seedEligible(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseC,
        acquiredAt: _now,
        installedAt: _now,
        title: _titleB,
        titleName: 'Eligible Title',
      );
      await subject.recordEnded(
        target: _target('profile-a', _serverA, _releaseA),
        endedAt: _now,
      );
      await subject.recordEnded(
        target: _target('profile-a', _serverA, _releaseC, title: _titleB),
        endedAt: _now.add(const Duration(hours: 1)),
      );

      final ready =
          await subject.watchRecent().first as ProfileRecentGamesReady;
      expect(ready.games, hasLength(1));
      expect(ready.games.single.install.titleId, _titleB.value);
      expect(ready.games.single.install.titleName, 'Eligible Title');
      expect(
        await database.select(database.profilePlayHistories).get(),
        hasLength(2),
      );
    },
  );

  test('foreign install root fails closed without exposing metadata', () async {
    final subject = repository();
    await _seedEligible(
      database,
      base,
      profileId: 'profile-a',
      server: _serverA,
      release: _releaseA,
      acquiredAt: _now,
      installedAt: _now,
      titleName: 'Must Not Leak',
    );
    await subject.recordEnded(
      target: _target('profile-a', _serverA, _releaseA),
      endedAt: _now,
    );
    final foreignRoot =
        ContentFileStore(
          baseDir: base,
          serverInstanceId: _serverB.value,
        ).contentRoot(
          platformShortName: 'snes',
          titleId: _title.value,
          releaseId: _releaseA.value,
        );
    await database.customStatement(
      'UPDATE local_installs SET content_root = ? '
      'WHERE server_instance_id = ? AND release_id = ?',
      <Object?>[foreignRoot.path, _serverA.value, _releaseA.value],
    );

    expect(
      await subject.watchRecent().first,
      isA<ProfileRecentGamesInconsistent>(),
    );
  });

  test('corrupt count or public id fails closed explicitly', () async {
    await database.customStatement('PRAGMA ignore_check_constraints = ON');
    await database.customStatement('''
INSERT INTO profile_play_histories VALUES (
  'profile-a', '${_serverA.value}', '${_title.value}', 'not-public',
  ${_now.millisecondsSinceEpoch}, 0
)
''');
    final subject = repository();
    expect(
      await subject.find(
        localProfileId: 'profile-a',
        serverInstanceId: _serverA,
        titleId: _title,
      ),
      isA<ProfilePlayHistoryInconsistent>(),
    );
    expect(
      await subject.watchRecent().first,
      isA<ProfileRecentGamesInconsistent>(),
    );
  });

  test('history survives process restart', () async {
    await database.close();
    final restartBase = await Directory.systemTemp.createTemp(
      'romd-history-restart-',
    );
    addTearDown(() => restartBase.delete(recursive: true));
    final file = File('${restartBase.path}/history.sqlite');
    final first = AppDatabase(NativeDatabase(file));
    await _seedAuthority(first, profileId: 'profile-a', server: _serverA);
    final firstRepo = DriftProfilePlayHistoryRepository(
      database: first,
      installs: _installs(first, restartBase, _serverA),
      authority: _authority('profile-a', _serverA),
    );
    await firstRepo.recordEnded(
      target: _target('profile-a', _serverA, _releaseA),
      endedAt: _now,
    );
    await first.close();

    final reopened = AppDatabase(NativeDatabase(file));
    final reopenedRepo = DriftProfilePlayHistoryRepository(
      database: reopened,
      installs: _installs(reopened, restartBase, _serverA),
      authority: _authority('profile-a', _serverA),
    );
    expect(
      await reopenedRepo.find(
        localProfileId: 'profile-a',
        serverInstanceId: _serverA,
        titleId: _title,
      ),
      isA<ProfilePlayHistoryFound>(),
    );
    await reopened.close();
  });

  test('no selected server projects an empty recent list', () async {
    final subject = DriftProfilePlayHistoryRepository(
      database: database,
      installs: _installs(database, base, _serverA),
      authority: null,
    );
    final ready = await subject.watchRecent().first as ProfileRecentGamesReady;
    expect(ready.games, isEmpty);
  });
}

Future<ProfileRecentGamesReady> _nextReady(
  StreamIterator<ProfileRecentGamesResult> events,
) async {
  expect(await events.moveNext(), isTrue);
  return events.current as ProfileRecentGamesReady;
}

InstallAuthorityContext _authority(
  String profileId,
  RomdServerInstanceId server,
) => InstallAuthorityContext(
  localProfileId: profileId,
  connection: ProfileServerConnection(
    instanceId: server,
    origin: Uri.parse(
      server == _serverA ? 'https://a.example' : 'https://b.example',
    ),
    firstSeenAt: _now,
    lastSeenAt: _now,
  ),
  generation: 1,
);

Future<void> _seedAuthority(
  AppDatabase database, {
  required String profileId,
  required RomdServerInstanceId server,
}) async {
  await database
      .into(database.serverConnections)
      .insert(
        ServerConnectionsCompanion.insert(
          instanceId: server.value,
          lastKnownOrigin: server == _serverA
              ? 'https://a.example'
              : 'https://b.example',
          firstSeenAt: _now,
          lastSeenAt: _now,
        ),
        mode: InsertMode.insertOrIgnore,
      );
  await database
      .into(database.localProfiles)
      .insert(
        LocalProfilesCompanion.insert(
          id: profileId,
          displayName: profileId,
          avatarKey: 'default',
          accentColor: 1,
          entryMode: 'open',
          createdAt: _now,
          updatedAt: _now,
          selectedServerInstanceId: Value(server.value),
          serverSelectionGeneration: const Value(1),
        ),
        mode: InsertMode.insertOrIgnore,
      );
}

DriftLocalInstallRepository _installs(
  AppDatabase database,
  Directory base,
  RomdServerInstanceId server,
) => DriftLocalInstallRepository(
  database: database,
  serverInstanceId: server,
  fileStore: ContentFileStore(baseDir: base, serverInstanceId: server.value),
);

Future<void> _seedEligible(
  AppDatabase database,
  Directory base, {
  required String profileId,
  required RomdServerInstanceId server,
  required RomdPublicId release,
  required DateTime acquiredAt,
  required DateTime installedAt,
  RomdPublicId? title,
  String titleName = 'Chrono Trigger',
  InstallState state = InstallState.installed,
}) async {
  final selectedTitle = title ?? _title;
  final installs = _installs(database, base, server);
  final root = ContentFileStore(baseDir: base, serverInstanceId: server.value)
      .contentRoot(
        platformShortName: 'snes',
        titleId: selectedTitle.value,
        releaseId: release.value,
      );
  await installs.upsert(
    LocalInstall(
      serverInstanceId: server.value,
      releaseId: release.value,
      titleId: selectedTitle.value,
      titleName: titleName,
      platformId: 'snes',
      platformName: 'SNES',
      platformShortName: 'snes',
      coverUrl: null,
      releaseName: release.value,
      releaseRevision: null,
      contentRoot: root.path,
      launchRelativePath: 'game.sfc',
      sizeBytes: 5,
      primarySha256: _sha,
      manifestFingerprint: 'fingerprint-${release.value}',
      state: state,
      installMode: 'permanent',
      items: const <InstalledItem>[
        InstalledItem(relativePath: 'game.sfc', sizeBytes: 5, sha256: _sha),
      ],
      installedAt: installedAt,
      lastPlayedAt: DateTime.utc(2020),
    ),
  );
  await database
      .into(database.profileLocalGames)
      .insert(
        ProfileLocalGamesCompanion.insert(
          localProfileId: profileId,
          serverInstanceId: server.value,
          releaseId: release.value,
          titleId: selectedTitle.value,
          authorizationState: ProfileGameAuthorization.authorized.name,
          acquiredAt: acquiredAt,
          lastCheckedAt: acquiredAt,
        ),
      );
}

ResolvedPlayTarget _target(
  String profileId,
  RomdServerInstanceId server,
  RomdPublicId release, {
  RomdPublicId? title,
}) {
  final selectedTitle = title ?? _title;
  return ResolvedPlayTarget(
    serverInstanceId: server.value,
    releaseId: release.value,
    titleId: selectedTitle.value,
    platformShortName: 'snes',
    displayName: 'Chrono Trigger',
    localProfileId: profileId,
    contentRoot: '/content',
    launchAbsolutePath: '/content/game.sfc',
    saveRoot: '/saves',
    stateRoot: '/states',
    configRoot: '/config',
  );
}
