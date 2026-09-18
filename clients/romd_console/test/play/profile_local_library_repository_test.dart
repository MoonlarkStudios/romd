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
import 'package:romd_console/src/play/content/data/drift_profile_local_library_repository.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/install_state.dart';
import 'package:romd_console/src/play/content/domain/local_install.dart';
import 'package:romd_console/src/play/content/domain/profile_local_library.dart';

final _serverA = RomdServerInstanceId.tryParse(
  '11111111-1111-4111-8111-111111111111',
)!;
final _serverB = RomdServerInstanceId.tryParse(
  '22222222-2222-4222-8222-222222222222',
)!;
final _releaseA = RomdPublicId.tryParse(RomdPublicId.encode(101))!;
final _releaseB = RomdPublicId.tryParse(RomdPublicId.encode(102))!;
final _releaseC = RomdPublicId.tryParse(RomdPublicId.encode(103))!;
final _titleA = RomdPublicId.tryParse(RomdPublicId.encode(201))!;
final _titleB = RomdPublicId.tryParse(RomdPublicId.encode(202))!;
final _now = DateTime.utc(2026, 7, 18, 10);
const _sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';

void main() {
  late AppDatabase database;
  late Directory base;

  setUp(() async {
    database = AppDatabase(NativeDatabase.memory());
    base = await Directory.systemTemp.createTemp('romd-local-library-');
    await _seedAuthority(database, profileId: 'profile-a', server: _serverA);
  });

  tearDown(() async {
    await database.close();
    await base.delete(recursive: true);
  });

  DriftProfileLocalLibraryRepository repository({
    String profileId = 'profile-a',
    RomdServerInstanceId? server,
    bool noServer = false,
  }) {
    final selected = server ?? _serverA;
    final installs = _installs(database, base, selected);
    return DriftProfileLocalLibraryRepository(
      database: database,
      installs: installs,
      authority: noServer ? null : _authority(profileId, selected),
    );
  }

  test('no selected server is an explicit non-library state', () async {
    expect(
      await repository(
        noServer: true,
      ).watchLibrary(sort: ProfileLocalLibrarySort.recentPlay).first,
      isA<ProfileLocalLibraryNoServer>(),
    );
    expect(
      await repository(noServer: true).readReleases(),
      isA<ProfileLocalReleasesNoServer>(),
    );
  });

  test(
    'exact profile and server references isolate reused physical identities',
    () async {
      await _seedAuthority(database, profileId: 'profile-b', server: _serverB);
      await _seedAuthority(database, profileId: 'profile-c', server: _serverA);
      await _seedGame(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseA,
        title: _titleA,
        titleName: 'Server A game',
      );
      await _seedGame(
        database,
        base,
        profileId: 'profile-b',
        server: _serverB,
        release: _releaseA,
        title: _titleA,
        titleName: 'Server B game',
      );

      final a =
          await repository()
                  .watchLibrary(sort: ProfileLocalLibrarySort.title)
                  .first
              as ProfileLocalLibraryReady;
      final b =
          await repository(
                profileId: 'profile-b',
                server: _serverB,
              ).watchLibrary(sort: ProfileLocalLibrarySort.title).first
              as ProfileLocalLibraryReady;
      final otherProfile =
          await repository(
                profileId: 'profile-c',
              ).watchLibrary(sort: ProfileLocalLibrarySort.title).first
              as ProfileLocalLibraryReady;

      expect(a.titles.single.install.titleName, 'Server A game');
      expect(b.titles.single.install.titleName, 'Server B game');
      expect(otherProfile.titles, isEmpty);
    },
  );

  test(
    'revocation stays visible and reacts without deleting local data',
    () async {
      await _seedGame(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseA,
        title: _titleA,
        titleName: 'Known game',
      );
      final events = StreamIterator<ProfileLocalLibraryResult>(
        repository().watchLibrary(sort: ProfileLocalLibrarySort.recentPlay),
      );
      addTearDown(events.cancel);
      expect(
        (await _nextReady(events)).titles.single.state,
        ProfileLocalLibraryState.ready,
      );

      await database
          .update(database.profileLocalGames)
          .write(
            const ProfileLocalGamesCompanion(
              authorizationState: Value('revoked'),
            ),
          );
      final revoked = await _nextReady(events);
      expect(revoked.titles.single.install.titleName, 'Known game');
      expect(
        revoked.titles.single.state,
        ProfileLocalLibraryState.accessRequired,
      );
      expect(await database.select(database.localInstalls).get(), hasLength(1));

      await database
          .update(database.profileLocalGames)
          .write(
            const ProfileLocalGamesCompanion(
              authorizationState: Value('authorized'),
            ),
          );
      expect(
        (await _nextReady(events)).titles.single.state,
        ProfileLocalLibraryState.ready,
      );
    },
  );

  test(
    'one title selects accessible history release and reports all references',
    () async {
      await _seedGame(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseA,
        title: _titleA,
        titleName: 'Older played release',
        acquiredAt: _now,
      );
      await _seedGame(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseB,
        title: _titleA,
        titleName: 'Newer release',
        acquiredAt: _now.add(const Duration(hours: 2)),
      );
      await database
          .into(database.profilePlayHistories)
          .insert(
            ProfilePlayHistoriesCompanion.insert(
              localProfileId: 'profile-a',
              serverInstanceId: _serverA.value,
              titleId: _titleA.value,
              lastReleaseId: _releaseA.value,
              lastPlayedAt: _now.add(const Duration(hours: 3)),
              playCount: 4,
            ),
          );

      final ready =
          await repository()
                  .watchLibrary(sort: ProfileLocalLibrarySort.recentPlay)
                  .first
              as ProfileLocalLibraryReady;
      expect(ready.titles, hasLength(1));
      expect(ready.titles.single.install.releaseId, _releaseA.value);
      expect(ready.titles.single.releaseCount, 2);
      expect(ready.titles.single.playCount, 4);
    },
  );

  test('an authorized release wins over a newer revoked release', () async {
    await _seedGame(
      database,
      base,
      profileId: 'profile-a',
      server: _serverA,
      release: _releaseA,
      title: _titleA,
      titleName: 'Playable release',
      acquiredAt: _now,
    );
    await _seedGame(
      database,
      base,
      profileId: 'profile-a',
      server: _serverA,
      release: _releaseB,
      title: _titleA,
      titleName: 'Revoked release',
      acquiredAt: _now.add(const Duration(hours: 1)),
      authorization: ProfileGameAuthorization.revoked,
    );

    final ready =
        await repository()
                .watchLibrary(sort: ProfileLocalLibrarySort.recentInstall)
                .first
            as ProfileLocalLibraryReady;

    expect(ready.titles.single.install.releaseId, _releaseA.value);
    expect(ready.titles.single.state, ProfileLocalLibraryState.ready);
    expect(ready.titles.single.releaseCount, 2);
  });

  test('authorization dominates local readiness state', () async {
    await _seedGame(
      database,
      base,
      profileId: 'profile-a',
      server: _serverA,
      release: _releaseA,
      title: _titleA,
      titleName: 'Needs repair',
      state: InstallState.corrupt,
    );
    await _seedGame(
      database,
      base,
      profileId: 'profile-a',
      server: _serverA,
      release: _releaseC,
      title: _titleB,
      titleName: 'Still installing',
      state: InstallState.installing,
    );

    ProfileLocalLibraryReady ready =
        await repository()
                .watchLibrary(sort: ProfileLocalLibrarySort.title)
                .first
            as ProfileLocalLibraryReady;
    expect(ready.titles[0].state, ProfileLocalLibraryState.repairRequired);
    expect(ready.titles[1].state, ProfileLocalLibraryState.notReady);

    await (database.update(
      database.profileLocalGames,
    )..where((row) => row.releaseId.equals(_releaseA.value))).write(
      const ProfileLocalGamesCompanion(authorizationState: Value('revoked')),
    );
    ready =
        await repository()
                .watchLibrary(sort: ProfileLocalLibrarySort.title)
                .first
            as ProfileLocalLibraryReady;
    expect(ready.titles[0].state, ProfileLocalLibraryState.accessRequired);
  });

  test(
    'an older playable release wins over newer authorized local work',
    () async {
      await _seedGame(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseA,
        title: _titleA,
        titleName: 'Playable release',
        acquiredAt: _now,
      );
      await _seedGame(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseB,
        title: _titleA,
        titleName: 'Newer incomplete release',
        acquiredAt: _now.add(const Duration(hours: 1)),
        state: InstallState.installing,
      );

      final ready =
          await repository()
                  .watchLibrary(sort: ProfileLocalLibrarySort.recentInstall)
                  .first
              as ProfileLocalLibraryReady;

      expect(ready.titles.single.install.releaseId, _releaseA.value);
      expect(ready.titles.single.state, ProfileLocalLibraryState.ready);
      expect(ready.titles.single.releaseCount, 2);
    },
  );

  test(
    'inconsistent stored grant timing makes the projection unavailable',
    () async {
      await _seedGame(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseA,
        title: _titleA,
        titleName: 'Must not render',
      );
      await (database.update(
        database.profileLocalGames,
      )..where((row) => row.releaseId.equals(_releaseA.value))).write(
        ProfileLocalGamesCompanion(
          lastCheckedAt: Value(_now.subtract(const Duration(minutes: 1))),
        ),
      );

      expect(
        await repository()
            .watchLibrary(sort: ProfileLocalLibrarySort.title)
            .first,
        isA<ProfileLocalLibraryUnavailable>(),
      );
    },
  );

  test(
    'missing install is omitted and strict install corruption fails closed',
    () async {
      await database
          .into(database.profileLocalGames)
          .insert(
            ProfileLocalGamesCompanion.insert(
              localProfileId: 'profile-a',
              serverInstanceId: _serverA.value,
              releaseId: _releaseC.value,
              titleId: _titleB.value,
              authorizationState: ProfileGameAuthorization.authorized.name,
              acquiredAt: _now,
              lastCheckedAt: _now,
            ),
          );
      final empty =
          await repository()
                  .watchLibrary(sort: ProfileLocalLibrarySort.title)
                  .first
              as ProfileLocalLibraryReady;
      expect(empty.titles, isEmpty);

      await _seedGame(
        database,
        base,
        profileId: 'profile-a',
        server: _serverA,
        release: _releaseA,
        title: _titleA,
        titleName: 'Must not leak',
      );
      await database
          .into(database.profileLocalGames)
          .insert(
            ProfileLocalGamesCompanion.insert(
              localProfileId: 'profile-a',
              serverInstanceId: _serverA.value,
              releaseId: _releaseB.value,
              titleId: _titleA.value,
              authorizationState: ProfileGameAuthorization.authorized.name,
              acquiredAt: _now,
              lastCheckedAt: _now,
            ),
          );
      final oneLocalRelease =
          await repository()
                  .watchLibrary(sort: ProfileLocalLibrarySort.title)
                  .first
              as ProfileLocalLibraryReady;
      expect(oneLocalRelease.titles.single.releaseCount, 1);
      await (database.update(
        database.localInstalls,
      )..where((row) => row.releaseId.equals(_releaseA.value))).write(
        const LocalInstallsCompanion(contentRoot: Value('/foreign/root')),
      );
      expect(
        await repository()
            .watchLibrary(sort: ProfileLocalLibrarySort.title)
            .first,
        isA<ProfileLocalLibraryUnavailable>(),
      );
    },
  );

  test('all local sort modes have deterministic tie breakers', () async {
    await _seedGame(
      database,
      base,
      profileId: 'profile-a',
      server: _serverA,
      release: _releaseA,
      title: _titleA,
      titleName: 'Zulu',
      platformName: 'Alpha',
    );
    await _seedGame(
      database,
      base,
      profileId: 'profile-a',
      server: _serverA,
      release: _releaseC,
      title: _titleB,
      titleName: 'Alpha',
      platformName: 'Zulu',
      acquiredAt: _now.add(const Duration(hours: 1)),
    );

    Future<List<String>> names(ProfileLocalLibrarySort sort) async =>
        ((await repository().watchLibrary(sort: sort).first)
                as ProfileLocalLibraryReady)
            .titles
            .map((title) => title.install.titleName)
            .toList();

    expect(await names(ProfileLocalLibrarySort.title), <String>[
      'Alpha',
      'Zulu',
    ]);
    expect(await names(ProfileLocalLibrarySort.system), <String>[
      'Zulu',
      'Alpha',
    ]);
    expect(await names(ProfileLocalLibrarySort.recentInstall), <String>[
      'Alpha',
      'Zulu',
    ]);
    expect(await names(ProfileLocalLibrarySort.recentPlay), <String>[
      'Alpha',
      'Zulu',
    ]);
  });
}

Future<ProfileLocalLibraryReady> _nextReady(
  StreamIterator<ProfileLocalLibraryResult> events,
) async {
  expect(await events.moveNext(), isTrue);
  return events.current as ProfileLocalLibraryReady;
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

Future<void> _seedGame(
  AppDatabase database,
  Directory base, {
  required String profileId,
  required RomdServerInstanceId server,
  required RomdPublicId release,
  required RomdPublicId title,
  required String titleName,
  String platformName = 'SNES',
  DateTime? acquiredAt,
  ProfileGameAuthorization authorization = ProfileGameAuthorization.authorized,
  InstallState state = InstallState.installed,
}) async {
  final acquired = acquiredAt ?? _now;
  final installs = _installs(database, base, server);
  final root = ContentFileStore(baseDir: base, serverInstanceId: server.value)
      .contentRoot(
        platformShortName: platformName.toLowerCase(),
        titleId: title.value,
        releaseId: release.value,
      );
  await installs.upsert(
    LocalInstall(
      serverInstanceId: server.value,
      releaseId: release.value,
      titleId: title.value,
      titleName: titleName,
      platformId: 'snes',
      platformName: platformName,
      platformShortName: platformName.toLowerCase(),
      coverUrl: null,
      releaseName: release.value,
      releaseRevision: null,
      contentRoot: root.path,
      launchRelativePath: 'game.rom',
      sizeBytes: 5,
      primarySha256: _sha,
      manifestFingerprint: 'fingerprint-${release.value}',
      state: state,
      installMode: 'permanent',
      items: const <InstalledItem>[
        InstalledItem(relativePath: 'game.rom', sizeBytes: 5, sha256: _sha),
      ],
      installedAt: acquired,
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
          titleId: title.value,
          authorizationState: authorization.name,
          acquiredAt: acquired,
          lastCheckedAt: acquired,
        ),
      );
}
