import 'dart:io';

import 'package:drift/drift.dart' hide isNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/data/local_profiles/app_database.dart';
import 'package:romd_console/src/data/refresh_token_store.dart';
import 'package:romd_console/src/data/release_access_api_client.dart';
import 'package:romd_console/src/data/release_manifest_api_client.dart';
import 'package:romd_console/src/domain/profile_server_connection.dart';
import 'package:romd_console/src/domain/release_access.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';
import 'package:romd_console/src/domain/server_bound_release_manifest.dart';
import 'package:romd_console/src/play/content/data/content_file_store.dart';
import 'package:romd_console/src/play/content/data/content_verifier.dart';
import 'package:romd_console/src/play/content/data/download_client.dart';
import 'package:romd_console/src/play/content/data/drift_local_install_repository.dart';
import 'package:romd_console/src/play/content/data/install_mutation_serializer.dart';
import 'package:romd_console/src/play/content/data/profile_game_cleanup_service.dart';
import 'package:romd_console/src/play/content/data/romd_install_service.dart';
import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

import 'support/fake_profile_local_library_repository.dart';

void main() {
  test(
    'schema version 14 creates profile history and scoped installs',
    () async {
      final db = AppDatabase(NativeDatabase.memory());
      addTearDown(db.close);

      final tables = await _tableNames(db);
      final localInstallColumns = await _columnNames(db, 'local_installs');
      final legacyInstallColumns = await _columnNames(
        db,
        'legacy_local_installs',
      );
      final localProfileColumns = await _columnNames(db, 'local_profiles');
      final accountLinkColumns = await _columnNames(db, 'romd_account_links');
      final profileGameIndex = await db
          .customSelect('PRAGMA index_info(profile_local_games_server_release)')
          .get();

      expect(db.schemaVersion, 14);
      expect(
        tables,
        containsAll(<String>{
          'server_connections',
          'pending_server_locators',
          'profile_local_games',
          'profile_play_histories',
          'play_activity_sync_preferences',
          'local_play_sessions',
          'play_activity_outbox',
          'controller_mapping_profiles',
          'controller_profile_binding_rules',
          'controller_hardware_mappings',
        }),
      );
      expect(tables, contains('legacy_local_installs'));
      expect(
        localProfileColumns,
        containsAll(<String>{
          'selected_server_instance_id',
          'server_selection_generation',
        }),
      );
      expect(accountLinkColumns, contains('server_instance_id'));
      expect(
        await _columnNames(db, 'profile_play_histories'),
        contains('last_played_at'),
      );
      expect(
        await _columnNames(db, 'profile_play_histories'),
        isNot(contains('last_completed_at')),
      );
      expect(
        profileGameIndex.map((row) => row.read<String>('name')).toList(),
        <String>['server_instance_id', 'release_id'],
      );
      expect(legacyInstallColumns, _version10LocalInstallColumns);
      expect(localInstallColumns, contains('server_instance_id'));
      expect(localInstallColumns, contains('release_id'));
      expect(await db.select(db.localInstalls).get(), isEmpty);
      expect(await db.select(db.legacyLocalInstalls).get(), isEmpty);
    },
  );

  test('fresh schema enforces v14 keys, checks, and delete behavior', () async {
    final db = AppDatabase(NativeDatabase.memory());
    addTearDown(db.close);
    await db.customStatement('PRAGMA foreign_keys = ON');

    await db.customStatement('''
INSERT INTO server_connections VALUES (
  '11111111-1111-4111-8111-111111111111',
  'https://library.example',
  1783612800000,
  1783612800000
)
''');
    for (final profileId in <String>['profile-a', 'profile-b']) {
      await db.customStatement('''
INSERT INTO local_profiles (
  id, display_name, avatar_key, accent_color, romd_server_origin,
  entry_mode, created_at, updated_at, last_used_at
) VALUES (
  '$profileId', '$profileId', 'default', 1, '',
  'open', 1783612800000, 1783612800000, NULL
)
''');
      await db.customStatement('''
INSERT INTO pending_server_locators VALUES (
  '$profileId', 'https://same.example', 1783612800000, NULL
)
''');
    }

    expect(
      await _scalarInt(
        db,
        "SELECT COUNT(*) AS value FROM pending_server_locators WHERE normalized_origin = 'https://same.example'",
      ),
      2,
    );
    await expectLater(
      db.customStatement('''
UPDATE local_profiles
SET server_selection_generation = -1
WHERE id = 'profile-a'
'''),
      throwsA(isA<Exception>()),
    );
    await expectLater(
      db.customStatement('''
UPDATE local_profiles
SET selected_server_instance_id = '22222222-2222-4222-8222-222222222222'
WHERE id = 'profile-a'
'''),
      throwsA(isA<Exception>()),
    );
    await expectLater(
      db.customStatement('''
INSERT INTO profile_local_games VALUES (
  'profile-a',
  '22222222-2222-4222-8222-222222222222',
  'release-invalid-server',
  'title-1',
  'authorized',
  1783612800000,
  1783612800000
)
'''),
      throwsA(isA<Exception>()),
    );
    await expectLater(
      db.customStatement('''
INSERT INTO profile_local_games VALUES (
  'profile-a',
  '11111111-1111-4111-8111-111111111111',
  'release-invalid-state',
  'title-1',
  'allowed',
  1783612800000,
  1783612800000
)
'''),
      throwsA(isA<Exception>()),
    );

    await db.customStatement('''
INSERT INTO profile_local_games VALUES (
  'profile-a',
  '11111111-1111-4111-8111-111111111111',
  'release-1',
  'title-1',
  'authorized',
  1783612800000,
  1783612800000
)
''');
    await db.customStatement('''
INSERT INTO profile_play_histories VALUES (
  'profile-a',
  '11111111-1111-4111-8111-111111111111',
  'title-1',
  'release-1',
  1783612800000,
  1
)
''');
    await expectLater(
      db.customStatement('''
INSERT INTO profile_play_histories VALUES (
  'profile-b',
  '11111111-1111-4111-8111-111111111111',
  'title-invalid-count',
  'release-1',
  1783612800000,
  0
)
'''),
      throwsA(isA<Exception>()),
    );
    await expectLater(
      db.customStatement('''
INSERT INTO profile_local_games VALUES (
  'profile-a',
  '11111111-1111-4111-8111-111111111111',
  'release-1',
  'title-2',
  'revoked',
  1783612800000,
  1783612800000
)
'''),
      throwsA(isA<Exception>()),
    );
    await expectLater(
      db.customStatement('''
DELETE FROM server_connections
WHERE instance_id = '11111111-1111-4111-8111-111111111111'
'''),
      throwsA(isA<Exception>()),
    );

    await db.customStatement(
      "DELETE FROM local_profiles WHERE id = 'profile-a'",
    );
    expect(
      await _scalarInt(
        db,
        "SELECT COUNT(*) AS value FROM pending_server_locators WHERE local_profile_id = 'profile-a'",
      ),
      0,
    );
    expect(
      await _scalarInt(
        db,
        "SELECT COUNT(*) AS value FROM profile_play_histories WHERE local_profile_id = 'profile-a'",
      ),
      0,
    );
    expect(
      await _scalarInt(
        db,
        "SELECT COUNT(*) AS value FROM profile_local_games WHERE local_profile_id = 'profile-a'",
      ),
      0,
    );
    expect(
      await _scalarInt(db, 'SELECT COUNT(*) AS value FROM server_connections'),
      1,
    );
  });

  test('upgrade from schema version 13 preserves play history', () async {
    final temp = await Directory.systemTemp.createTemp('romd-v13-v14-');
    addTearDown(() => temp.delete(recursive: true));
    final file = File('${temp.path}/migration.sqlite');
    final seeded = AppDatabase(NativeDatabase(file));
    await seeded.customStatement('''
INSERT INTO server_connections VALUES (
  '11111111-1111-4111-8111-111111111111',
  'https://library.example', 1783612800000, 1783612800000
)
''');
    await seeded.customStatement('''
INSERT INTO local_profiles (
  id, display_name, avatar_key, accent_color, romd_server_origin,
  entry_mode, created_at, updated_at, last_used_at,
  selected_server_instance_id, server_selection_generation
) VALUES (
  'profile-1', 'Player', 'default', 1, '', 'open',
  1783612800000, 1783612800000, NULL,
  '11111111-1111-4111-8111-111111111111', 1
)
''');
    await seeded.customStatement('''
INSERT INTO profile_play_histories VALUES (
  'profile-1', '11111111-1111-4111-8111-111111111111',
  'title-1', 'release-1', 1783699200, 3
)
''');
    await seeded.close();

    final migrated = AppDatabase(
      NativeDatabase(
        file,
        setup: (raw) {
          raw.execute('DROP TABLE play_activity_outbox');
          raw.execute('DROP TABLE local_play_sessions');
          raw.execute('DROP TABLE play_activity_sync_preferences');
          raw.execute(
            'ALTER TABLE profile_play_histories '
            'RENAME COLUMN last_played_at TO last_completed_at',
          );
          raw.execute('PRAGMA user_version = 13');
        },
      ),
    );
    addTearDown(migrated.close);

    expect(await _userVersion(migrated), 14);
    final history =
        (await migrated.select(migrated.profilePlayHistories).get()).single;
    expect(history.lastPlayedAt.toUtc(), DateTime.utc(2026, 7, 10, 16));
    expect(history.playCount, 3);
    expect(await migrated.select(migrated.localPlaySessions).get(), isEmpty);
    expect(await migrated.select(migrated.playActivityOutbox).get(), isEmpty);
  });

  for (final version in <int>[8, 9, 10]) {
    test(
      'upgrade from schema version $version preserves its full state',
      () async {
        final db = AppDatabase(
          NativeDatabase.memory(
            setup: (raw) {
              final execute = (String sql) => raw.execute(sql);
              _createLegacySchema(execute, version);
              _seedSupportedPriorFixture(execute, version);
            },
          ),
        );
        addTearDown(db.close);

        expect(await _userVersion(db), 14);
        expect(await db.select(db.profilePlayHistories).get(), isEmpty);
        expect((await db.select(db.localProfiles).get()).single.id, 'legacy');
        expect(
          (await db.select(db.romdAccountLinks).get()).single.serverInstanceId,
          isNull,
        );
        expect(await db.select(db.localInstalls).get(), isEmpty);
        expect(
          (await db.select(db.legacyLocalInstalls).get()).single.releaseId,
          'release-old',
        );
        expect(
          (await db.select(db.runtimeOverrideRules).get()).single.scopeValue,
          'snes',
        );
        expect(
          (await db.select(db.controllerBindingRules).get()).single.action,
          'menu',
        );
        expect(
          (await db.select(db.controllerPreferencesRows).get())
              .single
              .templateId,
          'xbox',
        );
        expect(await db.select(db.serverConnections).get(), isEmpty);
        expect(await db.select(db.profileLocalGames).get(), isEmpty);
        expect(
          (await db.select(db.pendingServerLocators).get())
              .single
              .normalizedOrigin,
          'https://legacy.example:5002',
        );

        final mappingProfiles = await db
            .select(db.controllerMappingProfiles)
            .get();
        final profileRules = await db
            .select(db.controllerProfileBindingRules)
            .get();
        if (version >= 9) {
          expect(mappingProfiles.single.displayName, 'Legacy Pad');
          expect(profileRules.single.action, 'menu');
        } else {
          expect(mappingProfiles, isEmpty);
          expect(profileRules, isEmpty);
        }
        expect(await db.select(db.controllerHardwareMappings).get(), isEmpty);
      },
    );
  }

  test('upgrade from schema version 11 quarantines installs exactly', () async {
    final db = AppDatabase(
      NativeDatabase.memory(
        setup: (raw) {
          final execute = (String sql) => raw.execute(sql);
          _createLegacySchema(execute, 10);
          _seedSupportedPriorFixture(execute, 10);
          _upgradeFixtureToVersion11(execute);
        },
      ),
    );
    addTearDown(db.close);

    expect(await _userVersion(db), 14);
    expect(await db.select(db.profilePlayHistories).get(), isEmpty);
    expect(await db.select(db.localInstalls).get(), isEmpty);
    expect(
      (await db.select(db.legacyLocalInstalls).get()).single.releaseId,
      'release-old',
    );
    expect(await db.select(db.profileLocalGames).get(), isEmpty);
    expect((await db.select(db.localProfiles).get()).single.id, 'legacy');
    expect(
      (await db.select(db.runtimeOverrideRules).get()).single.scopeValue,
      'snes',
    );
  });

  test(
    'realistic version 10 migration is additive, exact, and one-time',
    () async {
      final temp = await Directory.systemTemp.createTemp('romd-v10-v11-');
      addTearDown(() => temp.delete(recursive: true));
      final file = File('${temp.path}/migration.sqlite');

      final first = AppDatabase(
        NativeDatabase(
          file,
          setup: (raw) {
            final execute = (String sql) => raw.execute(sql);
            _createLegacySchema(execute, 10);
            _seedRealisticVersion10Fixture(execute);
          },
        ),
      );

      expect(await _userVersion(first), 14);
      expect(await first.select(first.serverConnections).get(), isEmpty);
      expect(await first.select(first.profileLocalGames).get(), isEmpty);
      expect(await first.select(first.profilePlayHistories).get(), isEmpty);

      final profiles = await first.customSelect('''
SELECT
  id,
  romd_server_origin,
  selected_server_instance_id,
  server_selection_generation
FROM local_profiles
ORDER BY id
''').get();
      expect(profiles.map((row) => row.data).toList(), <Map<String, Object?>>[
        <String, Object?>{
          'id': 'invalid-origin',
          'romd_server_origin': 'ftp://invalid.example/path',
          'selected_server_instance_id': null,
          'server_selection_generation': 0,
        },
        <String, Object?>{
          'id': 'linked-valid',
          'romd_server_origin': ' HTTPS://Library.Example:5002/ ',
          'selected_server_instance_id': null,
          'server_selection_generation': 0,
        },
        <String, Object?>{
          'id': 'local-empty',
          'romd_server_origin': '',
          'selected_server_instance_id': null,
          'server_selection_generation': 0,
        },
        <String, Object?>{
          'id': 'valid-no-link',
          'romd_server_origin': 'other.example:6000',
          'selected_server_instance_id': null,
          'server_selection_generation': 0,
        },
      ]);

      final pending = await first.customSelect('''
SELECT local_profile_id, normalized_origin, created_at, last_attempt_at
FROM pending_server_locators
ORDER BY local_profile_id
''').get();
      expect(pending.map((row) => row.data).toList(), <Map<String, Object?>>[
        <String, Object?>{
          'local_profile_id': 'linked-valid',
          'normalized_origin': 'https://library.example:5002',
          'created_at': 1783612800000,
          'last_attempt_at': null,
        },
        <String, Object?>{
          'local_profile_id': 'valid-no-link',
          'normalized_origin': 'http://other.example:6000',
          'created_at': 1783785600000,
          'last_attempt_at': null,
        },
      ]);

      final links = await first.customSelect('''
SELECT local_profile_id, romd_user_id, username, email, linked_at,
       last_login_at, server_instance_id
FROM romd_account_links
''').get();
      expect(links.single.data, <String, Object?>{
        'local_profile_id': 'linked-valid',
        'romd_user_id': 'romd-user-1',
        'username': 'linked-player',
        'email': 'linked@example.com',
        'linked_at': 1783612800000,
        'last_login_at': 1783699200000,
        'server_instance_id': null,
      });

      final installs = await first.customSelect('''
SELECT release_id, title_id, title_name, platform_id, platform_name,
       platform_short_name, cover_url, release_name, release_revision,
       content_root, launch_relative_path, size_bytes, primary_sha256,
       manifest_fingerprint, state, install_mode, manifest_snapshot,
       installed_at, last_played_at
FROM legacy_local_installs
ORDER BY release_id
''').get();
      expect(
        installs.map((row) => row.data).toList(),
        _expectedVersion10Installs,
      );

      expect(
        (await first.select(first.runtimeOverrideRules).get()).single.profileId,
        'retroarch-stable',
      );
      expect(
        (await first.select(first.controllerBindingRules).get()).single.button,
        'guide',
      );
      expect(
        (await first.select(first.controllerPreferencesRows).get())
            .single
            .templateId,
        'xbox',
      );
      expect(
        (await first.select(first.controllerMappingProfiles).get())
            .single
            .displayName,
        'Family Pad',
      );
      expect(
        (await first.select(first.controllerProfileBindingRules).get())
            .single
            .button,
        'start',
      );
      expect(
        (await first.select(first.controllerHardwareMappings).get())
            .single
            .displayName,
        'Family Pad Hardware',
      );
      await first.close();

      final reopened = AppDatabase(NativeDatabase(file));
      expect(await _userVersion(reopened), 14);
      expect(
        await reopened.select(reopened.profilePlayHistories).get(),
        isEmpty,
      );
      expect(
        await _scalarInt(
          reopened,
          'SELECT COUNT(*) AS value FROM pending_server_locators',
        ),
        2,
      );
      expect(await reopened.select(reopened.serverConnections).get(), isEmpty);
      expect(await reopened.select(reopened.profileLocalGames).get(), isEmpty);
      expect(
        await _scalarInt(
          reopened,
          'SELECT COUNT(*) AS value FROM legacy_local_installs',
        ),
        2,
      );
      expect(await reopened.select(reopened.localInstalls).get(), isEmpty);
      await reopened.close();
    },
  );

  test(
    'realistic version 12 migration invents no history and preserves state',
    () async {
      final temp = await Directory.systemTemp.createTemp('romd-v12-v14-');
      addTearDown(() => temp.delete(recursive: true));
      final file = File('${temp.path}/migration.sqlite');
      final first = AppDatabase(
        NativeDatabase(
          file,
          setup: (raw) {
            final execute = (String sql) => raw.execute(sql);
            _createLegacySchema(execute, 10);
            _seedSupportedPriorFixture(execute, 10);
            _upgradeFixtureToVersion11(execute);
            _upgradeFixtureToVersion12(execute);
            _seedRealisticVersion12Fixture(execute);
          },
        ),
      );

      expect(await _userVersion(first), 14);
      expect(await first.select(first.profilePlayHistories).get(), isEmpty);
      expect(await first.select(first.localProfiles).get(), hasLength(2));
      expect(await first.select(first.profileLocalGames).get(), hasLength(2));
      expect(await first.select(first.localInstalls).get(), hasLength(2));
      expect(await first.select(first.legacyLocalInstalls).get(), hasLength(1));
      expect(
        (await first
                .customSelect(
                  'SELECT last_played_at FROM local_installs ORDER BY release_id',
                )
                .get())
            .map((row) => row.read<int>('last_played_at'))
            .toList(),
        <int>[1783958400000, 1784044800000],
      );
      expect(
        (await first.select(first.runtimeOverrideRules).get())
            .single
            .scopeValue,
        'snes',
      );
      expect(
        (await first.select(first.controllerPreferencesRows).get())
            .single
            .templateId,
        'xbox',
      );
      expect(
        (await first.select(first.controllerHardwareMappings).get())
            .single
            .displayName,
        'Fixture Pad',
      );

      await first.customStatement('PRAGMA foreign_keys = ON');
      await first.customStatement('''
INSERT INTO profile_play_histories VALUES (
  'second', '11111111-1111-4111-8111-111111111111',
  'title-active-a', 'release-active-a', 1784131200000, 1
)
''');
      await expectLater(
        first.customStatement('''
INSERT INTO profile_play_histories VALUES (
  'second', '11111111-1111-4111-8111-111111111111',
  'title-active-a', 'release-active-b', 1784217600000, 2
)
'''),
        throwsA(isA<Exception>()),
      );
      await expectLater(
        first.customStatement('''
INSERT INTO profile_play_histories VALUES (
  'legacy', '11111111-1111-4111-8111-111111111111',
  'title-invalid', 'release-active-b', 1784217600000, 0
)
'''),
        throwsA(isA<Exception>()),
      );
      await first.customStatement(
        "DELETE FROM local_profiles WHERE id = 'second'",
      );
      expect(await first.select(first.profilePlayHistories).get(), isEmpty);
      expect(await first.select(first.localInstalls).get(), hasLength(2));
      await first.close();

      final reopened = AppDatabase(NativeDatabase(file));
      expect(await _userVersion(reopened), 14);
      expect(
        await reopened.select(reopened.profilePlayHistories).get(),
        isEmpty,
      );
      expect(await reopened.select(reopened.localProfiles).get(), hasLength(1));
      expect(await reopened.select(reopened.localInstalls).get(), hasLength(2));
      expect(
        (await reopened.select(reopened.controllerHardwareMappings).get())
            .single
            .displayName,
        'Fixture Pad',
      );
      await reopened.close();
    },
  );

  test('v10 canonical install migrates through verified online adoption', () async {
    final temp = await Directory.systemTemp.createTemp('romd-v10-adoption-');
    addTearDown(() => temp.delete(recursive: true));
    final databaseFile = File('${temp.path}/migration.sqlite');
    final instance = RomdServerInstanceId.tryParse(
      '11111111-1111-4111-8111-111111111111',
    )!;
    final releaseA = RomdPublicId.tryParse(RomdPublicId.encode(101))!;
    final releaseB = RomdPublicId.tryParse(RomdPublicId.encode(102))!;
    final titleA = RomdPublicId.tryParse(RomdPublicId.encode(201))!;
    final titleB = RomdPublicId.tryParse(RomdPublicId.encode(202))!;
    const platform = 'snes';
    final fileStore = ContentFileStore(
      baseDir: temp,
      serverInstanceId: instance.value,
    );
    final canonicalLegacyRoot = fileStore.legacyContentRoot(
      platformShortName: 'snes',
      titleId: titleA.value,
      releaseId: releaseA.value,
    );
    final noncanonicalLegacyRoot = Directory(
      '${temp.path}/unmanaged/${releaseB.value}',
    );
    await canonicalLegacyRoot.create(recursive: true);
    await noncanonicalLegacyRoot.create(recursive: true);
    await File('${canonicalLegacyRoot.path}/game.sfc').writeAsString('hello');
    await File(
      '${noncanonicalLegacyRoot.path}/game.sfc',
    ).writeAsString('hello');
    final legacyFingerprint = const ContentVerifier().manifestFingerprint(
      const <FingerprintItem>[
        (
          relativePath: 'game.sfc',
          sizeBytes: 5,
          sha256:
              '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824',
        ),
      ],
    );

    final db = AppDatabase(
      NativeDatabase(
        databaseFile,
        setup: (raw) {
          final execute = (String sql) => raw.execute(sql);
          _createLegacySchema(execute, 10);
          execute('''
INSERT INTO local_profiles VALUES (
  'profile-1', 'Player', 'default', 1, '', 'open',
  1783612800000, 1783612800000, NULL
)
''');
          for (final install in <({String release, String title, String root})>[
            (
              release: releaseA.value,
              title: titleA.value,
              root: canonicalLegacyRoot.path,
            ),
            (
              release: releaseB.value,
              title: titleB.value,
              root: noncanonicalLegacyRoot.path,
            ),
          ]) {
            execute('''
INSERT INTO local_installs VALUES (
  '${install.release}', '${install.title}', 'Migrated Game',
  '${platform}', 'SNES', 'snes', NULL, 'Migrated Game', NULL,
  '${install.root}', 'game.sfc', 5, NULL, '$legacyFingerprint',
  'installed', 'permanent',
  '[{"relativePath":"game.sfc","sizeBytes":5,"sha256":"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"}]',
  1783612800000, NULL
)
''');
          }
        },
      ),
    );
    addTearDown(db.close);
    expect(await _userVersion(db), 14);
    expect(await db.select(db.profilePlayHistories).get(), isEmpty);
    expect(await db.select(db.legacyLocalInstalls).get(), hasLength(2));

    final now = DateTime.utc(2026, 7, 18, 12);
    final connection = ProfileServerConnection(
      instanceId: instance,
      origin: Uri.parse('https://library.example'),
      firstSeenAt: now,
      lastSeenAt: now,
    );
    await db
        .into(db.serverConnections)
        .insert(
          ServerConnectionsCompanion.insert(
            instanceId: instance.value,
            lastKnownOrigin: connection.origin.toString(),
            firstSeenAt: now,
            lastSeenAt: now,
          ),
        );
    await (db.update(
      db.localProfiles,
    )..where((row) => row.id.equals('profile-1'))).write(
      LocalProfilesCompanion(
        selectedServerInstanceId: Value(instance.value),
        serverSelectionGeneration: const Value(1),
      ),
    );

    ServerBoundReleaseManifest manifest(
      RomdPublicId release,
      RomdPublicId title,
    ) => ServerBoundReleaseManifest(
      serverInstanceId: instance,
      releaseId: release,
      titleId: title,
      systemKey: 'snes',
      name: 'Migrated Game',
      revision: null,
      isComplete: true,
      runtime: const ServerBoundReleaseRuntime(
        contentType: 'single_rom',
        launch: ServerBoundLaunchTarget(type: 'file', relativePath: 'game.sfc'),
        packaging: 'direct_files',
        minimumInstallBytes: 5,
      ),
      items: <ServerBoundReleaseManifestItem>[
        ServerBoundReleaseManifestItem(
          relativePath: 'game.sfc',
          role: 'rom',
          sizeBytes: 5,
          sha256:
              '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824',
          isAvailable: true,
          contentGrant: ServerBoundContentGrant(
            downloadUrl: Uri.parse('https://library.example/content'),
            expiresAt: now.add(const Duration(minutes: 5)),
          ),
        ),
      ],
    );
    final downloads = _MigrationDownload();
    final mutationSerializer = InstallMutationSerializer();
    final cleanup = ProfileGameCleanupService(
      database: db,
      baseDir: temp,
      serializer: mutationSerializer,
      refreshTokenStore: _MigrationRefreshTokens(),
    );
    final service = RomdInstallService(
      releaseAccessApiClient: _MigrationAccess(instance),
      releaseManifestApiClient:
          _MigrationManifest(<String, ServerBoundReleaseManifest>{
            releaseA.value: manifest(releaseA, titleA),
            releaseB.value: manifest(releaseB, titleB),
          }),

      database: db,
      authority: InstallAuthorityContext(
        localProfileId: 'profile-1',
        connection: connection,
        generation: 1,
      ),
      downloadClient: downloads,
      verifier: const ContentVerifier(),
      fileStore: fileStore,
      installs: DriftLocalInstallRepository(
        database: db,
        serverInstanceId: instance,
        fileStore: fileStore,
      ),
      profileLocalLibrary: FakeProfileLocalLibraryRepository(),
      mutationSerializer: mutationSerializer,
      cleanupService: cleanup,
      now: () => now,
    );

    PlayTarget target(RomdPublicId release, RomdPublicId title) => PlayTarget(
      releaseId: release.value,
      titleId: title.value,
      displayName: 'Migrated Game',
      localProfileId: 'profile-1',
      platformId: platform,
      platformName: 'SNES',
    );

    final adopted = await service
        .install(
          target(releaseA, titleA),
          accessToken: 'token',
          operation: const _CurrentInstallOperation(),
        )
        .toList();
    expect(adopted.last, isA<InstallCompleted>());
    expect(downloads.calls, 0);
    expect(await canonicalLegacyRoot.exists(), isTrue);
    expect(await db.select(db.localInstalls).get(), hasLength(1));
    expect(await db.select(db.profileLocalGames).get(), hasLength(1));

    await cleanup.cleanupOrphans();
    expect(await canonicalLegacyRoot.exists(), isFalse);

    final rejectedLegacy = await service
        .install(
          target(releaseB, titleB),
          accessToken: 'token',
          operation: const _CurrentInstallOperation(),
        )
        .toList();
    expect(rejectedLegacy.last, isA<InstallCompleted>());
    expect(downloads.calls, 1);
    expect(await noncanonicalLegacyRoot.exists(), isTrue);
    final remainingLegacy = await db.select(db.legacyLocalInstalls).get();
    expect(remainingLegacy.single.releaseId, releaseB.value);
    expect(remainingLegacy.single.contentRoot, noncanonicalLegacyRoot.path);
    expect(await db.select(db.localInstalls).get(), hasLength(2));
    expect(await db.select(db.profileLocalGames).get(), hasLength(2));
  });
}

final class _CurrentInstallOperation implements InstallOperationLease {
  const _CurrentInstallOperation();

  @override
  bool get isCurrent => true;
}

final class _MigrationRefreshTokens implements RefreshTokenStore {
  @override
  Future<String?> read({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async => null;

  @override
  Future<void> write({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
    required String refreshToken,
  }) async {}

  @override
  Future<void> delete({
    required String profileId,
    required RomdServerInstanceId serverInstanceId,
  }) async {}

  @override
  Future<void> deleteLegacyOriginToken({
    required String profileId,
    required Uri serverOrigin,
  }) async {}
}

final class _MigrationAccess implements ReleaseAccessApiClient {
  const _MigrationAccess(this.instance);
  final RomdServerInstanceId instance;

  @override
  Future<ReleaseAccessResult> getReleaseAccess({
    required String accessToken,
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId releaseId,
    required RomdPublicId expectedTitleId,
  }) async => ReleaseAccessSuccess(
    ReleaseAccessAllowed(
      serverInstanceId: instance,
      releaseId: releaseId,
      titleId: expectedTitleId,
    ),
  );

  @override
  void close() {}
}

final class _MigrationManifest implements ReleaseManifestApiClient {
  const _MigrationManifest(this.manifests);
  final Map<String, ServerBoundReleaseManifest> manifests;

  @override
  Future<ReleaseManifestResult> getReleaseManifest({
    required String accessToken,
    required RomdServerInstanceId expectedServerInstanceId,
    required RomdPublicId releaseId,
    required RomdPublicId expectedTitleId,

    required String expectedSystemKey,
  }) async => ReleaseManifestSuccess(manifests[releaseId.value]!);

  @override
  void close() {}
}

final class _MigrationDownload implements DownloadClient {
  int calls = 0;

  @override
  Stream<int> download({required Uri url, required File destination}) async* {
    calls++;
    await destination.parent.create(recursive: true);
    await destination.writeAsString('hello');
    yield 5;
  }
}

typedef _ExecuteSql = void Function(String sql);

void _upgradeFixtureToVersion11(_ExecuteSql execute) {
  execute('''
CREATE TABLE server_connections (
  instance_id TEXT NOT NULL PRIMARY KEY,
  last_known_origin TEXT NOT NULL,
  first_seen_at INTEGER NOT NULL,
  last_seen_at INTEGER NOT NULL
)
''');
  execute(
    'ALTER TABLE local_profiles ADD COLUMN selected_server_instance_id TEXT NULL REFERENCES server_connections(instance_id)',
  );
  execute(
    'ALTER TABLE local_profiles ADD COLUMN server_selection_generation INTEGER NOT NULL DEFAULT 0 CHECK (server_selection_generation >= 0)',
  );
  execute('''
CREATE TABLE pending_server_locators (
  local_profile_id TEXT NOT NULL PRIMARY KEY REFERENCES local_profiles(id) ON DELETE CASCADE,
  normalized_origin TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  last_attempt_at INTEGER NULL
)
''');
  execute(
    'ALTER TABLE romd_account_links ADD COLUMN server_instance_id TEXT NULL REFERENCES server_connections(instance_id)',
  );
  execute('''
CREATE TABLE profile_local_games (
  local_profile_id TEXT NOT NULL REFERENCES local_profiles(id) ON DELETE CASCADE,
  server_instance_id TEXT NOT NULL REFERENCES server_connections(instance_id),
  release_id TEXT NOT NULL,
  title_id TEXT NOT NULL,
  authorization_state TEXT NOT NULL CHECK (authorization_state IN ('authorized', 'revoked')),
  acquired_at INTEGER NOT NULL,
  last_checked_at INTEGER NOT NULL,
  PRIMARY KEY (local_profile_id, server_instance_id, release_id)
)
''');
  execute(
    'CREATE INDEX profile_local_games_server_release ON profile_local_games (server_instance_id, release_id)',
  );
  execute('PRAGMA user_version = 11');
}

void _upgradeFixtureToVersion12(_ExecuteSql execute) {
  execute('ALTER TABLE local_installs RENAME TO legacy_local_installs');
  execute('''
CREATE TABLE local_installs (
  server_instance_id TEXT NOT NULL REFERENCES server_connections(instance_id),
  release_id TEXT NOT NULL,
  title_id TEXT NOT NULL,
  title_name TEXT NOT NULL DEFAULT '',
  platform_id TEXT NOT NULL,
  platform_name TEXT NULL,
  platform_short_name TEXT NOT NULL,
  cover_url TEXT NULL,
  release_name TEXT NOT NULL DEFAULT '',
  release_revision TEXT NULL,
  content_root TEXT NOT NULL,
  launch_relative_path TEXT NOT NULL,
  size_bytes INTEGER NOT NULL,
  primary_sha256 TEXT NULL,
  manifest_fingerprint TEXT NOT NULL,
  state TEXT NOT NULL,
  install_mode TEXT NOT NULL DEFAULT 'permanent',
  manifest_snapshot TEXT NOT NULL,
  installed_at INTEGER NOT NULL,
  last_played_at INTEGER NULL,
  PRIMARY KEY (server_instance_id, release_id)
)
''');
  execute('PRAGMA user_version = 12');
}

void _seedRealisticVersion12Fixture(_ExecuteSql execute) {
  execute('''
INSERT INTO server_connections VALUES
  ('11111111-1111-4111-8111-111111111111', 'https://a.example', 1783612800000, 1783612800000),
  ('22222222-2222-4222-8222-222222222222', 'https://b.example', 1783612800000, 1783612800000)
''');
  execute('''
UPDATE local_profiles
SET selected_server_instance_id = '11111111-1111-4111-8111-111111111111',
    server_selection_generation = 1
WHERE id = 'legacy'
''');
  execute('''
INSERT INTO local_profiles (
  id, display_name, avatar_key, accent_color, romd_server_origin, entry_mode,
  created_at, updated_at, last_used_at, selected_server_instance_id,
  server_selection_generation
) VALUES (
  'second', 'Second Player', 'default', 2, '', 'open',
  1783612800000, 1783612800000, NULL,
  '11111111-1111-4111-8111-111111111111', 1
)
''');
  execute('''
INSERT INTO profile_local_games VALUES
  ('legacy', '11111111-1111-4111-8111-111111111111', 'release-active-a', 'title-active-a', 'authorized', 1783612800000, 1783612800000),
  ('second', '11111111-1111-4111-8111-111111111111', 'release-active-b', 'title-active-b', 'revoked', 1783612800000, 1783612800000)
''');
  execute('''
INSERT INTO local_installs VALUES
  ('11111111-1111-4111-8111-111111111111', 'release-active-a', 'title-active-a', 'Alpha',
   'platform-snes', 'SNES', 'snes', NULL, 'Alpha', NULL,
   '/content/a', 'alpha.sfc', 4096, NULL, 'fingerprint-a', 'installed', 'permanent',
   '[{"relativePath":"alpha.sfc","sizeBytes":4096,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]',
   1783612800000, 1783958400000),
  ('11111111-1111-4111-8111-111111111111', 'release-active-b', 'title-active-b', 'Beta',
   'platform-snes', 'SNES', 'snes', NULL, 'Beta', NULL,
   '/content/b', 'beta.sfc', 8192, NULL, 'fingerprint-b', 'installed', 'permanent',
   '[{"relativePath":"beta.sfc","sizeBytes":8192,"sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}]',
   1783699200000, 1784044800000)
''');
  execute('''
INSERT INTO controller_hardware_mappings VALUES (
  'macos', 'fixture-guid', 'Fixture Pad', 1,
  '{"buttons":{}}', 1783612800000, 1783612800000
)
''');
}

void _createLegacySchema(_ExecuteSql execute, int version) {
  execute('''
CREATE TABLE local_profiles (
  id TEXT NOT NULL PRIMARY KEY,
  display_name TEXT NOT NULL,
  avatar_key TEXT NOT NULL,
  accent_color INTEGER NOT NULL,
  romd_server_origin TEXT NOT NULL DEFAULT '',
  entry_mode TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  last_used_at INTEGER NULL
)
''');
  execute('''
CREATE TABLE romd_account_links (
  local_profile_id TEXT NOT NULL PRIMARY KEY REFERENCES local_profiles(id),
  romd_user_id TEXT NOT NULL,
  username TEXT NOT NULL,
  email TEXT NOT NULL,
  linked_at INTEGER NOT NULL,
  last_login_at INTEGER NULL
)
''');
  execute('''
CREATE TABLE local_installs (
  release_id TEXT NOT NULL PRIMARY KEY,
  title_id TEXT NOT NULL,
  title_name TEXT NOT NULL DEFAULT '',
  platform_id TEXT NULL,
  platform_name TEXT NULL,
  platform_short_name TEXT NOT NULL,
  cover_url TEXT NULL,
  release_name TEXT NOT NULL DEFAULT '',
  release_revision TEXT NULL,
  content_root TEXT NOT NULL,
  launch_relative_path TEXT NOT NULL,
  size_bytes INTEGER NOT NULL,
  primary_sha256 TEXT NULL,
  manifest_fingerprint TEXT NOT NULL,
  state TEXT NOT NULL,
  install_mode TEXT NOT NULL DEFAULT 'permanent',
  manifest_snapshot TEXT NOT NULL,
  installed_at INTEGER NOT NULL,
  last_played_at INTEGER NULL
)
''');
  execute('''
CREATE TABLE runtime_override_rules (
  scope TEXT NOT NULL,
  scope_value TEXT NOT NULL,
  profile_id TEXT NOT NULL,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (scope, scope_value)
)
''');
  execute('''
CREATE TABLE controller_binding_rules (
  scope TEXT NOT NULL,
  scope_value TEXT NOT NULL,
  action TEXT NOT NULL,
  button TEXT NULL,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (scope, scope_value, action)
)
''');
  execute('''
CREATE TABLE controller_preferences_rows (
  id INTEGER NOT NULL PRIMARY KEY,
  template_id TEXT NULL,
  updated_at INTEGER NOT NULL
)
''');
  if (version >= 9) {
    execute('''
CREATE TABLE controller_mapping_profiles (
  local_profile_id TEXT NOT NULL REFERENCES local_profiles(id),
  sdl_guid TEXT NOT NULL,
  display_name TEXT NOT NULL,
  template_id TEXT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (local_profile_id, sdl_guid)
)
''');
    execute('''
CREATE TABLE controller_profile_binding_rules (
  local_profile_id TEXT NOT NULL REFERENCES local_profiles(id),
  sdl_guid TEXT NOT NULL,
  scope TEXT NOT NULL,
  scope_value TEXT NOT NULL,
  action TEXT NOT NULL,
  button TEXT NULL,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (local_profile_id, sdl_guid, scope, scope_value, action)
)
''');
  }
  if (version >= 10) {
    execute('''
CREATE TABLE controller_hardware_mappings (
  sdl_platform TEXT NOT NULL,
  sdl_guid TEXT NOT NULL,
  display_name TEXT NOT NULL,
  mapping_format_version INTEGER NOT NULL,
  mapping_data TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (sdl_platform, sdl_guid)
)
''');
  }
  execute('PRAGMA user_version = $version');
}

void _seedSupportedPriorFixture(_ExecuteSql execute, int version) {
  execute('''
INSERT INTO local_profiles VALUES (
  'legacy', 'Legacy Player', 'default', 1,
  ' HTTPS://Legacy.Example:5002/ ', 'open',
  1783612800000, 1783612800000, 1783699200000
)
''');
  execute('''
INSERT INTO romd_account_links VALUES (
  'legacy', 'legacy-user', 'legacy', 'legacy@example.com',
  1783612800000, 1783699200000
)
''');
  execute('''
INSERT INTO local_installs VALUES (
  'release-old', 'title-old', 'Legacy Game', 'platform-snes', 'SNES',
  'snes', NULL, 'Legacy Game', NULL, '/legacy/content', 'game.sfc',
  4096, NULL, 'legacy-fingerprint', 'installed', 'permanent',
  '{"items":[{"path":"game.sfc","sizeBytes":4096}]}',
  1783612800000, 1783699200000
)
''');
  execute(
    "INSERT INTO runtime_override_rules VALUES ('platform', 'snes', 'retroarch-stable', 1783612800000)",
  );
  execute(
    "INSERT INTO controller_binding_rules VALUES ('global', '', 'menu', 'guide', 1783612800000)",
  );
  execute(
    "INSERT INTO controller_preferences_rows VALUES (1, 'xbox', 1783612800000)",
  );
  if (version >= 9) {
    execute(
      "INSERT INTO controller_mapping_profiles VALUES ('legacy', 'legacy-guid', 'Legacy Pad', 'xbox', 1783612800000, 1783612800000)",
    );
    execute(
      "INSERT INTO controller_profile_binding_rules VALUES ('legacy', 'legacy-guid', 'global', '', 'menu', 'guide', 1783612800000)",
    );
  }
}

void _seedRealisticVersion10Fixture(_ExecuteSql execute) {
  execute('''
INSERT INTO local_profiles VALUES
  ('local-empty', 'Local', 'default', 1, '', 'open', 1783526400000, 1783526400000, NULL),
  ('linked-valid', 'Linked', 'default', 2, ' HTTPS://Library.Example:5002/ ', 'pin', 1783612800000, 1783699200000, 1783872000000),
  ('valid-no-link', 'Unlinked', 'default', 3, 'other.example:6000', 'open', 1783785600000, 1783785600000, NULL),
  ('invalid-origin', 'Repair Me', 'default', 4, 'ftp://invalid.example/path', 'open', 1783872000000, 1783872000000, NULL)
''');
  execute('''
INSERT INTO romd_account_links VALUES (
  'linked-valid', 'romd-user-1', 'linked-player', 'linked@example.com',
  1783612800000, 1783699200000
)
''');
  execute('''
INSERT INTO local_installs VALUES
  ('release-a', 'title-a', 'Alpha', 'platform-snes', 'SNES', 'snes',
   'https://img.example/a.jpg', 'Alpha Rev A', 'A', '/content/a', 'alpha.sfc',
   4096, 'aaaaaaaa', 'fingerprint-a', 'installed', 'permanent',
   '{"items":[{"path":"alpha.sfc","sizeBytes":4096,"sha256":"aaaaaaaa"}]}',
   1783612800000, 1783958400000),
  ('release-b', 'title-b', 'Beta', 'platform-psx', 'PlayStation', 'psx',
   NULL, 'Beta Disc 1', NULL, '/content/b', 'beta.cue',
   8192, NULL, 'fingerprint-b', 'installed', 'permanent',
   '{"items":[{"path":"beta.cue","sizeBytes":128},{"path":"beta.bin","sizeBytes":8064}]}',
   1783699200000, 1784044800000)
''');
  execute(
    "INSERT INTO runtime_override_rules VALUES ('platform', 'snes', 'retroarch-stable', 1783612800000)",
  );
  execute(
    "INSERT INTO controller_binding_rules VALUES ('global', '', 'menu', 'guide', 1783612800000)",
  );
  execute(
    "INSERT INTO controller_preferences_rows VALUES (1, 'xbox', 1783612800000)",
  );
  execute(
    "INSERT INTO controller_mapping_profiles VALUES ('linked-valid', 'family-guid', 'Family Pad', 'xbox', 1783612800000, 1783699200000)",
  );
  execute(
    "INSERT INTO controller_profile_binding_rules VALUES ('linked-valid', 'family-guid', 'global', '', 'menu', 'start', 1783699200000)",
  );
  execute('''
INSERT INTO controller_hardware_mappings VALUES (
  'macos', 'family-guid', 'Family Pad Hardware', 1,
  '{"buttons":{}}', 1783612800000, 1783699200000
)
''');
}

const Set<String> _version10LocalInstallColumns = <String>{
  'release_id',
  'title_id',
  'title_name',
  'platform_id',
  'platform_name',
  'platform_short_name',
  'cover_url',
  'release_name',
  'release_revision',
  'content_root',
  'launch_relative_path',
  'size_bytes',
  'primary_sha256',
  'manifest_fingerprint',
  'state',
  'install_mode',
  'manifest_snapshot',
  'installed_at',
  'last_played_at',
};

const List<Map<String, Object?>>
_expectedVersion10Installs = <Map<String, Object?>>[
  <String, Object?>{
    'release_id': 'release-a',
    'title_id': 'title-a',
    'title_name': 'Alpha',
    'platform_id': 'platform-snes',
    'platform_name': 'SNES',
    'platform_short_name': 'snes',
    'cover_url': 'https://img.example/a.jpg',
    'release_name': 'Alpha Rev A',
    'release_revision': 'A',
    'content_root': '/content/a',
    'launch_relative_path': 'alpha.sfc',
    'size_bytes': 4096,
    'primary_sha256': 'aaaaaaaa',
    'manifest_fingerprint': 'fingerprint-a',
    'state': 'installed',
    'install_mode': 'permanent',
    'manifest_snapshot':
        '{"items":[{"path":"alpha.sfc","sizeBytes":4096,"sha256":"aaaaaaaa"}]}',
    'installed_at': 1783612800000,
    'last_played_at': 1783958400000,
  },
  <String, Object?>{
    'release_id': 'release-b',
    'title_id': 'title-b',
    'title_name': 'Beta',
    'platform_id': 'platform-psx',
    'platform_name': 'PlayStation',
    'platform_short_name': 'psx',
    'cover_url': null,
    'release_name': 'Beta Disc 1',
    'release_revision': null,
    'content_root': '/content/b',
    'launch_relative_path': 'beta.cue',
    'size_bytes': 8192,
    'primary_sha256': null,
    'manifest_fingerprint': 'fingerprint-b',
    'state': 'installed',
    'install_mode': 'permanent',
    'manifest_snapshot':
        '{"items":[{"path":"beta.cue","sizeBytes":128},{"path":"beta.bin","sizeBytes":8064}]}',
    'installed_at': 1783699200000,
    'last_played_at': 1784044800000,
  },
];

Future<Set<String>> _tableNames(AppDatabase db) async {
  final rows = await db
      .customSelect("SELECT name FROM sqlite_master WHERE type = 'table'")
      .get();
  return rows.map((row) => row.read<String>('name')).toSet();
}

Future<Set<String>> _columnNames(AppDatabase db, String table) async {
  final rows = await db.customSelect('PRAGMA table_info($table)').get();
  return rows.map((row) => row.read<String>('name')).toSet();
}

Future<int> _scalarInt(AppDatabase db, String sql) async =>
    (await db.customSelect(sql).getSingle()).read<int>('value');

Future<int> _userVersion(AppDatabase db) async =>
    (await db.customSelect('PRAGMA user_version').getSingle()).read<int>(
      'user_version',
    );
