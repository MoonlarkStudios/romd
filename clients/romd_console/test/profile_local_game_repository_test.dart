import 'dart:io';

import 'package:drift/drift.dart' hide isNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/local_profiles/profile_local_game_repository.dart';
import 'package:romd_console/src/domain/profile_local_game.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';

void main() {
  const instanceA = '11111111-1111-4111-8111-111111111111';
  const instanceB = '22222222-2222-4222-8222-222222222222';
  final releaseA = RomdPublicId.tryParse(RomdPublicId.encode(101))!;
  final releaseB = RomdPublicId.tryParse(RomdPublicId.encode(102))!;
  final titleA = RomdPublicId.tryParse(RomdPublicId.encode(201))!;
  final titleB = RomdPublicId.tryParse(RomdPublicId.encode(202))!;
  final serverA = RomdServerInstanceId.tryParse(instanceA)!;
  final serverB = RomdServerInstanceId.tryParse(instanceB)!;
  final acquiredAt = DateTime.utc(2026, 7, 17, 12);
  late AppDatabase database;
  late DriftProfileLocalGameRepository repository;

  setUp(() async {
    database = AppDatabase(NativeDatabase.memory());
    repository = DriftProfileLocalGameRepository(database: database);
    await _seedAuthority(database, 'profile-1', instanceA, acquiredAt);
    await _seedAuthority(database, 'profile-2', instanceB, acquiredAt);
  });

  tearDown(() => database.close());

  ProfileLocalGameKey key({
    String profileId = 'profile-1',
    RomdServerInstanceId? server,
    RomdPublicId? release,
  }) => ProfileLocalGameKey(
    localProfileId: profileId,
    serverInstanceId: server ?? serverA,
    releaseId: release ?? releaseA,
  );

  test('find strictly round-trips one authority-scoped reference', () async {
    await _insertGame(
      database,
      profileId: 'profile-1',
      instanceId: instanceA,
      releaseId: releaseA.value,
      titleId: titleA.value,
      acquiredAt: acquiredAt,
      state: 'authorized',
    );

    final result = await repository.find(key());
    final game = (result as ProfileLocalGameFound).game;
    expect(game.titleId, titleA);
    expect(game.authorization, ProfileGameAuthorization.authorized);
    expect(game.key.serverInstanceId, serverA);
  });

  test('missing row is explicit and revoke never creates it', () async {
    expect(await repository.find(key()), isA<ProfileLocalGameNotFound>());

    final update = await repository.updateAuthorization(
      key: key(),
      expectedTitleId: titleA,
      authorization: ProfileGameAuthorization.revoked,
      lastCheckedAt: acquiredAt.add(const Duration(minutes: 1)),
    );

    expect(update, ProfileLocalGameUpdateResult.notFound);
    expect(await database.select(database.profileLocalGames).get(), isEmpty);
  });

  test('explicit revoke and restore preserve acquisition identity', () async {
    await _insertGame(
      database,
      profileId: 'profile-1',
      instanceId: instanceA,
      releaseId: releaseA.value,
      titleId: titleA.value,
      acquiredAt: acquiredAt,
      state: 'authorized',
    );
    final revokedAt = acquiredAt.add(const Duration(minutes: 1));
    expect(
      await repository.updateAuthorization(
        key: key(),
        expectedTitleId: titleA,
        authorization: ProfileGameAuthorization.revoked,
        lastCheckedAt: revokedAt,
      ),
      ProfileLocalGameUpdateResult.updated,
    );
    final restoredAt = revokedAt.add(const Duration(minutes: 1));
    expect(
      await repository.updateAuthorization(
        key: key(),
        expectedTitleId: titleA,
        authorization: ProfileGameAuthorization.authorized,
        lastCheckedAt: restoredAt,
      ),
      ProfileLocalGameUpdateResult.updated,
    );

    final row =
        (await database.select(database.profileLocalGames).get()).single;
    expect(row.authorizationState, 'authorized');
    expect(row.acquiredAt.isAtSameMomentAs(acquiredAt), isTrue);
    expect(row.titleId, titleA.value);
    expect(row.lastCheckedAt.isAtSameMomentAs(restoredAt), isTrue);
  });

  test('title mismatch and invalid timestamp make zero writes', () async {
    await _insertGame(
      database,
      profileId: 'profile-1',
      instanceId: instanceA,
      releaseId: releaseA.value,
      titleId: titleA.value,
      acquiredAt: acquiredAt,
      state: 'authorized',
    );

    expect(
      await repository.updateAuthorization(
        key: key(),
        expectedTitleId: titleB,
        authorization: ProfileGameAuthorization.revoked,
        lastCheckedAt: acquiredAt.add(const Duration(minutes: 1)),
      ),
      ProfileLocalGameUpdateResult.identityMismatch,
    );
    expect(
      await repository.updateAuthorization(
        key: key(),
        expectedTitleId: titleA,
        authorization: ProfileGameAuthorization.revoked,
        lastCheckedAt: acquiredAt.subtract(const Duration(minutes: 1)),
      ),
      ProfileLocalGameUpdateResult.identityMismatch,
    );
    final row =
        (await database.select(database.profileLocalGames).get()).single;
    expect(row.authorizationState, 'authorized');
    expect(row.lastCheckedAt.isAtSameMomentAs(acquiredAt), isTrue);
  });

  test('older or conflicting same-time decisions cannot win', () async {
    await _insertGame(
      database,
      profileId: 'profile-1',
      instanceId: instanceA,
      releaseId: releaseA.value,
      titleId: titleA.value,
      acquiredAt: acquiredAt,
      state: 'authorized',
    );
    final revokedAt = acquiredAt.add(const Duration(minutes: 2));
    await repository.updateAuthorization(
      key: key(),
      expectedTitleId: titleA,
      authorization: ProfileGameAuthorization.revoked,
      lastCheckedAt: revokedAt,
    );

    expect(
      await repository.updateAuthorization(
        key: key(),
        expectedTitleId: titleA,
        authorization: ProfileGameAuthorization.authorized,
        lastCheckedAt: revokedAt.subtract(const Duration(minutes: 1)),
      ),
      ProfileLocalGameUpdateResult.stale,
    );
    expect(
      await repository.updateAuthorization(
        key: key(),
        expectedTitleId: titleA,
        authorization: ProfileGameAuthorization.authorized,
        lastCheckedAt: revokedAt,
      ),
      ProfileLocalGameUpdateResult.stale,
    );
    final game = (await repository.find(key()) as ProfileLocalGameFound).game;
    expect(game.authorization, ProfileGameAuthorization.revoked);
  });

  test('list is isolated by both profile and server instance', () async {
    await _insertGame(
      database,
      profileId: 'profile-1',
      instanceId: instanceA,
      releaseId: releaseA.value,
      titleId: titleA.value,
      acquiredAt: acquiredAt,
      state: 'authorized',
    );
    await _insertGame(
      database,
      profileId: 'profile-2',
      instanceId: instanceB,
      releaseId: releaseA.value,
      titleId: titleB.value,
      acquiredAt: acquiredAt,
      state: 'revoked',
    );

    final first = await repository.listForProfileServer(
      localProfileId: 'profile-1',
      serverInstanceId: serverA,
    );
    final second = await repository.listForProfileServer(
      localProfileId: 'profile-2',
      serverInstanceId: serverB,
    );
    expect((first as ProfileLocalGameListFound).games.single.titleId, titleA);
    expect(
      (second as ProfileLocalGameListFound).games.single.authorization,
      ProfileGameAuthorization.revoked,
    );
  });

  test('one corrupt row fails the complete list closed', () async {
    await _insertGame(
      database,
      profileId: 'profile-1',
      instanceId: instanceA,
      releaseId: releaseA.value,
      titleId: titleA.value,
      acquiredAt: acquiredAt,
      state: 'authorized',
    );
    await _insertGame(
      database,
      profileId: 'profile-1',
      instanceId: instanceA,
      releaseId: 'not-a-public-id',
      titleId: titleB.value,
      acquiredAt: acquiredAt,
      state: 'revoked',
    );

    expect(
      await repository.listForProfileServer(
        localProfileId: 'profile-1',
        serverInstanceId: serverA,
      ),
      isA<ProfileLocalGameListProjectionInconsistent>(),
    );
  });

  test('reference count includes authorized and revoked rows', () async {
    await _seedAuthority(database, 'profile-2', instanceA, acquiredAt);
    await _insertGame(
      database,
      profileId: 'profile-1',
      instanceId: instanceA,
      releaseId: releaseA.value,
      titleId: titleA.value,
      acquiredAt: acquiredAt,
      state: 'authorized',
    );
    await _insertGame(
      database,
      profileId: 'profile-2',
      instanceId: instanceA,
      releaseId: releaseA.value,
      titleId: titleA.value,
      acquiredAt: acquiredAt,
      state: 'revoked',
    );

    expect(
      await repository.countReferences(
        serverInstanceId: serverA,
        releaseId: releaseA,
      ),
      2,
    );
  });

  test('delete removes only the exact profile authority reference', () async {
    await _insertGame(
      database,
      profileId: 'profile-1',
      instanceId: instanceA,
      releaseId: releaseA.value,
      titleId: titleA.value,
      acquiredAt: acquiredAt,
      state: 'authorized',
    );
    await _insertGame(
      database,
      profileId: 'profile-2',
      instanceId: instanceB,
      releaseId: releaseB.value,
      titleId: titleB.value,
      acquiredAt: acquiredAt,
      state: 'revoked',
    );

    expect(await repository.delete(key()), isTrue);
    expect(await repository.find(key()), isA<ProfileLocalGameNotFound>());
    expect(
      await database.select(database.profileLocalGames).get(),
      hasLength(1),
    );
  });

  test('rows survive a file-backed database restart', () async {
    await database.close();
    final directory = await Directory.systemTemp.createTemp('romd-games-');
    final file = File('${directory.path}/console.sqlite');
    try {
      final firstDatabase = AppDatabase(NativeDatabase(file));
      await _seedAuthority(
        firstDatabase,
        'restart-profile',
        instanceA,
        acquiredAt,
      );
      await _insertGame(
        firstDatabase,
        profileId: 'restart-profile',
        instanceId: instanceA,
        releaseId: releaseA.value,
        titleId: titleA.value,
        acquiredAt: acquiredAt,
        state: 'revoked',
      );
      await firstDatabase.close();

      final reopened = AppDatabase(NativeDatabase(file));
      final reopenedRepository = DriftProfileLocalGameRepository(
        database: reopened,
      );
      final result = await reopenedRepository.find(
        ProfileLocalGameKey(
          localProfileId: 'restart-profile',
          serverInstanceId: serverA,
          releaseId: releaseA,
        ),
      );
      expect(
        (result as ProfileLocalGameFound).game.authorization,
        ProfileGameAuthorization.revoked,
      );
      await reopened.close();
    } finally {
      await directory.delete(recursive: true);
    }
  });
}

Future<void> _seedAuthority(
  AppDatabase database,
  String profileId,
  String instanceId,
  DateTime timestamp,
) async {
  await database
      .into(database.serverConnections)
      .insertOnConflictUpdate(
        ServerConnectionsCompanion.insert(
          instanceId: instanceId,
          lastKnownOrigin: 'https://$instanceId.example',
          firstSeenAt: timestamp,
          lastSeenAt: timestamp,
        ),
      );
  await database
      .into(database.localProfiles)
      .insertOnConflictUpdate(
        LocalProfilesCompanion.insert(
          id: profileId,
          displayName: profileId,
          avatarKey: 'default',
          accentColor: 0xff1fbf8f,
          romdServerOrigin: const Value(''),
          entryMode: 'open',
          createdAt: timestamp,
          updatedAt: timestamp,
          selectedServerInstanceId: Value(instanceId),
        ),
      );
}

Future<void> _insertGame(
  AppDatabase database, {
  required String profileId,
  required String instanceId,
  required String releaseId,
  required String titleId,
  required DateTime acquiredAt,
  required String state,
}) => database
    .into(database.profileLocalGames)
    .insert(
      ProfileLocalGamesCompanion.insert(
        localProfileId: profileId,
        serverInstanceId: instanceId,
        releaseId: releaseId,
        titleId: titleId,
        authorizationState: state,
        acquiredAt: acquiredAt,
        lastCheckedAt: acquiredAt,
      ),
    );
